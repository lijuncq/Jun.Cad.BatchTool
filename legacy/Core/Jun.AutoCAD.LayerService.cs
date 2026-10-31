using System;
#if ZWCAD
using ZwSoft.ZwCAD.Colors;
#else
using Autodesk.AutoCAD.Colors;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 图层服务：确保目标图层存在（不存在则按配置继承默认颜色/线型/线宽自动创建），
    /// 并提供锁定判断（跳过锁定图层上的实体）。
    /// </summary>
    public static class LayerService
    {
        /// <summary>
        /// 确保图层存在，返回其 ObjectId。
        /// CreateIfMissing=false 且图层不存在时返回 ObjectId.Null。
        /// </summary>
        public static ObjectId EnsureLayer(Transaction tr, Database db, string layerName, RuleAction action, BatchLogger log)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return ObjectId.Null;
            }

            string name = layerName.Trim();
            bool createIfMissing = RuleContext(action);

            var layerTable = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (layerTable == null)
            {
                return ObjectId.Null;
            }

            if (layerTable.Has(name))
            {
                return layerTable[name];
            }

            if (!createIfMissing)
            {
                log?.Warn(string.Format("图层「{0}」不存在，且 CreateIfMissing=false，相关对象将被跳过。", name));
                return ObjectId.Null;
            }

            // 自动创建：继承配置中指定的默认颜色 / 线型 / 线宽
            layerTable.UpgradeOpen();

            int colorIndex = 7;
            if (action != null && action.LayerColorIndex.HasValue)
            {
                int raw = action.LayerColorIndex.Value;
                if (raw >= 1 && raw <= 255)
                {
                    colorIndex = raw;
                }
                else
                {
                    // 0 = 随块、256 = 随层，图层颜色只能是 1~255；非法值会让 AutoCAD 抛 eInvalidInput
                    log?.Warn(string.Format(
                        "颜色索引 {0} 不是合法的图层颜色（只能是 1~255，0=随块/256=随层仅用于实体），图层「{1}」改用默认颜色 7。",
                        raw, name));
                }
            }

            var record = new LayerTableRecord { Name = name };
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);

            if (action != null && !string.IsNullOrWhiteSpace(action.LayerLinetype))
            {
                ObjectId ltId = LinetypeService.EnsureLinetype(tr, db, action.LayerLinetype, log);
                if (!ltId.IsNull)
                {
                    record.LinetypeObjectId = ltId;
                }
            }

            string lineWeightText = "默认";
            if (action != null && action.LayerLineWeight.HasValue)
            {
                int raw = action.LayerLineWeight.Value;
                if (LineWeightUtil.IsValidLayerValue(raw))
                {
                    // 必须是 LineWeight 枚举里的具体值，直接强转任意整数会让宿主抛 eInvalidInput
                    record.LineWeight = (LineWeight)raw;
                    lineWeightText = LineWeightUtil.Describe(raw);
                }
                else
                {
                    // 非法值不中止规则：忽略这一项，保持 AutoCAD 默认线宽，并把"大概想要哪个"告诉用户
                    log?.Warn(string.Format(
                        "线宽 {0} 不是 AutoCAD 支持的图层线宽（只能是具体线宽：{1}，单位 0.01mm），图层「{2}」改用默认线宽；最接近的合法值是 {3}。",
                        raw, LineWeightUtil.LayerValuesText, name, LineWeightUtil.Describe(LineWeightUtil.NearestLayerValue(raw))));
                }
            }

            // 是否可打印：不写等于 AutoCAD 的默认值（可打印），只有显式 false 才关掉
            bool plottable = action == null || !action.LayerPlottable.HasValue || action.LayerPlottable.Value;
            bool plottableApplied = true;
            try
            {
                record.IsPlottable = plottable;
            }
            catch (Exception ex)
            {
                // 打印属性只是"锦上添花"，不该让整条规则中止
                plottableApplied = false;
                log?.Warn(string.Format("图层「{0}」的打印属性设置失败（{1}），将保持 AutoCAD 默认值。", name, ex.Message));
            }

            ObjectId id;
            try
            {
                id = layerTable.Add(record);
                tr.AddNewlyCreatedDBObject(record, true);
            }
            catch (Exception ex)
            {
                // 带上"是哪个图层的哪个属性"再抛，避免日志里只剩一句 eInvalidInput
                throw new RuleValidationException(string.Format(
                    "创建图层「{0}」失败（颜色索引 {1} / 线型 {2} / 线宽 {3}）：{4}",
                    name,
                    colorIndex,
                    string.IsNullOrWhiteSpace(action == null ? null : action.LayerLinetype) ? "默认" : action.LayerLinetype,
                    lineWeightText,
                    ex.Message));
            }

            log?.Info(string.Format("已自动创建图层「{0}」(颜色索引 {1}，线宽 {2}，{3})。",
                name, colorIndex, lineWeightText, plottableApplied ? (plottable ? "可打印" : "不打印") : "打印属性保持默认"));
            return id;
        }

        private static bool RuleContext(RuleAction action)
        {
            return action == null || !action.CreateIfMissing.HasValue || action.CreateIfMissing.Value;
        }

        /// <summary>图层是否被锁定（图层不存在时视为未锁定）。</summary>
        public static bool IsLocked(Transaction tr, Database db, string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            try
            {
                var layerTable = tr.GetObject(db.LayerTableId, OpenMode.ForRead, false) as LayerTable;
                if (layerTable == null || !layerTable.Has(layerName))
                {
                    return false;
                }

                var record = tr.GetObject(layerTable[layerName], OpenMode.ForRead, false) as LayerTableRecord;
                return record != null && record.IsLocked;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 线型服务：确保线型已加载（必要时从 acad.lin / acadiso.lin 加载）。
    /// </summary>
    public static class LinetypeService
    {
        public static readonly string[] StandardLinFiles = { "acad.lin", "acadiso.lin" };

        /// <summary>
        /// 确保线型可用，返回 ObjectId；"ByLayer"/"ByBlock" 返回 ObjectId.Null（由调用方特殊处理）。
        /// </summary>
        public static ObjectId EnsureLinetype(Transaction tr, Database db, string linetypeName, BatchLogger log)
        {
            if (string.IsNullOrWhiteSpace(linetypeName))
            {
                return ObjectId.Null;
            }

            string name = linetypeName.Trim();
            if (IsSymbolicLinetype(name))
            {
                return ObjectId.Null;
            }

            var table = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead, false) as LinetypeTable;
            if (table == null)
            {
                return ObjectId.Null;
            }

            if (table.Has(name))
            {
                return table[name];
            }

            // 尝试从标准线型文件加载
            foreach (string file in StandardLinFiles)
            {
                try
                {
                    db.LoadLineTypeFile(name, file);
                }
                catch
                {
                    // 文件不存在或线型未定义，继续尝试下一个
                }

                var reloaded = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead, false) as LinetypeTable;
                if (reloaded != null && reloaded.Has(name))
                {
                    log?.Info(string.Format("已从 {0} 加载线型「{1}」。", file, name));
                    return reloaded[name];
                }
            }

            log?.Warn(string.Format("线型「{0}」不存在且无法从标准线型文件加载。", name));
            return ObjectId.Null;
        }

        public static bool IsSymbolicLinetype(string name)
        {
            return string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "ByBlock", StringComparison.OrdinalIgnoreCase);
        }
    }
}
