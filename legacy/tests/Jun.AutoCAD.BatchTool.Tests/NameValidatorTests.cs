using System.Collections.Generic;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
    public class NameValidatorTests
    {
        [Fact]
        public void 查不了时不产生任何提示()
        {
            var lookup = new FakeNameLookup("a.dwg") { AlwaysUnknown = true };
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { SourceBlockName = "NOPE" },
                Action = new RuleAction { TargetLayer = "NOLAYER" }
            };

            Assert.Empty(NameValidator.Validate(rule, lookup));
        }

        [Fact]
        public void 过滤侧名称不存在时给黄色并带最近匹配()
        {
            var lookup = new FakeNameLookup("a.dwg").With(NameKind.Layer, "TK-FRAME");
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { LayerName = "TK-FRAM" },
                Action = new RuleAction { TargetLayer = "X" }
            };

            List<FieldIssue> issues = NameValidator.Validate(rule, lookup);

            Assert.Single(issues);
            Assert.Equal(IssueSeverity.Warning, issues[0].Severity);
            Assert.Equal("Filter.LayerName", issues[0].Path);
            Assert.Contains("TK-FRAME", issues[0].Message);
            Assert.Contains("a.dwg", issues[0].Message);
        }

        [Fact]
        public void 目标图层开启自动创建时豁免()
        {
            var lookup = new FakeNameLookup("a.dwg");
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { SourceBlockName = "B" },
                Action = new RuleAction { TargetLayer = "BRAND-NEW", CreateIfMissing = true }
            };
            lookup.With(NameKind.Block, "B");

            Assert.Empty(NameValidator.Validate(rule, lookup));
        }

        [Fact]
        public void 目标图层关闭自动创建时才检查()
        {
            var lookup = new FakeNameLookup("a.dwg").With(NameKind.Block, "B");
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { SourceBlockName = "B" },
                Action = new RuleAction { TargetLayer = "BRAND-NEW", CreateIfMissing = false }
            };

            List<FieldIssue> issues = NameValidator.Validate(rule, lookup);

            Assert.Single(issues);
            Assert.Equal("Action.TargetLayer", issues[0].Path);
        }

        [Fact]
        public void 目标块名不豁免()
        {
            var lookup = new FakeNameLookup("a.dwg").With(NameKind.Block, "SRC");
            var rule = new RuleConfig
            {
                Type = "BlockReplace",
                Filter = new RuleFilter { SourceBlockName = "SRC" },
                Action = new RuleAction { TargetBlockName = "MISSING" }
            };

            List<FieldIssue> issues = NameValidator.Validate(rule, lookup);

            Assert.Single(issues);
            Assert.Equal("Action.TargetBlockName", issues[0].Path);
        }

        [Fact]
        public void 目标文字样式豁免()
        {
            var lookup = new FakeNameLookup("a.dwg");
            var rule = new RuleConfig
            {
                Type = "TextFontChange",
                Action = new RuleAction { TargetTextStyle = "NEW-STYLE" }
            };

            Assert.Empty(NameValidator.Validate(rule, lookup));
        }

        [Fact]
        public void 名称存在时不提示()
        {
            var lookup = new FakeNameLookup("a.dwg").With(NameKind.Layer, "TK-FRAME");
            var rule = new RuleConfig
            {
                Type = "EntityLayerChange",
                Filter = new RuleFilter { LayerName = "TK-FRAME" },
                Action = new RuleAction { TargetLayer = "TK-FRAME" }
            };

            Assert.Empty(NameValidator.Validate(rule, lookup));
        }

        [Fact]
        public void 多个图层名逐个检查()
        {
            var lookup = new FakeNameLookup("a.dwg").With(NameKind.Layer, "L1");
            var rule = new RuleConfig
            {
                Type = "EntityLayerChange",
                Filter = new RuleFilter { LayerNames = new List<string> { "L1", "L2" } },
                Action = new RuleAction { TargetLayer = "L1" }
            };

            List<FieldIssue> issues = NameValidator.Validate(rule, lookup);

            Assert.Single(issues);
            Assert.Equal("Filter.LayerNames", issues[0].Path);
        }
    }

    public class NameMatcherTests
    {
        [Fact]
        public void 忽略大小写命中()
        {
            Assert.Equal("TK-FRAME", NameMatcher.Nearest("tk-frame", new List<string> { "TK-FRAME" }));
        }

        [Fact]
        public void 一个字符之差能给出候选()
        {
            Assert.Equal("TK-FRAME", NameMatcher.Nearest("TK-FRAM", new List<string> { "TK-FRAME", "WALL" }));
        }

        [Fact]
        public void 差太远时不给候选()
        {
            Assert.Null(NameMatcher.Nearest("ZZZZZZZZ", new List<string> { "TK-FRAME" }));
        }

        [Fact]
        public void 空候选返回null()
        {
            Assert.Null(NameMatcher.Nearest("X", new List<string>()));
            Assert.Null(NameMatcher.Nearest("X", null));
        }
    }
}
