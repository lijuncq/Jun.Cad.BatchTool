using System.Collections.Generic;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
    /// <summary>
    /// 摘要必须"说真话"：已设置的条件要么出现在摘要里，要么计入 +N。
    /// 这里钉住 2026-10-29 评审发现的那几个漏项。
    /// </summary>
    public class RuleSummaryTests
    {
        [Fact]
        public void LayerNames_必须出现在摘要里()
        {
            var filter = new RuleFilter { LayerNames = new List<string> { "A", "B" } };

            string text = RuleSummary.DescribeFilter(filter);

            Assert.Contains("层A/B", text);
            Assert.DoesNotContain("(全部)", text);
        }

        [Fact]
        public void 只有LayerNames时不能渲染成全图()
        {
            var rule = new RuleConfig
            {
                Type = "EntityLayerChange",
                Filter = new RuleFilter { LayerNames = new List<string> { "WALL" } },
                Action = new RuleAction { TargetLayer = "X" }
            };

            string text = RuleSummary.Describe(rule);

            // 老版本这里会输出 "(全部) → 图层 X"，同事会以为改全图
            Assert.StartsWith(" [层WALL]", text);
        }

        [Fact]
        public void 非默认Scope要显示_默认值不显示()
        {
            Assert.Contains("范围仅模型", RuleSummary.DescribeFilter(new RuleFilter { Scope = "ModelOnly" }));
            Assert.DoesNotContain("范围", RuleSummary.DescribeFilter(new RuleFilter { Scope = "ModelAndLayouts" }));
            Assert.DoesNotContain("范围", RuleSummary.DescribeFilter(new RuleFilter()));
        }

        [Fact]
        public void 关闭遍历开关时要显示()
        {
            string text = RuleSummary.DescribeFilter(new RuleFilter { SkipLockedLayers = false, IncludeNestedBlocks = false });

            Assert.Contains("不跳过锁定层", text);
            Assert.Contains("不含块内嵌套", text);
        }

        [Fact]
        public void 真彩色与标注样式要显示()
        {
            string text = RuleSummary.DescribeFilter(new RuleFilter
            {
                TrueColor = "#FF0000",
                DimensionStyleName = "GB_DIM"
            });

            Assert.Contains("真彩#FF0000", text);
            Assert.Contains("标注GB_DIM", text);
        }

        [Fact]
        public void 多值图案与多值图层都要显示()
        {
            string text = RuleSummary.DescribeFilter(new RuleFilter
            {
                SourcePatternNames = new List<string> { "ANSI31", "AR-CONC" },
                LayerNames = new List<string> { "L1", "L2" }
            });

            Assert.Contains("图案ANSI31/AR-CONC", text);
            Assert.Contains("层L1/L2", text);
        }

        [Fact]
        public void 全部字段都被渲染时不应出现加N()
        {
            var filter = new RuleFilter
            {
                SourceBlockName = "A1",
                EntityTypes = new List<string> { "LINE" },
                ColorIndex = 1,
                LayerName = "0",
                SkipLockedLayers = false
            };

            string text = RuleSummary.DescribeFilter(filter);

            Assert.DoesNotContain("项条件", text);
            Assert.False(RuleSummary.HasUnrenderedConditions(filter));
        }

        [Fact]
        public void 未识别的规则类型也要有非空摘要()
        {
            var rule = new RuleConfig
            {
                Type = "MyCustomRule",
                Filter = new RuleFilter { LayerName = "0" },
                Action = new RuleAction { TargetLayer = "X" }
            };

            string action = RuleSummary.DescribeAction(rule);

            Assert.False(string.IsNullOrWhiteSpace(action));
            Assert.Contains("MyCustomRule", action);
        }

        [Fact]
        public void 空过滤器显示全部()
        {
            Assert.Equal("(全部)", RuleSummary.DescribeFilter(null));
            Assert.Equal("(全部)", RuleSummary.DescribeFilter(new RuleFilter()));
        }

        [Fact]
        public void 新图层不打印要写进摘要_默认值不写()
        {
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { SourceBlockNames = new List<string> { "A1" } },
                Action = new RuleAction { TargetLayer = "TK-FRAME", LayerPlottable = false }
            };

            string text = RuleSummary.Describe(rule);
            Assert.Contains("TK-FRAME", text);
            Assert.Contains("新图层不打印", text);

            // 默认（可打印）不写出来，否则每条规则都多一句噪声
            rule.Action.LayerPlottable = true;
            Assert.DoesNotContain("不打印", RuleSummary.Describe(rule));
            rule.Action.LayerPlottable = null;
            Assert.DoesNotContain("不打印", RuleSummary.Describe(rule));
        }

        [Fact]
        public void 块归层默认写明块内对象与属性一并归层()
        {
            // 回归：只挪块参照时块内实体仍留在原图层，用户看到的是"改了但图上没变"，
            // 摘要必须能看出这一条规则会连块里的对象一起改。
            // 属性默认跟随块内对象开关（ATTDEF 都改了、已有 ATTRIB 不改等于只修一半）。
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Filter = new RuleFilter { SourceBlockNames = new List<string> { "A1" } },
                Action = new RuleAction { TargetLayer = "TK-FRAME" }
            };

            string text = RuleSummary.DescribeAction(rule);
            Assert.Contains("TK-FRAME", text);
            Assert.Contains("含块内对象、属性", text);

            // 显式关掉属性：只剩块内对象
            rule.Action.AlsoMoveAttributes = false;
            text = RuleSummary.DescribeAction(rule);
            Assert.Contains("含块内对象", text);
            Assert.DoesNotContain("属性", text);

            // 关掉块内对象归层 = 回到旧行为，属性也跟着不动
            rule.Action.AlsoMoveAttributes = null;
            rule.Action.AlsoMoveBlockContent = false;
            text = RuleSummary.DescribeAction(rule);
            Assert.DoesNotContain("含块内对象", text);
            Assert.DoesNotContain("属性", text);
        }
    }
}
