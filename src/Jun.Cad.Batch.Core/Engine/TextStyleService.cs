using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 文字样式服务：样式重映射 + 字体属性写入 + MText 内联字体解析。
    ///
    /// 设计要点（与 AutoCAD 版一致）：
    ///   改字体不是"只改实体上的样式指针"，而是**直接改 <see cref="TextStyle"/> 本身**
    ///   （Filename / BigFontFilename / Width / ObliqueAngle / Height），再把实体重映射过去。
    ///   这样一次修改覆盖所有引用该样式的文字，也不会产生一堆重复样式。
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
        /// TrueType 字体的字型标志。
        ///
        /// AutoCAD 的组码 1071 是一个"pitch + charset + family + italic/bold"打包长整数，
        /// ACadSharp 把它建模成只有 Regular/Italic/Bold 三个值的 <see cref="FontFlags"/>，
        /// 精度不如原生但够用：中文工程图几乎全是 SHX 字体，TTF 只做"能显示出来"的兜底。
        /// </summary>
        private const FontFlags DefaultTrueTypeFlags = FontFlags.Regular;

        /// <summary>
        /// 确保文字样式存在（不存在时创建），并把配置里的字体参数写进去。
        /// 名称非法时返回 null。
        /// </summary>
        public static TextStyle EnsureTextStyle(CadDocument doc, string styleName, RuleAction action, BatchLogger log)
        {
            if (doc == null || string.IsNullOrWhiteSpace(styleName))
            {
                return null;
            }

            string name = styleName.Trim();

            TextStyle existing;
            if (doc.TextStyles.TryGetValue(name, out existing))
            {
                // 已存在：仍然按配置刷新字体属性（"改样式记录"而不是"只改实体指针"）
                ApplyFontSettings(existing, action, log);
                return existing;
            }

            var style = new TextStyle(name);
            ApplyFontSettings(style, action, log);

            try
            {
                doc.TextStyles.Add(style);
            }
            catch (Exception ex)
            {
                throw new RuleValidationException(string.Format("创建文字样式「{0}」失败：{1}", name, ex.Message), ex);
            }

            if (log != null)
            {
                log.Info(string.Format("已创建文字样式「{0}」。", name));
            }

            return style;
        }

        /// <summary>
        /// 把字体相关配置写入 <see cref="TextStyle"/>。
        ///
        /// 说明：
        ///   * SHX 字体（.shx）：写 Filename + BigFontFilename；
        ///   * TrueType 字体（.ttf 或无扩展名）：额外写 <see cref="TextStyle.TrueType"/>，
        ///     AutoCAD 才会按 TTF 渲染（只写文件名会被当成缺失的 SHX）；
        ///   * <see cref="TextStyle.ObliqueAngle"/> 单位是【弧度】，配置里用【度】，这里换算。
        /// </summary>
        public static void ApplyFontSettings(TextStyle style, RuleAction action, BatchLogger log)
        {
            if (style == null || action == null)
            {
                return;
            }

            string fontFile = action.FontFileName == null ? null : action.FontFileName.Trim();
            bool isShx = !string.IsNullOrEmpty(fontFile) && fontFile.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(fontFile))
            {
                style.Filename = fontFile;

                if (isShx)
                {
                    style.BigFontFilename = string.IsNullOrWhiteSpace(action.BigFont) ? string.Empty : action.BigFont.Trim();
                }
                else
                {
                    style.TrueType = DefaultTrueTypeFlags;
                    style.BigFontFilename = string.Empty;
                }
            }
            else if (!string.IsNullOrWhiteSpace(action.BigFont))
            {
                style.BigFontFilename = action.BigFont.Trim();
            }

            if (action.WidthFactor.HasValue && action.WidthFactor.Value > 0)
            {
                style.Width = action.WidthFactor.Value;
            }

            if (action.ObliqueAngle.HasValue)
            {
                style.ObliqueAngle = action.ObliqueAngle.Value * Math.PI / 180.0;
            }

            if (action.TextStyleHeight.HasValue && action.TextStyleHeight.Value >= 0)
            {
                style.Height = action.TextStyleHeight.Value;
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

        /// <summary>内容里是否存在与目标字体不同的内联字体码（用于"已合规则跳过"）。</summary>
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

        /// <summary>取实体当前使用的文字样式名（不支持的对象返回空串）。</summary>
        public static string StyleNameOf(Entity entity)
        {
            if (entity == null)
            {
                return string.Empty;
            }

            var text = entity as TextEntity;
            if (text != null)
            {
                return text.Style == null ? string.Empty : text.Style.Name;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                return mtext.Style == null ? string.Empty : mtext.Style.Name;
            }

            var dim = entity as Dimension;
            if (dim != null)
            {
                // 标注文字的字样由标注样式决定，不是实体自己的属性
                return dim.Style == null || dim.Style.Style == null ? string.Empty : dim.Style.Style.Name;
            }

            return string.Empty;
        }

        /// <summary>
        /// 该实体能不能直接挂文字样式。
        ///
        /// 表格（<see cref="TableEntity"/>）的文字样式挂在单元格上，
        /// ACadSharp 的单元格内容模型没有公开可写的字样入口——离线版**不支持**表格内文字换样式，
        /// 由调用方记为"跳过"并在日志里说明，避免"跑完了但表格没变"这种沉默失败。
        /// </summary>
        public static bool CanSetStyleDirectly(Entity entity)
        {
            return entity is TextEntity || entity is MText;
        }

        /// <summary>取字体名（用于内联替换）：SHX 用文件名，TTF 用去扩展名的字体名。</summary>
        public static string FontToken(RuleAction action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.FontFileName))
            {
                return null;
            }

            string file = action.FontFileName.Trim();
            if (file.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }

            return Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>收集所有文字样式名（用于"目标不存在"时的提示）。</summary>
        public static List<string> GetAllStyleNames(CadDocument doc)
        {
            var names = new List<string>();
            if (doc == null)
            {
                return names;
            }

            foreach (TextStyle style in doc.TextStyles)
            {
                if (style != null)
                {
                    names.Add(style.Name);
                }
            }

            return names;
        }
    }
}
