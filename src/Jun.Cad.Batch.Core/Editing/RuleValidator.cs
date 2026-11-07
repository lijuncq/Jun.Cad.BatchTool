using System;
using System.Collections.Generic;
using System.Globalization;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Newtonsoft.Json.Linq;

namespace Jun.Cad.Batch.Editing
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
            else if (string.Equals(type, "TextReplace", StringComparison.OrdinalIgnoreCase))
            {
                Require(issues, "Action.SearchText", action.SearchText, "查找内容（Action.SearchText）");

                if (!string.IsNullOrEmpty(action.SearchText))
                {
                    string error;
                    if (TextMatcher.Create(action.MatchMode, action.SearchText, action.Replacement,
                            ConfigValues.Bool(action.CaseSensitive, false), out error) == null)
                    {
                        issues.Add(Error("Action.MatchMode", error));
                    }

                    if (string.IsNullOrWhiteSpace(action.Replacement)
                        && !ConfigValues.Bool(action.AllowEmptyResult, false))
                    {
                        issues.Add(Warning("Action.Replacement",
                            "替换内容为空，但 AllowEmptyResult=false：匹配到的文字会被跳过而不是清空。"));
                    }
                }
            }
            else if (string.Equals(type, "TextMask", StringComparison.OrdinalIgnoreCase))
            {
                string mode = (action.MaskMode ?? string.Empty).Trim();
                if (mode.Length > 0
                    && !string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mode, "Wipeout", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error("Action.MaskMode",
                        "MaskMode must be Auto or Wipeout; both use native masks. Solid and other values are not supported."));
                }

                if (action.Extra != null)
                {
                    foreach (var field in action.Extra)
                    {
                        if (string.Equals(field.Key, "MaskSolidColorIndex", StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(Warning("Action.MaskSolidColorIndex",
                                "MaskSolidColorIndex is deprecated and ignored. Native masks use the drawing background color; no RGB fill option is available."));
                        }
                        else if (string.Equals(field.Key, "MaskPadding", StringComparison.OrdinalIgnoreCase)
                                 && field.Value != null && field.Value.Type != JTokenType.Null)
                        {
                            double padding;
                            if (!double.TryParse(field.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out padding)
                                || padding != 0)
                            {
                                issues.Add(Error("Action.MaskPadding",
                                    "MaskPadding is retired; only zero can be ignored. Remove it and use MaskPaddingRatio (default 0.15 = 15% of text character height per side)."));
                            }
                        }
                    }
                }

                if (action.MaskPaddingRatio.HasValue
                    && (double.IsNaN(action.MaskPaddingRatio.Value)
                        || double.IsInfinity(action.MaskPaddingRatio.Value)
                        || action.MaskPaddingRatio.Value < 0 || action.MaskPaddingRatio.Value > 2))
                {
                    issues.Add(Error("Action.MaskPaddingRatio",
                        "MaskPaddingRatio must be finite and between 0 and 2; each side uses text character height × ratio (0.15 = 15%), not bounding-box height."));
                }

                string textBox = (action.MaskTextBox ?? string.Empty).Trim();
                if (textBox.Length > 0
                    && !string.Equals(textBox, "Auto", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(textBox, "Native", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(textBox, "Tight", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error("Action.MaskTextBox",
                        "MaskTextBox must be Auto (default), Native, or Tight. Auto keeps the native background when the text frame fits the text and switches to a tight WIPEOUT when the frame is looser."));
                }

                // 运行期的两个 Normalize* 已改成白名单 + 硬错误，校验器必须同口径，
                // 否则界面能保存、执行时才炸。
                string dimMaskMode = NormalizeText(action.DimensionMaskMode);
                if (dimMaskMode.Length > 0
                    && !string.Equals(dimMaskMode, "off", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dimMaskMode, "native", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dimMaskMode, "blockinternal", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error("Action.DimensionMaskMode",
                        "DimensionMaskMode=\"" + action.DimensionMaskMode.Trim() + "\" 不受支持：可选 Off / Native（默认）；旧值 BlockInternal 按 Native 处理。"));
                }

                string dimTextMode = NormalizeText(action.DimensionTextMode);
                if (dimTextMode.Length > 0
                    && !string.Equals(dimTextMode, "all", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dimTextMode, "explicitonly", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dimTextMode, "off", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error("Action.DimensionTextMode",
                        "DimensionTextMode=\"" + action.DimensionTextMode.Trim() + "\" 不受支持：可选 All（默认）/ ExplicitOnly / Off。"));
                }

                if (action.MaskMinOccludedRatio.HasValue
                    && (action.MaskMinOccludedRatio.Value < 0 || action.MaskMinOccludedRatio.Value > 1))
                {
                    issues.Add(Error("Action.MaskMinOccludedRatio", "MaskMinOccludedRatio 应在 0 ~ 1 之间。"));
                }

                // 「标注文字要不要处理」有两个开关，很容易互相打架：
                //   Action.DimensionTextMode  只决定"哪些标注"（全部 / 仅显式改过文字的 / 不处理）
                //   Filter.EntityTypes        决定"扫不扫 DIMENSION"
                // 用户写了 DimensionTextMode=All 却没把 DIMENSION 放进 EntityTypes 时，
                // 标注会被过滤器直接筛掉，DimensionTextMode 完全不起作用——而界面上看不出任何异常，
                // 只能在图上发现"标注文字还是没遮罩"。这里必须点出来。
                if (!string.Equals(NormalizeText(action.DimensionTextMode), "off", StringComparison.OrdinalIgnoreCase)
                    && filter.EntityTypes != null
                    && filter.EntityTypes.Count > 0
                    && !ContainsEntityType(filter.EntityTypes, "DIMENSION"))
                {
                    issues.Add(Warning("Filter.EntityTypes", string.Format(
                        "Filter.EntityTypes excludes DIMENSION, so Action.DimensionTextMode={0} cannot affect dimension text. "
                        + "Add DIMENSION or omit EntityTypes to include all supported text (TEXT / MTEXT / ATTDEF / ATTRIB / DIMENSION).",
                        string.IsNullOrWhiteSpace(action.DimensionTextMode) ? "All" : action.DimensionTextMode.Trim())));
                }

                if (ConfigValues.Bool(action.MaskOnlyWhenOccluded, false))
                {
                    issues.Add(Warning("Action.MaskOnlyWhenOccluded",
                        "MaskOnlyWhenOccluded 为实验性开关、结果不可靠：遮挡判定未纳入块参照（Insert）几何，0.05 面积比阈值与每条线 0.25 权重仍为启发式；建议保持 false（默认）。"));
                }
            }
            else if (string.Equals(type, "DimTextAudit", StringComparison.OrdinalIgnoreCase))
            {
                if (action.AuditTolerance.HasValue && action.AuditTolerance.Value < 0)
                {
                    issues.Add(Error("Action.AuditTolerance", "Action.AuditTolerance 不能为负。"));
                }

                if (action.AuditTolerancePercent.HasValue
                    && (action.AuditTolerancePercent.Value < 0 || action.AuditTolerancePercent.Value > 100))
                {
                    issues.Add(Error("Action.AuditTolerancePercent", "Action.AuditTolerancePercent 应在 0 ~ 100 之间。"));
                }
            }
            else if (string.Equals(type, "AttributeAudit", StringComparison.OrdinalIgnoreCase))
            {
                bool any = false;
                if (action.RequiredAttributeTags != null)
                {
                    foreach (string tag in action.RequiredAttributeTags)
                    {
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            any = true;
                            break;
                        }
                    }
                }

                if (!any)
                {
                    issues.Add(Error("Action.RequiredAttributeTags",
                        "至少要填一个必填属性 Tag，否则这条规则没有任何检查对象。"));
                }

                if (ConfigValues.Bool(action.AttributeAuditFillDefaults, false)
                    && string.IsNullOrWhiteSpace(action.AttributeDefaults))
                {
                    issues.Add(Warning("Action.AttributeDefaults",
                        "打开了「按默认值填充」，但没写默认值：空属性会被报告而不会被填充。"));
                }
            }
            else if (string.Equals(type, "LayerMap", StringComparison.OrdinalIgnoreCase))
            {
                if (Rules.LayerMapRule.ParseMappings(action.LayerMappings).Count == 0)
                {
                    issues.Add(Error("Action.LayerMappings",
                        "图层映射为空或格式不对。写法：\"旧层名模式=新层名\"，旧名支持 * 和 ?。"));
                }
            }
            else if (string.Equals(type, "LayoutRename", StringComparison.OrdinalIgnoreCase))
            {
                if (Rules.LayoutRenameRule.ParseMap(action.LayoutRenameMap).Count == 0)
                {
                    issues.Add(Error("Action.LayoutRenameMap",
                        "布局改名映射为空或格式不对。写法：\"旧名模式=新名\"，例如 \"Layout*=V-{n}\"。"));
                }
            }
            else if (string.Equals(type, "TitleBlockNormalize", StringComparison.OrdinalIgnoreCase))
            {
                if (Rules.TitleBlockNormalizeRule.ParseMap(action.TitleBlockAttributeMap).Count == 0)
                {
                    issues.Add(Error("Action.TitleBlockAttributeMap",
                        "属性写入表为空。写法：\"TAG=值\"，值可用 {filename} {layout} {date} {index}。"));
                }

                // 与运行期的护栏同一条判断：没有块名模式会把属性写进图上**每一个**带同名 Tag 的块
                if (string.IsNullOrWhiteSpace(filter.SourceBlockName)
                    && string.IsNullOrWhiteSpace(filter.ContainerBlockPattern))
                {
                    issues.Add(Error("Filter.SourceBlockName",
                        "必须指定 SourceBlockName（图框块名模式）或 ContainerBlockPattern，否则会写进所有带同名 Tag 的块。"));
                }
            }
            else if (string.Equals(type, "GeometryCleanup", StringComparison.OrdinalIgnoreCase))
            {
                bool any = ConfigValues.Bool(action.CleanZeroLength, true)
                           || ConfigValues.Bool(action.CleanEmptyText, true)
                           || ConfigValues.Bool(action.CleanDuplicateEntities, false);

                if (!any)
                {
                    issues.Add(Error("Action.CleanZeroLength", "三个清理开关都关着，这条规则不会做任何事。"));
                }

                if (!ConfigValues.Bool(action.CleanupDryRun, true))
                {
                    issues.Add(Warning("Action.CleanupDryRun",
                        "CleanupDryRun=false：会真的删除对象。建议先用默认的预演跑一遍，看清单对不对。"));
                }

                if (ConfigValues.Bool(action.CleanDuplicateEntities, false))
                {
                    issues.Add(Warning("Action.CleanDuplicateEntities",
                        "重复实体判定是\"同类型+同图层+包围盒一致\"，有意叠加的对象也可能被删。"));
                }
            }
            else if (string.Equals(type, "BomSync", StringComparison.OrdinalIgnoreCase))
            {
                if (action.BomColumnTags == null || !action.BomColumnTags.Exists(x => !string.IsNullOrWhiteSpace(x)))
                {
                    issues.Add(Error("Action.BomColumnTags", "至少要填一个材料表属性 Tag。"));
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

            // ---- 通用：写操作规则必须至少有一个过滤条件 ----
            // 这条护栏原来只写在 ConfigLoader 里，合并校验后挪到这里：
            // 命令行与界面看到的是同一个判断，不会出现"界面拦住了、命令行照跑"。
            if (RequiresAnyCondition(type) && !HasAnyCondition(filter, type))
            {
                issues.Add(Error("Filter.EntityTypes",
                    "没有任何过滤条件（EntityTypes / ColorIndex / LayerName 全为空），执行时会中止以避免误改整张图。"));
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

        /// <summary>这些规则在没有过滤条件时会误改整张图，必须要求至少一个条件。</summary>
        private static bool RequiresAnyCondition(string type)
        {
            switch ((type ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "ENTITYLAYERCHANGE":
                case "SETBYLAYER":
                case "ENTITYLINETYPECHANGE":
                case "ENTITYLINEWEIGHTCHANGE":
                case "HATCHPATTERNCHANGE":
                case "TEXTFONTCHANGE":
                case "TEXTHEIGHTCHANGE":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 与 <see cref="Engine.EntityFilter.HasCondition"/> 同一口径：空间/图框/区域也算条件。
        ///
        /// 另外把**规则自己实现**的过滤字段也算进来（这些字段不走 EntityFilter，
        /// 在规则内部逐条判定）。必须按类型判断，不能一律算数：
        /// <c>TextStyleName</c> 只有 TextFontChange 会真的用它过滤，
        /// TextHeightChange 根本不理这个字段——一律算数的话，
        /// "只写了 TextStyleName 的 TextHeightChange"就会被放行，然后改掉整张图的字高。
        /// </summary>
        private static bool HasAnyCondition(RuleFilter f, string type)
        {
            if (f == null)
            {
                return false;
            }

            if ((f.EntityTypes != null && f.EntityTypes.Exists(x => !string.IsNullOrWhiteSpace(x)))
                || !string.IsNullOrWhiteSpace(f.SourcePatternName)
                || (f.SourcePatternNames != null && f.SourcePatternNames.Exists(x => !string.IsNullOrWhiteSpace(x)))
                || f.ColorIndex.HasValue
                || (f.ColorIndexes != null && f.ColorIndexes.Count > 0)
                || !string.IsNullOrWhiteSpace(f.LayerName)
                || (f.LayerNames != null && f.LayerNames.Exists(x => !string.IsNullOrWhiteSpace(x)))
                || !string.IsNullOrWhiteSpace(f.TrueColor)
                || (f.LayoutPatterns != null && f.LayoutPatterns.Exists(x => !string.IsNullOrWhiteSpace(x)))
                || !string.IsNullOrWhiteSpace(f.ContainerBlockPattern)
                || (!string.IsNullOrWhiteSpace(f.RegionMode)
                    && f.RegionPoints != null && f.RegionPoints.Count > 0))
            {
                return true;
            }

            // 规则内部的过滤字段：只有对应规则才认
            if (IsType(type, "TextFontChange") && !string.IsNullOrWhiteSpace(f.TextStyleName))
            {
                return true;
            }

            if (IsType(type, "DimensionStyleChange") && !string.IsNullOrWhiteSpace(f.DimensionStyleName))
            {
                return true;
            }

            return false;
        }

        /// <summary>去掉首尾空白；null 变空串。用来做大小写无关的枚举值比较。</summary>
        private static string NormalizeText(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }

        private static bool ContainsEntityType(List<string> types, string expected)
        {
            foreach (string type in types)
            {
                if (string.Equals((type ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsType(string type, string expected)
        {
            return string.Equals((type ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase);
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
