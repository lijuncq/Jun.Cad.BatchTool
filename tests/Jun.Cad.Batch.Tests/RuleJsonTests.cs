using System.Collections.Generic;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Newtonsoft.Json;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 规则 JSON 的往返契约。
    ///
    /// 从 legacy 搬过来，顺手把当年那个真 bug 锁死：`RuleJson` 里判断"空 Filter/Action 要不要省略"
    /// 用的是 `type.Namespace.StartsWith("Jun.AutoCAD.Config")`，迁移到 `Jun.Cad.Batch` 之后
    /// 这个分支**永远不成立**，于是每条规则都会写出 `"Filter": {}, "Action": {}`。
    /// 这里第 5 条用例就是为它写的。
    /// </summary>
    public class RuleJsonTests
    {
        private const string WithExtra = @"{
  ""Name"": ""带自定义字段的规则"",
  ""Type"": ""BlockLayerChange"",
  ""Enabled"": true,
  ""Filter"": { ""SourceBlockName"": ""A1"", ""Note"": ""临时标记"" },
  ""Action"": { ""TargetLayer"": ""TK"", ""MyParam"": 42 }
}";

        [Fact]
        public void 未知字段进入Extra而不是被丢弃()
        {
            RuleConfig rule = JsonConvert.DeserializeObject<RuleConfig>(WithExtra);

            Assert.NotNull(rule.Filter.Extra);
            Assert.True(rule.Filter.Extra.ContainsKey("Note"));
            Assert.NotNull(rule.Action.Extra);
            Assert.True(rule.Action.Extra.ContainsKey("MyParam"));
        }

        [Fact]
        public void 未知字段能原样往返()
        {
            RuleConfig rule = JsonConvert.DeserializeObject<RuleConfig>(WithExtra);

            string output = RuleJson.Serialize(rule);

            Assert.Contains("\"Note\": \"临时标记\"", output);
            Assert.Contains("\"MyParam\": 42", output);

            RuleConfig again = JsonConvert.DeserializeObject<RuleConfig>(output);
            Assert.Equal("临时标记", again.Filter.Extra["Note"].ToString());
        }

        [Fact]
        public void 精简规则不输出null与空集合()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "BlockReplace",
                Filter = new RuleFilter { SourceBlockName = "A" },
                Action = new RuleAction { TargetBlockName = "B" }
            };

            string output = RuleJson.Serialize(rule);

            Assert.DoesNotContain("null", output);
            Assert.DoesNotContain("EntityTypes", output);
            Assert.Contains("\"SourceBlockName\": \"A\"", output);
        }

        [Fact]
        public void false与0不能被当成没配置丢掉()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "SetByLayer",
                Enabled = false,
                Action = new RuleAction { ForceColorByLayer = false, LineWeight = 0 }
            };

            string output = RuleJson.Serialize(rule);

            Assert.Contains("\"Enabled\": false", output);
            Assert.Contains("\"LineWeight\": 0", output);
            Assert.Contains("\"ForceColorByLayer\": false", output);
        }

        /// <summary>
        /// 迁移时踩过的坑：命名空间判断写死在旧名字上，导致空对象不再被省略。
        /// 这条用例红了，说明又有人把命名空间写死了。
        /// </summary>
        [Fact]
        public void 空Filter与空Action整体省略()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "PurgeUnused",
                Filter = new RuleFilter(),
                Action = new RuleAction()
            };

            string output = RuleJson.Serialize(rule);

            Assert.DoesNotContain("\"Filter\"", output);
            Assert.DoesNotContain("\"Action\"", output);
        }

        [Fact]
        public void 规则数组往返后条数与类型不变()
        {
            var config = new BatchConfig
            {
                Rules = new List<RuleConfig>
                {
                    new RuleConfig { Name = "a", Type = "EntityLayerChange", Action = new RuleAction { TargetLayer = "L" } },
                    new RuleConfig { Name = "b", Type = "TextReplace", Action = new RuleAction { SearchText = "x", Replacement = "y" } }
                }
            };

            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            BatchConfig back = JsonConvert.DeserializeObject<BatchConfig>(json);

            Assert.Equal(2, back.Rules.Count);
            Assert.Equal("EntityLayerChange", back.Rules[0].Type);
            Assert.Equal("TextReplace", back.Rules[1].Type);
            Assert.Equal("x", back.Rules[1].Action.SearchText);
        }
    }
}
