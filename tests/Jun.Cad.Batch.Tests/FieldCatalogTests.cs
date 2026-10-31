using System;
using System.Collections.Generic;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 字段目录：界面按它生成控件，schema 按它导出，README 的字段表也按它生成。
    /// 所以这里的断言盯的是"字段与类型对不对得上"，而不是界面长什么样。
    /// </summary>
    public class FieldCatalogTests
    {
        private static List<string> Paths(List<FieldDescriptor> fields)
        {
            var list = new List<string>();
            foreach (FieldDescriptor f in fields)
            {
                list.Add(f.Path);
            }

            return list;
        }

        [Fact]
        public void 每种内置类型都有字段()
        {
            foreach (string type in RuleFactory.BuiltInTypeNames)
            {
                Assert.True(FieldCatalog.ForType(type).Count > 0, type + " 没有任何字段");
            }
        }

        /// <summary>
        /// AppliesTo 写错类型名不会报错，只会让那个字段在界面上永远不出现。
        /// 这条用例是唯一的防线。
        /// </summary>
        [Fact]
        public void AppliesTo里的类型名必须都已注册()
        {
            List<string> problems = SchemaExport.Validate();
            Assert.True(problems.Count == 0, string.Join("；", problems.ToArray()));
        }

        [Fact]
        public void 按类型裁剪_BlockReplace看不到目标图层()
        {
            List<string> paths = Paths(FieldCatalog.ForType("BlockReplace"));

            Assert.Contains("Action.TargetBlockName", paths);
            Assert.DoesNotContain("Action.TargetLayer", paths);
            Assert.DoesNotContain("Action.Height", paths);
        }

        [Fact]
        public void 按类型裁剪_BlockLayerChange能看到目标图层与多块名()
        {
            List<string> paths = Paths(FieldCatalog.ForType("BlockLayerChange"));

            Assert.Contains("Action.TargetLayer", paths);
            Assert.Contains("Filter.SourceBlockNames", paths);
            Assert.Contains("Action.AlsoMoveAttributes", paths);
            Assert.Contains("Action.AlsoMoveBlockContent", paths);

            // 块内对象归层是"块归层"独有的开关，别被漏在界面外
            Assert.DoesNotContain("Action.AlsoMoveBlockContent", Paths(FieldCatalog.ForType("EntityLayerChange")));
        }

        [Fact]
        public void 引擎级字段对所有类型都可见()
        {
            foreach (string type in RuleFactory.BuiltInTypeNames)
            {
                List<string> paths = Paths(FieldCatalog.ForType(type));
                Assert.Contains("Filter.EntityTypes", paths);
                Assert.Contains("Filter.LayerName", paths);
                Assert.Contains("Filter.Scope", paths);
            }
        }

        [Fact]
        public void 类型用不上但已有值的字段仍然显示并标为不适用()
        {
            var rule = new RuleConfig
            {
                Type = "BlockReplace",
                Filter = new RuleFilter(),
                Action = new RuleAction { TargetBlockName = "B", TargetLayer = "残留图层" }
            };

            FieldDescriptor stale = FieldCatalog.For("BlockReplace", rule)
                .Find(f => f.Path == "Action.TargetLayer");

            Assert.NotNull(stale);
            Assert.True(stale.NotApplicable);
            Assert.True(stale.Disabled);
            Assert.Contains("不读取", stale.DisabledReason);
        }

        [Fact]
        public void 没有值的无关字段不显示()
        {
            var rule = new RuleConfig { Type = "BlockReplace", Action = new RuleAction { TargetBlockName = "B" } };

            Assert.DoesNotContain("Action.TargetLayer", Paths(FieldCatalog.For("BlockReplace", rule)));
        }

        [Fact]
        public void 强制随层时线型与线宽被禁用而不是隐藏()
        {
            var rule = new RuleConfig
            {
                Type = "EntityLinetypeChange",
                Action = new RuleAction { ForceByLayer = true, LinetypeName = "DASHED" }
            };

            FieldDescriptor linetype = FieldCatalog.For("EntityLinetypeChange", rule)
                .Find(f => f.Path == "Action.LinetypeName");

            Assert.NotNull(linetype);
            Assert.True(linetype.Disabled);
            Assert.Contains("强制随层", linetype.DisabledReason);
        }

        [Fact]
        public void 指定渐变后图案字段被禁用()
        {
            var rule = new RuleConfig
            {
                Type = "HatchPatternChange",
                Action = new RuleAction { TargetGradientName = "LINEAR", TargetPatternName = "ANSI31" }
            };

            List<FieldDescriptor> fields = FieldCatalog.For("HatchPatternChange", rule);

            Assert.True(fields.Find(f => f.Path == "Action.TargetPatternName").Disabled);
            Assert.True(fields.Find(f => f.Path == "Action.PatternScale").Disabled);
        }

        [Fact]
        public void 预定义图案时线间距与双线被禁用()
        {
            var rule = new RuleConfig
            {
                Type = "HatchPatternChange",
                Action = new RuleAction { TargetPatternName = "ANSI31", PatternType = "PreDefined" }
            };

            List<FieldDescriptor> fields = FieldCatalog.For("HatchPatternChange", rule);

            Assert.True(fields.Find(f => f.Path == "Action.PatternSpace").Disabled);
            Assert.True(fields.Find(f => f.Path == "Action.PatternDouble").Disabled);
        }

        [Fact]
        public void 不自动建层时图层特性被禁用()
        {
            var rule = new RuleConfig
            {
                Type = "EntityLayerChange",
                Action = new RuleAction { TargetLayer = "X", CreateIfMissing = false }
            };

            List<FieldDescriptor> fields = FieldCatalog.For("EntityLayerChange", rule);

            Assert.True(fields.Find(f => f.Path == "Action.LayerColorIndex").Disabled);
            Assert.True(fields.Find(f => f.Path == "Action.LayerPlottable").Disabled);
        }

        [Fact]
        public void 字段按分组与顺序排序()
        {
            List<FieldDescriptor> fields = FieldCatalog.ForType("EntityLayerChange");

            int lastGroup = -1;
            int lastOrder = -1;
            foreach (FieldDescriptor f in fields)
            {
                int group = (int)f.Group;
                Assert.True(group >= lastGroup, "分组顺序错了");
                if (group != lastGroup)
                {
                    lastGroup = group;
                    lastOrder = -1;
                }

                Assert.True(f.Order >= lastOrder, "组内顺序错了：" + f.Path);
                lastOrder = f.Order;
            }
        }

        [Fact]
        public void 用不上的字段集合可用于清理残留()
        {
            HashSet<string> notUsed = FieldCatalog.FieldPathsNotUsedBy("BlockReplace");

            Assert.Contains("Action.TargetLayer", notUsed);
            Assert.DoesNotContain("Action.TargetBlockName", notUsed);
        }

        [Fact]
        public void 下拉框写入的字符串会转成数值()
        {
            var rule = new RuleConfig
            {
                Type = "BlockLayerChange",
                Action = new RuleAction { TargetLayer = "X" }
            };

            FieldDescriptor field = FieldCatalog.For("BlockLayerChange", rule)
                .Find(f => f.Path == "Action.LayerLineWeight");
            Assert.NotNull(field);

            field.SetValue(rule, "25");
            Assert.Equal(25, rule.Action.LayerLineWeight);

            field.SetValue(rule, null);
            Assert.Null(rule.Action.LayerLineWeight);
        }
    }
}
