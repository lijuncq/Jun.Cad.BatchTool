using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Engine.Preflight;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 批量给文字加遮罩。
    ///
    /// **能力边界（3.8.35 实测，决定了本规则的形态）**：
    ///
    /// * MTEXT 有**原生背景遮罩**（<c>BackgroundFillFlags</c>），它是文字自身的属性，
    ///   不新增实体、不依赖绘制顺序、不影响句柄 —— 默认走这条路；
    /// * TEXT / ATTRIB 没有原生遮罩，只能新建 Wipeout / 2D Solid。而
    ///   <c>BlockRecord.Entities</c> 是 <c>CadObjectCollection</c>，**只有 Add/Remove，没有 Insert**；
    ///   新实体永远追加在末尾（句柄也最大），也就是**画在文字上面**——Wipeout 会把文字自己擦掉。
    ///   要让它排在文字之前，只能"移出文字 → 加遮罩 → 再把文字加回去"，而这会给文字
    ///   **重新分配句柄**（实测 48 → 4A，且 <c>Handle</c> 是只读属性，无法恢复）。
    ///   因此这条路做成显式开关 <c>MaskRebuildText</c>（默认关），关着时记 Blocked 并说明原因；
    /// * DIMENSION 更不能重建（标注带 <c>*D</c> 缓存块，重建会断开关联），
    ///   所以标注文字遮罩**当前版本不支持**，同样记 Blocked 并写明原因；
    /// * 无论哪种方式，遮罩都是**静态**的：文字/标注被编辑后不会跟着走。
    ///
    /// 这几条都不是"没实现"，而是纯文件模式下的真实边界，必须出现在预览和报告里。
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

            bool onlyOccluded = RuleContext.Bool(action.MaskOnlyWhenOccluded, true);
            double padRatio = RuleContext.Double(action.MaskPaddingRatio, 0.15);
            double padAbs = RuleContext.Double(action.MaskPadding, 0);
            double threshold = RuleContext.Double(action.MaskMinOccludedRatio, 0.05);
            bool useBlockBounds = !RuleContext.Bool(action.MaskIgnoreBlockBounds, true);
            bool rebuildText = RuleContext.Bool(action.MaskRebuildText, false);
            string maskLayerName = string.IsNullOrWhiteSpace(action.MaskLayer)
                ? MaskService.DefaultMaskLayer
                : action.MaskLayer.Trim();
            int maxMasks = RuleContext.Int(action.MaxMasksPerDrawing, 2000);
            string dimMode = NormalizeDimensionMode(action.DimensionTextMode);
            string dimMaskMode = NormalizeDimensionMaskMode(action.DimensionMaskMode);

            // ---- 只有"新建遮罩实体"这条路才需要 Wipeout 探测 ----
            string requested = NormalizeMaskMode(action.MaskMode);
            bool wantWipeout = !string.Equals(requested, "Solid", StringComparison.OrdinalIgnoreCase);
            string degradeNote = null;

            if (wantWipeout)
            {
                ProbeResult probe = CapabilityProbe.WipeoutWriter();
                if (probe.Status != ProbeStatus.Passed)
                {
                    if (string.Equals(requested, "Wipeout", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RuleValidationException(
                            "MaskMode=Wipeout，但 Wipeout 写出探测未通过：" + probe.Detail
                            + "。把 MaskMode 改成 Auto 可以让它自动降级为 2D Solid。");
                    }

                    wantWipeout = false;
                    degradeNote = "Wipeout 写出探测失败，改用 2D Solid：" + probe.Detail;
                    context.Log.Warn(degradeNote);
                }
            }

            // 只要有任何一条路径可能"新建遮罩实体"，就需要遮罩图层：
            // TEXT/ATTRIB 的重建路径、Solid 降级、标注的 *D 块内路径都算。
            bool mayCreateEntity = rebuildText || !wantWipeout
                                   || string.Equals(dimMaskMode, "BlockInternal", StringComparison.OrdinalIgnoreCase);

            Layer maskLayer = null;
            if (mayCreateEntity)
            {
                // 只有真的会建实体时才需要遮罩图层
                maskLayer = LayerService.EnsureLayer(context.Document, maskLayerName, action, context.Log, context.Resources);
                if (maskLayer == null)
                {
                    throw new RuleValidationException("遮罩图层「" + maskLayerName + "」不存在且无法创建。");
                }

                if (!RuleContext.Bool(action.MaskLayerPlottable, false))
                {
                    // Solid 是不透明实心，可打印会在图上留一块实色
                    maskLayer.PlotFlag = false;
                }
            }

            var targets = new List<MaskTarget>();
            CollectTargets(context, result, dimMode, padRatio, padAbs, targets);

            if (targets.Count == 0)
            {
                result.AddNote("没有找到需要加遮罩的文字。");
                return;
            }

            int created = 0;

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

                BoundsIndex occluders = BuildOccluders(space, list, useBlockBounds, maskLayerName);

                foreach (MaskTarget target in list)
                {
                    double ratio = 1.0;
                    if (onlyOccluded)
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

                    if (created >= maxMasks)
                    {
                        result.MarkSkipped("超过单图遮罩上限");
                        context.RecordBlocked(target.Entity, "加遮罩",
                            string.Format("已达单图上限 {0}，后续文字不再加遮罩", maxMasks), target.SpaceName);
                        continue;
                    }

                    if (ApplyMask(context, result, target, wantWipeout, rebuildText, maskLayer, ratio, degradeNote,
                            action, dimMaskMode))
                    {
                        created++;
                    }
                }
            }

            if (created > 0)
            {
                result.AddNote(string.Format(
                    "共为 {0} 处文字加了遮罩（{1}）。", created,
                    rebuildText ? "新建遮罩实体" : "MTEXT 原生背景遮罩"));
            }
        }

        // ------------------------------------------------------------------ 应用

        private static bool ApplyMask(
            RuleContext context,
            RuleExecutionResult result,
            MaskTarget target,
            bool wantWipeout,
            bool rebuildText,
            Layer maskLayer,
            double ratio,
            string degradeNote,
            RuleAction action,
            string dimMaskMode)
        {
            var mtext = target.Entity as MText;
            if (mtext != null)
            {
                // 原生背景遮罩：不动实体、不动顺序、不动句柄
                bool already = (mtext.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) != 0
                               || (mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0;

                if (already)
                {
                    result.MarkSkipped("已有背景遮罩");
                    context.RecordSkip(mtext, "加遮罩（MTEXT 原生）", "已有背景遮罩", false, target.SpaceName);
                    return false;
                }

                mtext.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
                mtext.BackgroundScale = 1.5;   // AutoCAD 默认值；该属性回读不保真，见文档

                result.MarkChanged("MTEXT");
                context.RecordChange(mtext, "加遮罩（MTEXT 原生背景）",
                    string.Format("遮挡 {0:P0}", ratio),
                    "BackgroundFillFlags |= UseDrawingWindowColor",
                    false,
                    "原生背景遮罩，不新增实体；BackgroundScale 在 3.8.35 回读恒为 1.5，无法精确控制",
                    target.SpaceName);
                return true;
            }

            if (target.Kind == "DIMENSION")
            {
                var dimension = target.Entity as Dimension;

                if (string.Equals(dimMaskMode, "BlockInternal", StringComparison.OrdinalIgnoreCase))
                {
                    return ApplyDimensionBlockMask(context, result, dimension, target, wantWipeout, maskLayer, ratio);
                }

                // 默认路径：遮罩必须排在标注之前，模型空间做不到（只能重建标注 → 断开关联）
                result.MarkSkipped("标注遮罩未启用");
                context.RecordBlocked(target.Entity, "加遮罩（DIMENSION）",
                    "纯文件模式默认不处理标注：遮罩要排在标注之前只能重建标注（会断开关联）。"
                    + "把 Action.DimensionMaskMode 设为 BlockInternal 可把遮罩放进标注的 *D 块（随标注移动，但标注重生成后会丢失）。",
                    target.SpaceName);
                return false;
            }

            if (!rebuildText)
            {
                result.MarkSkipped("需要重建文字");
                context.RecordBlocked(target.Entity, "加遮罩（" + target.Kind + "）",
                    "该类型没有原生遮罩；新建 Wipeout/Solid 只能追加在文字之后（会盖住文字），"
                    + "要放到下面必须重建文字实体并重新分配句柄。确认可接受时把 Action.MaskRebuildText 设为 true。",
                    target.SpaceName);
                return false;
            }

            Entity mask = wantWipeout
                ? (Entity)MaskService.CreateWipeout(context.Document, target.Rect)
                : MaskService.CreateSolid(target.Rect, RuleContext.Int(action.MaskSolidColorIndex, 255));

            mask.Layer = maskLayer;

            // 移出文字 → 追加遮罩 → 再把文字加回去：顺序变成 [遮罩, 文字]，遮罩在下
            MaskService.PlaceMaskBefore(target.Space, target.Entity, mask);

            result.MarkChanged(target.Kind);
            context.RecordChange(target.Entity, "加遮罩（" + target.Kind + "）",
                string.Format("遮挡 {0:P0}", ratio),
                string.Format("{0} {1:0.#}×{2:0.#}", wantWipeout ? "Wipeout" : "Solid",
                    target.Rect.Width, target.Rect.Height),
                true,
                (degradeNote == null ? string.Empty : degradeNote + "；")
                + "为把遮罩放到文字之下，文字实体已重建，句柄已改变",
                target.SpaceName);

            return true;
        }

        /// <summary>
        /// 把遮罩放进标注的 *D 缓存块，并排在块内文字之前。
        ///
        /// 坐标：*D 块内实体用的是世界坐标（实测块内 MText 的 InsertPoint 就是模型空间坐标），
        /// 所以文字矩形可以原样用。
        ///
        /// 顺序：块内实体集合同样只能追加，所以"移出文字 → 追加遮罩 → 再加回文字"，
        /// 让遮罩落在文字下面。这里动的都是**缓存实体**（AutoCAD 会按标注重算），
        /// 但它们的句柄确实会变，必须在预览里标出来。
        /// </summary>
        private static bool ApplyDimensionBlockMask(
            RuleContext context,
            RuleExecutionResult result,
            Dimension dimension,
            MaskTarget target,
            bool wantWipeout,
            Layer maskLayer,
            double ratio)
        {
            if (dimension == null || dimension.Block == null)
            {
                result.MarkSkipped("标注没有缓存块");
                context.RecordBlocked(dimension, "加遮罩（DIMENSION）",
                    "该标注没有 *D 缓存块（可能尚未生成），无法把遮罩放进去。", target.SpaceName);
                return false;
            }

            Entity textInBlock = null;
            foreach (Entity child in dimension.Block.Entities)
            {
                if (child is MText || (child is TextEntity && !(child is AttributeBase)))
                {
                    textInBlock = child;
                    break;
                }
            }

            if (textInBlock == null)
            {
                result.MarkSkipped("标注块内没有文字");
                context.RecordBlocked(dimension, "加遮罩（DIMENSION）",
                    "标注的 *D 块里找不到文字实体，无法确定遮罩顺序。", target.SpaceName);
                return false;
            }

            Entity mask = wantWipeout
                ? (Entity)MaskService.CreateWipeout(context.Document, target.Rect)
                : MaskService.CreateSolid(target.Rect, 255);

            mask.Layer = maskLayer;

            MaskService.PlaceMaskBefore(dimension.Block, textInBlock, mask);

            result.MarkChanged("DIMENSION");
            context.RecordChange(dimension, "加遮罩（DIMENSION · *D 块内）",
                string.Format("遮挡 {0:P0}", ratio),
                string.Format("{0} {1:0.#}×{2:0.#}", wantWipeout ? "Wipeout" : "Solid",
                    target.Rect.Width, target.Rect.Height),
                true,
                "遮罩放在标注的 *D 缓存块内：会随标注移动，但标注一旦被编辑（块重生成）就会丢失；"
                + "块内文字实体的句柄已改变",
                target.SpaceName);

            return true;
        }

        // ------------------------------------------------------------------ 收集目标

        private static void CollectTargets(
            RuleContext context,
            RuleExecutionResult result,
            string dimMode,
            double padRatio,
            double padAbs,
            List<MaskTarget> targets)
        {
            RuleFilter filter = context.Filter;
            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                var types = new List<string> { "TEXT", "MTEXT" };
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
                // 破坏 匹配 = 成功 + 跳过 + 失败（README 7.5 的 90 vs 71+228 就是这个 bug）
                result.Hit();

                if (hit.Depth > 0)
                {
                    // 块定义内的文字：遮罩要进块定义，会被所有实例共享，语义不对
                    result.MarkSkipped("块内实体不处理");
                    continue;
                }

                if (EntityScanner.ShouldSkipLocked(context, entity))
                {
                    result.MarkSkipped("锁定图层");
                    continue;
                }

                var dimension = entity as Dimension;
                if (dimension != null)
                {
                    MaskTarget dimTarget = BuildDimensionTarget(dimension, hit.Owner, dimMode, padRatio, padAbs,
                        EntityScanner.SpaceNameOf(hit));
                    if (dimTarget == null)
                    {
                        result.MarkSkipped("标注无可用文字位置");
                        continue;
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

                double pad = bounds.Height * padRatio + padAbs;
                targets.Add(new MaskTarget
                {
                    Entity = entity,
                    Space = hit.Owner,
                    Rect = bounds.Inflate(pad),
                    Kind = EntityFilter.DxfNameOf(entity),
                    SpaceName = EntityScanner.SpaceNameOf(hit)
                });
            }

            // ---- 块属性：挂在块参照上，不在块定义里 ----
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

                    double pad = bounds.Height * padRatio + padAbs;
                    targets.Add(new MaskTarget
                    {
                        Entity = attribute,
                        Space = hit.Owner,
                        Rect = bounds.Inflate(pad),
                        Kind = "ATTRIB",
                        Note = "属性位置按 WCS 读取",
                        SpaceName = EntityScanner.SpaceNameOf(hit)
                    });
                }
            }
        }

        private static MaskTarget BuildDimensionTarget(
            Dimension dimension, BlockRecord space, string dimMode, double padRatio, double padAbs, string spaceName)
        {
            bool hasExplicitText = !string.IsNullOrEmpty(dimension.Text) && dimension.Text != "<>";
            if (!hasExplicitText && string.Equals(dimMode, "ExplicitOnly", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            XYZ anchor;
            double height;
            double rotation;
            if (!TextBounds.TryGetDimensionTextAnchor(dimension, out anchor, out height, out rotation))
            {
                return null;
            }

            if (height <= 0)
            {
                height = dimension.Style == null ? 2.5 : dimension.Style.TextHeight;
            }

            if (height <= 0)
            {
                return null;
            }

            string text = TextBounds.PlainTextOf(dimension);
            double width = Math.Max(TextBounds.MeasureWidth(text, height), height);

            Bounds2d rect = TextBounds.RotatedRect(anchor, 0, -height, width, height, rotation);
            double pad = height * padRatio + padAbs;

            return new MaskTarget
            {
                Entity = dimension,
                Space = space,
                Rect = rect.Inflate(pad),
                Kind = "DIMENSION",
                Static = true,
                Note = "静态遮罩",
                SpaceName = spaceName
            };
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
        /// 遮挡占比。
        ///
        /// 分两类算，因为"包围盒相交"对细长图形是错的：
        ///   * **线状**（LINE / CIRCLE 环）：只看它是否真的穿过文字矩形，穿过一次记 0.25；
        ///     一条对角线斜穿整张图的包围盒会盖住所有文字，按面积算会把整图判成 100% 遮挡；
        ///   * **面状**（HATCH / SOLID / 其它）：按包围盒相交面积占比累加。
        ///
        /// 结果封顶 1.0。阈值默认 0.05，即"一条线穿过"或"5% 面积被盖"就加遮罩。
        /// </summary>
        private static double ComputeOcclusion(Bounds2d rect, BoundsIndex index)
        {
            if (rect.Area <= 0)
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

        private static string NormalizeMaskMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Auto";
            }

            string t = text.Trim();
            if (string.Equals(t, "Wipeout", StringComparison.OrdinalIgnoreCase))
            {
                return "Wipeout";
            }

            if (string.Equals(t, "Solid", StringComparison.OrdinalIgnoreCase))
            {
                return "Solid";
            }

            return "Auto";
        }

        private static string NormalizeDimensionMaskMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Off";
            }

            return string.Equals(text.Trim(), "BlockInternal", StringComparison.OrdinalIgnoreCase)
                ? "BlockInternal"
                : "Off";
        }

        private static string NormalizeDimensionMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "ExplicitOnly";
            }

            if (string.Equals(text.Trim(), "Off", StringComparison.OrdinalIgnoreCase))
            {
                return "Off";
            }

            return string.Equals(text.Trim(), "All", StringComparison.OrdinalIgnoreCase) ? "All" : "ExplicitOnly";
        }

        private sealed class MaskTarget
        {
            public Entity Entity;
            public BlockRecord Space;
            public Bounds2d Rect;
            public string Kind;
            public bool Static;
            public string Note;
            public string SpaceName;
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
