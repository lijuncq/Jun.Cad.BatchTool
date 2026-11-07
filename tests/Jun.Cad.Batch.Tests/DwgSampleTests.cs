using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 真实海工模块布置图上的集成测试。
    ///
    /// 这些用例**不启动 AutoCAD**：读写、遍历、规则执行全部走 ACadSharp。
    /// 样图目录不存在时整组跳过（见 <see cref="SampleDwgFactAttribute"/>），不会静默通过。
    /// </summary>
    public class DwgSampleTests
    {
        private readonly ITestOutputHelper _output;

        public DwgSampleTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> FirstSamples
        {
            get
            {
                foreach (string file in TestPaths.SampleDwgFiles().Take(3))
                {
                    yield return new object[] { file };
                }
            }
        }

        private static BatchLogger SilentLogger()
        {
            return new BatchLogger(TextWriter.Null, null, false);
        }

        [SampleDwgTheory]
        [MemberData(nameof(FirstSamples))]
        public void 样图能读入且有实体(string file)
        {
            using (CadFileSession session = CadFileSession.Open(file, null))
            {
                Assert.NotNull(session.Document);
                Assert.True(session.Document.ModelSpace.Entities.Count > 0, "模型空间没有实体");

                _output.WriteLine("{0}: 版本 {1}，模型空间 {2} 个实体",
                    Path.GetFileName(file),
                    CadFileSession.DescribeVersion(session.SourceVersion),
                    session.Document.ModelSpace.Entities.Count);
            }
        }

        [SampleDwgTheory]
        [MemberData(nameof(FirstSamples))]
        public void 写出后回读实体数不变(string file)
        {
            string dir = TestPaths.NewTempDirectory("roundtrip");
            string target = Path.Combine(dir, "out.dwg");

            int before;
            try
            {
                using (CadFileSession session = CadFileSession.Open(file, null))
                {
                    before = session.Document.ModelSpace.Entities.Count;
                    session.Save(target, false);
                }

                using (CadFileSession again = CadFileSession.Open(target, null))
                {
                    int after = again.Document.ModelSpace.Entities.Count;

                    // 只断言"没丢对象"。版本可能被提升（源版本不可安全写出时），那是既定策略。
                    Assert.Equal(before, after);
                }
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        /// <summary>
        /// 在真实图纸上跑一条规则，验证引擎的端到端链路与计数自洽性。
        /// </summary>
        [SampleDwgFact]
        public void 改层规则在真实图上跑通且计数自洽()
        {
            var config = new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig
                    {
                        Name = "LINE 归层",
                        Type = "EntityLayerChange",
                        Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } },
                        Action = new RuleAction { TargetLayer = "TEST-LAYER", CreateIfMissing = true }
                    }
                }
            };

            using (CadFileSession session = CadFileSession.Open(TestPaths.FirstSample(), null))
            using (BatchLogger log = SilentLogger())
            {
                var runner = new BatchRunner(config, null, log, new ResourceLocator());
                BatchRunSummary summary = runner.Run(session.Document);

                Assert.Single(summary.Results);
                RuleExecutionResult result = summary.Results[0];

                Assert.False(result.Aborted, result.AbortReason);
                Assert.True(result.Matched > 0, "这张图上应该至少有直线");
                Assert.Equal(0, result.CountGap);

                _output.WriteLine("匹配 {0}，改动 {1}，跳过 {2}",
                    result.Matched, result.Changed, result.Skipped);
            }
        }

        /// <summary>
        /// 预览与执行必须走同一段代码：预览模式下不写盘，但统计一致。
        /// </summary>
        [SampleDwgFact]
        public void 预览模式不写盘且统计与执行一致()
        {
            string dir = TestPaths.NewTempDirectory("preview");
            string target = Path.Combine(dir, "out.dwg");

            var config = new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig
                    {
                        Name = "LINE 归层",
                        Type = "EntityLayerChange",
                        Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } },
                        Action = new RuleAction { TargetLayer = "TEST-LAYER", CreateIfMissing = true }
                    }
                }
            };

            try
            {
                int previewChanged;

                using (CadFileSession session = CadFileSession.Open(TestPaths.FirstSample(), null))
                using (BatchLogger log = SilentLogger())
                {
                    var sink = new Engine.Preview.PreviewSink();
                    var runner = new BatchRunner(config, null, log, new ResourceLocator(), sink);
                    BatchRunSummary summary = runner.Run(session.Document);

                    previewChanged = summary.TotalChanged;
                    Assert.True(sink.Records.Count > 0, "预览应该有记录");
                }

                Assert.False(File.Exists(target), "预览模式不应写盘");

                using (CadFileSession session = CadFileSession.Open(TestPaths.FirstSample(), null))
                using (BatchLogger log = SilentLogger())
                {
                    var runner = new BatchRunner(config, null, log, new ResourceLocator());
                    BatchRunSummary summary = runner.Run(session.Document);
                    session.Save(target, false);

                    Assert.Equal(previewChanged, summary.TotalChanged);
                }
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        /// <summary>
        /// 只读检查类规则在真实图上不应改变任何对象。
        /// </summary>
        [SampleDwgFact]
        public void 标注审计只读不改图()
        {
            var config = new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig
                    {
                        Name = "标注审计",
                        Type = "DimTextAudit",
                        Filter = new RuleFilter { Scope = "ModelOnly" },
                        Action = new RuleAction()
                    }
                }
            };

            using (CadFileSession session = CadFileSession.Open(TestPaths.FirstSample(), null))
            using (BatchLogger log = SilentLogger())
            {
                int before = session.Document.ModelSpace.Entities.Count;

                var runner = new BatchRunner(config, null, log, new ResourceLocator());
                BatchRunSummary summary = runner.Run(session.Document);

                Assert.Equal(before, session.Document.ModelSpace.Entities.Count);
                Assert.Equal(0, summary.TotalChanged);
            }
        }

        /// <summary>
        /// R04 指出的窄缺口：此前没有任何用例把 TextMask 跑在**真实 DWG**上、写盘、再回读，
        /// 断言遮罩状态仍然在。库层单体 round-trip 不能替代这条链路（宿主渲染另见 VERIFY.md）。
        /// </summary>
        [SampleDwgFact]
        public void TextMask在真实图上写盘回读后遮罩仍在()
        {
            string dir = TestPaths.NewTempDirectory("textmask-roundtrip");
            string target = Path.Combine(dir, "out.dwg");

            var config = new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig
                    {
                        Name = "文字遮罩",
                        Type = "TextMask",
                        Filter = new RuleFilter { Scope = "ModelOnly" },
                        Action = new RuleAction
                        {
                            MaskMode = "Auto",
                            MaskLayer = "_MASK",
                            MaskOnlyWhenOccluded = false,
                            MaskInsideBlocks = true,
                            MaskRebuildText = true,
                            MaskSendHatchToBack = false,
                            DimensionTextMode = "All",
                            DimensionMaskMode = "Native",
                            MaxMasksPerDrawing = 0
                        }
                    }
                }
            };

            try
            {
                int before;
                int changed;

                using (CadFileSession session = CadFileSession.Open(TestPaths.FirstSample(), null))
                using (BatchLogger log = SilentLogger())
                {
                    before = CountMaskIndicators(session.Document);

                    var runner = new BatchRunner(config, null, log, new ResourceLocator());
                    BatchRunSummary summary = runner.Run(session.Document);
                    changed = summary.TotalChanged;

                    Assert.True(changed > 0, "真实样图上应至少有文字可加遮罩");
                    Assert.True(CountMaskIndicators(session.Document) > before,
                        "规则执行后内存模型里就应能看到新增遮罩");
                    session.Save(target, false);
                }

                Assert.True(File.Exists(target), "TextMask 结果应已写盘");

                using (CadFileSession back = CadFileSession.Open(target, null))
                {
                    int after = CountMaskIndicators(back.Document);
                    Assert.True(after > before,
                        string.Format("回读后遮罩应仍在：写盘前 {0} → 回读 {1}（本次改动 {2}）",
                            before, after, changed));

                    // 原生背景必须是 bit0+bit1 成套；只写 bit1（90=2）会被 AutoCAD 判为没有遮罩。
                    Assert.Contains(AllEntities(back.Document).OfType<MText>(),
                        m => (m.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0
                          && (m.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) != 0);

                    _output.WriteLine("遮罩计数 {0} → {1}（规则改动 {2}）", before, after, changed);
                }
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        /// <summary>模型空间 + 全部块定义（含块内文字），每个块记录只算一次。</summary>
        private static List<Entity> AllEntities(CadDocument doc)
        {
            var blocks = new List<BlockRecord>();
            if (doc.ModelSpace != null)
            {
                blocks.Add(doc.ModelSpace);
            }

            foreach (BlockRecord block in doc.BlockRecords)
            {
                bool duplicate = false;
                foreach (BlockRecord known in blocks)
                {
                    if (ReferenceEquals(known, block))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate && block != null)
                {
                    blocks.Add(block);
                }
            }

            var entities = new List<Entity>();
            foreach (BlockRecord block in blocks)
            {
                entities.AddRange(block.Entities);
            }

            return entities;
        }

        /// <summary>本规则会产出的三类遮罩各算一处，用于写盘前后的粗计数。</summary>
        private static int CountMaskIndicators(CadDocument doc)
        {
            int count = 0;
            foreach (Entity entity in AllEntities(doc))
            {
                if (entity is Wipeout)
                {
                    count++;
                    continue;
                }

                var mtext = entity as MText;
                if (mtext != null)
                {
                    if ((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0)
                    {
                        count++;
                    }

                    continue;
                }

                var dimension = entity as Dimension;
                if (dimension != null && dimension.HasStyleOverride)
                {
                    try
                    {
                        DxfClassMap map = dimension.GetStyleOverrideMap();
                        if (map != null && map.DxfProperties.ContainsKey(69))
                        {
                            count++;
                        }
                    }
                    catch
                    {
                        // 真实图纸里可能有宿主私有的 DSTYLE 记录，解析不了就不计入
                    }
                }
            }

            return count;
        }
    }
}
