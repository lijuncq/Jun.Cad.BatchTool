using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
using Newtonsoft.Json;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
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

        [Fact]
        public void 空Filter与空Action整体省略()
        {
            var rule = new RuleConfig { Name = "r", Type = "PurgeUnused", Filter = new RuleFilter(), Action = new RuleAction() };

            string output = RuleJson.Serialize(rule);

            Assert.DoesNotContain("\"Filter\"", output);
            Assert.DoesNotContain("\"Action\"", output);
        }
    }
}
