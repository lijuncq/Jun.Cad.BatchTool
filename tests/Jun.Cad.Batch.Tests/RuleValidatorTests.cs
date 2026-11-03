using System.Collections.Generic;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 类型级校验。这是**唯一**一份校验实现：命令行（`ConfigLoader`）把它翻译成一行一条，
    /// 界面用它标红卡保存。所以这里的用例同时锁住两个消费者的行为。
    /// </summary>
    public class RuleValidatorTests
    {
        private static List<FieldIssue> Validate(RuleConfig rule)
        {
            return RuleValidator.Validate(rule);
        }

        private static bool HasError(List<FieldIssue> issues, string path)
        {
            return issues.Exists(i => i.IsError && i.Path == path);
        }

        [Fact]
        public void BlockReplace缺源块名与目标块名各报一条()
        {
            var rule = new RuleConfig { Name = "r", Type = "BlockReplace" };
            List<FieldIssue> issues = Validate(rule);

            Assert.True(HasError(issues, "Filter.SourceBlockName"));
            Assert.True(HasError(issues, "Action.TargetBlockName"));
        }

        [Fact]
        public void EntityLayerChange缺目标图层报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "EntityLayerChange",
                Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } }
            };

            Assert.True(HasError(Validate(rule), "Action.TargetLayer"));
        }

        /// <summary>
        /// 这条护栏原来只写在 ConfigLoader 里，合并校验后必须在 RuleValidator 里也成立，
        /// 否则界面会放行一条"执行时必定中止"的规则。
        /// </summary>
        [Fact]
        public void 写操作规则没有任何过滤条件时报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "EntityLayerChange",
                Action = new RuleAction { TargetLayer = "X" }
            };

            List<FieldIssue> issues = Validate(rule);

            Assert.True(HasError(issues, "Filter.EntityTypes"));
            Assert.Contains("没有任何过滤条件", issues.Find(i => i.Path == "Filter.EntityTypes").Message);
        }

        [Fact]
        public void 只配空间条件也算有过滤条件()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "EntityLayerChange",
                Filter = new RuleFilter { ContainerBlockPattern = "TB-*" },
                Action = new RuleAction { TargetLayer = "X" }
            };

            Assert.False(HasError(Validate(rule), "Filter.EntityTypes"));
        }

        [Fact]
        public void TextReplace非法正则报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "TextReplace",
                Action = new RuleAction { MatchMode = "Regex", SearchText = "(", Replacement = "x" }
            };

            Assert.True(HasError(Validate(rule), "Action.MatchMode"));
        }

        [Fact]
        public void TextReplace空查找内容报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "TextReplace",
                Action = new RuleAction { SearchText = "", Replacement = "x" }
            };

            Assert.True(HasError(Validate(rule), "Action.SearchText"));
        }

        [Fact]
        public void LayerMap空映射报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "LayerMap",
                Action = new RuleAction { LayerMappings = new List<string> { "没有等号" } }
            };

            Assert.True(HasError(Validate(rule), "Action.LayerMappings"));
        }

        [Fact]
        public void GeometryCleanup三个开关全关报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "GeometryCleanup",
                Action = new RuleAction
                {
                    CleanZeroLength = false,
                    CleanEmptyText = false,
                    CleanDuplicateEntities = false
                }
            };

            Assert.True(HasError(Validate(rule), "Action.CleanZeroLength"));
        }

        [Fact]
        public void GeometryCleanup真删给警告()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "GeometryCleanup",
                Action = new RuleAction { CleanupDryRun = false }
            };

            List<FieldIssue> issues = Validate(rule);

            Assert.False(HasError(issues, "Action.CleanupDryRun"));
            Assert.Contains(issues, i => !i.IsError && i.Path == "Action.CleanupDryRun");
        }

        [Fact]
        public void BomSync没有列Tag报错()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "BomSync",
                Action = new RuleAction { BomColumnTags = new List<string>() }
            };

            Assert.True(HasError(Validate(rule), "Action.BomColumnTags"));
        }

        [Fact]
        public void 未知清理类别只给警告()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "PurgeUnused",
                Action = new RuleAction { PurgeCategories = new List<string> { "Layres" } }
            };

            List<FieldIssue> issues = Validate(rule);

            Assert.Contains(issues, i => !i.IsError && i.Path == "Action.PurgeCategories");
        }

        [Fact]
        public void 没有问题的规则不产生任何issue()
        {
            var rule = new RuleConfig
            {
                Name = "r",
                Type = "EntityLayerChange",
                Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } },
                Action = new RuleAction { TargetLayer = "X", CreateIfMissing = true }
            };

            Assert.Empty(Validate(rule));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Auto")]
        [InlineData(" auto ")]
        [InlineData("Wipeout")]
        [InlineData(" wIpEoUt ")]
        public void TextMask_AcceptsOnlyNativeModesAndDefaults(string mode)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { MaskMode = mode } };

            Assert.Empty(Validate(rule));
        }

        [Theory]
        [InlineData("Solid")]
        [InlineData(" solid ")]
        [InlineData("Unknown")]
        [InlineData("0")]
        public void TextMask_RejectsSolidAndUnknownModes(string mode)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { MaskMode = mode } };

            Assert.True(HasError(Validate(rule), "Action.MaskMode"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0.0)]
        [InlineData(0.15)]
        [InlineData(2.0)]
        public void TextMask_AcceptsFinitePaddingBoundaries(double? ratio)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { MaskPaddingRatio = ratio } };

            Assert.Empty(Validate(rule));
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(2.01)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void TextMask_RejectsInvalidPaddingRatios(double ratio)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { MaskPaddingRatio = ratio } };

            Assert.True(HasError(Validate(rule), "Action.MaskPaddingRatio"));
        }

        [Theory]
        [InlineData("MaskSolidColorIndex")]
        [InlineData("masksolidcolorindex")]
        public void TextMask_RetiredColorWarnsWithoutDiscardingExtra(string key)
        {
            var rule = new RuleConfig
            {
                Type = "TextMask",
                Action = new RuleAction { Extra = new Dictionary<string, JToken> { [key] = new JValue(255) } }
            };

            FieldIssue issue = Assert.Single(Validate(rule));
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Equal("Action.MaskSolidColorIndex", issue.Path);
            Assert.Contains("deprecated", issue.Message);
            Assert.Equal(255, rule.Action.Extra[key].Value<int>());
        }

        [Theory]
        [InlineData("0", false)]
        [InlineData("0.0", false)]
        [InlineData("\"0\"", false)]
        [InlineData("null", false)]
        [InlineData("1", true)]
        [InlineData("-0.01", true)]
        [InlineData("\"NaN\"", true)]
        [InlineData("\"Infinity\"", true)]
        [InlineData("\"invalid\"", true)]
        [InlineData("{}", true)]
        public void TextMask_RetiredPaddingRequiresMigrationUnlessZeroOrUnset(string json, bool error)
        {
            JToken value = JToken.Parse(json);
            var rule = new RuleConfig
            {
                Type = "TextMask",
                Action = new RuleAction
                {
                    MaskPaddingRatio = 0.15,
                    Extra = new Dictionary<string, JToken> { ["maskpadding"] = value }
                }
            };

            List<FieldIssue> issues = Validate(rule);
            Assert.Equal(error, HasError(issues, "Action.MaskPadding"));
            if (error)
            {
                Assert.Contains("MaskPaddingRatio", Assert.Single(issues).Message);
            }
            else
            {
                Assert.Empty(issues);
            }
            Assert.Same(value, rule.Action.Extra["maskpadding"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(2001)]
        public void TextMask_MaskLimitDoesNotRejectUnlimitedValues(int? limit)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { MaxMasksPerDrawing = limit } };

            Assert.Empty(Validate(rule));
        }

        [Theory]
        [InlineData("Off")]
        [InlineData("ExplicitOnly")]
        [InlineData("All")]
        public void TextMask_AcceptsDimensionTextModes(string mode)
        {
            var rule = new RuleConfig { Type = "TextMask", Action = new RuleAction { DimensionTextMode = mode } };

            Assert.Empty(Validate(rule));
        }

        [Fact]
        public void TextMask_DefaultDimensionScopeWarnsWhenFilteredOut()
        {
            var rule = new RuleConfig
            {
                Type = "TextMask",
                Filter = new RuleFilter { EntityTypes = new List<string> { "MTEXT" } }
            };

            FieldIssue issue = Assert.Single(Validate(rule));
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Equal("Filter.EntityTypes", issue.Path);
            Assert.Contains("DimensionTextMode=All", issue.Message);

            rule.Action.DimensionTextMode = "Off";
            Assert.Empty(Validate(rule));
        }

        [Fact]
        public void OtherRules_PreserveRetiredMaskFieldsWithoutMaskValidation()
        {
            var rule = new RuleConfig
            {
                Type = "TextReplace",
                Action = new RuleAction
                {
                    SearchText = "before",
                    Replacement = "after",
                    Extra = new Dictionary<string, JToken> { ["MaskPadding"] = new JValue(1) }
                }
            };

            Assert.Empty(Validate(rule));
            Assert.Equal(1, rule.Action.Extra["MaskPadding"].Value<int>());
        }

        [Fact]
        public void 所有内置类型都能被校验且不抛异常()
        {
            foreach (string type in Engine.RuleFactory.BuiltInTypeNames)
            {
                var rule = new RuleConfig { Name = "r", Type = type };
                List<FieldIssue> issues = Validate(rule);

                // 缺参数的类型应该有 issue；不校验的类型返回空列表也算通过，
                // 这里只要求"不抛异常、不返回 null"
                Assert.NotNull(issues);
            }
        }
    }
}
