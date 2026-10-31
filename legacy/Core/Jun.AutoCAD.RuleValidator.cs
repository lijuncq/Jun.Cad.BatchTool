using System;
using System.Collections.Generic;
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 规则校验的唯一入口。
    ///
    /// 为什么集中到一个类：
    ///   * 原来这套逻辑在 <c>ConfigLoader.ValidateByType</c> 里，只产出字符串、没有级别、没有字段锚点，
    ///     界面拿到之后无法把错误标到具体控件上；
    ///   * 11 种类型里只覆盖了 8 种，`PurgeUnused` / `EntityLineweightChange` / `SetByLayer` 零校验；
    ///   * 界面与命令行如果各写一套，迟早会不一致。
    ///
    /// 现在它返回 `FieldIssue(Path, Severity, Message)`：
    ///   * `ConfigLoader` 只取 `Warning` 级拼成字符串（保持命令行输出不变）；
    ///   * 界面用 `Error` 级阻断保存并把提示标到字段上。
    ///
    /// 与"字段元数据"的分工：属性描述**单个字段**（标签、控件、单位、默认值），
    /// 这里描述**类型语义**（哪些字段互斥、哪些组合才合法）。跨字段条件单字段元数据表达不了，
    /// 所以两者不是重复，但也不能再长出第三份——所有类型级判断都必须写在这里。
    ///
    /// 本类不引用任何宿主类型。
    /// </summary>
    public static class RuleValidator
    {
        private static readonly string[] KnownPurgeCategories =
        {
            "Block", "Layer", "Linetype", "TextStyle", "DimStyle", "RegApp", "Group", "MLeaderStyle"
        };

        public static List<FieldIssue> Validate(RuleConfig rule)
        {
            var issues = new List<FieldIssue>();
            if (rule == null)
            {
                return issues;
            }

            string type = (rule.Type ?? string.Empty).Trim();
            RuleFilter filter = rule.Filter ?? new RuleFilter();
            RuleAction action = rule.Action ?? new RuleAction();

            if (string.Equals(type, "BlockReplace", StringComparison.OrdinalIgnoreCase))
            {
                Require(issues, "Filter.SourceBlockName", filter.SourceBlockName, "源块名（Filter.SourceBlockName）");
                Require(issues, "Action.TargetBlockName", action.TargetBlockName, "目标块名（Action.TargetBlockName）");
            }
            else if (string.Equals(type, "EntityLayerChange", StringComparison.OrdinalIgnoreCase))
            {
                Require(issues, "Action.TargetLayer", action.TargetLayer, "目标图层（Action.TargetLayer）");
            }
            else if (string.Equals(type, "BlockLayerChange", StringComparison.OrdinalIgnoreCase))
            {
                bool hasSingle = !string.IsNullOrWhiteSpace(filter.SourceBlockName);
                bool hasMany = filter.SourceBlockNames != null
                               && filter.SourceBlockNames.Exists(x => !string.IsNullOrWhiteSpace(x));

                if (!hasSingle && !hasMany)
                {
                    issues.Add(Error("Filter.SourceBlockName",
                        "缺少源块名：Filter.SourceBlockName 与 Filter.SourceBlockNames 至少要填一个。"));
                }

                Require(issues, "Action.TargetLayer", action.TargetLayer, "目标图层（Action.TargetLayer）");
            }
            else if (string.Equals(type, "TextFontChange", StringComparison.OrdinalIgnoreCase))
            {
                Require(issues, "Action.TargetTextStyle", action.TargetTextStyle, "目标文字样式（Action.TargetTextStyle）");
            }
            else if (string.Equals(type, "TextHeightChange", StringComparison.OrdinalIgnoreCase))
            {
                if (!action.Height.HasValue || action.Height.Value <= 0)
                {
                    issues.Add(Error("Action.Height", "Action.Height 必须是正数。"));
                }
            }
            else if (string.Equals(type, "EntityLinetypeChange", StringComparison.OrdinalIgnoreCase))
            {
                bool force = ConfigValues.Bool(action.ForceByLayer, false);
                if (!force && string.IsNullOrWhiteSpace(action.LinetypeName))
                {
                    issues.Add(Error("Action.LinetypeName",
                        "既没有填 Action.LinetypeName，也没有把 Action.ForceByLayer 设为 true。"));
                }
            }
            else if (string.Equals(type, "EntityLineweightChange", StringComparison.OrdinalIgnoreCase))
            {
                bool force = ConfigValues.Bool(action.ForceByLayer, false);
                if (!force)
                {
                    if (!action.LineWeight.HasValue)
                    {
                        issues.Add(Error("Action.LineWeight",
                            "既没有填 Action.LineWeight，也没有把 Action.ForceByLayer 设为 true。"));
                    }
                    else if (!LineWeightUtil.IsValidEntityValue(action.LineWeight.Value))
                    {
                        // 任意整数强转成 LineWeight 再赋值，宿主会抛 eInvalidInput——必须在保存前挡住
                        issues.Add(Error("Action.LineWeight", string.Format(
                            "Action.LineWeight={0} 不是 AutoCAD 认可的线宽（合法值：{1}，单位 0.01mm）。最接近的合法值是 {2}。",
                            action.LineWeight.Value,
                            LineWeightUtil.EntityValuesText,
                            LineWeightUtil.Describe(LineWeightUtil.NearestLayerValue(action.LineWeight.Value)))));
                    }
                }
            }
            else if (string.Equals(type, "DimensionStyleChange", StringComparison.OrdinalIgnoreCase))
            {
                Require(issues, "Action.TargetDimensionStyle", action.TargetDimensionStyle,
                    "目标标注样式（Action.TargetDimensionStyle）");
            }
            else if (string.Equals(type, "SetByLayer", StringComparison.OrdinalIgnoreCase))
            {
                bool any = ConfigValues.Bool(action.ForceColorByLayer, true)
                           || ConfigValues.Bool(action.ForceLinetypeByLayer, true)
                           || ConfigValues.Bool(action.ForceLineWeightByLayer, true)
                           || ConfigValues.Bool(action.ForcePlotStyleByLayer, true);

                if (!any)
                {
                    issues.Add(Warning("Action.ForceColorByLayer",
                        "四个随层开关都被设为 false，这条规则不会改动任何对象。"));
                }
            }
            else if (string.Equals(type, "HatchPatternChange", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(action.TargetPatternName)
                    && string.IsNullOrWhiteSpace(action.TargetGradientName))
                {
                    issues.Add(Error("Action.TargetPatternName",
                        "Action.TargetPatternName 与 Action.TargetGradientName 至少要填一个。"));
                }

                if (action.PatternScale.HasValue && action.PatternScale.Value <= 0)
                {
                    issues.Add(Error("Action.PatternScale", "Action.PatternScale 必须大于 0。"));
                }

                if (action.PatternSpace.HasValue && action.PatternSpace.Value <= 0)
                {
                    issues.Add(Error("Action.PatternSpace", "Action.PatternSpace 必须大于 0。"));
                }

                if (action.GradientShift.HasValue
                    && (action.GradientShift.Value < 0 || action.GradientShift.Value > 1))
                {
                    issues.Add(Error("Action.GradientShift", "Action.GradientShift 应在 0 ~ 1 之间。"));
                }
            }
            else if (string.Equals(type, "PurgeUnused", StringComparison.OrdinalIgnoreCase))
            {
                if (action.PurgePasses.HasValue && action.PurgePasses.Value < 1)
                {
                    issues.Add(Error("Action.PurgePasses", "Action.PurgePasses 至少为 1。"));
                }

                if (action.PurgeCategories != null)
                {
                    foreach (string category in action.PurgeCategories)
                    {
                        if (string.IsNullOrWhiteSpace(category))
                        {
                            continue;
                        }

                        if (!IsKnownPurgeCategory(category.Trim()))
                        {
                            issues.Add(Warning("Action.PurgeCategories", string.Format(
                                "未知的清理类别「{0}」，会被忽略。可用：{1}。",
                                category.Trim(), string.Join("/", KnownPurgeCategories))));
                        }
                    }
                }
            }

            // ---- 通用：自动创建图层用的颜色/线宽（EntityLayerChange / BlockLayerChange 共用） ----
            if (action.LayerColorIndex.HasValue)
            {
                int c = action.LayerColorIndex.Value;
                if (c < 1 || c > 255)
                {
                    issues.Add(Warning("Action.LayerColorIndex", string.Format(
                        "Action.LayerColorIndex={0} 不是合法的图层颜色索引：图层只能是 1~255（0=随块、256=随层只用于实体）。执行时会改用默认颜色 7。", c)));
                }
            }

            if (action.LayerLineWeight.HasValue)
            {
                int lw = action.LayerLineWeight.Value;
                if (!LineWeightUtil.IsValidLayerValue(lw))
                {
                    // 任意整数强转成 LineWeight 再赋给图层，宿主会抛 eInvalidInput（曾导致整条规则中止）
                    issues.Add(Warning("Action.LayerLineWeight", string.Format(
                        "Action.LayerLineWeight={0} 不是 AutoCAD 支持的图层线宽：图层只能是具体线宽 {1}（单位 0.01mm，0.25mm 写 25），不能用随层/随块。执行时会忽略这一项（保持默认线宽）；最接近的合法值是 {2}。",
                        lw, LineWeightUtil.LayerValuesText, LineWeightUtil.Describe(LineWeightUtil.NearestLayerValue(lw)))));
                }
            }

            // ---- 通用：颜色索引范围 ----
            if (filter.ColorIndex.HasValue)
            {
                int c = filter.ColorIndex.Value;
                if (c < 0 || c > 256)
                {
                    issues.Add(Error("Filter.ColorIndex", string.Format("Filter.ColorIndex={0} 超出 0~256 范围。", c)));
                }
            }

            if (filter.ColorIndexes != null)
            {
                foreach (int c in filter.ColorIndexes)
                {
                    if (c < 0 || c > 256)
                    {
                        issues.Add(Error("Filter.ColorIndexes",
                            string.Format("Filter.ColorIndexes 含非法值 {0}（应在 0~256）。", c)));
                    }
                }
            }

            return issues;
        }

        private static void Require(List<FieldIssue> issues, string path, string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add(Error(path, "缺少" + label + "。"));
            }
        }

        private static FieldIssue Error(string path, string message)
        {
            return new FieldIssue(path, IssueSeverity.Error, message);
        }

        private static FieldIssue Warning(string path, string message)
        {
            return new FieldIssue(path, IssueSeverity.Warning, message);
        }

        private static bool IsKnownPurgeCategory(string category)
        {
            foreach (string known in KnownPurgeCategories)
            {
                if (string.Equals(known, category, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
