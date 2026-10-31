using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.GraphicsInterface;
#else
using Autodesk.AutoCAD.GraphicsInterface;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 文字样式服务：样式重映射 + 字体属性写入 + MText 内联字体解析。
    ///
    /// 设计要点：
    ///   本插件改字体不是"只改实体上的 TextStyleId"，而是**直接改 TextStyleTableRecord 本身**
    ///   （FileName / BigFontFileName / XScale / ObliqueAngle / TextSize），
    ///   再把实体重映射到该样式——这样一次修改即可覆盖所有引用该样式的文字，
    ///   也避免产生大量重复样式。
    /// </summary>
    public static class TextStyleService
    {
        /// <summary>
        /// MText 内联字体格式码：{\Fgbenor.shx|b0|i0|c134|p2;文字}
        /// 捕获：cmd = F/f（保留大小写）、name = 字体名、opts = |b0|i0... 可选后缀
        /// </summary>
        private static readonly Regex InlineFontRegex = new Regex(
            @"\\(?<cmd>[Ff])(?<name>[^;|\\{}]*)(?<opts>\|[^;\\{}]*)?;",
            RegexOptions.Compiled);

        /// <summary>
        /// 确保文字样式存在（不存在时按配置创建），返回其 ObjectId。
        /// </summary>
        public static ObjectId EnsureTextStyle(Transaction tr, Database db, string styleName, RuleAction action, BatchLogger log)
        {
            if (string.IsNullOrWhiteSpace(styleName))
            {
                return ObjectId.Null;
            }

            string name = styleName.Trim();
            var table = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
            if (table == null)
            {
                return ObjectId.Null;
            }

            if (table.Has(name))
            {
                ObjectId existingId = table[name];

                // 已存在：仍然按配置刷新字体属性（"修改样式记录"而非"只改实体样式指针"）
                var existing = tr.GetObject(existingId, OpenMode.ForWrite, false) as TextStyleTableRecord;
                if (existing != null)
                {
                    ApplyFontSettings(existing, action, log);
                }

                return existingId;
            }

            table.UpgradeOpen();
            var record = new TextStyleTableRecord { Name = name };
            ObjectId id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);

            ApplyFontSettings(record, action, log);
            log?.Info(string.Format("已创建文字样式「{0}」。", name));
            return id;
        }

        /// <summary>
        /// 把字体相关配置写入 TextStyleTableRecord。
        /// 说明：
        ///   * SHX 字体（.shx）：写 FileName + BigFontFileName；
        ///   * TrueType 字体（.ttf 或无扩展名）：额外写 FontDescriptor，AutoCAD 才会按 TTF 渲染；
        ///   * ObliqueAngle 在 API 中单位是【弧度】，配置里用【度】，这里做换算。
        /// </summary>
        public static void ApplyFontSettings(TextStyleTableRecord record, RuleAction action, BatchLogger log)
        {
            if (record == null || action == null)
            {
                return;
            }

            string fontFile = action.FontFileName == null ? null : action.FontFileName.Trim();
            bool isShx = !string.IsNullOrEmpty(fontFile) && fontFile.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(fontFile))
            {
                record.FileName = fontFile;

                if (isShx)
                {
                    record.BigFontFileName = string.IsNullOrWhiteSpace(action.BigFont) ? string.Empty : action.BigFont.Trim();
                }
                else
                {
                    // TrueType：FileName 写字体名，同时设置 FontDescriptor
                    try
                    {
                        string typeFace = Path.GetFileNameWithoutExtension(fontFile);
                        record.Font = new FontDescriptor(typeFace, false, false, 0, 0);
                        record.BigFontFileName = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        log?.Warn(string.Format("设置 TrueType 字体描述符失败（{0}）：{1}", fontFile, ex.Message));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(action.BigFont))
            {
                record.BigFontFileName = action.BigFont.Trim();
            }

            if (action.WidthFactor.HasValue && action.WidthFactor.Value > 0)
            {
                record.XScale = action.WidthFactor.Value;
            }

            if (action.ObliqueAngle.HasValue)
            {
                // API 中 ObliquingAngle 的单位是弧度，配置里用度
                record.ObliquingAngle = action.ObliqueAngle.Value * Math.PI / 180.0;
            }

            if (action.TextStyleHeight.HasValue && action.TextStyleHeight.Value >= 0)
            {
                record.TextSize = action.TextStyleHeight.Value;
            }
        }

        /// <summary>
        /// 替换 MText 内容中的 \F 内联字体名称，返回替换后的内容。
        /// 未开启 HandleMTextInlineFont 或内容不含格式码时原样返回。
        /// </summary>
        public static string ReplaceMTextInlineFont(string contents, string fontName, out int replacedCount)
        {
            replacedCount = 0;
            if (string.IsNullOrEmpty(contents) || string.IsNullOrWhiteSpace(fontName))
            {
                return contents;
            }

            int count = 0;
            string result = InlineFontRegex.Replace(contents, m =>
            {
                count++;
                string cmd = m.Groups["cmd"].Value;
                string opts = m.Groups["opts"].Success ? m.Groups["opts"].Value : string.Empty;
                return "\\" + cmd + fontName + opts + ";";
            });

            replacedCount = count;
            return result;
        }

        /// <summary>统计 MText 内容中的 \F 格式码数量。</summary>
        public static int CountInlineFontCodes(string contents)
        {
            if (string.IsNullOrEmpty(contents))
            {
                return 0;
            }

            return InlineFontRegex.Matches(contents).Count;
        }

        /// <summary>
        /// 判断 MText 内容里是否存在与目标字体不同的内联字体码（用于"已合规则跳过"）。
        /// </summary>
        public static bool HasForeignInlineFont(string contents, string fontName)
        {
            if (string.IsNullOrEmpty(contents))
            {
                return false;
            }

            foreach (Match m in InlineFontRegex.Matches(contents))
            {
                string name = m.Groups["name"].Value;
                if (!string.Equals(name, fontName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 反射兜底：给"可能没有 TextStyleId 属性"的对象（MLeader / 形位公差等）
        /// 赋文字样式。AutoCAD 2021~2025 之间这些类的属性存在差异，
        /// 用反射可以避免为一个可选能力引入编译期版本分支；失败时返回 false 并给出原因，由调用方记日志。
        /// 参数类型用 object：Table 的 Cell 并不是 DBObject，但也需要同一套兜底逻辑。
        /// </summary>
        public static bool TrySetTextStyleIdViaReflection(object obj, ObjectId styleId, out string reason)
        {
            reason = null;
            if (obj == null)
            {
                reason = "对象为空";
                return false;
            }

            try
            {
                PropertyInfo prop = obj.GetType().GetProperty("TextStyleId", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite)
                {
                    reason = string.Format("{0} 不支持 TextStyleId", obj.GetType().Name);
                    return false;
                }

                Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (propType != typeof(ObjectId))
                {
                    reason = string.Format("{0}.TextStyleId 类型不是 ObjectId", obj.GetType().Name);
                    return false;
                }

                prop.SetValue(obj, styleId, null);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 表格：逐个单元格设置文字样式（Table 的文字样式挂在 Cell 上）。
        /// </summary>
        public static int ApplyToTableCells(Table table, ObjectId styleId, out string error)
        {
            error = null;
            int count = 0;
            if (table == null)
            {
                return 0;
            }

            int rows, columns;
            try
            {
                rows = table.Rows.Count;
                columns = table.Columns.Count;
            }
            catch (Exception ex)
            {
                error = "读取表格行列数失败：" + ex.Message;
                return 0;
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    try
                    {
                        Cell cell = table.Cells[r, c];
                        if (cell == null)
                        {
                            continue;
                        }

                        // Cell.TextStyleId 是 ObjectId?（可为空），已是目标样式则跳过
                        if (cell.TextStyleId.HasValue && cell.TextStyleId.Value == styleId)
                        {
                            continue;
                        }

                        cell.TextStyleId = styleId;
                        count++;
                    }
                    catch (Exception ex)
                    {
                        if (error == null)
                        {
                            error = string.Format("单元格[{0},{1}]：{2}", r, c, ex.Message);
                        }
                    }
                }
            }

            return count;
        }

        /// <summary>取文字样式名（失败返回空串）。</summary>
        public static string GetTextStyleName(Transaction tr, ObjectId styleId)
        {
            if (styleId.IsNull)
            {
                return string.Empty;
            }

            try
            {
                var record = tr.GetObject(styleId, OpenMode.ForRead, false) as TextStyleTableRecord;
                return record == null ? string.Empty : record.Name;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>收集所有文字样式名（用于提示信息）。</summary>
        public static List<string> GetAllStyleNames(Transaction tr, Database db)
        {
            var names = new List<string>();
            var table = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead, false) as TextStyleTable;
            if (table == null)
            {
                return names;
            }

            foreach (ObjectId id in table)
            {
                var record = tr.GetObject(id, OpenMode.ForRead, false) as TextStyleTableRecord;
                if (record != null)
                {
                    names.Add(record.Name);
                }
            }

            return names;
        }
    }
}
