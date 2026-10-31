using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>
    /// 把一条规则压成一行"人话"摘要，给界面列表和日志用。
    ///
    /// 目标：用户扫一眼就知道这条规则"选什么、改什么"，不用去翻 JSON。
    /// 例：
    ///   A1_TITLE → A1_TITLE_NEW
    ///   LINE,LWPOLYLINE,CIRCLE [色1] → 图层 EQUIPMENT
    ///   TEXT,MTEXT [色3] → 文字样式 GB_STANDARD
    ///   HATCH AR-CONC → 图案 ANSI31 比例1 角度45°
    ///
    /// 两条硬性约定（2026-10-29 评审后补）：
    ///   1. **说真话**：`Filter` 里每一个"已设置"的字段，要么出现在摘要里，要么被计入末尾的"+N 项条件"。
    ///      早期版本漏掉了 `LayerNames`、`Scope`、`TrueColor` 等字段，导致
    ///      `LayerNames=[A,B] → 图层 X` 被渲染成 `(全部) → 图层 X`，同事读到"改全图"，实际只改两层。
    ///      现在的做法是反射枚举 `RuleFilter` 的属性做兜底计数——将来新增字段如果忘了写渲染，
    ///      它会自动变成"+1 项条件"，而不是悄悄消失。
    ///   2. **不认识的类型也要有摘要**：自研规则类型不再返回空串，回落成"类型名 + 已设 N 项"。
    ///
    /// 本类不引用任何宿主类型（`Autodesk.AutoCAD.*` / `ZwSoft.ZwCAD.*`），可以被单元测试直接覆盖。
    /// </summary>
    public static class RuleSummary
    {
        private const int DefaultMaxNestingDepth = 8;
        private const string DefaultScope = "ModelAndLayouts";

        private static readonly PropertyInfo[] FilterProperties = SerializableProperties(typeof(RuleFilter));
        private static readonly PropertyInfo[] ActionProperties = SerializableProperties(typeof(RuleAction));

        /// <summary>过滤条件的简短描述。</summary>
        public static string DescribeFilter(RuleFilter filter)
        {
            int unrendered;
            return DescribeFilter(filter, out unrendered);
        }

        /// <summary>过滤条件的简短描述，并给出"已设置但没能显示"的字段数。</summary>
        public static string DescribeFilter(RuleFilter filter, out int unrendered)
        {
            unrendered = 0;
            if (filter == null)
            {
                return "(全部)";
            }

            var sb = new StringBuilder();
            var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---- 块名 ----
            if (!IsBlank(filter.SourceBlockName))
            {
                Append(sb, "块 " + filter.SourceBlockName.Trim());
                rendered.Add("SourceBlockName");
            }

            if (HasItems(filter.SourceBlockNames))
            {
                Append(sb, "块 " + JoinTrimmed(filter.SourceBlockNames));
                rendered.Add("SourceBlockNames");
            }

            // ---- 实体类型 ----
            if (HasItems(filter.EntityTypes))
            {
                Append(sb, JoinTrimmed(filter.EntityTypes));
                rendered.Add("EntityTypes");
            }

            // ---- 颜色 ----
            if (filter.ColorIndex.HasValue)
            {
                sb.Append(" [色").Append(filter.ColorIndex.Value).Append(']');
                rendered.Add("ColorIndex");
            }

            if (filter.ColorIndexes != null && filter.ColorIndexes.Count > 0)
            {
                sb.Append(" [色").Append(string.Join("/", filter.ColorIndexes)).Append(']');
                rendered.Add("ColorIndexes");
            }

            // ---- 真彩色 ----
            if (!IsBlank(filter.TrueColor))
            {
                sb.Append(" [真彩").Append(filter.TrueColor.Trim()).Append(']');
                rendered.Add("TrueColor");
            }

            // ---- 图层 ----
            if (!IsBlank(filter.LayerName))
            {
                sb.Append(" [层").Append(filter.LayerName.Trim()).Append(']');
                rendered.Add("LayerName");
            }

            if (HasItems(filter.LayerNames))
            {
                sb.Append(" [层").Append(JoinTrimmed(filter.LayerNames)).Append(']');
                rendered.Add("LayerNames");
            }

            // ---- 空间 / 图框 / 区域 ----
            if (HasItems(filter.LayoutPatterns))
            {
                sb.Append(" [布局").Append(JoinTrimmed(filter.LayoutPatterns)).Append(']');
                rendered.Add("LayoutPatterns");
            }

            if (!IsBlank(filter.ContainerBlockPattern))
            {
                sb.Append(" [图框").Append(filter.ContainerBlockPattern.Trim()).Append(']');
                rendered.Add("ContainerBlockPattern");
            }

            if (!IsBlank(filter.ContainerAttributeTag))
            {
                sb.Append(" [图框属性 ").Append(filter.ContainerAttributeTag.Trim()).Append(']');
                rendered.Add("ContainerAttributeTag");
            }

            if (!IsBlank(filter.ContainerAttributePattern))
            {
                rendered.Add("ContainerAttributePattern");
            }

            if (!IsBlank(filter.RegionMode))
            {
                sb.Append(" [区域").Append(filter.RegionMode.Trim());
                if (HasItems(filter.RegionPoints))
                {
                    sb.Append(' ').Append(filter.RegionPoints.Count).Append("点");
                }

                sb.Append(']');
                rendered.Add("RegionMode");
                rendered.Add("RegionPoints");
            }

            if (!IsBlank(filter.RegionPredicate))
            {
                rendered.Add("RegionPredicate");
            }

            if (filter.RegionIncludesNested.HasValue)
            {
                rendered.Add("RegionIncludesNested");
            }

            // ---- 填充图案 ----
            if (!IsBlank(filter.SourcePatternName))
            {
                sb.Append(" [图案").Append(filter.SourcePatternName.Trim()).Append(']');
                rendered.Add("SourcePatternName");
            }

            if (HasItems(filter.SourcePatternNames))
            {
                sb.Append(" [图案").Append(JoinTrimmed(filter.SourcePatternNames)).Append(']');
                rendered.Add("SourcePatternNames");
            }

            // ---- 样式 ----
            if (!IsBlank(filter.TextStyleName))
            {
                sb.Append(" [样式").Append(filter.TextStyleName.Trim()).Append(']');
                rendered.Add("TextStyleName");
            }

            if (!IsBlank(filter.DimensionStyleName))
            {
                sb.Append(" [标注").Append(filter.DimensionStyleName.Trim()).Append(']');
                rendered.Add("DimensionStyleName");
            }

            // ---- 扫描范围 / 遍历开关（非默认时才显示，默认值写出来只是噪声）----
            if (!IsBlank(filter.Scope)
                && !string.Equals(filter.Scope.Trim(), DefaultScope, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" [范围").Append(DescribeScope(filter.Scope)).Append(']');
                rendered.Add("Scope");
            }

            if (filter.IncludeNestedBlocks.HasValue && !filter.IncludeNestedBlocks.Value)
            {
                sb.Append(" [不含块内嵌套]");
                rendered.Add("IncludeNestedBlocks");
            }

            if (filter.SkipLockedLayers.HasValue && !filter.SkipLockedLayers.Value)
            {
                sb.Append(" [不跳过锁定层]");
                rendered.Add("SkipLockedLayers");
            }

            if (filter.MaxNestingDepth.HasValue && filter.MaxNestingDepth.Value != DefaultMaxNestingDepth)
            {
                sb.Append(" [深度").Append(filter.MaxNestingDepth.Value).Append(']');
                rendered.Add("MaxNestingDepth");
            }

            // ---- 兜底：已设置但没被渲染的字段计数 ----
            unrendered = CountUnrendered(filter, rendered);
            if (unrendered > 0)
            {
                sb.Append(" +").Append(unrendered).Append(" 项条件");
            }

            return sb.Length == 0 ? "(全部)" : sb.ToString();
        }

        /// <summary>
        /// 是否存在"已设置但没能在摘要里显示"的条件。
        /// 供界面决定是否需要给出展开态；目前所有字段都能渲染，所以通常是 false。
        /// </summary>
        public static bool HasUnrenderedConditions(RuleFilter filter)
        {
            if (filter == null)
            {
                return false;
            }

            int unrendered;
            DescribeFilter(filter, out unrendered);
            return unrendered > 0;
        }

        /// <summary>操作目标的简短描述。</summary>
        public static string DescribeAction(RuleConfig rule)
        {
            if (rule == null || rule.Action == null)
            {
                return string.Empty;
            }

            RuleAction a = rule.Action;
            string type = rule.Type == null ? string.Empty : rule.Type.Trim();

            if (Eq(type, "BlockReplace"))
            {
                return "→ 块 " + (a.TargetBlockName ?? "?")
                       + (ConfigValues.Bool(a.AllowDynamicBlock, false) ? "（含动态块）" : string.Empty);
            }

            if (Eq(type, "EntityLayerChange"))
            {
                return "→ 图层 " + (a.TargetLayer ?? "?") + LayerTraitSuffix(a);
            }

            if (Eq(type, "BlockLayerChange"))
            {
                // 块内对象默认一并归层（只挪块参照的话，图上看起来"没改"，见 BlockLayerChangeRule）；
                // 属性默认跟随块内对象开关，所以这里的默认值也要跟规则里一致，否则摘要会说反话
                bool content = ConfigValues.Bool(a.AlsoMoveBlockContent, true);
                bool attributes = ConfigValues.Bool(a.AlsoMoveAttributes, content);

                var moved = new List<string>();
                if (content) moved.Add("块内对象");
                if (attributes) moved.Add("属性");

                return "→ 图层 " + (a.TargetLayer ?? "?")
                       + (moved.Count == 0 ? string.Empty : "（含" + string.Join("、", moved.ToArray()) + "）")
                       + LayerTraitSuffix(a);
            }

            if (Eq(type, "TextFontChange"))
            {
                return "→ 文字样式 " + (a.TargetTextStyle ?? "?")
                       + (string.IsNullOrEmpty(a.FontFileName) ? string.Empty : " / " + a.FontFileName);
            }

            if (Eq(type, "EntityLinetypeChange"))
            {
                return "→ 线型 " + (ConfigValues.Bool(a.ForceByLayer, false) ? "ByLayer" : (a.LinetypeName ?? "?"));
            }

            if (Eq(type, "EntityLineweightChange"))
            {
                return "→ 线宽 " + (ConfigValues.Bool(a.ForceByLayer, false)
                    ? "ByLayer"
                    : (a.LineWeight.HasValue ? a.LineWeight.Value + " (0.01mm)" : "?"));
            }

            if (Eq(type, "DimensionStyleChange"))
            {
                return "→ 标注样式 " + (a.TargetDimensionStyle ?? "?");
            }

            if (Eq(type, "TextHeightChange"))
            {
                return "→ 字高 " + (a.Height.HasValue ? a.Height.Value.ToString("0.###") : "?");
            }

            if (Eq(type, "SetByLayer"))
            {
                var parts = new List<string>();
                if (ConfigValues.Bool(a.ForceColorByLayer, true)) parts.Add("颜色");
                if (ConfigValues.Bool(a.ForceLinetypeByLayer, true)) parts.Add("线型");
                if (ConfigValues.Bool(a.ForceLineWeightByLayer, true)) parts.Add("线宽");
                if (ConfigValues.Bool(a.ForcePlotStyleByLayer, true)) parts.Add("打印样式");
                return "→ 随层：" + string.Join("/", parts.ToArray());
            }

            if (Eq(type, "HatchPatternChange"))
            {
                if (!string.IsNullOrWhiteSpace(a.TargetGradientName))
                {
                    return "→ 渐变 " + a.TargetGradientName;
                }

                var sb = new StringBuilder("→ 图案 " + (a.TargetPatternName ?? "?"));
                if (a.PatternScale.HasValue) sb.Append(" 比例").Append(a.PatternScale.Value.ToString("0.###"));
                if (a.PatternAngle.HasValue) sb.Append(" 角度").Append(a.PatternAngle.Value.ToString("0.###")).Append('°');
                return sb.ToString();
            }

            if (Eq(type, "PurgeUnused"))
            {
                return a.PurgeCategories == null || a.PurgeCategories.Count == 0
                    ? "→ 清理全部类别"
                    : "→ 清理 " + string.Join("/", a.PurgeCategories.ToArray());
            }

            if (Eq(type, "TextReplace"))
            {
                string mode = string.IsNullOrWhiteSpace(a.MatchMode) ? "包含" : a.MatchMode.Trim();
                return "→ 替换文字 " + mode + " \"" + (a.SearchText ?? "?") + "\" ⇒ \"" + (a.Replacement ?? string.Empty) + "\"";
            }

            if (Eq(type, "TextMask"))
            {
                string maskMode = string.IsNullOrWhiteSpace(a.MaskMode) ? "Auto" : a.MaskMode.Trim();
                return "→ 文字遮罩 " + maskMode
                       + (ConfigValues.Bool(a.MaskOnlyWhenOccluded, true) ? "（仅遮挡时）" : "（全部文字）")
                       + " 层 " + (string.IsNullOrWhiteSpace(a.MaskLayer) ? "_MASK" : a.MaskLayer.Trim());
            }

            if (Eq(type, "DimTextAudit"))
            {
                double tol = ConfigValues.Double(a.AuditTolerance, 0.5);
                return "→ 审计标注文字（容差 ±" + tol.ToString("0.###") + "，只报告）";
            }

            if (Eq(type, "AttributeAudit"))
            {
                string tags = a.RequiredAttributeTags == null
                    ? "?"
                    : string.Join("/", a.RequiredAttributeTags.ToArray());
                return "→ 检查必填属性 " + tags
                       + (ConfigValues.Bool(a.AttributeAuditFillDefaults, false) ? "（空值按默认值填充）" : "（只报告）");
            }

            if (Eq(type, "LayerMap"))
            {
                int count = Rules.LayerMapRule.ParseMappings(a.LayerMappings).Count;
                return "→ 图层映射 " + count + " 条"
                       + (ConfigValues.Bool(a.LayerMapDeleteEmpty, false) ? "（含删空源层）" : string.Empty);
            }

            if (Eq(type, "LayoutRename"))
            {
                int count = Rules.LayoutRenameRule.ParseMap(a.LayoutRenameMap).Count;
                return "→ 布局改名 " + count + " 条"
                       + (string.IsNullOrWhiteSpace(a.LayoutRenameSyncTag) ? string.Empty : "（同步属性 " + a.LayoutRenameSyncTag + "）");
            }

            if (Eq(type, "TitleBlockNormalize"))
            {
                int count = Rules.TitleBlockNormalizeRule.ParseMap(a.TitleBlockAttributeMap).Count;
                return "→ 写标题栏 " + count + " 个属性"
                       + (ConfigValues.Bool(a.TitleBlockOnlyIfEmpty, true) ? "（只填空）" : "（强制改写）");
            }

            if (Eq(type, "GeometryCleanup"))
            {
                var parts = new List<string>();
                if (ConfigValues.Bool(a.CleanZeroLength, true)) parts.Add("零长线");
                if (ConfigValues.Bool(a.CleanEmptyText, true)) parts.Add("空文字");
                if (ConfigValues.Bool(a.CleanDuplicateEntities, false)) parts.Add("重复实体");
                return "→ 清理 " + string.Join("/", parts.ToArray())
                       + (ConfigValues.Bool(a.CleanupDryRun, true) ? "（仅预演）" : "（真删）");
            }

            if (Eq(type, "BomSync"))
            {
                int count = a.BomColumnTags == null ? 0 : a.BomColumnTags.Count;
                return "→ 材料表导出 " + count + " 列"
                       + (string.IsNullOrWhiteSpace(a.BomComparePath) ? string.Empty : " + 比对");
            }

            // 未识别的类型（自研规则）：不要返回空串，否则同事看到一条没有任何说明的规则
            return DescribeUnknownAction(a, type);
        }

        /// <summary>整条规则的一行摘要："过滤 → 操作"。</summary>
        public static string Describe(RuleConfig rule)
        {
            if (rule == null)
            {
                return string.Empty;
            }

            string filter = DescribeFilter(rule.Filter);
            string action = DescribeAction(rule);

            if (string.IsNullOrEmpty(action))
            {
                return filter;
            }

            return filter + " " + action;
        }

        // ------------------------------------------------------------------ 内部

        /// <summary>
        /// 新建图层特性里"说真话"的那一项：只有显式关掉打印才补一句，
        /// 默认值（可打印）不写出来——摘要里每多一句默认值都是噪声。
        /// </summary>
        private static string LayerTraitSuffix(RuleAction action)
        {
            return action != null && action.LayerPlottable.HasValue && !action.LayerPlottable.Value
                ? "（新图层不打印）"
                : string.Empty;
        }

        /// <summary>未知类型的动作描述：报类型名 + 已设置的字段数，让读者知道"这条规则有配置，只是不认类型"。</summary>
        private static string DescribeUnknownAction(RuleAction action, string type)
        {
            int set = CountSetFields(action, ActionProperties);
            string name = string.IsNullOrEmpty(type) ? "(未填 Type)" : type;

            if (set == 0)
            {
                return "→ " + name;
            }

            return string.Format("→ {0}（已设 {1} 项）", name, set);
        }

        private static int CountUnrendered(RuleFilter filter, HashSet<string> rendered)
        {
            int count = 0;
            foreach (PropertyInfo p in FilterProperties)
            {
                if (rendered != null && rendered.Contains(p.Name))
                {
                    continue;
                }

                if (IsSet(p.GetValue(filter, null)))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSetFields(object target, PropertyInfo[] properties)
        {
            if (target == null)
            {
                return 0;
            }

            int count = 0;
            foreach (PropertyInfo p in properties)
            {
                object value;
                try
                {
                    value = p.GetValue(target, null);
                }
                catch
                {
                    continue;
                }

                if (IsSet(value))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>字段是否算"已设置"：非 null、非空白字符串、非空集合。</summary>
        private static bool IsSet(object value)
        {
            if (value == null)
            {
                return false;
            }

            var text = value as string;
            if (text != null)
            {
                return !string.IsNullOrWhiteSpace(text);
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                IEnumerator e = enumerable.GetEnumerator();
                try
                {
                    return e.MoveNext();
                }
                finally
                {
                    var disposable = e as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
            }

            return true;
        }

        private static PropertyInfo[] SerializableProperties(Type type)
        {
            var list = new List<PropertyInfo>();
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                list.Add(p);
            }

            return list.ToArray();
        }

        private static void Append(StringBuilder sb, string text)
        {
            if (sb.Length > 0)
            {
                sb.Append(" / ");
            }

            sb.Append(text);
        }

        private static string JoinTrimmed(List<string> values)
        {
            var list = new List<string>();
            foreach (string v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    list.Add(v.Trim());
                }
            }

            return string.Join("/", list.ToArray());
        }

        private static bool HasItems(List<string> values)
        {
            if (values == null)
            {
                return false;
            }

            foreach (string v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBlank(string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        /// <summary>把配置里的 Scope 翻成中文；未知取值原样显示，避免摘要"吃掉"用户写错的值。</summary>
        private static string DescribeScope(string scope)
        {
            switch ((scope ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "MODELANDLAYOUTS":
                case "ALL":
                case "MODEL_AND_LAYOUTS":
                    return "模型+布局";
                case "MODELONLY":
                case "MODEL":
                    return "仅模型";
                case "LAYOUTSONLY":
                case "LAYOUTS":
                case "PAPER":
                    return "仅布局";
                case "SELECTION":
                case "SELECT":
                    return "当前选择集";
                default:
                    return scope.Trim();
            }
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
