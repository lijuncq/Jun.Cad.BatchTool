using System.Collections.Generic;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
    public class RuleValidatorTests
    {
        private static RuleConfig Rule(string type, RuleFilter filter, RuleAction action)
        {
            return new RuleConfig { Name = "t", Type = type, Filter = filter ?? new RuleFilter(), Action = action ?? new RuleAction() };
        }

        private static bool HasError(List<FieldIssue> issues, string path)
        {
            return issues.Exists(i => i.Severity == IssueSeverity.Error && i.Path == path);
        }

        private static bool HasWarning(List<FieldIssue> issues, string path)
        {
            return issues.Exists(i => i.Severity == IssueSeverity.Warning && i.Path == path);
        }

        [Fact]
        public void BlockReplace_缺源块与目标块都是Error()
        {
            var issues = RuleValidator.Validate(Rule("BlockReplace", new RuleFilter(), new RuleAction()));

            Assert.True(HasError(issues, "Filter.SourceBlockName"));
            Assert.True(HasError(issues, "Action.TargetBlockName"));
        }

        [Fact]
        public void BlockLayerChange_缺块名是Error()
        {
            var issues = RuleValidator.Validate(Rule("BlockLayerChange", new RuleFilter(), new RuleAction { TargetLayer = "X" }));

            Assert.True(HasError(issues, "Filter.SourceBlockName"));
        }

        [Fact]
        public void BlockLayerChange_有多个块名时通过()
        {
            var filter = new RuleFilter { SourceBlockNames = new List<string> { "A", "B" } };
            var issues = RuleValidator.Validate(Rule("BlockLayerChange", filter, new RuleAction { TargetLayer = "X" }));

            Assert.False(HasError(issues, "Filter.SourceBlockName"));
        }

        [Fact]
        public void EntityLinetypeChange_两者都空是Error()
        {
            var issues = RuleValidator.Validate(Rule("EntityLinetypeChange", null, new RuleAction()));

            Assert.True(HasError(issues, "Action.LinetypeName"));
        }

        [Fact]
        public void EntityLinetypeChange_开了ForceByLayer就通过()
        {
            var issues = RuleValidator.Validate(Rule("EntityLinetypeChange", null, new RuleAction { ForceByLayer = true }));

            Assert.False(HasError(issues, "Action.LinetypeName"));
        }

        [Fact]
        public void EntityLineweightChange_原本零校验_现在有Error()
        {
            var issues = RuleValidator.Validate(Rule("EntityLineweightChange", null, new RuleAction()));

            Assert.True(HasError(issues, "Action.LineWeight"));
        }

        [Fact]
        public void EntityLineweightChange_超范围是Error()
        {
            var issues = RuleValidator.Validate(Rule("EntityLineweightChange", null, new RuleAction { LineWeight = 999 }));

            Assert.True(HasError(issues, "Action.LineWeight"));
        }

        [Fact]
        public void EntityLineweightChange_合法线宽不报错()
        {
            var issues = RuleValidator.Validate(Rule("EntityLineweightChange", null, new RuleAction { LineWeight = 25 }));

            Assert.False(HasError(issues, "Action.LineWeight"));
        }

        [Fact]
        public void EntityLineweightChange_枚举里不存在的值1是Error()
        {
            // 回归：1 在 -3~211 范围内，旧的"范围检查"放过了它，执行时宿主抛 eInvalidInput
            var issues = RuleValidator.Validate(Rule("EntityLineweightChange", null, new RuleAction { LineWeight = 1 }));

            Assert.True(HasError(issues, "Action.LineWeight"));
        }

        [Fact]
        public void BlockLayerChange_新建图层线宽1是Warning()
        {
            // 回归：用户配置 "LayerLineWeight": 1 → 创建图层时抛 eInvalidInput，整条规则中止
            var filter = new RuleFilter { SourceBlockName = "SYMBOL27" };
            var action = new RuleAction { TargetLayer = "NO-PRINT", LayerLineWeight = 1 };

            var issues = RuleValidator.Validate(Rule("BlockLayerChange", filter, action));

            Assert.True(HasWarning(issues, "Action.LayerLineWeight"));
            Assert.Contains("0.25mm", issues.Find(i => i.Path == "Action.LayerLineWeight").Message);
        }

        [Fact]
        public void BlockLayerChange_新建图层线宽25通过()
        {
            var filter = new RuleFilter { SourceBlockName = "SYMBOL27" };
            var action = new RuleAction { TargetLayer = "NO-PRINT", LayerLineWeight = 25 };

            var issues = RuleValidator.Validate(Rule("BlockLayerChange", filter, action));

            Assert.False(HasWarning(issues, "Action.LayerLineWeight"));
            Assert.False(HasError(issues, "Action.LayerLineWeight"));
        }

        [Fact]
        public void 图层颜色索引0和256是Warning_255通过()
        {
            var filter = new RuleFilter { SourceBlockName = "SYMBOL27" };

            Assert.True(HasWarning(RuleValidator.Validate(Rule("BlockLayerChange", filter,
                new RuleAction { TargetLayer = "X", LayerColorIndex = 0 })), "Action.LayerColorIndex"));
            Assert.True(HasWarning(RuleValidator.Validate(Rule("BlockLayerChange", filter,
                new RuleAction { TargetLayer = "X", LayerColorIndex = 256 })), "Action.LayerColorIndex"));
            Assert.False(HasWarning(RuleValidator.Validate(Rule("BlockLayerChange", filter,
                new RuleAction { TargetLayer = "X", LayerColorIndex = 255 })), "Action.LayerColorIndex"));
        }

        [Fact]
        public void SetByLayer_原本零校验_全关时给Warning()
        {
            var action = new RuleAction
            {
                ForceColorByLayer = false,
                ForceLinetypeByLayer = false,
                ForceLineWeightByLayer = false,
                ForcePlotStyleByLayer = false
            };

            var issues = RuleValidator.Validate(Rule("SetByLayer", null, action));

            Assert.True(HasWarning(issues, "Action.ForceColorByLayer"));
        }

        [Fact]
        public void PurgeUnused_原本零校验_轮次非法是Error()
        {
            var issues = RuleValidator.Validate(Rule("PurgeUnused", null, new RuleAction { PurgePasses = 0 }));

            Assert.True(HasError(issues, "Action.PurgePasses"));
        }

        [Fact]
        public void PurgeUnused_未知类别给Warning()
        {
            var action = new RuleAction { PurgeCategories = new List<string> { "Block", "NotACategory" } };

            var issues = RuleValidator.Validate(Rule("PurgeUnused", null, action));

            Assert.True(HasWarning(issues, "Action.PurgeCategories"));
        }

        [Fact]
        public void HatchPatternChange_两个目标都空是Error()
        {
            var issues = RuleValidator.Validate(Rule("HatchPatternChange", null, new RuleAction()));

            Assert.True(HasError(issues, "Action.TargetPatternName"));
        }

        [Fact]
        public void HatchPatternChange_比例非法是Error()
        {
            var issues = RuleValidator.Validate(Rule("HatchPatternChange", null,
                new RuleAction { TargetPatternName = "ANSI31", PatternScale = 0 }));

            Assert.True(HasError(issues, "Action.PatternScale"));
        }

        [Fact]
        public void 颜色索引越界是Error()
        {
            var issues = RuleValidator.Validate(Rule("EntityLayerChange",
                new RuleFilter { ColorIndex = 999 }, new RuleAction { TargetLayer = "X" }));

            Assert.True(HasError(issues, "Filter.ColorIndex"));
        }

        [Fact]
        public void 合法的BlockReplace没有任何问题()
        {
            var filter = new RuleFilter { SourceBlockName = "A1" };
            var action = new RuleAction { TargetBlockName = "A2" };

            Assert.Empty(RuleValidator.Validate(Rule("BlockReplace", filter, action)));
        }
    }
}
