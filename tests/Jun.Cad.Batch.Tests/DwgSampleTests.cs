using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    }
}
