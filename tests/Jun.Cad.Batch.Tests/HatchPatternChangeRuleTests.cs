using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Rules;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>找不到 acadiso.pat 时在发现阶段跳过的 [Fact]（不是静默通过）。</summary>
    public sealed class PatternFileFactAttribute : FactAttribute
    {
        public PatternFileFactAttribute()
        {
            if (HatchPatternChangeRuleTests.FindPatternFile() == null)
            {
                Skip = "找不到 acadiso.pat（需要先构建 CLI，让 Resources 落到输出目录）";
            }
        }
    }
}

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 换图案时的比例决定。
    ///
    /// 这里只测一条最容易错的路径：**纯色填充换成图案填充**。
    /// 纯色填充的 `PatternScale` 是 0，如果"没填比例就沿用原值"，新图案会以 0 比例绘制——
    /// 图案线间距被压成 0，AutoCAD 里看起来就是一整块实色（用户反馈"比例和颜色都不对"）。
    ///
    /// 需要 acadiso.pat 才能跑（规则从图案库取定义）；找不到时整组跳过。
    /// </summary>
    public class HatchPatternChangeRuleTests
    {
        /// <summary>在仓库里找一份 acadiso.pat（CLI 的输出目录里就有）。</summary>
        internal static string FindPatternFile()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(dir, "Resources", "acadiso.pat"),
                    Path.Combine(dir, "src", "Jun.Cad.Batch.Cli", "bin", "Debug", "net8.0", "Resources", "acadiso.pat"),
                    Path.Combine(dir, "src", "Jun.Cad.Batch.Cli", "bin", "Release", "net8.0", "Resources", "acadiso.pat")
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static bool HasPatternFile
        {
            get { return FindPatternFile() != null; }
        }

        /// <summary>造一个纯色填充：`IsSolid = true`、`PatternScale = 0`（DWG 里就是这样）。</summary>
        private static Hatch NewSolidHatch()
        {
            var hatch = new Hatch { IsSolid = true, PatternScale = 0 };
            hatch.Color = new Color((short)9);
            hatch.Layer = new ACadSharp.Tables.Layer("0");

            // 边界本身跟被测逻辑无关（规则只改图案，不动 Paths），这里造一个最小矩形
            var path = new Hatch.BoundaryPath();
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(0, 0), End = new XY(100, 0) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(100, 0), End = new XY(100, 100) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(100, 100), End = new XY(0, 100) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(0, 100), End = new XY(0, 0) });
            hatch.Paths.Add(path);

            return hatch;
        }

        private static BatchConfig ConfigFor(string targetPattern, double? scale = null, bool? matchSpacing = null)
        {
            return new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig
                    {
                        Name = "换图案",
                        Type = "HatchPatternChange",
                        Filter = new RuleFilter { EntityTypes = new List<string> { "HATCH" } },
                        Action = new RuleAction
                        {
                            TargetPatternName = targetPattern,
                            PatternType = "PreDefined",
                            PatternScale = scale,
                            MatchSpacing = matchSpacing
                        }
                    }
                }
            };
        }

        private static RuleExecutionResult RunRule(Hatch hatch, BatchConfig config, string patternFile)
        {
            return RunRule(hatch, config, patternFile, null);
        }

        /// <summary>跑一条规则；<paramref name="logLines"/> 非空时把日志收进去供断言。</summary>
        private static RuleExecutionResult RunRule(Hatch hatch, BatchConfig config, string patternFile, List<string> logLines)
        {
            var doc = new CadDocument();
            doc.ModelSpace.Entities.Add(hatch);

            var resources = new ResourceLocator { PatternFile = patternFile };
            TextWriter console = logLines == null ? TextWriter.Null : (TextWriter)new ListWriter(logLines);

            using (var log = new BatchLogger(console, null, true))
            {
                var ctx = new RuleContext(doc, config, config.Rules[0], log, resources);
                var result = new RuleExecutionResult(config.Rules[0].Name, config.Rules[0].Type);

                new HatchPatternChangeRule().Execute(ctx, result);
                return result;
            }
        }

        /// <summary>造一个"双线"图案填充：两族同向、给定线距与缝宽；图案线里已经是最终几何。</summary>
        private static Hatch NewDoubleLineHatch(string patternName, double spacing, double gap, double patternScale)
        {
            double rad = 45 * Math.PI / 180.0;
            double nx = -Math.Sin(rad);
            double ny = Math.Cos(rad);

            var first = new HatchPattern.Line
            {
                Angle = rad,
                BasePoint = new XY(0, 0),
                Offset = new XY(nx * spacing, ny * spacing),
                DashLengths = new List<double>()
            };

            var second = new HatchPattern.Line
            {
                Angle = rad,
                BasePoint = new XY(nx * gap, ny * gap),
                Offset = new XY(nx * spacing, ny * spacing),
                DashLengths = new List<double>()
            };

            var hatch = new Hatch { IsSolid = false, PatternScale = patternScale };
            hatch.Color = new Color((short)9);
            hatch.Layer = new ACadSharp.Tables.Layer("0");
            hatch.Pattern = new HatchPattern(patternName)
            {
                Description = "双线",
                Lines = new List<HatchPattern.Line> { first, second }
            };

            var path = new Hatch.BoundaryPath();
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(0, 0), End = new XY(100, 0) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(100, 0), End = new XY(100, 100) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(100, 100), End = new XY(0, 100) });
            path.Edges.Add(new Hatch.BoundaryPath.Line { Start = new XY(0, 100), End = new XY(0, 0) });
            hatch.Paths.Add(path);

            return hatch;
        }

        [PatternFileFact]
        public void 纯色填充换图案时比例取1而不是沿用0()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewSolidHatch();
            RuleExecutionResult result = RunRule(hatch, ConfigFor("ANSI31"), patternFile);

            Assert.Equal(1, result.Changed);
            Assert.False(hatch.IsSolid);
            Assert.Equal("ANSI31", hatch.Pattern.Name);
            Assert.True(hatch.PatternScale > 0, "比例必须为正，否则图案会退化成实色块");
            Assert.Equal(1.0, hatch.PatternScale, 3);
        }

        [PatternFileFact]
        public void 纯色填充换图案时颜色不变()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewSolidHatch();
            short before = hatch.Color.Index;

            RunRule(hatch, ConfigFor("ANSI31"), patternFile);

            Assert.Equal(before, hatch.Color.Index);
        }

        [PatternFileFact]
        public void 显式指定的比例优先于保持线距()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewSolidHatch();
            RunRule(hatch, ConfigFor("ANSI31", 5.0), patternFile);

            Assert.Equal(5.0, hatch.PatternScale, 3);
        }

        /// <summary>
        /// 关掉 MatchSpacing 时也不能把纯色填充的 0 带过来——0 是非法比例。
        /// </summary>
        [PatternFileFact]
        public void 关掉保持线距时比例也不会是0()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewSolidHatch();
            RunRule(hatch, ConfigFor("ANSI31", null, false), patternFile);

            Assert.True(hatch.PatternScale > 0, "比例必须为正");
        }

        /// <summary>
        /// 双线图案换双线图案：线距保持不变，比例按"目标基准线距"反算。
        ///
        /// 这是用户实测场景（2_SECONDARY_ESCAPE_ROUTE → JIS_LC_8A）：
        /// 源线距 3、JIS_LC_8A 基准线距 7.8，于是比例必然是 3 ÷ 7.8 = 0.384615。
        /// 用户看到"没填比例怎么比例变了"，这里把这个换算关系钉死——
        /// 比例变化是**结果**，不是算错；反过来如果比例是 1，线距就会变成 7.8。
        /// </summary>
        [PatternFileFact]
        public void 换双线图案时线距保持而比例反算()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewDoubleLineHatch("2_SECONDARY_ESCAPE_ROUTE", spacing: 3, gap: 0.5, patternScale: 1);

            RunRule(hatch, ConfigFor("JIS_LC_8A"), patternFile);

            Assert.Equal("JIS_LC_8A", hatch.Pattern.Name);

            // 比例 = 原线距 3 ÷ JIS_LC_8A 基准线距 7.8
            Assert.Equal(3.0 / 7.8, hatch.PatternScale, 4);

            // 换完之后图案线的实际垂距仍是 3 —— "疏密不变"这条承诺的落点
            double? spacing = HatchPatternMetrics.ReferenceSpacing(hatch.Pattern);
            Assert.True(spacing.HasValue);
            Assert.Equal(3.0, spacing.Value, 4);
        }

        /// <summary>
        /// 关掉「保持线距」时，**沿用原比例**——比例字段与图案线必须描述同一件事。
        ///
        /// 真实事故：配置里 `MatchSpacing=false`（早先试验时选的「否」）被留了下来。
        /// 源图是"基准线距 3、比例 75"的自定义图案（图案线已缩放成 225），
        /// 换图案后组码 41 还是 75，图案线却是 JIS_LC_8A 未缩放的 7.8。
        /// AutoCAD 按图案线绘制 → 实际线距 7.8，比原来的 225 密了 29 倍，
        /// 而属性面板显示比例 75。看哪个数都对不上。
        /// </summary>
        [PatternFileFact]
        public void 关掉保持线距时沿用原比例()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewDoubleLineHatch("2_SECONDARY_ESCAPE_ROUTE", spacing: 225, gap: 37.5, patternScale: 75);

            RunRule(hatch, ConfigFor("JIS_LC_8A", null, false), patternFile);

            Assert.Equal("JIS_LC_8A", hatch.Pattern.Name);

            // 比例沿用 75，图案线按 .pat × 75 缩放（7.8 × 75 = 585）
            Assert.Equal(75.0, hatch.PatternScale, 4);

            double? spacing = HatchPatternMetrics.ReferenceSpacing(hatch.Pattern);
            Assert.True(spacing.HasValue);
            Assert.Equal(7.8 * 75, spacing.Value, 2);
        }

        /// <summary>关掉保持线距、且原比例本来就是 1 时，不缩放（线距 = 目标图案基准线距）。</summary>
        [PatternFileFact]
        public void 关掉保持线距时原比例为1则按基准线距画()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewDoubleLineHatch("2_SECONDARY_ESCAPE_ROUTE", spacing: 3, gap: 0.5, patternScale: 1);

            RunRule(hatch, ConfigFor("JIS_LC_8A", null, false), patternFile);

            Assert.Equal(1.0, hatch.PatternScale, 6);
            Assert.Equal(7.8, HatchPatternMetrics.ReferenceSpacing(hatch.Pattern).Value, 4);
        }

        /// <summary>
        /// 关掉保持线距时线距变化是**静默**的——必须告警，否则用户只能怀疑工具算错。
        /// 这条锁住"告警里带得上两个线距数字"。
        /// </summary>
        [PatternFileFact]
        public void 关掉保持线距时告警说明线距变化()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewDoubleLineHatch("2_SECONDARY_ESCAPE_ROUTE", spacing: 3, gap: 0.5, patternScale: 1);

            var lines = new List<string>();
            RunRule(hatch, ConfigFor("JIS_LC_8A", null, false), patternFile, lines);

            Assert.Contains(lines, l => l.Contains("未开启「保持线距」"));
            Assert.Contains(lines, l => l.Contains("3") && l.Contains("7.8"));
        }

        /// <summary>开着保持线距时不该出现"线距会变"的告警。</summary>
        [PatternFileFact]
        public void 开启保持线距时不报线距变化()
        {
            string patternFile = FindPatternFile();

            Hatch hatch = NewDoubleLineHatch("2_SECONDARY_ESCAPE_ROUTE", spacing: 3, gap: 0.5, patternScale: 1);

            var lines = new List<string>();
            RunRule(hatch, ConfigFor("JIS_LC_8A"), patternFile, lines);

            Assert.DoesNotContain(lines, l => l.Contains("未开启「保持线距」"));
        }

        /// <summary>把日志行收进 List，供断言。</summary>
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
