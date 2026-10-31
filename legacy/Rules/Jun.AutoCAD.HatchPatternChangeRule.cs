using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using HostException = ZwSoft.ZwCAD.Runtime.Exception;
using HostErrorStatus = ZwSoft.ZwCAD.Runtime.ErrorStatus;
#else
using Autodesk.AutoCAD.DatabaseServices;
using HostException = Autodesk.AutoCAD.Runtime.Exception;
using HostErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 扩展规则：批量替换填充图案（Hatch）。
    ///
    /// 典型场景：
    ///   * 各专业图纸的混凝土/钢材/保温填充不统一 → 统一成企业标准的 ANSI31 / AR-CONC；
    ///   * 外来图纸的填充比例五花八门 → 统一比例与角度；
    ///   * 纯色填充（SOLID）批量改成图案填充，或图案填充批量改成渐变填充。
    ///
    /// 过滤：实体类型（默认 HATCH）+ 颜色索引 + 图层 + 源图案名（SourcePatternName）。
    /// 操作：目标图案名 / 图案类型 / 比例 / 角度 / 间距 / 双线 / 填充样式；也可切到渐变填充。
    ///
    /// 实现要点：
    ///   1. Hatch.PatternName / PatternType 是**只读**属性，换图案必须调用
    ///      SetHatchPattern(HatchPatternType, string)，它会重建图案定义并保留填充边界（Loops）；
    ///   2. 图案名必须在 acad.pat / acadiso.pat 中存在，否则 SetHatchPattern 抛 eInvalidInput——
    ///      本规则在**第一次**失败时就中止整条规则并提示，避免几百个填充逐个报错；
    ///   3. 角度在 API 中是弧度，配置里用【度】；
    ///   4. 用户自定义图案（UserDefined）用 PatternSpace/PatternDouble，预定义图案用 PatternScale；
    ///   5. 关联填充（Associative）只换图案、不动边界，关联关系保持不变。
    /// </summary>
    public sealed class HatchPatternChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "HatchPatternChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            var tr = ctx.Transaction;

            string targetPattern = ctx.Action.TargetPatternName == null ? null : ctx.Action.TargetPatternName.Trim();
            string targetGradient = ctx.Action.TargetGradientName == null ? null : ctx.Action.TargetGradientName.Trim();
            bool toGradient = !string.IsNullOrEmpty(targetGradient);

            if (!toGradient && string.IsNullOrEmpty(targetPattern))
            {
                throw new RuleValidationException(
                    "HatchPatternChange 规则缺少 Action.TargetPatternName（或 Action.TargetGradientName）。");
            }

            HatchPatternType patternType = ParsePatternType(ctx.Action.PatternType);
            GradientPatternType gradientType = ParseGradientType(ctx.Action.GradientType);

            // 未指定实体类型时只看填充
            if (ctx.Filter.EntityTypes == null || ctx.Filter.EntityTypes.Count == 0)
            {
                ctx.Filter.EntityTypes = new List<string> { "HATCH" };
            }

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "HatchPatternChange 规则没有任何过滤条件，为避免把整张图的填充都改掉，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            if (toGradient)
            {
                ctx.Log.Detail(string.Format("目标：渐变填充「{0}」({1})", targetGradient, gradientType));
            }
            else
            {
                ctx.Log.Detail(string.Format("目标：图案「{0}」({1})；比例 {2}；角度 {3}°；间距 {4}",
                    targetPattern,
                    patternType,
                    ctx.Action.PatternScale.HasValue ? ctx.Action.PatternScale.Value.ToString("0.###") : "(保持)",
                    ctx.Action.PatternAngle.HasValue ? ctx.Action.PatternAngle.Value.ToString("0.###") : "(保持)",
                    ctx.Action.PatternSpace.HasValue ? ctx.Action.PatternSpace.Value.ToString("0.###") : "(保持)"));
            }

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            bool firstAttempt = true;

            // 同一属性告警只打印一次：避免几百个填充重复刷同一条警告
            var warned = new HashSet<string>(StringComparer.Ordinal);

            foreach (ScanHit hit in hits)
            {
                Entity ent = null;
                try
                {
                    ent = tr.GetObject(hit.Id, OpenMode.ForRead, false) as Entity;
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("{0} 打开失败：{1}", ObjectUtil.HandleOf(hit.Id), ex.Message));
                    continue;
                }

                var hatch = ent as Hatch;
                if (hatch == null || hatch.IsErased)
                {
                    continue;
                }

                // ---- 源图案名过滤（渐变填充按渐变名匹配）----
                if (!MatchesSourcePattern(ctx, hatch))
                {
                    continue;
                }

                result.Hit();
                string desc = ObjectUtil.Describe(hatch);

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, hatch.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, hatch.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                string oldPattern = DescribeCurrent(hatch);

                // ---- 已经合规：图案名相同且比例/角度都无需改 ----
                if (IsAlreadyTarget(ctx, hatch, targetPattern, targetGradient, toGradient))
                {
                    result.MarkSkipped(string.Format("{0}：已是目标填充（{1}），跳过。", desc, oldPattern));
                    continue;
                }

                try
                {
                    hatch.UpgradeOpen();

                    if (toGradient)
                    {
                        hatch.SetGradient(gradientType, targetGradient);
                        ApplyGradientSettings(ctx, hatch, warned);
                    }
                    else
                    {
                        // 换图案：保留填充边界（Loops）与关联性
                        hatch.SetHatchPattern(patternType, targetPattern);
                        ApplyPatternSettings(ctx, hatch, patternType, warned);
                    }

                    string detail = string.Format("{0}：{1} → {2}", desc, oldPattern, DescribeCurrent(hatch));
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (HostException ex)
                {
                    string msg = string.Format("{0} 替换填充失败（{1}）：{2}", desc, ex.ErrorStatus, ex.Message);

                    // 第一次就失败（多半是目标图案名不在 acad.pat 里）→ 直接中止整条规则，
                    // 避免几百个填充逐个刷错误日志
                    if (firstAttempt && ex.ErrorStatus == HostErrorStatus.InvalidInput)
                    {
                        throw new RuleValidationException(
                            string.Format("目标图案「{0}」不可用（{1}）。请确认图案名在 acad.pat / acadiso.pat 中存在，" +
                                          "或改用 UserDefined 类型并指定 PatternSpace。原始错误：{2}",
                                targetPattern, patternType, ex.Message), ex);
                    }

                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 替换填充失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
                finally
                {
                    firstAttempt = false;
                }
            }
        }

        /// <summary>按源图案名过滤；未配置则全部通过。</summary>
        private static bool MatchesSourcePattern(RuleExecutionContext ctx, Hatch hatch)
        {
            var wanted = new List<string>();

            if (!string.IsNullOrWhiteSpace(ctx.Filter.SourcePatternName))
            {
                wanted.Add(ctx.Filter.SourcePatternName.Trim());
            }

            if (ctx.Filter.SourcePatternNames != null)
            {
                foreach (string s in ctx.Filter.SourcePatternNames)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        wanted.Add(s.Trim());
                    }
                }
            }

            if (wanted.Count == 0)
            {
                return true;
            }

            string current = hatch.PatternName ?? string.Empty;
            string gradient = SafeGradientName(hatch);

            foreach (string name in wanted)
            {
                if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)
                    || (gradient != null && string.Equals(gradient, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>是否已经是目标状态（避免无意义地重建图案定义）。</summary>
        private static bool IsAlreadyTarget(
            RuleExecutionContext ctx,
            Hatch hatch,
            string targetPattern,
            string targetGradient,
            bool toGradient)
        {
            if (toGradient)
            {
                if (!hatch.IsGradient)
                {
                    return false;
                }

                return string.Equals(SafeGradientName(hatch), targetGradient, StringComparison.OrdinalIgnoreCase)
                       && !ctx.Action.GradientAngle.HasValue
                       && !ctx.Action.GradientShift.HasValue;
            }

            if (hatch.IsGradient)
            {
                return false;
            }

            if (!string.Equals(hatch.PatternName, targetPattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ctx.Action.PatternScale.HasValue && Math.Abs(hatch.PatternScale - ctx.Action.PatternScale.Value) > 1e-9)
            {
                return false;
            }

            if (ctx.Action.PatternAngle.HasValue)
            {
                double targetRad = ctx.Action.PatternAngle.Value * Math.PI / 180.0;
                if (Math.Abs(hatch.PatternAngle - targetRad) > 1e-6)
                {
                    return false;
                }
            }

            if (ctx.Action.PatternSpace.HasValue && Math.Abs(hatch.PatternSpace - ctx.Action.PatternSpace.Value) > 1e-9)
            {
                return false;
            }

            if (ctx.Action.PatternDouble.HasValue && hatch.PatternDouble != ctx.Action.PatternDouble.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ctx.Action.HatchStyle)
                && !string.Equals(hatch.HatchStyle.ToString(), ctx.Action.HatchStyle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 写入图案参数。逐项 try/catch：某个参数对当前图案不适用（例如给预定义图案设 PatternDouble）
        /// 时只告警，不放弃整次替换。
        /// </summary>
        private static void ApplyPatternSettings(RuleExecutionContext ctx, Hatch hatch, HatchPatternType patternType, HashSet<string> warned)
        {
            var action = ctx.Action;

            if (action.PatternScale.HasValue)
            {
                if (action.PatternScale.Value > 0)
                {
                    TrySet(() => hatch.PatternScale = action.PatternScale.Value, ctx, warned, "PatternScale");
                }
                else
                {
                    ctx.Log.Warn("PatternScale 必须大于 0，已忽略该项。");
                }
            }

            if (action.PatternAngle.HasValue)
            {
                // 配置用度，API 用弧度
                double rad = action.PatternAngle.Value * Math.PI / 180.0;
                TrySet(() => hatch.PatternAngle = rad, ctx, warned, "PatternAngle");
            }

            if (action.PatternSpace.HasValue)
            {
                if (action.PatternSpace.Value > 0)
                {
                    TrySet(() => hatch.PatternSpace = action.PatternSpace.Value, ctx, warned, "PatternSpace");
                }
                else
                {
                    ctx.Log.Warn("PatternSpace 必须大于 0，已忽略该项。");
                }
            }

            if (action.PatternDouble.HasValue)
            {
                TrySet(() => hatch.PatternDouble = action.PatternDouble.Value, ctx, warned, "PatternDouble");
            }

            if (!string.IsNullOrWhiteSpace(action.HatchStyle))
            {
                HatchStyle style;
                if (TryParseHatchStyle(action.HatchStyle, out style))
                {
                    TrySet(() => hatch.HatchStyle = style, ctx, warned, "HatchStyle");
                }
                else
                {
                    ctx.Log.Warn(string.Format("HatchStyle 取值「{0}」无效（可用 Normal / Outer / Ignore），已忽略。", action.HatchStyle));
                }
            }

            if (patternType == HatchPatternType.UserDefined
                && !action.PatternSpace.HasValue
                && !action.PatternScale.HasValue
                && (warned == null || warned.Add("UserDefinedHint")))
            {
                ctx.Log.Detail("提示：用户自定义图案（UserDefined）通常需要 PatternSpace 控制间距。");
            }
        }

        private static void ApplyGradientSettings(RuleExecutionContext ctx, Hatch hatch, HashSet<string> warned)
        {
            var action = ctx.Action;

            if (action.GradientAngle.HasValue)
            {
                double rad = action.GradientAngle.Value * Math.PI / 180.0;
                TrySet(() => hatch.GradientAngle = rad, ctx, warned, "GradientAngle");
            }

            if (action.GradientShift.HasValue)
            {
                float shift = (float)action.GradientShift.Value;
                if (shift < 0f)
                {
                    shift = 0f;
                }
                else if (shift > 1f)
                {
                    shift = 1f;
                }

                TrySet(() => hatch.GradientShift = shift, ctx, warned, "GradientShift");
            }

            if (action.GradientOneColorMode.HasValue)
            {
                TrySet(() => hatch.GradientOneColorMode = action.GradientOneColorMode.Value, ctx, warned, "GradientOneColorMode");
            }
        }

        private static void TrySet(Action setter, RuleExecutionContext ctx, HashSet<string> warned, string name)
        {
            try
            {
                setter();
            }
            catch (Exception ex)
            {
                string msg = string.Format("设置填充属性 {0} 失败（该参数可能不适用于当前图案）：{1}", name, ex.Message);
                if (warned == null || warned.Add(name))
                {
                    ctx.Log.Warn(msg);
                }
                else
                {
                    ctx.Log.Detail(msg);
                }
            }
        }

        private static string DescribeCurrent(Hatch hatch)
        {
            try
            {
                if (hatch.IsGradient)
                {
                    return string.Format("渐变「{0}」", SafeGradientName(hatch) ?? "?");
                }

                return string.Format("图案「{0}」比例 {1:0.###} 角度 {2:0.###}°",
                    hatch.PatternName,
                    hatch.PatternScale,
                    hatch.PatternAngle * 180.0 / Math.PI);
            }
            catch
            {
                return "(读取填充信息失败)";
            }
        }

        private static string SafeGradientName(Hatch hatch)
        {
            try
            {
                return hatch.IsGradient ? hatch.GradientName : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析图案类型字符串（大小写不敏感，容忍 Custom/CustomDefined 等写法）。</summary>
        public static HatchPatternType ParsePatternType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return HatchPatternType.PreDefined;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "USERDEFINED":
                case "USER":
                case "自定义":
                    return HatchPatternType.UserDefined;

                case "CUSTOMDEFINED":
                case "CUSTOM":
                case "自定义文件":
                    return HatchPatternType.CustomDefined;

                default:
                    return HatchPatternType.PreDefined;
            }
        }

        public static GradientPatternType ParseGradientType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return GradientPatternType.PreDefinedGradient;
            }

            return string.Equals(text.Trim(), "UserDefinedGradient", StringComparison.OrdinalIgnoreCase)
                ? GradientPatternType.UserDefinedGradient
                : GradientPatternType.PreDefinedGradient;
        }

        private static bool TryParseHatchStyle(string text, out HatchStyle style)
        {
            switch ((text ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "NORMAL":
                case "普通":
                    style = HatchStyle.Normal;
                    return true;

                case "OUTER":
                case "外部":
                    style = HatchStyle.Outer;
                    return true;

                case "IGNORE":
                case "忽略":
                    style = HatchStyle.Ignore;
                    return true;

                default:
                    style = HatchStyle.Normal;
                    return false;
            }
        }
    }
}
