using System;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 标注样式服务。
    ///
    /// 注意一个容易踩坑的边界：**标注文字的文字样式不由 Dimension 实体直接控制**，
    /// 而是由它引用的 DimStyleTableRecord.TextStyleId 决定。
    /// 所以"改标注文字字体"必须落到标注样式上（见 TextFontChangeRule），
    /// 单改实体上的 TextStyleId 对标注无效。
    /// </summary>
    public static class DimStyleService
    {
        /// <summary>
        /// 确保标注样式存在；不存在时以 Standard（或表中第一个样式）为模板克隆一个。
        /// </summary>
        public static ObjectId EnsureDimStyle(Transaction tr, Database db, string styleName, RuleAction action, BatchLogger log, out bool created)
        {
            created = false;
            if (string.IsNullOrWhiteSpace(styleName))
            {
                return ObjectId.Null;
            }

            string name = styleName.Trim();
            var table = tr.GetObject(db.DimStyleTableId, OpenMode.ForRead) as DimStyleTable;
            if (table == null)
            {
                return ObjectId.Null;
            }

            if (table.Has(name))
            {
                ObjectId existId = table[name];
                ApplyOverrides(tr, existId, action, log);
                return existId;
            }

            bool createIfMissing = action == null || !action.CreateIfMissing.HasValue || action.CreateIfMissing.Value;
            if (!createIfMissing)
            {
                log?.Warn(string.Format("标注样式「{0}」不存在，且 CreateIfMissing=false，已跳过。", name));
                return ObjectId.Null;
            }

            // 选模板：优先 Standard，否则取表中第一个
            ObjectId templateId = ObjectId.Null;
            if (table.Has("Standard"))
            {
                templateId = table["Standard"];
            }
            else
            {
                foreach (ObjectId id in table)
                {
                    templateId = id;
                    break;
                }
            }

            if (templateId.IsNull)
            {
                log?.Warn("标注样式表为空，无法创建新标注样式。");
                return ObjectId.Null;
            }

            var template = tr.GetObject(templateId, OpenMode.ForRead, false) as DimStyleTableRecord;
            if (template == null)
            {
                return ObjectId.Null;
            }

            table.UpgradeOpen();
            var record = (DimStyleTableRecord)template.Clone();
            record.Name = name;
            ObjectId newId = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);

            created = true;
            log?.Info(string.Format("已基于「{0}」创建标注样式「{1}」。", template.Name, name));

            ApplyOverrides(tr, newId, action, log);
            return newId;
        }

        /// <summary>同步箭头大小、文字高度等常用参数。</summary>
        public static void ApplyOverrides(Transaction tr, ObjectId dimStyleId, RuleAction action, BatchLogger log)
        {
            if (dimStyleId.IsNull || action == null)
            {
                return;
            }

            if (!action.ArrowSize.HasValue && !action.TextHeight.HasValue)
            {
                return;
            }

            try
            {
                var record = tr.GetObject(dimStyleId, OpenMode.ForWrite, false) as DimStyleTableRecord;
                if (record == null)
                {
                    return;
                }

                if (action.ArrowSize.HasValue && action.ArrowSize.Value > 0)
                {
                    record.Dimasz = action.ArrowSize.Value;
                }

                if (action.TextHeight.HasValue && action.TextHeight.Value > 0)
                {
                    record.Dimtxt = action.TextHeight.Value;
                }
            }
            catch (Exception ex)
            {
                log?.Warn("更新标注样式参数失败：" + ex.Message);
            }
        }

        /// <summary>把标注样式的文字样式指向目标文字样式（标注文字字体的唯一正确入口）。</summary>
        public static bool SetTextStyle(Transaction tr, ObjectId dimStyleId, ObjectId textStyleId, BatchLogger log)
        {
            if (dimStyleId.IsNull || textStyleId.IsNull)
            {
                return false;
            }

            try
            {
                var record = tr.GetObject(dimStyleId, OpenMode.ForWrite, false) as DimStyleTableRecord;
                if (record == null)
                {
                    return false;
                }

                if (record.Dimtxsty == textStyleId)
                {
                    return false;
                }

                record.Dimtxsty = textStyleId;
                return true;
            }
            catch (Exception ex)
            {
                log?.Warn("设置标注样式文字样式失败：" + ex.Message);
                return false;
            }
        }

        public static string GetName(Transaction tr, ObjectId dimStyleId)
        {
            if (dimStyleId.IsNull)
            {
                return string.Empty;
            }

            try
            {
                var record = tr.GetObject(dimStyleId, OpenMode.ForRead, false) as DimStyleTableRecord;
                return record == null ? string.Empty : record.Name;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
