using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp.Entities;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 填充图案的"线距"度量。
    ///
    /// 为什么需要它：换图案时如果只改图案名、沿用原比例，**看起来的疏密会变**。
    /// 因为每个图案在 .pat 里的基准线距不同（ANSI31 是 3.175、JIS_LC_8A 是 7.8、
    /// JIS_RC_20 是 20），同样的比例 1 画出来的疏密差好几倍。
    /// 要做到"换图案但看起来间距不变"，必须把比例换算一遍。
    ///
    /// 两个关键认知（都经过 AutoCAD 实测组码验证，别凭直觉改）：
    ///
    ///   一、<c>HatchPattern.Line.Offset</c> 存的是**世界坐标**位移，不是 .pat 里的原始值。
    ///       .pat 写 `45,0,0,0,7.8`，进了内存是 (-5.5154, 5.5154)——解析时按线角度旋转过了。
    ///       所以垂距不能用 <c>|Offset.Y|</c>，要用叉积：|dx·sinθ − dy·cosθ|。
    ///       （JIS_LC_8A 实测：|Offset.Y| = 5.5154，真正的垂距是 7.8。）
    ///
    ///   二、**图案线里已经是"最终几何"**：比例和角度都被烘进去了，<c>PatternScale</c> /
    ///       <c>PatternAngle</c> 只是记录值。实测 `8001_SYSTEMSTYLE_1`：
    ///       比例 1 时偏移 (-2.0, 3.4641) → 垂距 4.0；比例 75 时偏移 (-150, 259.808)
    ///       → 垂距 300 = 4.0 × 75。所以算实际线距**不能再乘一次比例**。
    ///       ACadSharp 的 <c>Hatch.PatternScale</c> setter 也是这个语义：赋值会把比例
    ///       乘进图案线里（<c>Pattern.Update(0, 0, scale)</c>），因此"赋原始定义 + 设比例"
    ///       恰好得到正确的最终几何，且只缩放一次。
    ///
    /// 多线族图案取**最小**非零垂距：那是最紧的那道缝，也是眼睛最先读到的疏密。
    /// 对绝大多数标准图案（ANSI31 / LINE / NET / JIS 系列）各线族间距相同，取谁都一样。
    /// </summary>
    public static class HatchPatternMetrics
    {
        /// <summary>间距小于该值视为"没有间距"（退化图案 / 数值噪声）。</summary>
        private const double MinMeaningfulSpacing = 1e-6;

        /// <summary>
        /// 图案**实际**的线距（垂直间距）。取不到返回 null（纯色填充、渐变、空定义）。
        ///
        /// 注意：图案线里已包含比例与角度，不要再去乘 <c>Hatch.PatternScale</c>。
        /// </summary>
        public static double? ReferenceSpacing(HatchPattern pattern)
        {
            if (pattern == null || pattern.Lines == null || pattern.Lines.Count == 0)
            {
                return null;
            }

            double best = 0;

            foreach (HatchPattern.Line line in pattern.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                double spacing = PerpendicularOffset(line);
                if (spacing <= MinMeaningfulSpacing)
                {
                    continue;
                }

                if (best <= 0 || spacing < best)
                {
                    best = spacing;
                }
            }

            return best > 0 ? best : (double?)null;
        }

        /// <summary>实体当前的实际线距。图案线已是最终几何，故不再乘比例。</summary>
        public static double? EffectiveSpacing(Hatch hatch)
        {
            if (hatch == null || hatch.IsSolid)
            {
                return null;
            }

            return ReferenceSpacing(hatch.Pattern);
        }

        /// <summary>
        /// 多线族图案里"最紧的那道缝"——同一方向上相邻两条线族的垂直间隙。
        ///
        /// 为什么单独要这个值：JIS_LC_8A、2_SECONDARY_ESCAPE_ROUTE 这类"双线"图案，
        /// 疏密由线距决定，**观感**却由这道缝决定。换图案时线距能保持（比例反算），
        /// 缝宽却只能跟着比例一起缩（各图案的缝/距比值本来就不同），
        /// 于是"线距没变，看起来却更挤"——用户第一反应是"比例算错了"。
        /// 把缝宽的变化说出来，才不会被误判。
        ///
        /// 只在**所有线族同向**时计算（交叉图案各族方向不同，"族间垂直间隙"没有意义，
        /// 那种情况下返回 null，不做提示）。
        /// </summary>
        public static double? PairGap(HatchPattern pattern)
        {
            if (pattern == null || pattern.Lines == null || pattern.Lines.Count < 2)
            {
                return null;
            }

            var lines = pattern.Lines.Where(l => l != null).ToList();
            if (lines.Count < 2)
            {
                return null;
            }

            double angle = lines[0].Angle;
            foreach (HatchPattern.Line line in lines)
            {
                if (Math.Abs(NormalizeDegrees((line.Angle - angle) * 180.0 / Math.PI)) > 0.5)
                {
                    return null;
                }
            }

            double nx = -Math.Sin(angle);
            double ny = Math.Cos(angle);

            var projections = lines
                .Select(l => l.BasePoint.X * nx + l.BasePoint.Y * ny)
                .OrderBy(v => v)
                .ToList();

            double best = 0;
            for (int i = 1; i < projections.Count; i++)
            {
                double gap = projections[i] - projections[i - 1];
                if (gap > MinMeaningfulSpacing && (best <= 0 || gap < best))
                {
                    best = gap;
                }
            }

            return best > 0 ? best : (double?)null;
        }

        /// <summary>
        /// 两个图案"结构相同、只差一个旋转"时，返回把 <paramref name="target"/> 转到
        /// <paramref name="source"/> 方向所需的旋转角（弧度）。
        ///
        /// "结构相同"的判据（三条都要满足）：
        ///   1. 线族数相同；
        ///   2. 各族角度按顺序对齐后，相对角度吻合（±0.5°）——即两者是同一个图案的旋转；
        ///   3. 每族的虚线段数相同——实线与虚线是两种观感，不能算"几乎相同"。
        ///
        /// 用途：换图案时如果目标图案只是原图案的一个旋转，就把方向也调回去，
        /// 做到"换图案名、观感不变"。
        /// </summary>
        public static bool TryGetOrientationDelta(HatchPattern source, HatchPattern target, out double delta)
        {
            delta = 0;

            if (source == null || target == null
                || source.Lines == null || target.Lines == null
                || source.Lines.Count == 0 || source.Lines.Count != target.Lines.Count)
            {
                return false;
            }

            List<double> sourceAngles = FamilyAngles(source);
            List<double> targetAngles = FamilyAngles(target);

            if (sourceAngles.Count != targetAngles.Count || sourceAngles.Count == 0)
            {
                return false;
            }

            List<int> sourceDashes = DashCounts(source);
            List<int> targetDashes = DashCounts(target);

            if (!sourceDashes.SequenceEqual(targetDashes))
            {
                return false;
            }

            double candidate = NormalizeDegrees(sourceAngles[0] - targetAngles[0]);

            for (int i = 0; i < sourceAngles.Count; i++)
            {
                double residual = NormalizeDegrees(sourceAngles[i] - targetAngles[i] - candidate);
                if (residual > 0.5 && residual < 179.5)
                {
                    return false;
                }
            }

            // 线方向不分正反：165° 与 -15° 画出来一样。取绝对值小的那个，
            // 调用方拿去写 PatternAngle 时不会得到一个让人困惑的 165。
            if (candidate > 90.0)
            {
                candidate -= 180.0;
            }

            delta = candidate * Math.PI / 180.0;
            return true;
        }

        /// <summary>
        /// 只对齐"主方向"：两个图案结构不同（线族数或虚线结构不一致）时，
        /// 把 <paramref name="target"/> 的主方向转到 <paramref name="source"/> 的主方向上。
        ///
        /// 为什么还要这一步：交叉图案换成单线族图案时线族数必然对不上，但"主方向"通常仍该对齐——
        /// 否则换完连方向都变了，看起来像是换错了图案。少掉的那条线族由调用方提示。
        /// </summary>
        public static bool TryGetDominantDelta(HatchPattern source, HatchPattern target, out double delta)
        {
            delta = 0;

            if (source == null || target == null
                || source.Lines == null || target.Lines == null
                || source.Lines.Count == 0 || target.Lines.Count == 0)
            {
                return false;
            }

            double candidate = NormalizeDegrees(DominantAngleDegrees(source) - DominantAngleDegrees(target));

            if (candidate > 90.0)
            {
                candidate -= 180.0;
            }

            delta = candidate * Math.PI / 180.0;
            return true;
        }

        /// <summary>
        /// 图案的"主方向"（度，0~180）。
        ///
        /// 取**垂距最大**的线族：交叉图案里疏密由最稀的那族决定，用它代表"眼睛看到的方向"最稳。
        /// 垂距相同时取**角度较小**的那族——45°/135° 的交叉图案会返回 45°，
        /// 这样结果是确定的，不会因为线在数组里的先后而变。
        /// </summary>
        public static double DominantAngleDegrees(HatchPattern pattern)
        {
            if (pattern == null || pattern.Lines == null || pattern.Lines.Count == 0)
            {
                return 0;
            }

            double bestAngle = 0;
            double bestSpacing = -1;

            foreach (HatchPattern.Line line in pattern.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                double cos = Math.Cos(line.Angle);
                double sin = Math.Sin(line.Angle);
                double spacing = Math.Abs(line.Offset.X * sin - line.Offset.Y * cos);
                double angle = NormalizeDegrees(line.Angle * 180.0 / Math.PI);

                if (spacing > bestSpacing + MinMeaningfulSpacing)
                {
                    bestSpacing = spacing;
                    bestAngle = angle;
                }
                else if (Math.Abs(spacing - bestSpacing) <= MinMeaningfulSpacing && angle < bestAngle)
                {
                    bestAngle = angle;
                }
            }

            return bestAngle;
        }

        /// <summary>各线族角度（度，归一到 0~180，升序）。</summary>
        private static List<double> FamilyAngles(HatchPattern pattern)
        {
            var angles = new List<double>();

            foreach (HatchPattern.Line line in pattern.Lines)
            {
                if (line != null)
                {
                    angles.Add(NormalizeDegrees(line.Angle * 180.0 / Math.PI));
                }
            }

            angles.Sort();
            return angles;
        }

        /// <summary>各线族的虚线段数（升序），用于区分实线与虚线。</summary>
        private static List<int> DashCounts(HatchPattern pattern)
        {
            var counts = new List<int>();

            foreach (HatchPattern.Line line in pattern.Lines)
            {
                counts.Add(line == null || line.DashLengths == null ? 0 : line.DashLengths.Count);
            }

            counts.Sort();
            return counts;
        }

        /// <summary>把角度归一到 0~180（线的方向不分正反）。</summary>
        private static double NormalizeDegrees(double degrees)
        {
            degrees %= 180.0;
            if (degrees < 0)
            {
                degrees += 180.0;
            }

            return degrees;
        }

        /// <summary>
        /// 让新图案的实际线距等于 <paramref name="targetSpacing"/>，返回应设的图案比例。
        ///
        /// 传入的必须是**未经缩放**的定义（直接来自 .pat 的那种），因为
        /// <c>Hatch.PatternScale</c> 的 setter 会把这个比例乘进图案线。
        /// 取不到新图案线距时返回 null。
        /// </summary>
        public static double? ScaleForSpacing(HatchPattern newPattern, double targetSpacing)
        {
            double? reference = ReferenceSpacing(newPattern);
            if (!reference.HasValue || targetSpacing <= MinMeaningfulSpacing)
            {
                return null;
            }

            return targetSpacing / reference.Value;
        }

        /// <summary>
        /// 一条图案线到其下一条平行线的垂直距离。
        ///
        /// Offset 是世界坐标位移，垂距 = |d × u|（u 为线方向单位向量），
        /// 即 |dx·sinθ − dy·cosθ|。沿线方向的分量（虚线错位）不参与计算。
        /// </summary>
        private static double PerpendicularOffset(HatchPattern.Line line)
        {
            double cos = Math.Cos(line.Angle);
            double sin = Math.Sin(line.Angle);
            return Math.Abs(line.Offset.X * sin - line.Offset.Y * cos);
        }
    }
}
