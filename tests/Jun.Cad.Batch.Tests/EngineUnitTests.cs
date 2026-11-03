using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 实体判定的三个历史陷阱。
    ///
    /// 这三个都是**继承关系**造成的误判，靠读代码很难发现，只能靠用例锁住：
    ///   * `Arc` 继承 `Circle` → 按 CIRCLE 过滤会把 1417 个圆弧一起捞进来；
    ///   * `Wipeout` 继承 `RasterImage` → 遮挡框被报成 IMAGE；
    ///   * `TableEntity` 继承 `Insert` → 表格被当成普通块参照。
    /// </summary>
    public class EntityFilterTests
    {
        private static Circle NewCircle()
        {
            return new Circle { Center = new XYZ(0, 0, 0), Radius = 5 };
        }

        private static Arc NewArc()
        {
            return new Arc { Center = new XYZ(0, 0, 0), Radius = 5, StartAngle = 0, EndAngle = Math.PI };
        }

        [Fact]
        public void 圆弧不算圆()
        {
            Assert.True(EntityFilter.MatchesType(NewCircle(), "CIRCLE"));
            Assert.False(EntityFilter.MatchesType(NewArc(), "CIRCLE"));
        }

        [Fact]
        public void 圆弧仍然算圆弧()
        {
            Assert.True(EntityFilter.MatchesType(NewArc(), "ARC"));
        }

        [Fact]
        public void 遮挡框不算图片()
        {
            var wipeout = new Wipeout();

            Assert.True(EntityFilter.MatchesType(wipeout, "WIPEOUT"));
            Assert.False(EntityFilter.MatchesType(wipeout, "IMAGE"));
        }

        [Fact]
        public void 表格不算块参照()
        {
            var table = new TableEntity();

            Assert.True(EntityFilter.MatchesType(table, "ACAD_TABLE"));
            Assert.True(EntityFilter.MatchesType(table, "TABLE"));
            Assert.False(EntityFilter.MatchesType(table, "INSERT"));
        }

        [Fact]
        public void 属性定义不算普通文字()
        {
            var attdef = new AttributeDefinition();

            Assert.True(EntityFilter.MatchesType(attdef, "ATTDEF"));
            Assert.False(EntityFilter.MatchesType(attdef, "TEXT"));
        }

        [Fact]
        public void 多段线的三个子类都算POLYLINE()
        {
            Assert.True(EntityFilter.MatchesType(new Polyline2D(), "POLYLINE"));
            Assert.True(EntityFilter.MatchesType(new Polyline3D(), "POLYLINE"));
            Assert.True(EntityFilter.MatchesType(new PolyfaceMesh(), "POLYLINE"));
        }

        [Fact]
        public void 颜色索引过滤命中显式颜色()
        {
            Circle circle = NewCircle();
            circle.Color = new Color((short)1);

            EntityFilter filter = EntityFilter.Build(new RuleFilter { ColorIndex = 1 });
            Assert.True(filter.Matches(circle));

            EntityFilter other = EntityFilter.Build(new RuleFilter { ColorIndex = 3 });
            Assert.False(other.Matches(circle));
        }

        [Fact]
        public void 图层过滤大小写不敏感()
        {
            Circle circle = NewCircle();
            circle.Layer = new ACadSharp.Tables.Layer("PIPE");

            EntityFilter filter = EntityFilter.Build(new RuleFilter { LayerName = "pipe" });
            Assert.True(filter.Matches(circle));
        }

        /// <summary>
        /// 只配了空间/图框/区域时，`HasCondition` 必须为真——否则"只处理 TB-2025 图框里的对象"
        /// 会被护栏判成"没有任何过滤条件"并中止。
        /// </summary>
        [Fact]
        public void 空间条件计入HasCondition()
        {
            Assert.True(EntityFilter.Build(new RuleFilter { ContainerBlockPattern = "TB-*" }).HasCondition);
            Assert.True(EntityFilter.Build(new RuleFilter
            {
                RegionMode = "Rect",
                RegionPoints = new List<string> { "0,0", "10,10" }
            }).HasCondition);
            Assert.True(EntityFilter.Build(new RuleFilter
            {
                LayoutPatterns = new List<string> { "A1-*" }
            }).HasCondition);

            Assert.False(EntityFilter.Build(new RuleFilter()).HasCondition);
        }
    }

    /// <summary>二维几何谓词：区域过滤与遮罩相交都靠它。</summary>
    public class Geo2dTests
    {
        private static List<XY> Square()
        {
            return new List<XY>
            {
                new XY(0, 0), new XY(10, 0), new XY(10, 10), new XY(0, 10)
            };
        }

        [Fact]
        public void 点在矩形内()
        {
            Assert.True(Geo2d.Contains(Square(), 5, 5));
            Assert.False(Geo2d.Contains(Square(), 15, 5));
            Assert.False(Geo2d.Contains(Square(), -1, 5));
        }

        /// <summary>凹多边形：射线法必须能处理，不能用凸包近似。</summary>
        [Fact]
        public void 凹多边形的凹口不算在内部()
        {
            // U 形：缺口在 (3,3)-(7,7)
            var polygon = new List<XY>
            {
                new XY(0, 0), new XY(10, 0), new XY(10, 10),
                new XY(7, 10), new XY(7, 3), new XY(3, 3), new XY(3, 10), new XY(0, 10)
            };

            Assert.True(Geo2d.Contains(polygon, 1, 1));
            Assert.True(Geo2d.Contains(polygon, 5, 1));
            Assert.False(Geo2d.Contains(polygon, 5, 8));   // 凹口里
        }

        [Fact]
        public void 包围盒与多边形相交()
        {
            var box = new Bounds2d(9, 9, 20, 20);   // 与正方形右下角相交

            Assert.True(Geo2d.IntersectsBox(Square(), box));
            Assert.False(Geo2d.IntersectsBox(Square(), new Bounds2d(20, 20, 30, 30)));
        }

        [Fact]
        public void 自交多边形能被识别()
        {
            var bowtie = new List<XY>
            {
                new XY(0, 0), new XY(10, 10), new XY(10, 0), new XY(0, 10)
            };

            Assert.True(Geo2d.IsSelfIntersecting(bowtie));
            Assert.False(Geo2d.IsSelfIntersecting(Square()));
        }
    }

    /// <summary>文字/标注/遮罩的包围盒估算——库对这些返回退化点，只能自己算。</summary>
    public class TextBoundsTests
    {
        [Fact]
        public void 汉字按一个字高西文按零点六二()
        {
            Assert.Equal(10, TextBounds.MeasureWidth("中", 10), 3);
            Assert.Equal(20, TextBounds.MeasureWidth("中文", 10), 3);
            Assert.Equal(12.4, TextBounds.MeasureWidth("AB", 10), 3);
        }

        [Fact]
        public void 全角标点也算宽字符()
        {
            Assert.True(TextBounds.IsWide('，'));
            Assert.True(TextBounds.IsWide('中'));
            Assert.False(TextBounds.IsWide('A'));
        }

        /// <summary>
        /// 旋转 90° 的文字：AABB 的宽高应当互换。
        /// </summary>
        [Fact]
        public void 旋转矩形展开成轴对齐包络()
        {
            Bounds2d box = TextBounds.RotatedRect(new XYZ(0, 0, 0), 0, 0, 10, 2, Math.PI / 2);

            Assert.Equal(2, box.Width, 3);
            Assert.Equal(10, box.Height, 3);
        }

        /// <summary>
        /// Wipeout 的裁剪边界是像素坐标，世界范围必须通过图像尺寸及 U/V 向量展开。
        /// </summary>
        [Fact]
        public void 旋转遮罩的包围盒按像素坐标和UV向量展开()
        {
            var wipeout = new Wipeout
            {
                InsertPoint = new XYZ(0, 0, 0),
                Size = new XY(1, 1),
                UVector = new XYZ(0, 10, 0),
                VVector = new XYZ(-10, 0, 0)
            };

            wipeout.ClipBoundaryVertices.Add(new XY(-0.5, 0.5));
            wipeout.ClipBoundaryVertices.Add(new XY(0.5, 0.5));
            wipeout.ClipBoundaryVertices.Add(new XY(0.5, 0.1));
            wipeout.ClipBoundaryVertices.Add(new XY(-0.5, 0.1));

            Bounds2d box = TextBounds.ForWipeout(wipeout);

            // 局部矩形 10×4 旋转 90° 后：世界范围 X:-4..0, Y:0..10。
            Assert.Equal(-4, box.MinX, 3);
            Assert.Equal(0, box.MaxX, 3);
            Assert.Equal(0, box.MinY, 3);
            Assert.Equal(10, box.MaxY, 3);
        }

        [Fact]
        public void 新建文字的包围盒不是退化点()
        {
            var text = new TextEntity { Value = "TEST", Height = 2.5, InsertPoint = new XYZ(10, 20, 0) };

            Bounds2d box;
            Assert.True(TextBounds.TryGet(text, out box));
            Assert.True(box.Width > 0, "文字宽度不应为 0");
            Assert.True(box.Height > 0, "文字高度不应为 0");
        }

        [Fact]
        public void 表格没有包围盒()
        {
            Bounds2d box;
            Assert.False(TextBounds.TryGet(new TableEntity(), out box));
        }
    }

    /// <summary>规则执行结果的计数口径。</summary>
    public class RuleExecutionResultTests
    {
        [Fact]
        public void 自洽时差值为零()
        {
            var result = new RuleExecutionResult("r", "T");
            result.Hit();
            result.MarkChanged("x");
            result.Hit();
            result.MarkSkipped("锁定图层");

            Assert.Equal(0, result.CountGap);
        }

        [Fact]
        public void 漏报时差值为正()
        {
            var result = new RuleExecutionResult("r", "T");
            result.Hit();
            result.Hit();

            Assert.Equal(2, result.CountGap);
        }

        [Theory]
        [InlineData("锁定图层", "锁定图层")]
        [InlineData("#4A MTEXT：图层被锁定", "锁定图层")]
        [InlineData("块内实体不处理", "块内实体")]
        [InlineData("无遮挡", "无遮挡")]
        [InlineData("已是目标名", "已是目标状态")]
        [InlineData("替换后为空", "结果为空")]
        [InlineData("未匹配", "未匹配")]
        [InlineData("超过单图遮罩上限", "超出上限")]
        [InlineData("预演", "仅预演")]
        [InlineData("", "未说明")]
        public void 跳过原因能归到固定几类(string detail, string expected)
        {
            Assert.Equal(expected, RuleExecutionResult.ClassifySkipReason(detail));
        }

        [Fact]
        public void 跳过原因摘要按次数排序且最多四类()
        {
            var result = new RuleExecutionResult("r", "T");
            result.MarkSkipped("无遮挡");
            result.MarkSkipped("无遮挡");
            result.MarkSkipped("锁定图层");
            result.MarkSkipped("块内实体不处理");
            result.MarkSkipped("未匹配");
            result.MarkSkipped("已是目标名");

            string summary = result.DescribeSkipReasons();

            Assert.StartsWith("无遮挡 2", summary);
            Assert.Equal(4, summary.Split('/').Length);
        }
    }

    /// <summary>
    /// `$PSTYLEMODE` 归一化。
    ///
    /// ACadSharp 对这个变量**读写不对称**（AutoCAD 2025 内核实测）：
    /// 颜色相关（CTB）的图读出来是 8192，而写入只认语义值 0/1。
    /// 原样写回去，AutoCAD 会把图当成"命名（STB）"——打印对话框的打印样式表
    /// 下拉框就只列 STB，用户自建的 CTB 目录（哪怕已加进支持文件搜索路径）一个都不显示，
    /// 而画面上看不出任何异常。
    /// </summary>
    public class PlotStyleModeTests
    {
        [Theory]
        [InlineData((short)0, (short)0)]      // 命名（STB）：保持不变
        [InlineData((short)1, (short)1)]      // 已是语义值：保持不变
        [InlineData((short)8192, (short)1)]   // 颜色相关（CTB）在库里的原始值
        [InlineData((short)-1, (short)1)]     // 其它非零值：一律按 CTB
        public void PSTYLEMODE原始值折成语义值(short raw, short expected)
        {
            Assert.Equal(expected, CadFileSession.NormalizePlotStyleModeValue(raw));
        }
    }
}
