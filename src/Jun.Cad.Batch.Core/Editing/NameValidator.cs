using System;
using System.Collections.Generic;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>
    /// 保存时的名称校验：对照当前图纸的符号表，检查配置里写的图层名 / 块名 / 样式名是否存在。
    ///
    /// 定位与边界：
    ///   * **只产 Warning**。批处理的目标是目录里的一批图纸，当前打开的这张不是判据；
    ///     把它做成 Error 会阻止用户保存一份对目标图纸完全正确的配置。
    ///   * **查不了就不说话**。<see cref="INameLookup"/> 返回 <c>Known=false</c> 时直接跳过，
    ///     绝不因为"读不到"而报"不存在"。
    ///   * **自动创建类字段豁免**。`TargetLayer`（`CreateIfMissing`）、`TargetTextStyle`、
    ///     `TargetDimensionStyle` 允许指向不存在的名字——handler 会按配置创建它们。
    ///     `TargetBlockName` 不豁免：`BlockReplaceRule` 要求目标块已存在，不存在会中止整条规则。
    ///
    /// 本类不引用任何宿主类型，可用假的 <see cref="INameLookup"/> 直接单测。
    /// </summary>
    public static class NameValidator
    {
        public static List<FieldIssue> Validate(RuleConfig rule, INameLookup lookup)
        {
            var issues = new List<FieldIssue>();
            if (rule == null || lookup == null)
            {
                return issues;
            }

            RuleFilter filter = rule.Filter;
            RuleAction action = rule.Action;

            if (filter != null)
            {
                CheckOne(issues, lookup, NameKind.Layer, "Filter.LayerName", filter.LayerName);
                CheckMany(issues, lookup, NameKind.Layer, "Filter.LayerNames", filter.LayerNames);

                CheckOne(issues, lookup, NameKind.Block, "Filter.SourceBlockName", filter.SourceBlockName);
                CheckMany(issues, lookup, NameKind.Block, "Filter.SourceBlockNames", filter.SourceBlockNames);

                CheckOne(issues, lookup, NameKind.TextStyle, "Filter.TextStyleName", filter.TextStyleName);
                CheckOne(issues, lookup, NameKind.DimStyle, "Filter.DimensionStyleName", filter.DimensionStyleName);
            }

            if (action != null)
            {
                // 目标图层：CreateIfMissing 默认 true，此时允许不存在
                bool layerAutoCreate = ConfigValues.Bool(action.CreateIfMissing, true);
                if (!layerAutoCreate)
                {
                    CheckOne(issues, lookup, NameKind.Layer, "Action.TargetLayer", action.TargetLayer);
                }

                CheckOne(issues, lookup, NameKind.Linetype, "Action.LayerLinetype", action.LayerLinetype);
                CheckOne(issues, lookup, NameKind.Block, "Action.TargetBlockName", action.TargetBlockName);

                // 目标文字样式 / 标注样式：handler 会自动创建，豁免
            }

            return issues;
        }

        private static void CheckMany(List<FieldIssue> issues, INameLookup lookup, NameKind kind, string path, List<string> names)
        {
            if (names == null)
            {
                return;
            }

            foreach (string name in names)
            {
                CheckOne(issues, lookup, kind, path, name);
            }
        }

        private static void CheckOne(List<FieldIssue> issues, INameLookup lookup, NameKind kind, string path, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            NameLookupResult result;
            try
            {
                result = lookup.Lookup(kind, name.Trim());
            }
            catch
            {
                // 查询本身抛异常时静默跳过：校验不能变成保存失败的原因
                return;
            }

            if (result == null || !result.Known || result.Found)
            {
                return;
            }

            issues.Add(new FieldIssue(path, IssueSeverity.Warning, Describe(kind, name.Trim(), result.Nearest, lookup.ContextName)));
        }

        private static string Describe(NameKind kind, string name, string nearest, string context)
        {
            string text = string.Format("{0}「{1}」在{2}中不存在",
                KindName(kind),
                name,
                string.IsNullOrWhiteSpace(context) ? "当前图纸" : "当前图纸（" + context + "）");

            if (!string.IsNullOrWhiteSpace(nearest))
            {
                text += "，最接近「" + nearest + "」";
            }

            return text + "。批处理会在目标图纸上执行，此处仅提示。";
        }

        private static string KindName(NameKind kind)
        {
            switch (kind)
            {
                case NameKind.Layer:
                    return "图层";
                case NameKind.Block:
                    return "块";
                case NameKind.TextStyle:
                    return "文字样式";
                case NameKind.DimStyle:
                    return "标注样式";
                case NameKind.Linetype:
                    return "线型";
                default:
                    return "名称";
            }
        }
    }

    /// <summary>
    /// 名称的"最近匹配"计算。
    ///
    /// 规则：忽略大小写相等 → 前缀匹配 → 编辑距离 ≤ 2，取距离最小者。
    /// 只用于提示文案，不参与任何判定，因此允许粗糙。
    /// </summary>
    public static class NameMatcher
    {
        private const int MaxDistance = 2;

        public static string Nearest(string name, IList<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(name) || candidates == null || candidates.Count == 0)
            {
                return null;
            }

            string best = null;
            int bestDistance = int.MaxValue;
            string lower = name.Trim().ToLowerInvariant();

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string other = candidate.Trim().ToLowerInvariant();

                if (string.Equals(other, lower, StringComparison.Ordinal))
                {
                    return candidate;
                }

                int distance = other.StartsWith(lower, StringComparison.Ordinal) || lower.StartsWith(other, StringComparison.Ordinal)
                    ? 1
                    : Levenshtein(lower, other);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= MaxDistance ? best : null;
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
