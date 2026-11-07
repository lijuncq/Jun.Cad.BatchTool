using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;
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
            // C02：填充置后已停用。原实现只调 SortEntitiesTable.MoveToTop 并计数，
            // 而 ACadSharp 写出的块记录扩展字典 AutoCAD 读不回，排序表根本不落盘——
            // 文件里没有任何变化，汇总却报「填充置后 N」。默认关掉，显式开也只告警。
            bool sendHatchToBack = RuleContext.Bool(action.MaskSendHatchToBack, false);
            if (sendHatchToBack)
            {
                context.Log.Warn("MaskSendHatchToBack=true 已停用：SORTENTS 排序表不落盘，该开关不产生任何文件变更；绘制顺序由 MaskBringTextToFront 负责。");
            }

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

            // C04：按**实际**重排数汇总。Wipeout 路径为了保证遮罩排在文字之前，
            // 即使 MaskBringTextToFront=false 也会重排，旧汇总按配置开关撒了谎。
            int reordered = 0;

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

            // C05：单图上限要跨次执行生效——把执行前图纸里已有的遮罩计入预算，
            // 否则同一输出文件每跑一次就再加一批，直到全部文字都加上。
            int existingMasks = maxMasks > 0 ? CountExistingMasks(bySpace.Keys, maskLayerName) : 0;

            foreach (KeyValuePair<BlockRecord, List<MaskTarget>> pair in bySpace)
            {
                BlockRecord space = pair.Key;
                List<MaskTarget> list = pair.Value;

                // 只有判遮挡时才需要整空间索引。填充置后已停用（C02），
                // 不该再为一个不产生任何文件变更的开关每次建一遍 BoundsIndex。
                BoundsIndex occluders = onlyOccluded
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

                    if (maxMasks > 0 && created + existingMasks >= maxMasks)
                    {
                        result.MarkSkipped("超过单图遮罩上限");
                        context.RecordBlocked(target.Entity, "加遮罩",
                            string.Format("已达单图上限 {0}（含本次执行前已有的 {1} 处遮罩），后续文字不再加遮罩",
                                maxMasks, existingMasks), target.SpaceName);
                        continue;
                    }

                    MaskPath path;
                    if (ApplyMask(context, result, runtime, target, rebuildText, dimMaskMode, maskTextBox,
                            bringToFront, ref reordered, out path))
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
                    }
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

                if (reordered > 0)
                {
                    // 汇总必须反映真实重排数：Wipeout / 紧贴路径不受 MaskBringTextToFront 控制，
                    // 但同样改了实体集合顺序、同样换了句柄。
                    parts.Add("重排到最上层 " + reordered + "（含 Wipeout 路径，句柄已变）");
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
            ref int reordered,
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
                    reordered++;
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
                // 紧贴遮罩：AutoCAD 的原生背景按**文字框**填充——宽度吃 DXF 41，
                // 高度吃 DXF 46 行框（实测 1.85 × 字高）× BackgroundScale，合计约 2.13 字高。
                // 框比文字宽时遮罩一侧空出一大截（受控出图实测：框 60/文字 6，断口正好 60），
                // 框不高时也会上下各多出半行——表格里就是它截断相邻行线的。
                // 改用我们自己算的紧贴 Wipeout——不动 41/46，零折行风险。
                if (runtime.HasOverlappingWipeout(target.Space, target.Rect, target.Rotation))
                {
                    result.MarkSkipped("已有紧贴遮罩");
                    context.RecordSkip(mtext, "加遮罩（MTEXT 紧贴）", "同位置已有 Wipeout 遮罩", false, target.SpaceName);
                    return false;
                }

                // 用户自己设的**指定色**背景遮罩（bit0 开、bit1 关）是刻意的选择，
                // 换成 Wipeout 会丢掉颜色，这种一律不动。
                if ((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0
                    && (mtext.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) == 0)
                {
                    result.MarkSkipped("已有自定义色遮罩");
                    context.RecordSkip(mtext, "加遮罩（MTEXT 紧贴）",
                        "文字自带指定色的背景遮罩，保留用户设置（换成 Wipeout 会丢颜色）", false, target.SpaceName);
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
                // 这条路径为遮罩顺序所必需，不受 MaskBringTextToFront 控制（C04）。
                MaskService.PlaceMaskBefore(target.Space, mtext, tightMask);
                reordered++;

                result.MarkChanged("MTEXT");
                context.RecordChange(mtext, "加遮罩（MTEXT 紧贴）",
                    string.Format("字高 {0:0.##}，边界 {1:P0}", target.TextHeight, runtime.PadRatio),
                    string.Format("Wipeout 紧贴实测文字 {0:0.#}×{1:0.#}", target.Rect.Width, target.Rect.Height),
                    true,
                    (target.Note == null ? string.Empty : target.Note + "；")
                    + "原生背景填充按文字框（DXF 41 框宽 / DXF 46 行框高）画，行框实测 1.85 字高、"
                    + "乘留白后约 2.13 字高，会截断上下相邻的行线；已换成紧贴字形的 Wipeout"
                    + "（RectangleWidth / RectangleHeight 均未动，不会折行；文字实体句柄已变）",
                    target.SpaceName);
                path = MaskPath.EntityWipeout;
                return true;
            }

            if (target.Kind == "DIMENSION")
            {
                return ApplyDimensionMask(context, result, runtime, target, dimMaskMode, bringToFront, ref reordered, out path);
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
            if (runtime.HasOverlappingWipeout(target.Space, target.Rect, target.Rotation))
            {
                result.MarkSkipped("已有遮罩");
                context.RecordSkip(target.Entity, "加遮罩（" + target.Kind + "）", "同位置已有 Wipeout 遮罩", false, target.SpaceName);
                return false;
            }

            Wipeout mask = runtime.CreateWipeout(target);

            // 移出文字 → 追加遮罩 → 再把文字加回去：顺序变成 [遮罩, 文字] 且都在最上层。
            // 块属性不在空间实体表里，顺序代表是它所属的 Insert（搬属性会破坏块参照）。
            // 这条路径同样不受 MaskBringTextToFront 控制（C04）。
            MaskService.PlaceMaskBefore(target.Space, target.OrderAnchor ?? target.Entity, mask);
            reordered++;

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
        ///   * 不新增实体、不碰 *D 块；
        ///   * 默认配置下 MaskBringTextToFront=true 会把整个标注前置，标注获得新句柄。
        ///
        /// 覆盖以"只追加 DIMTFILL 一项"的方式写进 ACAD XData 的 DSTYLE 段，
        /// 段内其它覆盖与**段外所有记录**原样保留（不能调 SetStyleOverrideMap：
        /// 它先 edata.Records.Clear()，会把用户的 ACAD XData 全部抹掉）。
        /// </summary>
        private static bool ApplyDimensionMask(
            RuleContext context,
            RuleExecutionResult result,
            MaskRuntime runtime,
            MaskTarget target,
            string dimMaskMode,
            bool bringToFront,
            ref int reordered,
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

            if (HasDimensionFillOverride(dimension))
            {
                result.MarkSkipped("标注已有 DIMTFILL 覆盖");
                context.RecordSkip(dimension, "加遮罩（DIMENSION · DIMTFILL）",
                    "标注已有 DIMTFILL 覆盖，保留用户设置（含 0=无填充 / 2=指定色）", false, target.SpaceName);
                return false;
            }

            ApplyDimensionFillOverride(dimension);

            // DIMTFILL 的填充画在标注自身几何里；后画的线照样能压住标注文字。
            // 前置整个标注（线、箭头一起上去）是制图惯例，文字背景才不会被盖。
            if (bringToFront)
            {
                MaskService.BringToFront(target.Space, dimension);
                reordered++;
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

        /// <summary>该标注是否已有 DIMTFILL（组码 69）样式覆盖。只看键是否存在，不看取值：
        /// 用户显式设的 0（无填充）/2（指定色）是有效配置，不能被本规则静默改成 1。</summary>
        private static bool HasDimensionFillOverride(Dimension dimension)
        {
            int open;
            int close;
            ExtendedData edata = AcadaXDataOf(dimension);
            if (!FindDStyleSection(edata, out open, out close))
            {
                return false;
            }

            for (int i = open + 1; i < close; i++)
            {
                var code = edata.Records[i] as ExtendedDataInteger16;
                if (code != null && code.Value == 69)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 只往 ACAD XData 的 DSTYLE 段里补 `1070 . 69 / 1070 . 1`，段内其它覆盖与
        /// **段外所有记录**一条都不删。
        ///
        /// 绝不调用 <c>SetStyleOverrideMap</c>：库里第一句就是
        /// <c>edata.Records.Clear()</c>，会把用户挂在 ACAD 下的 XData 整段抹掉
        /// （C03：本功能唯一确认的用户数据丢失路径）。
        /// </summary>
        private static void ApplyDimensionFillOverride(Dimension dimension)
        {
            ExtendedData edata = AcadaXDataOf(dimension);
            if (edata == null)
            {
                edata = dimension.ExtendedData.TryAdd(AppId.DefaultName, new ExtendedData());
            }

            List<ExtendedDataRecord> records = edata.Records;
            int open;
            int close;
            if (!FindDStyleSection(edata, out open, out close))
            {
                // 没有 DSTYLE 段：插在 ACAD 记录的**开头**，而不是末尾。
                // 库与宿主都按「ACAD 段首条是 DSTYLE 头」识别覆盖；追加到末尾会让
                // HasStyleOverride / GetStyleOverrideMap 直接返回 false，等于没写。
                records.InsertRange(0, new ExtendedDataRecord[]
                {
                    new ExtendedDataString(DimensionStyle.StyleOverrideEntryName),
                    new ExtendedDataControlString(false),
                    new ExtendedDataInteger16(69),
                    new ExtendedDataInteger16((short)DimensionTextBackgroundFillMode.DrawingBackgroundColor),
                    new ExtendedDataControlString(true)
                });
                return;
            }

            for (int i = open + 1; i < close; i++)
            {
                var code = records[i] as ExtendedDataInteger16;
                if (code != null && code.Value == 69)
                {
                    return;   // 已有 69（不论取值）→ 保留用户设置
                }
            }

            short fill = (short)DimensionTextBackgroundFillMode.DrawingBackgroundColor;
            records.Insert(close, new ExtendedDataInteger16(fill));
            records.Insert(close, new ExtendedDataInteger16(69));
        }

        /// <summary>取标注挂在 ACAD AppId 下的 XData；没有则返回 null（不改对象）。</summary>
        private static ExtendedData AcadaXDataOf(Dimension dimension)
        {
            if (dimension == null)
            {
                return null;
            }

            foreach (KeyValuePair<AppId, ExtendedData> entry in dimension.ExtendedData)
            {
                if (entry.Key != null
                    && string.Equals(entry.Key.Name, AppId.DefaultName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// 在 ACAD XData 里定位 DSTYLE 段的 `{` / `}` 控制串下标（支持嵌套）。
        /// 返回 false 表示没有可用段，调用方需要自己新建一段。
        /// </summary>
        private static bool FindDStyleSection(ExtendedData edata, out int open, out int close)
        {
            open = -1;
            close = -1;
            if (edata == null)
            {
                return false;
            }

            List<ExtendedDataRecord> records = edata.Records;
            int header = -1;
            for (int i = 0; i < records.Count; i++)
            {
                var text = records[i] as ExtendedDataString;
                if (text != null
                    && string.Equals(text.Value, DimensionStyle.StyleOverrideEntryName, StringComparison.Ordinal))
                {
                    header = i;
                    break;
                }
            }

            if (header < 0)
            {
                return false;
            }

            int depth = 0;
            for (int i = header + 1; i < records.Count; i++)
            {
                var control = records[i] as ExtendedDataControlString;
                if (control == null)
                {
                    continue;
                }

                if (!control.IsClosing)
                {
                    if (open < 0)
                    {
                        open = i;
                    }

                    depth++;
                }
                else
                {
                    depth--;
                    if (depth <= 0 && open >= 0)
                    {
                        close = i;
                        return true;
                    }
                }
            }

            return false;
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
                                              || (hit.Owner.IsAnonymous && !DatabaseWalker.IsDynamicInstanceBlock(hit.Owner))))
                    {
                        // 布局块 = 顶层空间（depth 不会 >0，兜底）；外部参照不许改；
                        // *D（标注缓存）/*T（表）由宿主重建，改了也会被覆盖。
                        // *U（动态块匿名实例）例外：它含真实显示的文字，允许加遮罩。
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

                // C15：空文字不该有遮罩。旧实现只看几何，空 MTEXT + 非零 DXF 41
                // 会得到 Width>0 && Height>0，凭空多一个 Wipeout。
                if (IsBlankText(entity))
                {
                    result.MarkSkipped("空文字");
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

                // MTEXT 遮罩框模式：
                //   * 原生背景按**文字框**填充——宽度吃 DXF 41、高度吃 DXF 46 行框
                //     （实测 1.85 × 字高），再乘 BackgroundScale 1.15 → 遮罩高 2.13 × 字高。
                //     字形本身只有 1 个字高，多出来的部分会压掉上下相邻的行线，
                //     这就是"遮罩过大、截断不该截断的线条"的根因，靠调小留白治不了。
                //   * 因此 Auto 对**单行 MTEXT** 一律改走紧贴 Wipeout（字形带 + 留白）；
                //     多行/折行仍走原生——真实行数由宿主排版决定，我们算不出来，
                //     按源码行数造框会让末行露到遮罩外（漏字比遮罩大得多严重）。
                bool nativeMTextMask = true;
                XY[] maskVertices = TextBounds.TextMaskVertices(entity, pad);
                Bounds2d maskRect = bounds.Inflate(pad);

                var mtextEntity = entity as MText;
                if (mtextEntity != null && !string.Equals(maskTextBox, "Native", StringComparison.OrdinalIgnoreCase))
                {
                    bool useTight = string.Equals(maskTextBox, "Tight", StringComparison.OrdinalIgnoreCase)
                                    || TextBounds.IsSingleLineMText(mtextEntity);

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

                // C16：判重键要带旋转角——同 AABB 的 0° 与 90° 文字不是同一个遮罩。
                double rotation = 0;
                if (maskVertices.Length >= 2)
                {
                    rotation = Math.Atan2(maskVertices[1].Y - maskVertices[0].Y,
                                          maskVertices[1].X - maskVertices[0].X);
                }

                targets.Add(new MaskTarget
                {
                    Entity = entity,
                    Space = hit.Owner,
                    Rect = maskRect,
                    MaskVertices = maskVertices,
                    Rotation = rotation,
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
                // C07：ATTRIB 只挂在 Insert 上，不会出现在 BlockRecord.Entities 里。
                // 这里曾经用 `hit.Depth > 0` 一刀切跳过，导致嵌套块参照上的属性
                // 全部漏遮。只跳过外参与 *D/*T 匿名块（与上面的块内文字同口径）。
                if (hit.Owner != null && (DatabaseWalker.IsExternalReference(hit.Owner)
                                          || (hit.Owner.IsAnonymous && !DatabaseWalker.IsDynamicInstanceBlock(hit.Owner))))
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
                    if (attribute == null || attribute.IsInvisible || IsBlankText(attribute))
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
                    XY[] vertices = TextBounds.TextMaskVertices(attribute, pad);
                    double rotation = vertices != null && vertices.Length >= 2
                        ? Math.Atan2(vertices[1].Y - vertices[0].Y, vertices[1].X - vertices[0].X)
                        : 0;

                    targets.Add(new MaskTarget
                    {
                        Entity = attribute,
                        Space = hit.Owner,
                        Rect = bounds.Inflate(pad),
                        MaskVertices = vertices,
                        Rotation = rotation,
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

        /// <summary>
        /// 文字是否为空。MTEXT 的格式码（\f…;、\A1;、{}…）与换行 \P 不算可见内容，
        /// 所以先解码成纯文本再判空白。取不到值时不擅自跳过，交给几何判定处理。
        /// </summary>
        private static bool IsBlankText(Entity entity)
        {
            string value = null;
            var mtext = entity as MText;
            if (mtext != null)
            {
                value = mtext.Value;
            }
            else
            {
                var text = entity as TextEntity;
                if (text != null)
                {
                    value = text.Value;
                }
            }

            if (value == null)
            {
                return false;
            }

            string plain = MTextTokenizer.ToPlainText(value);
            return string.IsNullOrWhiteSpace(plain.Replace("\n", string.Empty)
                                                  .Replace("\r", string.Empty)
                                                  .Replace("{}", string.Empty));
        }

        /// <summary>
        /// 统计本次执行**之前**图纸里已经存在的遮罩，让 MaxMasksPerDrawing 成为真正的
        /// "单图上限"而不是"单次执行新增上限"。只统计本规则自己会产出的三类遮罩。
        /// </summary>
        private static int CountExistingMasks(IEnumerable<BlockRecord> spaces, string maskLayerName)
        {
            int count = 0;
            foreach (BlockRecord space in spaces)
            {
                if (space == null)
                {
                    continue;
                }

                foreach (Entity entity in space.Entities)
                {
                    if (entity == null)
                    {
                        continue;
                    }

                    var wipeout = entity as Wipeout;
                    if (wipeout != null)
                    {
                        // 只算遮罩图层上的 Wipeout，别把用户自己的图像裁剪算进预算。
                        string layer = wipeout.Layer == null ? string.Empty : wipeout.Layer.Name;
                        if (string.Equals(layer, maskLayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                        }

                        continue;
                    }

                    var mtext = entity as MText;
                    if (mtext != null
                        && (mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0)
                    {
                        count++;
                        continue;
                    }

                    var dimension = entity as Dimension;
                    if (dimension != null && HasDimensionFillOverride(dimension))
                    {
                        count++;
                    }
                }
            }

            return count;
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

        // C02（2026-11-05）：PushHatchesToBack 已整体删除。
        // 它通过 SortEntitiesTable.MoveToTop 计数并打印「填充置后 N」，但 ACadSharp
        // 写出的块记录扩展字典 AutoCAD 读不回，排序表根本不落盘——文件里没有任何变化。
        // 默认关闭 + 移除调用点，才不会再报一个不存在的成功。绘制顺序由
        // MaskBringTextToFront（实体集合顺序）负责。配置字段保留仅为兼容旧文件。

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

            string mode = text.Trim();
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return "Off";
            }

            // "BlockInternal" 是旧值：*D 缓存块方案已退役，仍按 Native 接受（有意兼容），
            // 但其它任何拼写错误都必须报错，不能静默当成 Native。
            if (string.Equals(mode, "Native", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "BlockInternal", StringComparison.OrdinalIgnoreCase))
            {
                return "Native";
            }

            throw new RuleValidationException(
                "DimensionMaskMode=\"" + mode + "\" 不受支持：可选 Off / Native（默认）；旧值 BlockInternal 按 Native 处理。");
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

            string mode = text.Trim();
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return "Off";
            }

            if (string.Equals(mode, "ExplicitOnly", StringComparison.OrdinalIgnoreCase))
            {
                return "ExplicitOnly";
            }

            if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
            {
                return "All";
            }

            throw new RuleValidationException(
                "DimensionTextMode=\"" + mode + "\" 不受支持：可选 All（默认）/ ExplicitOnly / Off。");
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
            private readonly Dictionary<BlockRecord, List<WipeoutBox>> _wipeoutBoxes =
                new Dictionary<BlockRecord, List<WipeoutBox>>();

            /// <summary>C16：判重键 = AABB + 旋转角，同位置的 0° 与 90° 文字不能互相顶掉。</summary>
            private struct WipeoutBox
            {
                public Bounds2d Rect;
                public double Rotation;
            }

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

                // C10/C14：**每个** Wipeout 都自检。旧版用 `_encodingChecked` 短路，
                // 只查第一个，后面编码坏掉的会静默写进图纸（preflight 的判据又和生产不同）。
                string reason;
                if (!MaskService.IsWipeoutEncoded(wipeout, out reason))
                {
                    // 没有 2D Solid 兜底：写不出来就停，让用户看到原因。
                    throw new RuleValidationException("Wipeout 编码不可用：" + reason + "。本规则没有 Solid 兜底。");
                }

                wipeout.Layer = MaskLayer();
                RegisterWipeout(target.Space, target.Rect, target.Rotation);
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
            public bool HasOverlappingWipeout(BlockRecord space, Bounds2d box, double rotation)
            {
                if (space == null || !box.IsValid || box.Area <= 0)
                {
                    return false;
                }

                List<WipeoutBox> boxes;
                if (!_wipeoutBoxes.TryGetValue(space, out boxes))
                {
                    boxes = new List<WipeoutBox>();
                    foreach (Entity entity in space.Entities)
                    {
                        var existingWipeout = entity as Wipeout;
                        if (existingWipeout == null)
                        {
                            continue;
                        }

                        Bounds2d existing = TextBounds.ForWipeout(existingWipeout);
                        if (existing.IsValid && existing.Area > 0)
                        {
                            boxes.Add(new WipeoutBox
                            {
                                Rect = existing,
                                Rotation = RotationOf(existingWipeout)
                            });
                        }
                    }

                    _wipeoutBoxes[space] = boxes;
                }

                var candidate = new WipeoutBox { Rect = box, Rotation = rotation };
                foreach (WipeoutBox existing in boxes)
                {
                    if (SameBox(existing, candidate))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void RegisterWipeout(BlockRecord space, Bounds2d box, double rotation)
            {
                List<WipeoutBox> boxes;
                if (!_wipeoutBoxes.TryGetValue(space, out boxes))
                {
                    boxes = new List<WipeoutBox>();
                    _wipeoutBoxes[space] = boxes;
                }

                boxes.Add(new WipeoutBox { Rect = box, Rotation = rotation });
            }

            /// <summary>已有 Wipeout 的旋转角从 U 向量反算；取不到就按 0 处理。</summary>
            private static double RotationOf(Wipeout wipeout)
            {
                // 旋转角取裁剪多边形第 0→1 条边的方向——与 CollectTargets 给 target.Rotation
                // 的算法（maskVertices[0]→[1]）同源。U/V 向量是带尺寸的变换向量而非方向，
                // Atan2(U.Y,U.X) 对旋转遮罩恒为 0，会漏掉全部旋转文字的去重。
                XY[] corners = TextBounds.WipeoutCorners(wipeout);
                if (corners.Length >= 2)
                {
                    double dx = corners[1].X - corners[0].X;
                    double dy = corners[1].Y - corners[0].Y;
                    if (Math.Abs(dx) > 1e-12 || Math.Abs(dy) > 1e-12)
                    {
                        return Math.Atan2(dy, dx);
                    }
                }

                XYZ u = wipeout.UVector;
                if (Math.Abs(u.X) < 1e-12 && Math.Abs(u.Y) < 1e-12)
                {
                    return 0;
                }

                return Math.Atan2(u.Y, u.X);
            }

            private static bool SameBox(WipeoutBox a, WipeoutBox b)
            {
                Bounds2d x = a.Rect;
                Bounds2d y = b.Rect;
                double tolerance = Math.Max(1e-4, Math.Max(x.Width, x.Height) * 0.01);

                // 旋转角按**模 π** 比：矩形绕自己转 180° 是同一个矩形，
                // 而 U 向量读回来可能是 (0,-1) 而不是 (0,1)（90° vs −90°）。
                // 不取模的话，所有旋转文字的遮罩在第二次运行都会被判成"新遮罩"再叠一层
                // （30250-003 实测：重跑一次多出 53 个重复 Wipeout，全是 90°/270° 文字）。
                double delta = NormalizeAngle(a.Rotation - b.Rotation);
                double orientationDelta = Math.Min(Math.Abs(delta), Math.PI - Math.Abs(delta));

                return Math.Abs(x.CenterX - y.CenterX) <= tolerance
                       && Math.Abs(x.CenterY - y.CenterY) <= tolerance
                       && Math.Abs(x.Width - y.Width) <= tolerance
                       && Math.Abs(x.Height - y.Height) <= tolerance
                       && orientationDelta <= 1e-3;
            }

            private static double NormalizeAngle(double angle)
            {
                while (angle > Math.PI)
                {
                    angle -= 2 * Math.PI;
                }

                while (angle < -Math.PI)
                {
                    angle += 2 * Math.PI;
                }

                return angle;
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

                // C06：EnsureLayer 命中已存在图层时直接返回 existing，不改 PlotFlag。
                // 老版本曾默认 false，留下一批带非打印 _MASK 图层的输出图；只在新层上
                // 设值是修不回来的，这里必须按配置显式纠正已存在的图层。
                bool plottable = RuleContext.Bool(_action.MaskLayerPlottable, true);
                if (plottable && !_layer.PlotFlag)
                {
                    _context.Log.Info(string.Format("遮罩图层「{0}」原本不可打印，已改为可打印。", _layerName));
                }
                else if (!plottable && _layer.PlotFlag)
                {
                    _context.Log.Warn("MaskLayerPlottable=false：该图层上的 Wipeout 遮罩不会出现在打印结果里。");
                }

                _layer.PlotFlag = plottable;

                return _layer;
            }
        }

        private sealed class MaskTarget
        {
            public Entity Entity;
            public BlockRecord Space;
            public Bounds2d Rect;
            public XY[] MaskVertices;

            /// <summary>遮罩矩形的旋转角（弧度），用于 Wipeout 判重。</summary>
            public double Rotation;

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
