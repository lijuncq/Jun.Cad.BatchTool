using System;
using ACadSharp;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 图层服务：确保目标图层存在（不存在则按配置继承默认颜色/线型/线宽自动创建），
    /// 并提供锁定判断（跳过锁定图层上的实体）。
    ///
    /// 与 AutoCAD 版的差别：
    ///   * 不再有 <c>LayerTableRecord</c> 的 OpenMode / UpgradeOpen——<see cref="CadDocument.Layers"/>
    ///     就是内存集合，<c>Add</c> 即登记；
    ///   * 非法颜色/线宽**没有内核兜底**（AutoCAD 版靠 eInvalidInput 抛出来），
    ///     所以这里必须在写进模型之前主动校验，否则脏值会静默落进 DWG。
    /// </summary>
    public static class LayerService
    {
        /// <summary>
        /// 确保图层存在并返回图层对象。
        /// <paramref name="action"/> 里 <c>CreateIfMissing=false</c> 且图层不存在时返回 null。
        /// </summary>
        public static Layer EnsureLayer(CadDocument doc, string layerName, RuleAction action, BatchLogger log, ResourceLocator resources)
        {
            if (doc == null || string.IsNullOrWhiteSpace(layerName))
            {
                return null;
            }

            string name = layerName.Trim();
            bool createIfMissing = action == null || !action.CreateIfMissing.HasValue || action.CreateIfMissing.Value;

            Layer existing;
            if (doc.Layers.TryGetValue(name, out existing))
            {
                return existing;
            }

            if (!createIfMissing)
            {
                if (log != null)
                {
                    log.Warn(string.Format("图层「{0}」不存在，且 CreateIfMissing=false，相关对象将被跳过。", name));
                }

                return null;
            }

            // ---- 颜色：1~255 才是合法图层颜色（0=随块、256=随层只用于实体）----
            int colorIndex = 7;
            if (action != null && action.LayerColorIndex.HasValue)
            {
                int raw = action.LayerColorIndex.Value;
                if (raw >= 1 && raw <= 255)
                {
                    colorIndex = raw;
                }
                else if (log != null)
                {
                    log.Warn(string.Format(
                        "颜色索引 {0} 不是合法的图层颜色（只能是 1~255，0=随块/256=随层仅用于实体），图层「{1}」改用默认颜色 7。",
                        raw, name));
                }
            }

            var layer = new Layer(name)
            {
                Color = new Color((short)colorIndex)
            };

            // ---- 线型：缺失时尝试从 .lin 建出来 ----
            string linetypeText = "Continuous";
            if (action != null && !string.IsNullOrWhiteSpace(action.LayerLinetype))
            {
                LineType lt = LinetypeService.EnsureLinetype(doc, action.LayerLinetype, log, resources);
                if (lt != null)
                {
                    layer.LineType = lt;
                    linetypeText = lt.Name;
                }
                else
                {
                    linetypeText = "Continuous（指定的「" + action.LayerLinetype.Trim() + "」不可用）";
                }
            }
            else if (doc.LineTypes.Continuous != null)
            {
                layer.LineType = doc.LineTypes.Continuous;
            }

            // ---- 线宽：只能是具体线宽 ----
            string lineWeightText = "默认";
            if (action != null && action.LayerLineWeight.HasValue)
            {
                int raw = action.LayerLineWeight.Value;
                LineWeightType lw;
                if (LineWeightUtil.IsValidLayerValue(raw) && LineWeightUtil.TryToLineWeightType(raw, out lw))
                {
                    layer.LineWeight = lw;
                    lineWeightText = LineWeightUtil.Describe(raw);
                }
                else if (log != null)
                {
                    // 非法值不中止规则：保持库默认线宽，并把"大概想要哪个"告诉用户
                    log.Warn(string.Format(
                        "线宽 {0} 不是 AutoCAD 支持的图层线宽（只能是具体线宽：{1}，单位 0.01mm），图层「{2}」改用默认线宽；最接近的合法值是 {3}。",
                        raw, LineWeightUtil.LayerValuesText, name, LineWeightUtil.Describe(LineWeightUtil.NearestLayerValue(raw))));
                }
            }

            // ---- 可打印：不写等于默认（可打印）----
            bool plottable = action == null || !action.LayerPlottable.HasValue || action.LayerPlottable.Value;
            layer.PlotFlag = plottable;

            try
            {
                doc.Layers.Add(layer);
            }
            catch (Exception ex)
            {
                throw new RuleValidationException(string.Format(
                    "创建图层「{0}」失败（颜色索引 {1} / 线型 {2} / 线宽 {3}）：{4}",
                    name, colorIndex, linetypeText, lineWeightText, ex.Message), ex);
            }

            if (log != null)
            {
                log.Info(string.Format("已自动创建图层「{0}」(颜色索引 {1}，线型 {2}，线宽 {3}，{4})。",
                    name, colorIndex, linetypeText, lineWeightText, plottable ? "可打印" : "不打印"));
            }

            return layer;
        }

        /// <summary>图层是否被锁定（图层不存在时视为未锁定）。</summary>
        public static bool IsLocked(CadDocument doc, string layerName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            Layer layer;
            if (!doc.Layers.TryGetValue(layerName, out layer) || layer == null)
            {
                return false;
            }

            return ((int)layer.Flags & (int)LayerFlags.Locked) != 0;
        }

        /// <summary>图层是否被冻结（冻结层上的实体不显示，批量改它通常没意义）。</summary>
        public static bool IsFrozen(CadDocument doc, string layerName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            Layer layer;
            if (!doc.Layers.TryGetValue(layerName, out layer) || layer == null)
            {
                return false;
            }

            return ((int)layer.Flags & (int)LayerFlags.Frozen) != 0;
        }

        /// <summary>实体所在图层名（实体 Layer 为 null 时返回空串）。</summary>
        public static string LayerNameOf(ACadSharp.Entities.Entity entity)
        {
            if (entity == null || entity.Layer == null)
            {
                return string.Empty;
            }

            return entity.Layer.Name;
        }
    }

    /// <summary>
    /// 线型服务：确保线型已加载。
    ///
    /// 关键差别：AutoCAD 版可以喊 <c>db.LoadLineTypeFile(name, "acadiso.lin")</c> 让内核去读；
    /// 离线版没有内核，**必须自己解析 .lin 再逐段建出 LineType**，
    /// 否则"把线改成 DASHED"这类需求在目标图没预置该线型时会直接失败。
    /// </summary>
    public static class LinetypeService
    {
        /// <summary>
        /// 确保线型可用并返回线型对象。
        /// "ByLayer"/"ByBlock" 返回表中的符号线型（不是 null——调用方直接赋给实体即可）。
        /// 找不到且建不出来时返回 null。
        /// </summary>
        public static LineType EnsureLinetype(CadDocument doc, string linetypeName, BatchLogger log, ResourceLocator resources)
        {
            if (doc == null || string.IsNullOrWhiteSpace(linetypeName))
            {
                return null;
            }

            string name = linetypeName.Trim();

            if (IsByLayer(name))
            {
                return doc.LineTypes.ByLayer;
            }

            if (IsByBlock(name))
            {
                return doc.LineTypes.ByBlock;
            }

            LineType existing;
            if (doc.LineTypes.TryGetValue(name, out existing))
            {
                return existing;
            }

            // 从 .lin 建出来
            if (resources != null)
            {
                LineTypeDefinition def = resources.GetLinetype(name);
                if (def != null)
                {
                    var lt = new LineType(def.Name) { Description = def.Description ?? string.Empty };
                    foreach (double element in def.Elements)
                    {
                        lt.AddSegment(new LineType.Segment { Length = element });
                    }

                    try
                    {
                        doc.LineTypes.Add(lt);
                        if (log != null)
                        {
                            log.Info(string.Format("已按线型库定义创建线型「{0}」（{1} 段）。", def.Name, def.Elements.Count));
                        }

                        return lt;
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                        {
                            log.Warn(string.Format("创建线型「{0}」失败：{1}", def.Name, ex.Message));
                        }
                    }
                }
            }

            if (log != null)
            {
                log.Warn(string.Format("线型「{0}」不存在且无法从线型库（.lin）创建。", name));
            }

            return null;
        }

        public static bool IsByLayer(string name)
        {
            return string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsByBlock(string name)
        {
            return string.Equals(name, "ByBlock", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否是符号线型（随层/随块），这类名字不是真实线型，不能去表里找。</summary>
        public static bool IsSymbolicLinetype(string name)
        {
            return IsByLayer(name) || IsByBlock(name);
        }
    }
}
