using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 批量给文字加遮罩。默认**所有**文字都加（含块定义里的文字、标注里的文字）。
    ///
    /// 只有一条主线：原生遮罩，没有 2D Solid 兜底。
    ///   * MTEXT（模型/布局/块定义）→ 自身背景填充，用**图形背景色**；
    ///     文字框 (41) 比文字宽时（AutoCAD 原生背景按框宽填充，一侧会空出一大截）
    ///     改用紧贴实测文字宽度的 Wipeout（MaskTextBox=Auto，默认）；
    ///   * TEXT / ATTRIB / ATTDEF → WIPEOUT（真擦除，露出背景）；
    ///   * 标注文字 → DIMTFILL=1 样式覆盖（图形背景色，重生成不丢）。
    ///
    /// 绘制顺序：遮罩必须排在文字之前（否则文字自己被擦掉），且两者都要在
    /// 其它图形之上（否则线/填充穿过遮罩）。默认把遮罩+文字推到容器实体集合
    /// 末尾 = 最上层（MaskBringTextToFront，默认开），见 MaskService.PlaceMaskBefore。
    ///
    /// 边界偏移：每边 = **字高 × MaskPaddingRatio**（默认 0.15），不是包围盒高度——
    /// 多行 MTEXT 的包围盒高度是行数倍，用它会算出完全不同的留白。
    /// </summary>
    public sealed class TextMaskRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextMask"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            double padRatio = NormalizePaddingRatio(RuleContext.Double(action.MaskPaddingRatio, MaskService.DefaultPaddingRatio));
            bool onlyOccluded = RuleContext.Bool(action.MaskOnlyWhenOccluded, false);
            double threshold = RuleContext.Double(action.MaskMinOccludedRatio, 0.05);
            bool useBlockBounds = !RuleContext.Bool(action.MaskIgnoreBlockBounds, true);
            bool rebuildText = RuleContext.Bool(action.MaskRebuildText, true);
            string maskLayerName = string.IsNullOrWhiteSpace(action.MaskLayer)
                ? MaskService.DefaultMaskLayer
                : action.MaskLayer.Trim();
            int maxMasks = RuleContext.Int(action.MaxMasksPerDrawing, 0);
            string dimMode = NormalizeDimensionMode(action.DimensionTextMode);
            string dimMaskMode = NormalizeDimensionMaskMode(action.DimensionMaskMode);
            string maskTextBox = NormalizeMaskTextBox(action.MaskTextBox);
            bool bringToFront = RuleContext.Bool(action.MaskBringTextToFront, true);
            bool sendHatchToBack = RuleContext.Bool(action.MaskSendHatchToBack, true);
            bool maskInsideBlocks = RuleContext.Bool(action.MaskInsideBlocks, true);

            // 只剩原生遮罩一条路：Auto / Wipeout 同义。Solid 与其它写法是硬错误，
            // 不做"猜一个我能处理的模式"这种静默降级。
            RequireNativeMaskMode(action.MaskMode);

            var runtime = new MaskRuntime(context, action, maskLayerName, padRatio);

            var targets = new List<MaskTarget>();
            CollectTargets(context, result, dimMode, padRatio, maskInsideBlocks, maskTextBox, targets);

            if (targets.Count == 0)
            {
                result.AddNote("没有找到需要加遮罩的文字。");
                return;
            }

            int created = 0;
            int maskedMText = 0;
            int maskedDimension = 0;
            int maskedWipeout = 0;
            int movedToBack = 0;
            var hatchesToBack = new List<Hatch>();

            var bySpace = new Dictionary<BlockRecord, List<MaskTarget>>();
            foreach (MaskTarget target in targets)
            {
                List<MaskTarget> list;
                if (!bySpace.TryGetValue(target.Space, out list))
                {
                    list = new List<MaskTarget>();
                    bySpace[target.Space] = list;
                }

                list.Add(target);
            }

            foreach (KeyValuePair<BlockRecord, List<MaskTarget>> pair in bySpace)
            {
                BlockRecord space = pair.Key;
                List<MaskTarget> list = pair.Value;

                // 索引只在两种情况下要：判遮挡、找"压在文字上的填充"。
                // 两个开关都关时不建，省一遍全空间遍历。
                BoundsIndex occluders = (onlyOccluded || sendHatchToBack)
                    ? BuildOccluders(space, list, useBlockBounds, maskLayerName)
                    : null;

                foreach (MaskTarget target in list)
                {
                    double ratio = 1.0;
                    if (onlyOccluded)
                    {
                        // 块内文字**跳过遮挡判定**，直接加。
                        //
                        // 理由：块定义里的文字坐标是定义坐标系，真正会不会被压住取决于
                        // 每个块参照插到哪里、旁边画了什么——这些在定义层面根本看不到。
                        // 拿"块定义自身的实体"去判，得到的永远是"无遮挡"，
                        // 于是块内文字一个遮罩都不会加（这正是用户报的现象）。
                        // 判不了就不判，按"宁可多加"处理；数量上限 MaxMasksPerDrawing 兜底。
                        //
                        // 标注也跳过：DIMTFILL 覆盖不建几何，没有"白加"的问题。
                        if (!target.InsideBlock && target.Kind != "DIMENSION")
                        {
                            ratio = ComputeOcclusion(target.Rect, occluders);
                            if (ratio < threshold)
                            {
                                result.MarkSkipped("无遮挡");
                                context.RecordSkip(target.Entity, "加遮罩",
                                    string.Format("遮挡占比 {0:P0} < 阈值 {1:P0}", ratio, threshold),
                                    false, target.SpaceName);
                                continue;
                            }
                        }
                    }

                    if (maxMasks > 0 && created >= maxMasks)
                    {
                        result.MarkSkipped("超过单图遮罩上限");
                        context.RecordBlocked(target.Entity, "加遮罩",
                            string.Format("已达单图上限 {0}，后续文字不再加遮罩", maxMasks), target.SpaceName);
                        continue;
                    }

                    MaskPath path;
                    if (ApplyMask(context, result, runtime, target, rebuildText, dimMaskMode, maskTextBox, bringToFront, out path))
                    {
                        created++;

                        // 按 ApplyMask **实际走的路径**分类计数（配置开关不是路径）
                        switch (path)
                        {
                            case MaskPath.MTextNative:
                                maskedMText++;
                                break;
                            case MaskPath.DimensionOverride:
                                maskedDimension++;
                                break;
                            case MaskPath.EntityWipeout:
                                maskedWipeout++;
                                break;
                        }

                        // 遮罩要看得见，前提是遮挡物排在文字**下面**。
                        // 很多图的填充是后画的（排序表里排在最后 = 画在最上面），
                        // 这时遮罩被填充整块盖住，图上看着就像"根本没加遮罩"。
                        if (sendHatchToBack && occluders != null && target.Rect.IsValid)
                        {
                            foreach (int idx in occluders.Query(target.Rect))
                            {
                                var hatch = occluders.EntityOf(idx) as Hatch;
                                if (hatch != null)
                                {
                                    hatchesToBack.Add(hatch);
                                }
                            }
                        }
                    }
                }

                if (sendHatchToBack && hatchesToBack.Count > 0)
                {
                    movedToBack += PushHatchesToBack(context, result, space, hatchesToBack);
                    hatchesToBack.Clear();
                }
            }

            if (created > 0)
            {
                var parts = new List<string>();
                if (maskedMText > 0)
                {
                    parts.Add("MTEXT 原生背景 " + maskedMText);
                }

                if (maskedDimension > 0)
                {
                    parts.Add("标注文字 " + maskedDimension);
                }

                if (maskedWipeout > 0)
                {
                    parts.Add("Wipeout " + maskedWipeout);
                }

                if (movedToBack > 0)
                {
                    parts.Add("填充置后 " + movedToBack);
                }

                if (bringToFront && created > 0)
                {
                    parts.Add("文字前置 " + created);
                }

                result.AddNote(string.Format("共为 {0} 处文字加了遮罩（{1}）。", created, string.Join(" / ", parts)));
            }
        }

        // ------------------------------------------------------------------ 应用

        private static bool ApplyMask(
            RuleContext context,
            RuleExecutionResult result,
            MaskRuntime runtime,
            MaskTarget target,
            bool rebuildText,
            string dimMaskMode,
            string maskTextBox,
            bool bringToFront,
            out MaskPath path)
        {
            path = MaskPath.None;

            var mtext = target.Entity as MText;
            if (mtext != null && target.NativeMTextMask)
            {
                // 原生背景遮罩：不动实体内容，也绝不改 RectangleWidth
                //（写 41 会让 AutoCAD 重新折行，把一行文字拆成两行）。
                if (!MaskService.ApplyMTextBackground(mtext, runtime.PadRatio))
                {
                    result.MarkSkipped("已有背景遮罩");
                    context.RecordSkip(mtext, "加遮罩（MTEXT 原生）", "已有背景遮罩", false, target.SpaceName);
                    return false;
                }

                // 前置：Remove + Add 挪到容器末尾 = 绘制最上层，斜线/填充压不住遮罩。
                // 代价是句柄变化（排序表路径走不通，见 MaskService.BringToFront 的注释）。
                if (bringToFront)
                {
                    MaskService.BringToFront(target.Space, mtext);
                }

                result.MarkChanged("MTEXT");
                context.RecordChange(mtext, "加遮罩（MTEXT 原生背景）",
                    string.Format("字高 {0:0.##}，边界 {1:P0}", target.TextHeight, runtime.PadRatio),
                    string.Format("BackgroundFillFlags |= 图形背景色；BackgroundScale = {0:0.###}", MaskService.MaskOffsetFactor(runtime.PadRatio)),
                    false,
                    (target.Note == null ? string.Empty : target.Note + "；")
                    + "原生背景遮罩：不新增实体，填充用图形背景色"
                    + (bringToFront ? "；已移到绘制顺序最上层（句柄已变）" : ""),
                    target.SpaceName);
                path = MaskPath.MTextNative;
                return true;
            }

            if (mtext != null)
            {
                // 紧贴遮罩：AutoCAD 的原生背景按**文字框宽度**（DXF 41）填充，
                // 框比文字宽时遮罩一侧空出一大截（受控出图实测：框 60/文字 6，断口正好 60）。
                // 改用我们自己算的紧贴 Wipeout——不动 41，零折行风险。
                if (runtime.HasOverlappingWipeout(target.Space, target.Rect))
                {
                    result.MarkSkipped("已有紧贴遮罩");
                    context.RecordSkip(mtext, "加遮罩（MTEXT 紧贴）", "同位置已有 Wipeout 遮罩", false, target.SpaceName);
                    return false;
                }

                if (!rebuildText)
                {
                    result.MarkSkipped("需要重建文字");
                    context.RecordBlocked(target.Entity, "加遮罩（MTEXT 紧贴）",
                        "紧贴遮罩需要把 Wipeout 放到文字之前，而实体集合没有按位插入，只能移出文字再追加。"
                        + "确认可接受时把 Action.MaskRebuildText 设为 true（默认已是 true）。",
                        target.SpaceName);
                    return false;
                }

                // 原生背景（可能是旧版本写的松弛框）清掉，否则它会继续画在 Wipeout 之上。
                MaskService.ClearMTextBackground(mtext);

                Wipeout tightMask = runtime.CreateWipeout(target);

                // 移出文字 → 追加遮罩 → 文字回末尾：两者都到了最上层，遮罩在文字之下。
                MaskService.PlaceMaskBefore(target.Space, mtext, tightMask);

                result.MarkChanged("MTEXT");
                context.RecordChange(mtext, "加遮罩（MTEXT 紧贴）",
                    string.Format("字高 {0:0.##}，边界 {1:P0}", target.TextHeight, runtime.PadRatio),
                    string.Format("Wipeout 紧贴实测文字 {0:0.#}×{1:0.#}", target.Rect.Width, target.Rect.Height),
                    true,
                    (target.Note == null ? string.Empty : target.Note + "；")
                    + "原生背景填充按文字框宽度画、框宽时一侧留白过大，已换成紧贴文字的 Wipeout"
                    + "（RectangleWidth 未动，不会折行；文字实体句柄已变）",
                    target.SpaceName);
                path = MaskPath.EntityWipeout;
                return true;
            }

            if (target.Kind == "DIMENSION")
            {
                return ApplyDimensionMask(context, result, runtime, target, dimMaskMode, bringToFront, out path);
            }

            if (!rebuildText)
            {
                result.MarkSkipped("需要重建文字");
                context.RecordBlocked(target.Entity, "加遮罩（" + target.Kind + "）",
                    "该类型没有原生背景遮罩；Wipeout 只能追加在实体集合末尾（会盖住文字），"
                    + "要放到文字之前必须重建文字实体并重新分配句柄。确认可接受时把 Action.MaskRebuildText 设为 true（默认已是 true）。",
                    target.SpaceName);
                return false;
            }

            // 幂等：同位置已有几何一致的 Wipeout（多为对同一文件重复运行）就不再叠加。
            if (runtime.HasOverlappingWipeout(target.Space, target.Rect))
            {
                result.MarkSkipped("已有遮罩");
                context.RecordSkip(target.Entity, "加遮罩（" + target.Kind + "）", "同位置已有 Wipeout 遮罩", false, target.SpaceName);
                return false;
            }

            Wipeout mask = runtime.CreateWipeout(target);

            // 移出文字 → 追加遮罩 → 再把文字加回去：顺序变成 [遮罩, 文字] 且都在最上层。
            // 块属性不在空间实体表里，顺序代表是它所属的 Insert（搬属性会破坏块参照）。
            MaskService.PlaceMaskBefore(target.Space, target.OrderAnchor ?? target.Entity, mask);

            result.MarkChanged(target.Kind);
            context.RecordChange(target.Entity, "加遮罩（" + target.Kind + "）",
                string.Format("字高 {0:0.##}，边界 {1:P0}", target.TextHeight, runtime.PadRatio),
                string.Format("Wipeout {0:0.#}×{1:0.#}", target.Rect.Width, target.Rect.Height),
                true,
                (target.Note == null ? string.Empty : target.Note + "；")
                + "Wipeout 擦除到图形背景；实体集合顺序已调整（文字与遮罩在最上层"
                + (target.OrderAnchor == null ? "，该文字句柄已改变）" : "，文字本身未重建）"),
                target.SpaceName);

            path = MaskPath.EntityWipeout;
            return true;
        }

        /// <summary>
        /// 标注文字：写 **DIMTFILL=1**（图形背景色）的标注样式覆盖（DSTYLE 扩展数据）。
        ///
        /// 为什么不用 *D 缓存块：实测（2026-11-02，AutoCAD 2025）缓存块里塞 Wipeout 或
        /// 改缓存 MTEXT 的背景标志，标注一重生成（打开、REGENALL、编辑）就全部丢失——
        /// 那是宿主按样式重算的临时几何。DIMTFILL 是标注的**原生**文字背景：
        ///   * 1 = 图形背景色（与需求一致；2 才是 DIMTFILLCLR 指定色）；
        ///   * 存在实体自带的样式覆盖里，重生成不丢；
        ///   * 不新增实体、不动句柄、不碰 *D 块。
        ///
        /// 覆盖以"只改 DIMTFILL 一项"的方式追加，实体上已有的其它样式覆盖原样保留。
        /// </summary>
        private static bool ApplyDimensionMask(
            RuleContext context,
            RuleExecutionResult result,
            MaskRuntime runtime,
            MaskTarget target,
            string dimMaskMode,
            bool bringToFront,
            out MaskPath path)
        {
            path = MaskPath.None;

            var dimension = target.Entity as Dimension;

            if (string.Equals(dimMaskMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                result.MarkSkipped("标注遮罩未启用");
                context.RecordBlocked(dimension, "加遮罩（DIMENSION）",
                    "DimensionMaskMode=Off：标注文字不处理。改成 Native（默认）可给标注文字加背景填充。",
                    target.SpaceName);
                return false;
            }

            if (dimension == null)
            {
                result.MarkSkipped("不是标注实体");
                return false;
            }

            if (HasDrawingBackgroundFill(dimension))
            {
                result.MarkSkipped("标注文字已有背景遮罩");
                context.RecordSkip(dimension, "加遮罩（DIMENSION · DIMTFILL）",
                    "标注文字已有背景填充", false, target.SpaceName);
                return false;
            }

            // 追加而非清空：实体上可能已有用户自己的样式覆盖（DIMTXT、DIMTMOVE…），
            // 直接 SetStyleOverrideMap(新map) 会把它们全部抹掉。
            DxfClassMap map = dimension.GetStyleOverrideMap();
            if (map == null)
            {
                map = new DxfClassMap { Name = DimensionStyle.StyleOverrideEntryName };
            }

            DxfProperty fill = DxfClassMap.Create<DimensionStyle>().DxfProperties[69];
            fill.StoredValue = (short)DimensionTextBackgroundFillMode.DrawingBackgroundColor;
            map.DxfProperties[69] = fill;

            dimension.SetStyleOverrideMap(map);

            // DIMTFILL 的填充画在标注自身几何里；后画的线照样能压住标注文字。
            // 前置整个标注（线、箭头一起上去）是制图惯例，文字背景才不会被盖。
            if (bringToFront)
            {
                MaskService.BringToFront(target.Space, dimension);
            }

            result.MarkChanged("DIMENSION");
            context.RecordChange(dimension, "加遮罩（DIMENSION · DIMTFILL 覆盖）",
                "DIMTFILL=1",
                "DSTYLE 覆盖 + DIMTFILL=1（图形背景色）",
                false,
                (target.Note == null ? string.Empty : target.Note + "；")
                + "标注文字背景填充：不新增实体，标注重生成后依然生效"
                + (bringToFront ? "；标注已移到绘制顺序最上层（句柄已变）" : ""),
                target.SpaceName);
            path = MaskPath.DimensionOverride;
            return true;
        }

        /// <summary>该标注是否已经带 DIMTFILL=1（图形背景色）的样式覆盖。</summary>
        private static bool HasDrawingBackgroundFill(Dimension dimension)
        {
            if (!dimension.HasStyleOverride)
            {
                return false;
            }

            DxfClassMap map = dimension.GetStyleOverrideMap();
            if (map == null || !map.DxfProperties.TryGetValue(69, out DxfProperty fill))
            {
                return false;
            }

            try
            {
                return Convert.ToInt16(fill.StoredValue) == (short)DimensionTextBackgroundFillMode.DrawingBackgroundColor;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ 收集目标

        private static void CollectTargets(
            RuleContext context,
            RuleExecutionResult result,
            string dimMode,
            double padRatio,
            bool maskInsideBlocks,
            string maskTextBox,
            List<MaskTarget> targets)
        {
            // 块定义 → 引用它的块参照数量（预览里要说清"改这一处影响几个实例"）；用到才建
            Dictionary<BlockRecord, int> refCounts = null;

            RuleFilter filter = context.Filter;
            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                // 不写 EntityTypes 时就是"所有受支持的文字"。
                // ATTDEF 只覆盖常属性：非常属性的文字由块参照上的 ATTRIB 负责，
                // 两边都加会在同一位置叠两个遮罩（视觉无差，但实体数和处理时间翻倍）。
                var types = new List<string> { "TEXT", "MTEXT", "ATTDEF", "ATTRIB" };
                if (!string.Equals(dimMode, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    types.Add("DIMENSION");
                }

                filter.EntityTypes = types;
            }

            EntityFilter entityFilter = EntityFilter.Build(filter);

            foreach (ScanHit hit in EntityScanner.Scan(context, entityFilter))
            {
                Entity entity = hit.Entity;

                // Hit 必须在任何 MarkSkipped 之前：否则"跳过"会大于"匹配"，
                // 破坏 匹配 = 成功 + 跳过 + 失败
                result.Hit();

                if (hit.Depth > 0)
                {
                    // 块定义内的文字：遮罩要加进**块定义**，所有插入共享（改一次全生效）。
                    if (!maskInsideBlocks)
                    {
                        result.MarkSkipped("块内文字不处理（MaskInsideBlocks=false）");
                        continue;
                    }

                    if (hit.Owner != null && (DatabaseWalker.IsLayoutBlock(hit.Owner)
                                              || DatabaseWalker.IsExternalReference(hit.Owner)
                                              || hit.Owner.IsAnonymous))
                    {
                        // 布局块 = 顶层空间（depth 不会 >0，兜底）；外部参照不许改；*D/*T 由宿主重建
                        result.MarkSkipped("块定义不可写");
                        continue;
                    }

                    var attdef = entity as AttributeDefinition;
                    if (attdef != null && (attdef.Flags & AttributeFlags.Constant) == 0)
                    {
                        // 非常属性的 ATTDEF 不画东西，真正显示的是块参照上的 ATTRIB；
                        // 在定义里加遮罩会与插入级遮罩重叠，跳过。
                        result.MarkSkipped("非常属性定义由块参照属性处理");
                        continue;
                    }
                }

                if (EntityScanner.ShouldSkipLocked(context, entity))
                {
                    result.MarkSkipped("锁定图层");
                    continue;
                }

                var dimension = entity as Dimension;
                if (dimension != null)
                {
                    MaskTarget dimTarget = BuildDimensionTarget(dimension, hit.Owner, dimMode, padRatio,
                        EntityScanner.SpaceNameOf(hit));
                    if (dimTarget == null)
                    {
                        result.MarkSkipped("标注无可用文字位置");
                        continue;
                    }

                    if (hit.Depth > 0)
                    {
                        dimTarget.Note = DescribeBlockTarget(context, hit.Owner, ref refCounts);
                        dimTarget.InsideBlock = true;
                    }

                    targets.Add(dimTarget);
                    continue;
                }

                Bounds2d bounds;
                if (!TextBounds.TryGet(entity, out bounds) || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    result.MarkSkipped("无法估算文字矩形");
                    continue;
                }

                double height = CharacterHeightOf(entity);
                double pad = height * padRatio;

                // MTEXT 遮罩框模式：原生背景按**文字框宽度**（DXF 41）填充，
                // 框比文字宽时遮罩一侧空一大截。Auto 模式在框明显松时改走紧贴 Wipeout。
                bool nativeMTextMask = true;
                XY[] maskVertices = TextBounds.TextMaskVertices(entity, pad);
                Bounds2d maskRect = bounds.Inflate(pad);

                var mtextEntity = entity as MText;
                if (mtextEntity != null && !string.Equals(maskTextBox, "Native", StringComparison.OrdinalIgnoreCase))
                {
                    double tightWidth = TextBounds.MeasureMTextTightWidth(mtextEntity);
                    bool useTight = string.Equals(maskTextBox, "Tight", StringComparison.OrdinalIgnoreCase)
                                    || (tightWidth > 0 && mtextEntity.RectangleWidth > tightWidth * 1.05);

                    if (useTight)
                    {
                        nativeMTextMask = false;
                        maskVertices = TextBounds.TextMaskVertices(entity, pad, tightFrame: true);
                        maskRect = TextBounds.AabbOf(maskVertices);
                    }
                }

                if (maskVertices == null || !maskRect.IsValid || maskRect.Width <= 0 || maskRect.Height <= 0)
                {
                    result.MarkSkipped("无法估算文字矩形");
                    continue;
                }

                targets.Add(new MaskTarget
                {
                    Entity = entity,
                    Space = hit.Owner,
                    Rect = maskRect,
                    MaskVertices = maskVertices,
                    NativeMTextMask = nativeMTextMask,
                    TextHeight = height,
                    Kind = EntityFilter.DxfNameOf(entity),
                    SpaceName = EntityScanner.SpaceNameOf(hit),
                    Note = hit.Depth > 0 ? DescribeBlockTarget(context, hit.Owner, ref refCounts) : null,
                    InsideBlock = hit.Depth > 0
                });
            }

            // ---- 块属性：挂在块参照上，不在块定义里 ----
            if (!AcceptsAttributes(filter))
            {
                return;
            }

            var insertFilter = new RuleFilter { EntityTypes = new List<string> { "INSERT" } };
            foreach (ScanHit hit in EntityScanner.Scan(context, EntityFilter.Build(insertFilter)))
            {
                if (hit.Depth > 0)
                {
                    continue;
                }

                var insert = hit.Entity as Insert;
                if (insert == null || insert is TableEntity || insert.Attributes == null)
                {
                    continue;
                }

                foreach (AttributeEntity attribute in insert.Attributes)
                {
                    if (attribute == null || attribute.IsInvisible)
                    {
                        continue;
                    }

                    Bounds2d bounds;
                    if (!TextBounds.TryGet(attribute, out bounds) || bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        continue;
                    }

                    // 属性是单独收集的（不在 BlockRecord.Entities 里），Hit 要在这里补
                    result.Hit();

                    double height = CharacterHeightOf(attribute);
                    double pad = height * padRatio;

                    targets.Add(new MaskTarget
                    {
                        Entity = attribute,
                        Space = hit.Owner,
                        Rect = bounds.Inflate(pad),
                        MaskVertices = TextBounds.TextMaskVertices(attribute, pad),
                        TextHeight = height,
                        Kind = "ATTRIB",
                        // 顺序代表是 Insert：属性不在空间实体表里，搬它会破坏块参照
                        OrderAnchor = insert,
                        Note = "属性位置按 WCS 读取；遮罩排在所属块参照之前",
                        SpaceName = EntityScanner.SpaceNameOf(hit)
                    });
                }
            }
        }

        /// <summary>配置是否包含块属性（默认的"所有文字"包含）。</summary>
        private static bool AcceptsAttributes(RuleFilter filter)
        {
            if (filter == null || filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                return true;
            }

            return filter.EntityTypes.Any(t => EntityFilter.MatchesType(new AttributeEntity(), t)
                                               || string.Equals((t ?? string.Empty).Trim(), "ATTRIB", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 块内文字的说明文本。
        ///
        /// 必须写清"这一处改动会影响几个块参照"：块定义是共享的，改一次全部实例都变。
        /// 用户看到"改了 1 处"却整张图都变了，比不改更糟。
        /// </summary>
        private static string DescribeBlockTarget(
            RuleContext context, BlockRecord block, ref Dictionary<BlockRecord, int> cache)
        {
            if (block == null)
            {
                return null;
            }

            if (cache == null)
            {
                cache = BuildReferenceCounts(context.Document);
            }

            int count;
            cache.TryGetValue(block, out count);

            return string.Format("遮罩写进块定义「{0}」，直接引用 {1} 处", block.Name, count);
        }

        /// <summary>统计每个块定义被多少个块参照直接引用（一次遍历）。</summary>
        private static Dictionary<BlockRecord, int> BuildReferenceCounts(CadDocument doc)
        {
            var map = new Dictionary<BlockRecord, int>();
            if (doc == null)
            {
                return map;
            }

            foreach (BlockRecord owner in doc.BlockRecords)
            {
                if (owner == null)
                {
                    continue;
                }

                foreach (Entity child in owner.Entities)
                {
                    var insert = child as Insert;
                    if (insert == null || insert.Block == null)
                    {
                        continue;
                    }

                    int count;
                    map.TryGetValue(insert.Block, out count);
                    map[insert.Block] = count + 1;
                }
            }

            return map;
        }

        private static MaskTarget BuildDimensionTarget(
            Dimension dimension, BlockRecord space, string dimMode, double padRatio, string spaceName)
        {
            if (string.Equals(dimMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            bool hasExplicitText = !string.IsNullOrEmpty(dimension.Text) && dimension.Text != "<>";
            if (!hasExplicitText && string.Equals(dimMode, "ExplicitOnly", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // 标注遮罩走 DIMTFILL 覆盖，不需要几何。矩形只用来找"压住标注文字的填充"
            //（填充置后），有 *D 缓存文字就算一下，没有就放弃——不影响遮罩本身。
            Bounds2d rect = default(Bounds2d);
            Entity cachedText = FindDimensionText(dimension);
            if (cachedText != null && TextBounds.TryGet(cachedText, out Bounds2d box)
                && box.Width > 0 && box.Height > 0)
            {
                rect = box;
            }

            return new MaskTarget
            {
                Entity = dimension,
                Space = space,
                Rect = rect,
                CachedText = cachedText,
                Kind = "DIMENSION",
                Note = "标注文字背景填充",
                SpaceName = spaceName
            };
        }

        /// <summary>*D 缓存块里的文字实体（优先 MTEXT）——遮罩要排在它之前。</summary>
        private static Entity FindDimensionText(Dimension dimension)
        {
            if (dimension == null || dimension.Block == null)
            {
                return null;
            }

            Entity textEntity = null;
            foreach (Entity child in dimension.Block.Entities)
            {
                if (child is MText)
                {
                    return child;
                }

                if (textEntity == null && child is TextEntity && !(child is AttributeBase))
                {
                    textEntity = child;
                }
            }

            return textEntity;
        }

        /// <summary>文字的字高（不是包围盒高度）。多行 MTEXT 的盒高是行数倍，留白必须按字高算。</summary>
        private static double CharacterHeightOf(Entity entity)
        {
            var mtext = entity as MText;
            if (mtext != null)
            {
                return mtext.Height > 0 ? mtext.Height : 2.5;
            }

            var text = entity as TextEntity;
            if (text != null)
            {
                return text.Height > 0 ? text.Height : 2.5;
            }

            return 2.5;
        }

        // ------------------------------------------------------------------ 遮挡判定

        private static BoundsIndex BuildOccluders(
            BlockRecord space, List<MaskTarget> targets, bool useBlockBounds, string maskLayerName)
        {
            var index = new BoundsIndex(EstimateCellSize(space));
            var skip = new HashSet<Entity>();
            foreach (MaskTarget target in targets)
            {
                skip.Add(target.Entity);
            }

            int i = 0;
            foreach (Entity entity in space.Entities)
            {
                if (entity == null || skip.Contains(entity))
                {
                    continue;
                }

                string layerName = entity.Layer == null ? string.Empty : entity.Layer.Name;

                // 定义点、遮罩层自身、已有遮罩都不参与判定
                if (string.Equals(layerName, "Defpoints", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(layerName, maskLayerName, StringComparison.OrdinalIgnoreCase)
                    || entity is Wipeout)
                {
                    continue;
                }

                // 块参照包络默认不参与（包络远大于实际图形）
                if (!useBlockBounds && entity is Insert)
                {
                    continue;
                }

                Bounds2d bounds;
                if (!TextBounds.TryGet(entity, out bounds))
                {
                    continue;
                }

                index.Add(i++, bounds, entity);
            }

            return index;
        }

        /// <summary>
        /// 把"盖住被遮罩文字的填充"送到绘制顺序最底层（MaskSendHatchToBack，默认开）。
        ///
        /// **注意（2026-11-02 实测）：这条路径实际上已失效**——它通过 SORTENTS 排序表实现，
        /// 而 ACadSharp 写出的块记录扩展字典 AutoCAD 读不到，排序表根本不落地（见
        /// MaskService.BringToFront 的注释）。文字遮罩的绘制顺序现在靠
        /// MaskBringTextToFront（把文字+遮罩推到集合末尾 = 最上层）保证，
        /// 填充压不压文字已无关紧要。保留本开关只为兼容旧配置，不再依赖。
        ///
        /// **命名是反的**：这个库的 <c>MoveToTop</c> 才等于 AutoCAD 的"置后"。
        /// </summary>
        private static int PushHatchesToBack(
            RuleContext context, RuleExecutionResult result, BlockRecord space, List<Hatch> hatches)
        {
            SortEntitiesTable table = space.SortEntitiesTable;
            if (table == null)
            {
                context.Log.Warn("该空间没有排序表（SORTENTSTABLE），无法调整绘制顺序；遮罩可能被填充盖住。");
                return 0;
            }

            int moved = 0;
            foreach (Hatch hatch in hatches)
            {
                table.MoveToTop(hatch);
                moved++;
            }

            context.Log.Detail(string.Format("已把 {0} 个遮挡文字的填充送到最底层（绘制顺序）", moved));
            return moved;
        }

        /// <summary>
        /// 遮挡占比。
        ///
        /// 分两类算，因为"包围盒相交"对细长图形是错的：
        ///   * **线状**（LINE / CIRCLE 环）：只看它是否真的穿过文字矩形，穿过一次记 0.25；
        ///   * **面状**（HATCH / SOLID / 其它）：按包围盒相交面积占比累加。
        ///
        /// 结果封顶 1.0。阈值默认 0.05，即"一条线穿过"或"5% 面积被盖"就加遮罩。
        /// 默认配置下（MaskOnlyWhenOccluded=false）本函数不会被调用。
        /// </summary>
        private static double ComputeOcclusion(Bounds2d rect, BoundsIndex index)
        {
            if (rect.Area <= 0 || index == null)
            {
                return 0;
            }

            double covered = 0;
            int crossings = 0;

            foreach (int i in index.Query(rect))
            {
                Entity entity = index.EntityOf(i);

                var line = entity as Line;
                if (line != null)
                {
                    if (Geo2d.SegmentIntersectsBox(
                            new XY(line.StartPoint.X, line.StartPoint.Y),
                            new XY(line.EndPoint.X, line.EndPoint.Y), rect))
                    {
                        crossings++;
                    }

                    continue;
                }

                var circle = entity as Circle;
                if (circle != null && !(entity is Arc))
                {
                    // 环线是否穿过矩形：圆心到矩形中心的距离与半径之差在半对角线内
                    double dx = circle.Center.X - rect.CenterX;
                    double dy = circle.Center.Y - rect.CenterY;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    double halfDiagonal = Math.Sqrt(rect.Width * rect.Width + rect.Height * rect.Height) * 0.5;

                    if (Math.Abs(distance - circle.Radius) <= halfDiagonal)
                    {
                        crossings++;
                    }

                    continue;
                }

                covered += rect.IntersectionArea(index.BoundsOf(i));
            }

            return Math.Min(1.0, covered / rect.Area + crossings * 0.25);
        }

        private static double EstimateCellSize(BlockRecord space)
        {
            var all = default(Bounds2d);
            bool any = false;

            foreach (Entity entity in space.Entities)
            {
                Bounds2d bounds;
                if (!TextBounds.TryGetFromLibrary(entity, out bounds))
                {
                    continue;
                }

                all = any ? all.Union(bounds) : bounds;
                any = true;
            }

            if (!any)
            {
                return 1000;
            }

            double size = Math.Max(all.Width, all.Height) / 128.0;
            return size <= 0 ? 1000 : Math.Max(size, 1);
        }

        // ------------------------------------------------------------------ 解析

        private static double NormalizePaddingRatio(double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            {
                return MaskService.DefaultPaddingRatio;
            }

            if (ratio < 0)
            {
                return 0;
            }

            return ratio > 2 ? 2 : ratio;
        }

        /// <summary>
        /// 遮罩模式：只接受 Auto / Wipeout，两者都表示"原生遮罩"。
        /// Solid 与未知值直接报错——静默换成别的模式比报错糟得多。
        /// </summary>
        private static void RequireNativeMaskMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string mode = text.Trim();
            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Wipeout", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new RuleValidationException(
                "MaskMode=\"" + mode + "\" 不受支持：本规则只有原生遮罩一条路（MTEXT 背景填充 + Wipeout 擦除），"
                + "没有 2D Solid 兜底。把 MaskMode 改成 Auto 或删掉这一项。");
        }

        private static string NormalizeDimensionMaskMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Native";
            }

            // "BlockInternal" 是旧值：*D 缓存块方案已退役，读到它按 Native 处理，
            // 让老配置继续可用（在日志里说明一次比报错友好）。
            return string.Equals(text.Trim(), "Off", StringComparison.OrdinalIgnoreCase)
                ? "Off"
                : "Native";
        }

        /// <summary>
        /// 遮罩框模式：Auto（默认）/ Native / Tight，未知值硬报错——
        /// 静默换成别的模式会让"为什么遮罩变宽/变紧"变得无法解释。
        /// </summary>
        private static string NormalizeMaskTextBox(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Auto";
            }

            string mode = text.Trim();
            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Native", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Tight", StringComparison.OrdinalIgnoreCase))
            {
                return mode;
            }

            throw new RuleValidationException(
                "MaskTextBox=\"" + mode + "\" 不受支持：可选 Auto（默认，框宽贴文字才用原生背景）、"
                + "Native（始终原生背景，按文字框宽度填充）、Tight（始终紧贴文字的 Wipeout）。");
        }

        private static string NormalizeDimensionMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "All";
            }

            if (string.Equals(text.Trim(), "Off", StringComparison.OrdinalIgnoreCase))
            {
                return "Off";
            }

            return string.Equals(text.Trim(), "ExplicitOnly", StringComparison.OrdinalIgnoreCase) ? "ExplicitOnly" : "All";
        }

        /// <summary>遮罩实际走的路径（汇总统计用，必须反映真实执行分支而不是配置开关）。</summary>
        private enum MaskPath
        {
            None,
            MTextNative,
            DimensionOverride,
            EntityWipeout
        }

        /// <summary>
        /// 一次执行内的遮罩工厂：图层、WIPEOUTFRAME、编码自检都按需做一次。
        ///
        /// 懒加载的理由：只处理 MTEXT 的图不需要 _MASK 图层，也不该被写一条
        /// `ACAD_WIPEOUT_VARS`——那会凭空给用户图纸加设置。
        /// </summary>
        private sealed class MaskRuntime
        {
            private readonly RuleContext _context;
            private readonly RuleAction _action;
            private readonly string _layerName;

            private Layer _layer;
            private bool _frameHidden;
            private bool _encodingChecked;
            private readonly Dictionary<BlockRecord, List<Bounds2d>> _wipeoutBoxes = new Dictionary<BlockRecord, List<Bounds2d>>();

            public MaskRuntime(RuleContext context, RuleAction action, string layerName, double padRatio)
            {
                _context = context;
                _action = action;
                _layerName = layerName;
                PadRatio = padRatio;
            }

            public double PadRatio { get; private set; }

            public Wipeout CreateWipeout(MaskTarget target)
            {
                if (!_frameHidden)
                {
                    // 边框默认"显示并打印"（WIPEOUTFRAME=1），不关掉的话每个遮罩
                    // 出图时都会多一个矩形框。建第一个遮罩之前改一次，整张图生效。
                    MaskService.HideWipeoutFrame(_context.Document);
                    _frameHidden = true;
                }

                Wipeout wipeout = target.MaskVertices == null
                    ? MaskService.CreateWipeout(target.Rect)
                    : MaskService.CreateWipeout(target.MaskVertices);

                if (!_encodingChecked)
                {
                    string reason;
                    if (!MaskService.IsWipeoutEncoded(wipeout, out reason))
                    {
                        // 没有 2D Solid 兜底：写不出来就停，让用户看到原因。
                        throw new RuleValidationException("Wipeout 编码不可用：" + reason + "。本规则没有 Solid 兜底。");
                    }

                    _encodingChecked = true;
                }

                wipeout.Layer = MaskLayer();
                RegisterWipeout(target.Space, target.Rect);
                return wipeout;
            }

            /// <summary>
            /// 同位置是否已经有一个几何一致的 Wipeout。
            ///
            /// 幂等保护：对同一输出文件重复运行时，TEXT/紧贴 MTEXT 的 Wipeout 会在
            /// 完全相同的位置再叠一个——渲染结果不变但实体数翻倍。按"包围盒中心与
            /// 尺寸都在容差内"判重，宁可漏加（视觉上已有遮罩）也不重复叠加。
            /// 代价：两段完全重叠的相同文字只处理第一个，极罕见，可接受。
            /// </summary>
            public bool HasOverlappingWipeout(BlockRecord space, Bounds2d box)
            {
                if (space == null || !box.IsValid || box.Area <= 0)
                {
                    return false;
                }

                List<Bounds2d> boxes;
                if (!_wipeoutBoxes.TryGetValue(space, out boxes))
                {
                    boxes = new List<Bounds2d>();
                    foreach (Entity entity in space.Entities)
                    {
                        if (!(entity is Wipeout))
                        {
                            continue;
                        }

                        Bounds2d existing = TextBounds.ForWipeout((Wipeout)entity);
                        if (existing.IsValid && existing.Area > 0)
                        {
                            boxes.Add(existing);
                        }
                    }

                    _wipeoutBoxes[space] = boxes;
                }

                foreach (Bounds2d existing in boxes)
                {
                    if (SameBox(existing, box))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void RegisterWipeout(BlockRecord space, Bounds2d box)
            {
                List<Bounds2d> boxes;
                if (!_wipeoutBoxes.TryGetValue(space, out boxes))
                {
                    boxes = new List<Bounds2d>();
                    _wipeoutBoxes[space] = boxes;
                }

                boxes.Add(box);
            }

            private static bool SameBox(Bounds2d a, Bounds2d b)
            {
                double tolerance = Math.Max(1e-4, Math.Max(a.Width, a.Height) * 0.01);
                return Math.Abs(a.CenterX - b.CenterX) <= tolerance
                       && Math.Abs(a.CenterY - b.CenterY) <= tolerance
                       && Math.Abs(a.Width - b.Width) <= tolerance
                       && Math.Abs(a.Height - b.Height) <= tolerance;
            }

            private Layer MaskLayer()
            {
                if (_layer != null)
                {
                    return _layer;
                }

                _layer = LayerService.EnsureLayer(_context.Document, _layerName, _action, _context.Log, _context.Resources);
                if (_layer == null)
                {
                    throw new RuleValidationException("遮罩图层「" + _layerName + "」不存在且无法创建。");
                }

                // Wipeout 依靠图层参与出图；不可打印的图层上等于没有遮罩。
                if (!RuleContext.Bool(_action.MaskLayerPlottable, true))
                {
                    _layer.PlotFlag = false;
                    _context.Log.Warn("MaskLayerPlottable=false：该图层上的 Wipeout 遮罩不会出现在打印结果里。");
                }

                return _layer;
            }
        }

        private sealed class MaskTarget
        {
            public Entity Entity;
            public BlockRecord Space;
            public Bounds2d Rect;
            public XY[] MaskVertices;

            /// <summary>
            /// MTEXT 是否走原生背景遮罩（false = 紧贴 Wipeout）。
            /// 判定依据 MaskTextBox：Auto 模式在文字框 (41) 明显比实测文字宽时为 false。
            /// 非 MTEXT 目标此值无意义（恒 true）。
            /// </summary>
            public bool NativeMTextMask;

            /// <summary>文字的字高（留白按它算，不是包围盒高度）。</summary>
            public double TextHeight;

            /// <summary>
            /// 在实体流里代表"文字位置"的实体：普通文字就是自己（null），
            /// 块属性则是它所属的 Insert。
            /// </summary>
            public Entity OrderAnchor;

            /// <summary>标注 *D 块里的文字实体（有就用它的几何，比估算可靠）。</summary>
            public Entity CachedText;

            public string Kind;
            public string Note;
            public string SpaceName;

            /// <summary>目标在块定义内部（遮挡判定跳过，见 Execute 里的说明）。</summary>
            public bool InsideBlock;
        }

        /// <summary>包围盒网格索引：避免 O(n²) 的遮挡判定（1 万实体的图上是必须的）。</summary>
        private sealed class BoundsIndex
        {
            private readonly double _cell;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly List<Bounds2d> _bounds = new List<Bounds2d>();
            private readonly List<Entity> _entities = new List<Entity>();

            public BoundsIndex(double cell)
            {
                _cell = cell <= 0 ? 1000 : cell;
            }

            public Bounds2d BoundsOf(int index)
            {
                return _bounds[index];
            }

            public Entity EntityOf(int index)
            {
                return _entities[index];
            }

            public void Add(int index, Bounds2d bounds, Entity entity)
            {
                _bounds.Add(bounds);
                _entities.Add(entity);

                int minX = (int)Math.Floor(bounds.MinX / _cell);
                int maxX = (int)Math.Floor(bounds.MaxX / _cell);
                int minY = (int)Math.Floor(bounds.MinY / _cell);
                int maxY = (int)Math.Floor(bounds.MaxY / _cell);

                if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 4096)
                {
                    maxX = minX;
                    maxY = minY;
                }

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        long key = ((long)x << 32) ^ (uint)y;
                        List<int> list;
                        if (!_cells.TryGetValue(key, out list))
                        {
                            list = new List<int>();
                            _cells[key] = list;
                        }

                        list.Add(index);
                    }
                }
            }

            public IEnumerable<int> Query(Bounds2d rect)
            {
                var seen = new HashSet<int>();
                int minX = (int)Math.Floor(rect.MinX / _cell);
                int maxX = (int)Math.Floor(rect.MaxX / _cell);
                int minY = (int)Math.Floor(rect.MinY / _cell);
                int maxY = (int)Math.Floor(rect.MaxY / _cell);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        long key = ((long)x << 32) ^ (uint)y;
                        List<int> list;
                        if (!_cells.TryGetValue(key, out list))
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            if (seen.Add(i))
                            {
                                yield return i;
                            }
                        }
                    }
                }
            }
        }
    }
}
