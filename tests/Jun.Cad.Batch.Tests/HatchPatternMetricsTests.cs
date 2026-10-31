using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using CSMath;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 换图案时的"结构比对"：判断目标图案是不是原图案的一个旋转。
    ///
    /// 用途是让"换图案名但观感不变"成立：结构相同就把方向转回去；
    /// 结构不同（线族数、虚线结构变了）就别装，让用户看到提示。
    /// </summary>
    public class HatchPatternMetricsTests
    {
        private static HatchPattern.Line Line(double angleDeg, double spacing, int dashes = 0)
        {
            double rad = angleDeg * Math.PI / 180.0;
            double nx = -Math.Sin(rad);
            double ny = Math.Cos(rad);

            var line = new HatchPattern.Line
            {
                Angle = rad,
                BasePoint = new XY(0, 0),
                Offset = new XY(nx * spacing, ny * spacing),
                DashLengths = new List<double>()
            };

            for (int i = 0; i < dashes; i++)
            {
                line.DashLengths.Add(1.0);
            }

            return line;
        }

        private static HatchPattern Pattern(params HatchPattern.Line[] lines)
        {
            return new HatchPattern("T") { Lines = new List<HatchPattern.Line>(lines) };
        }

        [Fact]
        public void 同结构只差角度时给出旋转差()
        {
            HatchPattern source = Pattern(Line(30, 300));
            HatchPattern target = Pattern(Line(45, 3.175));

            double delta;
            Assert.True(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
            Assert.Equal(-15.0, delta * 180.0 / Math.PI, 3);
        }

        [Fact]
        public void 完全相同就是零旋转()
        {
            HatchPattern source = Pattern(Line(45, 225));
            HatchPattern target = Pattern(Line(45, 3.175));

            double delta;
            Assert.True(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
            Assert.Equal(0.0, delta, 6);
        }

        [Fact]
        public void 线族数不同就不算相同()
        {
            HatchPattern source = Pattern(Line(45, 450), Line(135, 450));   // 交叉
            HatchPattern target = Pattern(Line(45, 3.175));                 // 单线族

            double delta;
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
        }

        [Fact]
        public void 虚线结构不同就不算相同()
        {
            HatchPattern source = Pattern(Line(45, 225, dashes: 2));        // 虚线
            HatchPattern target = Pattern(Line(45, 3.175));                 // 实线

            double delta;
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
        }

        [Fact]
        public void 交叉图案整体旋转也能识别()
        {
            HatchPattern source = Pattern(Line(60, 300), Line(150, 300));
            HatchPattern target = Pattern(Line(45, 3.175), Line(135, 3.175));

            double delta;
            Assert.True(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
            Assert.Equal(15.0, delta * 180.0 / Math.PI, 3);
        }

        [Fact]
        public void 相对角度不吻合就不算相同()
        {
            // 两个都是 2 族，但一族夹角不同（60° vs 90°），不是同一个图案
            HatchPattern source = Pattern(Line(45, 300), Line(105, 300));
            HatchPattern target = Pattern(Line(45, 3.175), Line(135, 3.175));

            double delta;
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));
        }

        [Fact]
        public void 空图案不算相同()
        {
            double delta;
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(null, Pattern(Line(45, 1)), out delta));
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(Pattern(Line(45, 1)), new HatchPattern("X"), out delta));
        }

        /// <summary>
        /// 交叉图案（45°/135°）的"主方向"要稳定取 45°，不能因为线在数组里的先后而变。
        /// </summary>
        [Fact]
        public void 主方向在垂距相同时取较小角度()
        {
            Assert.Equal(45, HatchPatternMetrics.DominantAngleDegrees(Pattern(Line(45, 300), Line(135, 300))), 3);
            Assert.Equal(45, HatchPatternMetrics.DominantAngleDegrees(Pattern(Line(135, 300), Line(45, 300))), 3);
        }

        /// <summary>
        /// 线族数不同时整体对齐没有意义，但主方向仍要对齐——
        /// 交叉图案换成单线族图案后，方向不能跟着目标图案跑到 0°。
        /// </summary>
        [Fact]
        public void 结构不同时按主方向对齐()
        {
            HatchPattern source = Pattern(Line(45, 3), Line(135, 3));   // 交叉 45/135
            HatchPattern target = Pattern(Line(0, 3.175));              // 目标自带 0°

            double delta;
            Assert.False(HatchPatternMetrics.TryGetOrientationDelta(source, target, out delta));

            Assert.True(HatchPatternMetrics.TryGetDominantDelta(source, target, out delta));
            Assert.Equal(45.0, delta * 180.0 / Math.PI, 3);
        }

        [Fact]
        public void 主方向已一致时旋转为零()
        {
            HatchPattern source = Pattern(Line(45, 3), Line(135, 3));
            HatchPattern target = Pattern(Line(45, 3.175));

            double delta;
            Assert.True(HatchPatternMetrics.TryGetDominantDelta(source, target, out delta));
            Assert.Equal(0.0, delta, 6);
        }

        [Fact]
        public void 主方向对齐也做等价角归一化()
        {
            // 源 170°、目标 10°：差值 160° ≡ -20°
            HatchPattern source = Pattern(Line(170, 3));
            HatchPattern target = Pattern(Line(10, 3.175));

            double delta;
            Assert.True(HatchPatternMetrics.TryGetDominantDelta(source, target, out delta));
            Assert.Equal(-20.0, delta * 180.0 / Math.PI, 3);
        }

        [Fact]
        public void 线距取最小非零垂距()
        {
            // 多线族：一族 300、一族 100 → 取更紧的 100
            HatchPattern pattern = Pattern(Line(45, 300), Line(135, 100));

            Assert.Equal(100, HatchPatternMetrics.ReferenceSpacing(pattern).Value, 3);
        }

        // ------------------------------------------------------------ 双线缝宽

        /// <summary>
        /// 同向双线族：缝宽是两族之间的垂直距离，与各族自己的线距无关。
        ///
        /// 这个值是用来解释"线距没变、看起来却更挤"的——JIS_LC_8A 的缝/距比是 1:11，
        /// 2_SECONDARY_ESCAPE_ROUTE 是 1:6，线距对齐后缝必然不一样宽。
        /// </summary>
        [Fact]
        public void 双线缝宽按族间垂直距离算()
        {
            double rad = 45 * Math.PI / 180.0;
            double nx = -Math.Sin(rad);
            double ny = Math.Cos(rad);

            HatchPattern.Line first = Line(45, 3);
            HatchPattern.Line second = Line(45, 3);

            // 第二族沿法向平移 0.5（这就是"缝"）
            second.BasePoint = new XY(nx * 0.5, ny * 0.5);

            double? gap = HatchPatternMetrics.PairGap(Pattern(first, second));

            Assert.True(gap.HasValue);
            Assert.Equal(0.5, gap.Value, 4);
        }

        [Fact]
        public void 单线族没有缝宽()
        {
            Assert.Null(HatchPatternMetrics.PairGap(Pattern(Line(45, 3))));
            Assert.Null(HatchPatternMetrics.PairGap(null));
        }

        /// <summary>交叉图案各族方向不同，"族间垂直间隙"没有意义，不能硬算一个数出来误导用户。</summary>
        [Fact]
        public void 交叉图案不算缝宽()
        {
            Assert.Null(HatchPatternMetrics.PairGap(Pattern(Line(45, 3), Line(135, 3))));
        }

        /// <summary>缝宽与线距是两个独立量：线距 3 的图案可以缝宽 0.5，也可以缝宽 0.27。</summary>
        [Fact]
        public void 缝宽与线距互不影响()
        {
            double rad = 45 * Math.PI / 180.0;
            double nx = -Math.Sin(rad);
            double ny = Math.Cos(rad);

            HatchPattern.Line second = Line(45, 3);
            second.BasePoint = new XY(nx * 0.272, ny * 0.272);

            HatchPattern pattern = Pattern(Line(45, 3), second);

            Assert.Equal(3, HatchPatternMetrics.ReferenceSpacing(pattern).Value, 4);
            Assert.Equal(0.272, HatchPatternMetrics.PairGap(pattern).Value, 4);
        }
    }
}
