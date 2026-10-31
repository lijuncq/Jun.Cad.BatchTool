using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using CSMath;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 图形测量单位（<c>$MEASUREMENT</c>）修正。
    ///
    /// 背景（AutoCAD 2025 内核 accoreconsole 实测）：
    ///   * <c>$MEASUREMENT=0</c> 时 AutoCAD 按 <c>acad.pat</c> 解析图案名；
    ///   * <c>JIS_LC_8A</c> 只存在于 <c>acadiso.pat</c>，所以在 0 的图上
    ///     <c>(setvar "HPNAME" "JIS_LC_8A")</c> 报"无效的图案名称"——
    ///     填充画得出来，但属性面板的预定义列表里没有这个名字；
    ///   * 把 <c>$MEASUREMENT</c> 改成 1 后同一句立刻成功。
    /// 这组测试锁住"什么时候该改、什么时候不该改"。
    /// </summary>
    public class DrawingUnitsTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        private const string IsoLibrary =
            "*JISONLY, 只在公制库里\n" +
            "45,0,0,0,7.8\n" +
            "45,1,0,0,7.8\n" +
            "*ANSI31, 两边都有\n" +
            "45,0,0,0,3.175\n";

        private const string ImperialLibrary =
            "*ANSI31, 两边都有\n" +
            "45,0,0,0,3.175\n";

        private string WriteTemp(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "jun-pat-" + Guid.NewGuid().ToString("N") + ".pat");
            File.WriteAllText(path, content);
            _tempFiles.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (string path in _tempFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 临时文件删不掉不影响测试结论
                }
            }
        }

        private ResourceLocator NewLocator(bool withImperial)
        {
            return new ResourceLocator
            {
                PatternFile = WriteTemp(IsoLibrary),
                ImperialPatternFile = withImperial ? WriteTemp(ImperialLibrary) : "none"
            };
        }

        // ------------------------------------------------------------ 策略解析

        [Theory]
        [InlineData(null, MeasurementPolicy.Auto)]
        [InlineData("", MeasurementPolicy.Auto)]
        [InlineData("Auto", MeasurementPolicy.Auto)]
        [InlineData("auto", MeasurementPolicy.Auto)]
        [InlineData("Keep", MeasurementPolicy.Keep)]
        [InlineData("不改", MeasurementPolicy.Keep)]
        [InlineData("Metric", MeasurementPolicy.Metric)]
        [InlineData("公制", MeasurementPolicy.Metric)]
        [InlineData("English", MeasurementPolicy.English)]
        [InlineData("imperial", MeasurementPolicy.English)]
        [InlineData("乱七八糟", MeasurementPolicy.Auto)]
        public void 策略字符串解析(string text, MeasurementPolicy expected)
        {
            Assert.Equal(expected, DrawingUnits.ParsePolicy(text));
        }

        // ------------------------------------------------------------ 图案名归属

        [Fact]
        public void 只在公制库里的图案需要公制()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            Assert.True(locator.RequiresMetric("JISONLY"));
        }

        [Fact]
        public void 两份库都有的图案不需要公制()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            Assert.False(locator.RequiresMetric("ANSI31"));
        }

        [Fact]
        public void 图案名大小写不敏感()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            Assert.True(locator.RequiresMetric("jisonly"));
            Assert.False(locator.RequiresMetric("ansi31"));
        }

        [Fact]
        public void 库里没有的图案名不去猜()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            Assert.False(locator.RequiresMetric("完全不存在的图案"));
        }

        [Fact]
        public void 没有英制库时保守认为需要公制()
        {
            // 没加载到 acad.pat 就分不清某个名字英制图纸认不认得。
            // 本工具写出去的图案都来自 acadiso.pat，只有公制图纸能保证解析得出来。
            ResourceLocator locator = NewLocator(withImperial: false);
            Assert.False(locator.HasImperialPatternLibrary);
            Assert.True(locator.RequiresMetric("ANSI31"));
        }

        // ------------------------------------------------------------ 修正动作

        private static Hatch NewPatternHatch(string patternName)
        {
            var hatch = new Hatch { IsSolid = false, PatternScale = 1 };
            hatch.Pattern = new HatchPattern(patternName)
            {
                Description = "test",
                Lines = new List<HatchPattern.Line>
                {
                    new HatchPattern.Line
                    {
                        Angle = Math.PI / 4,
                        BasePoint = new XY(0, 0),
                        Offset = new XY(0, 7.8),
                        DashLengths = new List<double>()
                    }
                }
            };
            hatch.Layer = new ACadSharp.Tables.Layer("0");
            return hatch;
        }

        private static CadDocument NewDoc(MeasurementUnits units, string patternName)
        {
            var doc = new CadDocument();
            doc.Header.MeasurementUnits = units;
            doc.ModelSpace.Entities.Add(NewPatternHatch(patternName));
            return doc;
        }

        private static BatchLogger NullLog(out List<string> lines)
        {
            var captured = new List<string>();
            lines = captured;
            return new BatchLogger(new ListWriter(captured), null, true);
        }

        [Fact]
        public void 自动模式遇到只在公制库里的图案会改成公制()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.English, "JISONLY");

            List<string> lines;
            BatchLogger log = NullLog(out lines);
            using (log)
            {
                string note = DrawingUnits.Apply(doc, locator, "Auto", false, log);

                Assert.Equal(MeasurementUnits.Metric, doc.Header.MeasurementUnits);
                Assert.Contains("JISONLY", note);
                Assert.Contains(lines, l => l.Contains("$MEASUREMENT"));
            }
        }

        [Fact]
        public void 自动模式不会为了普通图案动测量单位()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.English, "ANSI31");

            List<string> lines;
            using (BatchLogger log = NullLog(out lines))
            {
                string note = DrawingUnits.Apply(doc, locator, "Auto", false, log);

                Assert.Equal(string.Empty, note);
                Assert.Equal(MeasurementUnits.English, doc.Header.MeasurementUnits);
                Assert.DoesNotContain(lines, l => l.Contains("$MEASUREMENT"));
            }
        }

        [Fact]
        public void 已经是公制时不做任何事()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.Metric, "JISONLY");

            List<string> lines;
            using (BatchLogger log = NullLog(out lines))
            {
                Assert.Equal(string.Empty, DrawingUnits.Apply(doc, locator, "Auto", false, log));
                Assert.Equal(MeasurementUnits.Metric, doc.Header.MeasurementUnits);
            }
        }

        [Fact]
        public void 保持模式不改但会告警说明后果()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.English, "JISONLY");

            List<string> lines;
            using (BatchLogger log = NullLog(out lines))
            {
                Assert.Equal(string.Empty, DrawingUnits.Apply(doc, locator, "Keep", false, log));
                Assert.Equal(MeasurementUnits.English, doc.Header.MeasurementUnits);
                Assert.Contains(lines, l => l.Contains("JISONLY") && l.Contains("acadiso.pat"));
            }
        }

        [Fact]
        public void 强制公制()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.English, "ANSI31");

            using (BatchLogger log = NullLog(out _))
            {
                DrawingUnits.Apply(doc, locator, "Metric", false, log);
                Assert.Equal(MeasurementUnits.Metric, doc.Header.MeasurementUnits);
            }
        }

        [Fact]
        public void 强制英制()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.Metric, "ANSI31");

            using (BatchLogger log = NullLog(out _))
            {
                DrawingUnits.Apply(doc, locator, "English", false, log);
                Assert.Equal(MeasurementUnits.English, doc.Header.MeasurementUnits);
            }
        }

        [Fact]
        public void 预演模式措辞标明未写盘()
        {
            ResourceLocator locator = NewLocator(withImperial: true);
            CadDocument doc = NewDoc(MeasurementUnits.English, "JISONLY");

            using (BatchLogger log = NullLog(out _))
            {
                string note = DrawingUnits.Apply(doc, locator, "Auto", true, log);
                Assert.Contains("预演", note);
            }
        }

        [Fact]
        public void 块定义内部的填充也算数()
        {
            // 块里的填充一样要能按名字解析。注意必须真的插入块参照——
            // 没人引用的块定义不参与显示，扫描器也不会进去。
            ResourceLocator locator = NewLocator(withImperial: true);

            var doc = new CadDocument();
            doc.Header.MeasurementUnits = MeasurementUnits.English;

            var block = new ACadSharp.Tables.BlockRecord("HATCH_BLOCK");
            doc.BlockRecords.Add(block);
            block.Entities.Add(NewPatternHatch("JISONLY"));
            doc.ModelSpace.Entities.Add(new Insert(block));

            using (BatchLogger log = NullLog(out _))
            {
                DrawingUnits.Apply(doc, locator, "Auto", false, log);
                Assert.Equal(MeasurementUnits.Metric, doc.Header.MeasurementUnits);
            }
        }

        /// <summary>把日志写进 List，供断言。</summary>
        private sealed class ListWriter : TextWriter
        {
            private readonly List<string> _lines;

            public ListWriter(List<string> lines)
            {
                _lines = lines;
            }

            public override System.Text.Encoding Encoding
            {
                get { return System.Text.Encoding.UTF8; }
            }

            public override void WriteLine(string value)
            {
                _lines.Add(value ?? string.Empty);
            }
        }
    }
}
