using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>区域判定谓词。</summary>
    public enum RegionPredicate
    {
        /// <summary>包围盒中心在区域内（默认）。</summary>
        CenterInside,

        /// <summary>包围盒四角全部在区域内。</summary>
        FullyInside,

        /// <summary>包围盒与区域有交集。</summary>
        Intersects
    }

    /// <summary>
    /// 空间过滤：把"处理哪些布局 / 哪些图框里 / 哪块区域"翻译成一次可复用的判定。
    ///
    /// 三层，从粗到细：
    ///   1. **空间级**：模型空间 + 指定布局（精确名或通配符）。布局枚举按 <see cref="BlockRecord"/>
    ///      引用去重——实测 <c>*Model_Space</c> 的 <c>Layout.Name</c> 就是 <c>"Model"</c>，
    ///      不去重会让模型空间的实体被处理两遍（442 → 884）；
    ///   2. **容器级**（推荐主路径）：按图框块参照的包围盒裁一块。用
    ///      <c>Insert.GetBoundingBox()</c>，实测含块内实体、含缩放，可直接用；
    ///   3. **几何区域级**：矩形对角点或多边形顶点 + 三档谓词。
    ///
    /// 容器与区域同时配置时取**交集**（先落在图框内，再落在区域里）。
    ///
    /// 已知误差（都来自"只有轴对齐包络"这件事）：
    ///   * 旋转块参照：包络大于实际形状，"部分相交"会误收；
    ///   * 长曲线（大半径弧/样条）：包络明显大于视觉范围；
    ///   * Wipeout：库的包围盒忽略 U/V 向量，这里用 <see cref="TextBounds.ForWipeout"/> 自己算；
    ///   * 文字：库返回退化点，用 <see cref="TextBounds"/> 估算；
    ///   * 表格：没有包围盒，直接判 <c>Skipped (no bbox)</c>。
    /// </summary>
    public sealed class SpatialFilter
    {
        private readonly List<Bounds2d> _containerRects = new List<Bounds2d>();
        private readonly List<List<XY>> _regions = new List<List<XY>>();
        private readonly List<string> _notes = new List<string>();

        private SpatialFilter()
        {
            Spaces = new List<BlockRecord>();
        }

        /// <summary>解析后的种子空间（模型空间 + 匹配的布局块）。</summary>
        public List<BlockRecord> Spaces { get; private set; }

        /// <summary>是否需要按区域/容器再筛一遍。</summary>
        public bool HasAreaFilter
        {
            get { return _containerRects.Count > 0 || _regions.Count > 0; }
        }

        /// <summary>配了图框/区域但一个都没匹配上——调用方应直接返回空结果，而不是"没过滤"。</summary>
        public bool MatchedNothing { get; private set; }

        /// <summary>
        /// 因为"拿不到包围盒"而无法判定、被排除掉的对象数（表格 / 未知实体）。
        /// 这些对象**不会**被规则处理——"只处理这块区域"必须真的只处理区域内的。
        /// </summary>
        public int UndecidableCount { get; private set; }

        public RegionPredicate Predicate { get; private set; }

        public bool IncludesNested { get; private set; }

        public IReadOnlyList<string> Notes
        {
            get { return _notes; }
        }

        public int ContainerCount
        {
            get { return _containerRects.Count; }
        }

        public int RegionCount
        {
            get { return _regions.Count; }
        }

        // ------------------------------------------------------------------ 构建

        /// <summary>
        /// 由规则上下文构建。没有任何空间配置时返回 null（调用方按原逻辑走）。
        /// </summary>
        public static SpatialFilter Build(RuleContext ctx, ScanScope scope)
        {
            if (ctx == null || ctx.Document == null)
            {
                return null;
            }

            RuleFilter filter = ctx.Filter;
            bool hasLayout = filter.LayoutPatterns != null && filter.LayoutPatterns.Exists(x => !string.IsNullOrWhiteSpace(x));
            bool hasContainer = !string.IsNullOrWhiteSpace(filter.ContainerBlockPattern);
            bool hasRegion = ParseRegionMode(filter.RegionMode) != RegionMode.None
                             && filter.RegionPoints != null && filter.RegionPoints.Count > 0;

            if (!hasLayout && !hasContainer && !hasRegion)
            {
                return null;
            }

            var result = new SpatialFilter();
            result.Predicate = ParsePredicate(filter.RegionPredicate);
            result.IncludesNested = ConfigValues.Bool(filter.RegionIncludesNested, false);

            // ---- 1. 空间 ----
            List<BlockRecord> seeds = DatabaseWalker.GetSpaces(ctx.Document, scope);
            if (hasLayout)
            {
                var patterns = new List<string>();
                foreach (string p in filter.LayoutPatterns)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        patterns.Add(p.Trim());
                    }
                }

                var kept = new List<BlockRecord>();
                bool modelSpaceKept = false;

                foreach (BlockRecord block in seeds)
                {
                    string layoutName = LayoutNameOf(ctx.Document, block);
                    if (layoutName == null)
                    {
                        // 不是布局块（不该出现在 seeds 里），保留
                        kept.Add(block);
                        continue;
                    }

                    if (MatchesAny(layoutName, patterns))
                    {
                        kept.Add(block);
                        if (ReferenceEquals(block, ctx.Document.ModelSpace))
                        {
                            modelSpaceKept = true;
                        }
                    }
                }

                if (!modelSpaceKept && (scope == ScanScope.ModelAndLayouts || scope == ScanScope.ModelOnly))
                {
                    result._notes.Add("LayoutPatterns 不含能匹配 \"Model\" 的模式，模型空间已被排除（要保留请写 Model 或 *）。");
                }

                seeds = kept;
                result._notes.Add(string.Format("布局模式 {0} → 命中 {1} 个空间。",
                    string.Join("/", patterns.ToArray()), seeds.Count));
            }

            result.Spaces = seeds;

            // ---- 2. 容器（图框） ----
            if (hasContainer)
            {
                int found = 0;
                string pattern = filter.ContainerBlockPattern.Trim();
                string attrTag = filter.ContainerAttributeTag;
                string attrPattern = filter.ContainerAttributePattern;

                foreach (BlockRecord space in seeds)
                {
                    foreach (Entity entity in space.Entities)
                    {
                        var ins = entity as Insert;
                        if (ins == null || ins is TableEntity)
                        {
                            continue;
                        }

                        string blockName = EntityFilter.EffectiveBlockName(ins.Block);
                        if (!Wildcard.IsMatch(blockName, pattern))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(attrTag) && !AttributeMatches(ins, attrTag, attrPattern))
                        {
                            continue;
                        }

                        Bounds2d bounds;
                        if (!TextBounds.TryGetFromLibrary(ins, out bounds))
                        {
                            result._notes.Add(string.Format("图框 {0} 没有可用包围盒，已忽略。", blockName));
                            continue;
                        }

                        result._containerRects.Add(bounds);
                        found++;
                    }
                }

                if (found == 0)
                {
                    result.MatchedNothing = true;
                    result._notes.Add(string.Format("图框块名模式「{0}」没有匹配到任何块参照，本规则不会处理任何对象。", pattern));
                }
                else
                {
                    result._notes.Add(string.Format("图框容器 {0} → 命中 {1} 个图框。", pattern, found));
                }
            }

            // ---- 3. 几何区域 ----
            if (hasRegion)
            {
                RegionMode mode = ParseRegionMode(filter.RegionMode);
                List<XY> points = ParsePoints(filter.RegionPoints);

                if (mode == RegionMode.Rect && points.Count >= 2)
                {
                    XY a = points[0];
                    XY b = points[1];
                    double minX = Math.Min(a.X, b.X);
                    double maxX = Math.Max(a.X, b.X);
                    double minY = Math.Min(a.Y, b.Y);
                    double maxY = Math.Max(a.Y, b.Y);

                    result._regions.Add(new List<XY>
                    {
                        new XY(minX, minY), new XY(maxX, minY), new XY(maxX, maxY), new XY(minX, maxY)
                    });
                }
                else if (mode == RegionMode.Polygon && points.Count >= 3)
                {
                    if (Geo2d.IsSelfIntersecting(points))
                    {
                        result.MatchedNothing = true;
                        result._notes.Add("RegionPoints 构成的多边形自交，已拒绝执行（请检查顶点顺序）。");
                    }
                    else
                    {
                        result._regions.Add(points);
                    }
                }
                else
                {
                    result.MatchedNothing = true;
                    result._notes.Add(string.Format(
                        "RegionMode={0} 但 RegionPoints 数量不足（矩形要 2 点，多边形要 3 点以上），已拒绝执行。", mode));
                }

                if (result._regions.Count > 0)
                {
                    result._notes.Add(string.Format("几何区域 {0} → {1} 个，判定={2}。",
                        mode, result._regions.Count, result.Predicate));
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ 判定

        /// <summary>扫描结果是否落在过滤范围内。</summary>
        public bool Accepts(ScanHit hit)
        {
            if (hit == null)
            {
                return false;
            }

            if (!HasAreaFilter)
            {
                return true;
            }

            // 块内实体是块坐标，没有累计变换——默认**排除**而不是放过。
            // 放过的话"只处理这块区域"会变成"处理整张图的所有块内容"，
            // 实测一张 442 实体的图会因此匹配到 1953 个块内 LINE。
            if (hit.Depth > 0 && !IncludesNested)
            {
                return false;
            }

            Bounds2d bounds;
            if (!TextBounds.TryGet(hit.Entity, out bounds))
            {
                // 没有包围盒（表格 / 未知实体）：无法判定"在不在区域内"。
                // 这里选择**排除**而不是放过——"只处理 A3 图框内"如果连区域外的表格也一起改，
                // 区域过滤就成了假的安全感。数量会汇总后提示用户。
                UndecidableCount++;
                return false;
            }

            if (_containerRects.Count > 0)
            {
                bool inAny = false;
                foreach (Bounds2d rect in _containerRects)
                {
                    if (Passes(bounds, new List<XY>
                    {
                        new XY(rect.MinX, rect.MinY), new XY(rect.MaxX, rect.MinY),
                        new XY(rect.MaxX, rect.MaxY), new XY(rect.MinX, rect.MaxY)
                    }))
                    {
                        inAny = true;
                        break;
                    }
                }

                if (!inAny)
                {
                    return false;
                }
            }

            if (_regions.Count > 0)
            {
                bool inAny = false;
                foreach (List<XY> polygon in _regions)
                {
                    if (Passes(bounds, polygon))
                    {
                        inAny = true;
                        break;
                    }
                }

                if (!inAny)
                {
                    return false;
                }
            }

            return true;
        }

        private bool Passes(Bounds2d bounds, List<XY> polygon)
        {
            switch (Predicate)
            {
                case RegionPredicate.FullyInside:
                    return Geo2d.Contains(polygon, bounds.MinX, bounds.MinY)
                           && Geo2d.Contains(polygon, bounds.MaxX, bounds.MinY)
                           && Geo2d.Contains(polygon, bounds.MaxX, bounds.MaxY)
                           && Geo2d.Contains(polygon, bounds.MinX, bounds.MaxY);

                case RegionPredicate.Intersects:
                    return Geo2d.IntersectsBox(polygon, bounds);

                default:
                    return Geo2d.Contains(polygon, bounds.CenterX, bounds.CenterY);
            }
        }

        // ------------------------------------------------------------------ 解析

        private enum RegionMode
        {
            None,
            Rect,
            Polygon
        }

        private static RegionMode ParseRegionMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return RegionMode.None;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "RECT":
                case "RECTANGLE":
                    return RegionMode.Rect;
                case "POLYGON":
                case "POLY":
                    return RegionMode.Polygon;
                default:
                    return RegionMode.None;
            }
        }

        private static RegionPredicate ParsePredicate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return RegionPredicate.CenterInside;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "FULLYINSIDE":
                case "FULLY_INSIDE":
                case "CONTAINS":
                    return RegionPredicate.FullyInside;
                case "INTERSECTS":
                case "CROSSING":
                    return RegionPredicate.Intersects;
                default:
                    return RegionPredicate.CenterInside;
            }
        }

        private static List<XY> ParsePoints(List<string> raw)
        {
            var points = new List<XY>();
            if (raw == null)
            {
                return points;
            }

            foreach (string item in raw)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                string[] parts = item.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                double x;
                double y;
                if (parts.Length >= 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                {
                    points.Add(new XY(x, y));
                }
            }

            return points;
        }

        private static string LayoutNameOf(CadDocument doc, BlockRecord block)
        {
            if (block == null)
            {
                return null;
            }

            if (block.Layout != null)
            {
                return block.Layout.Name;
            }

            if (ReferenceEquals(block, doc.ModelSpace))
            {
                return "Model";
            }

            return null;
        }

        private static bool AttributeMatches(Insert insert, string tag, string pattern)
        {
            if (insert.Attributes == null)
            {
                return false;
            }

            foreach (AttributeEntity attribute in insert.Attributes)
            {
                if (attribute == null || string.IsNullOrEmpty(attribute.Tag))
                {
                    continue;
                }

                if (!string.Equals(attribute.Tag, tag.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return true;
                }

                return Wildcard.IsMatch(attribute.Value ?? string.Empty, pattern.Trim());
            }

            return false;
        }

        private static bool MatchesAny(string text, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0)
            {
                return true;
            }

            foreach (string pattern in patterns)
            {
                if (Wildcard.IsMatch(text, pattern))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 通配符匹配（<c>*</c> 任意长度、<c>?</c> 单字符），大小写不敏感。
    ///
    /// 为什么不直接用 <c>new Regex(pattern)</c>：图层名/块名/布局名里带 <c>(</c>、<c>+</c>、
    /// <c>-</c>、<c>.</c> 的很多（例如 <c>A1-平面(1)</c>），不转义会当成正则语法抛异常。
    /// </summary>
    public static class Wildcard
    {
        public static bool IsMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*")
            {
                return true;
            }

            if (text == null)
            {
                text = string.Empty;
            }

            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>二维几何谓词（点/多边形/包围盒）。</summary>
    public static class Geo2d
    {
        private const double Epsilon = 1e-9;

        /// <summary>射线法判点在多边形内（支持凹多边形）。</summary>
        public static bool Contains(List<XY> polygon, double x, double y)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }

            bool inside = false;
            int n = polygon.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[i].X;
                double yi = polygon[i].Y;
                double xj = polygon[j].X;
                double yj = polygon[j].Y;

                bool crosses = ((yi > y) != (yj > y))
                               && (x < (xj - xi) * (y - yi) / (yj - yi + Epsilon) + xi);

                if (crosses)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>包围盒与多边形是否相交（任一角在内 / 任一条边穿过盒子）。</summary>
        public static bool IntersectsBox(List<XY> polygon, Bounds2d box)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }

            if (Contains(polygon, box.CenterX, box.CenterY))
            {
                return true;
            }

            if (Contains(polygon, box.MinX, box.MinY)
                || Contains(polygon, box.MaxX, box.MinY)
                || Contains(polygon, box.MaxX, box.MaxY)
                || Contains(polygon, box.MinX, box.MaxY))
            {
                return true;
            }

            for (int i = 0; i < polygon.Count; i++)
            {
                XY a = polygon[i];
                XY b = polygon[(i + 1) % polygon.Count];
                if (SegmentIntersectsBox(a, b, box))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool SegmentIntersectsBox(XY a, XY b, Bounds2d box)
        {
            // 线段与四条边逐条求交（Liang–Barsky 的简化版）
            if (SegmentIntersectsSegment(a, b, new XY(box.MinX, box.MinY), new XY(box.MaxX, box.MinY))) return true;
            if (SegmentIntersectsSegment(a, b, new XY(box.MaxX, box.MinY), new XY(box.MaxX, box.MaxY))) return true;
            if (SegmentIntersectsSegment(a, b, new XY(box.MaxX, box.MaxY), new XY(box.MinX, box.MaxY))) return true;
            if (SegmentIntersectsSegment(a, b, new XY(box.MinX, box.MaxY), new XY(box.MinX, box.MinY))) return true;
            return false;
        }

        private static bool SegmentIntersectsSegment(XY p1, XY p2, XY p3, XY p4)
        {
            double d1 = Cross(p3, p4, p1);
            double d2 = Cross(p3, p4, p2);
            double d3 = Cross(p1, p2, p3);
            double d4 = Cross(p1, p2, p4);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }

            return false;
        }

        private static double Cross(XY a, XY b, XY c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        /// <summary>多边形是否自交（自交的顶点顺序没有明确"内/外"，必须拒绝而不是猜）。</summary>
        public static bool IsSelfIntersecting(List<XY> polygon)
        {
            if (polygon == null || polygon.Count < 4)
            {
                return false;
            }

            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                XY a1 = polygon[i];
                XY a2 = polygon[(i + 1) % n];

                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1)
                    {
                        continue;
                    }

                    XY b1 = polygon[j];
                    XY b2 = polygon[(j + 1) % n];

                    if (SegmentIntersectsSegment(a1, a2, b1, b2))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
