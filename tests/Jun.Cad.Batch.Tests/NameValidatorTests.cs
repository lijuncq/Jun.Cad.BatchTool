using System.Collections.Generic;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 名称校验的三态语义。
    ///
    /// 最关键的一条：**读不了图纸时不能把"没读到"说成"名字不存在"**。
    /// 批处理的目标是一批图，参考图纸只是参照；查不到就该沉默，而不是标红。
    /// </summary>
    public class NameValidatorTests
    {
        private static RuleConfig LayerRule(string layerName)
        {
            return new RuleConfig
            {
                Name = "r",
                Type = "EntityLayerChange",
                Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } },
                // CreateIfMissing=false 才需要"目标层必须已存在"的校验；
                // 默认 true 表示会自动建层，名称不存在是允许的。
                Action = new RuleAction { TargetLayer = layerName, CreateIfMissing = false }
            };
        }

        [Fact]
        public void 名字存在时不产生提示()
        {
            var lookup = new FakeNameLookup("ref.dwg").With(NameKind.Layer, "PIPE");

            List<FieldIssue> issues = NameValidator.Validate(LayerRule("PIPE"), lookup);

            Assert.Empty(issues);
        }

        [Fact]
        public void 名字不存在时给出最接近的候选()
        {
            var lookup = new FakeNameLookup("ref.dwg").With(NameKind.Layer, "PIPE-DIM", "PIPE-EQUIP");

            List<FieldIssue> issues = NameValidator.Validate(LayerRule("PIPE-DIN"), lookup);

            Assert.Single(issues);
            Assert.False(issues[0].IsError);        // 名称提示一律是 Warning，不阻断保存
            Assert.Contains("PIPE-DIM", issues[0].Message);
        }

        [Fact]
        public void 差得太远就不猜()
        {
            var lookup = new FakeNameLookup("ref.dwg").With(NameKind.Layer, "PIPE");

            List<FieldIssue> issues = NameValidator.Validate(LayerRule("完全无关的名字"), lookup);

            Assert.Single(issues);
            Assert.DoesNotContain("最接近", issues[0].Message);
        }

        /// <summary>
        /// 三态的核心：读不了图纸（Unknown）时静默跳过，不能报"名字不存在"。
        /// </summary>
        [Fact]
        public void 读不了图纸时不产生任何提示()
        {
            var lookup = new FakeNameLookup("ref.dwg") { AlwaysUnknown = true };

            Assert.Empty(NameValidator.Validate(LayerRule("随便什么层"), lookup));
        }

        [Fact]
        public void 没有参考图纸时直接跳过()
        {
            Assert.Empty(NameValidator.Validate(LayerRule("随便什么层"), null));
        }

        [Fact]
        public void 块名与样式名也走同一套校验()
        {
            var lookup = new FakeNameLookup("ref.dwg")
                .With(NameKind.Block, "A1_TITLE")
                .With(NameKind.TextStyle, "GB_STANDARD");

            var blockRule = new RuleConfig
            {
                Name = "r",
                Type = "BlockReplace",
                Filter = new RuleFilter { SourceBlockName = "A1_TITLE" },
                Action = new RuleAction { TargetBlockName = "A1_TITLE_NEW" }
            };

            List<FieldIssue> issues = NameValidator.Validate(blockRule, lookup);
            Assert.Contains(issues, i => i.Path == "Action.TargetBlockName");

            var fontRule = new RuleConfig
            {
                Name = "r",
                Type = "TextFontChange",
                Filter = new RuleFilter { EntityTypes = new List<string> { "TEXT" } },
                Action = new RuleAction { TargetTextStyle = "GB_STANDARD" }
            };

            Assert.Empty(NameValidator.Validate(fontRule, lookup));
        }
    }
}
