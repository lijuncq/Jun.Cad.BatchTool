using System;
using System.Collections.Generic;
using System.Globalization;
using ACadSharp.Entities;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量替换填充图案（Hatch）。
    ///
    /// 典型场景：
    ///   * 各专业图纸的混凝土/钢材/保温填充不统一 → 统一成企业标准的 ANSI31 / AR-CONC；
    ///   * 外来图纸的填充比例五花八门 → 统一比例与角度；
    ///   * 纯色填充（SOLID）批量改成图案填充，或图案填充批量改成渐变填充。
    ///
    /// 与 AutoCAD 版的**关键差别**（三个坑，都已在真实图纸上验证过）：
    ///
    ///   1. 换图案不需要 <c>SetHatchPattern</c>：ACadSharp 的 <see cref="Hatch.Pattern"/> 是**可写**的，
    ///      直接赋一个完整的 <see cref="HatchPattern"/>（含 Lines）即可，填充边界（Paths）原样保留。
    ///      只改 <c>Pattern.Name</c> 是不够的——图案线定义不变，AutoCAD 里看起来还是旧图案。
    ///
    ///   2. <see cref="HatchPatternType"/> 的**枚举名是反的**：
    ///      <c>PatternFill = 0</c> 其实是"用户自定义"，<c>SolidFill = 1</c> 才是"预定义"。
    ///      用错会让 AutoCAD 把预定义图案当用户自定义解析，图案直接消失（实测）。
    ///      本类用 <see cref="ToLibraryPatternType"/> 统一转换，不要在别处直接写枚举名。
    ///
    ///   3. 渐变参数不能置 null：<c>h.GradientColor = null</c> 会让写出器抛空引用，
    ///      正确做法是保留对象、只把 <c>Enabled</c> 置 false。
    ///
    ///   4. **换图案必须重算比例**，否则视觉疏密会变：各图案在 .pat 里的基准线距不同
    ///      （ANSI31 = 3.175、JIS_LC_8A = 7.8、JIS_RC_20 = 20），沿用原比例只改图案名，
    ///      画出来会突然变密或变疏。见 <see cref="ApplySpacingMatch"/> 与
    ///      <see cref="HatchPatternMetrics"/>：按"新基准线距 × 新比例 == 旧基准线距 × 旧比例"
    ///      反算比例，做到换图案而疏密不变（<c>Action.MatchSpacing</c>，默认开）。
    ///      关掉它时语义是"沿用原比例"，线距必然变——必须告警（见 <see cref="WarnSpacingChanged"/>），
    ///      否则用户只看到图上变稀却查不到原因。
    ///
    /// 另外：用户自定义图案（UserDefined）的"间距"在 DWG 里不是独立字段，而是编码在
    /// 图案线的 Offset 里。所以 <c>Action.PatternSpace</c> 的实现是"按间距合成一条图案线"，
    /// 而不是给某个属性赋值。
    /// </summary>
    public sealed class HatchPatternChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "HatchPatternChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            string targetPattern = ctx.Action.TargetPatternName == null ? null : ctx.Action.TargetPatternName.Trim();
            string targetGradient = ctx.Action.TargetGradientName == null ? null : ctx.Action.TargetGradientName.Trim();
            bool toGradient = !string.IsNullOrEmpty(targetGradient);

            if (!toGradient && string.IsNullOrEmpty(targetPattern))
            {
                throw new RuleValidationException(
                    "HatchPatternChange 规则缺少 Action.TargetPatternName（或 Action.TargetGradientName）。");
            }

            bool toSolid = !toGradient && string.Equals(targetPattern, "SOLID", StringComparison.OrdinalIgnoreCase);

            HatchPatternType libraryType = ToLibraryPatternType(ParsePatternType(ctx.Action.PatternType));

            // 预先取目标图案定义：取不到就直接中止，别等几百个填充逐个报错
            HatchPattern targetDefinition = null;
            if (!toGradient && !toSolid)
            {
                targetDefinition = ctx.Resources.GetPattern(targetPattern);
                if (targetDefinition == null)
                {
                    throw new RuleValidationException(string.Format(
                        "目标图案「{0}」在图案库（.pat）中不存在，规则中止。请确认图案名拼写，或把 acadiso.pat 放到程序的 Resources 目录下。",
                        targetPattern));
                }

                ctx.Log.Detail(string.Format("目标图案「{0}」定义：{1} 条图案线。",
                    targetDefinition.Name, targetDefinition.Lines == null ? 0 : targetDefinition.Lines.Count));
            }

            // 未指定实体类型时只看填充
            if (ctx.Filter.EntityTypes == null || ctx.Filter.EntityTypes.Count == 0)
            {
                ctx.Filter.EntityTypes = new List<string> { "HATCH" };
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "HatchPatternChange 规则没有任何过滤条件，为避免把整张图的填充都改掉，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            if (toGradient)
            {
                ctx.Log.Detail(string.Format("目标：渐变填充「{0}」({1})", targetGradient, ParseGradientType(ctx.Action.GradientType)));
            }
            else if (toSolid)
            {
                ctx.Log.Detail("目标：纯色填充（SOLID）");
            }
            else
            {
                ctx.Log.Detail(string.Format("目标：图案「{0}」({1})；比例 {2}；角度 {3}°；间距 {4}；保持线距 {5}",
                    targetPattern,
                    libraryType,
                    ctx.Action.PatternScale.HasValue ? ctx.Action.PatternScale.Value.ToString("0.###") : "(保持)",
                    ctx.Action.PatternAngle.HasValue ? ctx.Action.PatternAngle.Value.ToString("0.###") : "(保持)",
                    ctx.Action.PatternSpace.HasValue ? ctx.Action.PatternSpace.Value.ToString("0.###") : "(保持)",
                    RuleContext.Bool(ctx.Action.MatchSpacing, true) ? "开" : "关（线距会变）"));
            }

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            // 同一属性告警只打印一次：避免几百个填充重复刷同一条警告
            var warned = new HashSet<string>(StringComparer.Ordinal);

            foreach (ScanHit hit in hits)
            {
                var hatch = hit.Entity as Hatch;
                if (hatch == null)
                {
                    continue;
                }

                // ---- 源图案名过滤（渐变填充按渐变名匹配）----
                if (!MatchesSourcePattern(ctx, hatch))
                {
                    continue;
                }

                result.Hit();
                string desc = EntityDescribe.Describe(hit);

                if (EntityScanner.ShouldSkipLocked(ctx, hatch))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, LayerService.LayerNameOf(hatch));
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                string before = DescribeCurrent(hatch);

                // ---- 已经合规：图案名相同且比例/角度都无需改 ----
                if (IsAlreadyTarget(ctx, hatch, targetPattern, targetGradient, toGradient, toSolid))
                {
                    result.MarkSkipped(string.Format("{0}：已是目标填充（{1}），跳过。", desc, before));
                    continue;
                }

                try
                {
                    if (toGradient)
                    {
                        ApplyGradient(ctx, hatch, targetGradient, warned);
                    }
                    else if (toSolid)
                    {
                        ApplySolid(hatch);
                    }
                    else
                    {
                        ApplyPattern(ctx, hatch, targetDefinition, libraryType, warned);
                    }

                    string detail = string.Format("{0}：{1} → {2}", desc, before, DescribeCurrent(hatch));
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 替换填充失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        // ---------------------------------------------------------------- 三种目标形态

        /// <summary>
        /// 换图案：赋完整定义（含 Lines），保留边界与关联性。
        /// </summary>
        private static void ApplyPattern(
            RuleContext ctx, Hatch hatch, HatchPattern definition, HatchPatternType libraryType, HashSet<string> warned)
        {
            bool userDefined = libraryType == HatchPatternType.PatternFill;
            bool matchSpacing = RuleContext.Bool(ctx.Action.MatchSpacing, true);

            // 必须在覆盖 Pattern 之前取：旧图案的定义就藏在实体自己身上（DWG 把图案线内联存储）
            HatchPattern oldPattern = hatch.Pattern;
            double? oldSpacing = HatchPatternMetrics.EffectiveSpacing(hatch);
            double oldPatternScale = hatch.PatternScale;

            if (userDefined)
            {
                // 用户自定义图案：间距编码在图案线 Offset 里，必须自己合成定义
                double space = ctx.Action.PatternSpace.HasValue && ctx.Action.PatternSpace.Value > 0
                    ? ctx.Action.PatternSpace.Value
                    : 0;

                if (space <= 0 && matchSpacing && oldSpacing.HasValue)
                {
                    // 没指定间距但要求保持疏密：直接沿用旧线距
                    // （图案线里已是最终几何，不需要再除一次比例）
                    space = oldSpacing.Value;
                    if (warned.Add("UserDefinedKeepSpacing"))
                    {
                        ctx.Log.Detail(string.Format("用户自定义图案未指定 PatternSpace，按原线距 {0:0.####} 处理。", space));
                    }
                }

                if (space <= 0)
                {
                    // 没有间距就没有图案：退化成默认间距 1.0 并明确告警，而不是留一个画不出来的填充
                    space = 1.0;
                    if (warned.Add("UserDefinedNoSpace"))
                    {
                        ctx.Log.Warn("PatternType=UserDefined 但未指定 PatternSpace（线间距），已按 1.0 处理。");
                    }
                }

                double angleRad = ctx.Action.PatternAngle.HasValue
                    ? ctx.Action.PatternAngle.Value * Math.PI / 180.0
                    : 0.0;

                hatch.Pattern = BuildUserDefinedPattern(space, angleRad, RuleContext.Bool(ctx.Action.PatternDouble, false));
                hatch.PatternType = libraryType;
                hatch.IsSolid = false;

                // 间距已经烘进图案线里了，比例记录值必须是 1：
                // 从纯色填充转过来时它是 0，留着会让 AutoCAD 再乘一次 0。
                hatch.PatternScale = 1.0;

                DisableGradient(hatch);
                return;
            }

            // 预定义 / 自定义文件图案：直接赋完整定义
            hatch.Pattern = definition.Clone();
            hatch.PatternType = libraryType;
            hatch.IsSolid = false;
            DisableGradient(hatch);

            ApplyPatternSettings(ctx, hatch, warned);
            ApplySpacingMatch(ctx, hatch, oldSpacing, oldPatternScale, matchSpacing, warned);

            // 目标图案如果只是原图案的一个旋转（线族数、相对角度、虚线结构都一致），
            // 就把方向也调回原样——"换图案名、观感不变"。
            // 配置里显式给了角度或比例时不动：那是用户明确要的结果。
            TryMatchOrientation(ctx, hatch, oldPattern, warned);

            // 剩下的差异（线族数变了、方向确实不同、双线缝宽变了）不是比例问题，但用户看到的就是
            // "跟原图不一样"。显式说出来，免得被误当成比例算错了。
            WarnAppearanceChange(ctx, hatch, oldPattern, warned);

            // 兜底：比例 <= 0 在 DWG 里是非法值。纯色填充的 PatternScale 是 0，
            // 关掉 MatchSpacing 或显式写了 0 都会把 0 带过来，写出去就是一整块实色。
            if (hatch.PatternScale <= 0)
            {
                hatch.PatternScale = 1.0;

                if (warned.Add("ScaleFallback"))
                {
                    ctx.Log.Warn("填充比例算出来是 0（或负数），已按 1.0 处理——否则图案会退化成实色块。");
                }
            }
        }

        /// <summary>
        /// 按"保持视觉线距"重算图案比例。
        ///
        /// 各图案在 .pat 里的基准线距不同（ANSI31 = 3.175、JIS_LC_8A = 7.8、JIS_RC_20 = 20），
        /// 沿用原比例直接换图案，看起来会突然变密或变疏。这里把比例换算成
        /// "新图案基准线距 × 比例 == 替换前的实际线距"，替换前后疏密一致。
        ///
        /// 赋值 <c>Hatch.PatternScale</c> 会把比例乘进图案线（见 <see cref="HatchPatternMetrics"/>），
        /// 所以此刻 <c>hatch.Pattern</c> 必须是**未缩放**的原始定义——即刚 Clone 进来、还没被
        /// <see cref="ApplyPatternSettings"/> 动过 PatternScale 的状态。这也是下面要先判
        /// "是否显式指定了 PatternScale" 的原因：那种情况图案线已经被缩放过，不能再算一次。
        /// </summary>
        private static void ApplySpacingMatch(
            RuleContext ctx, Hatch hatch, double? oldSpacing, double oldPatternScale, bool matchSpacing, HashSet<string> warned)
        {
            if (!matchSpacing)
            {
                // 「关掉保持线距」的语义是**沿用原比例**，不是"把比例当成 1"。
                // 必须把原比例显式写回去：`PatternScale` 的 setter 会把 .pat 的图案线按这个比例缩放，
                // 于是"组码 41"和"图案线几何"始终描述同一件事。
                //
                // 不写回去会怎样（实测）：源图是"基准线距 3、比例 75"的自定义图案，
                // 换图案后组码 41 还是 75，图案线却是 JIS_LC_8A 未缩放的 7.8。
                // AutoCAD 按图案线绘制 → 实际线距 7.8，比原来的 225 密了 29 倍，
                // 而属性面板还显示比例 75——用户无论看哪个都对不上。
                if (!ctx.Action.PatternScale.HasValue
                    && oldPatternScale > 0
                    && Math.Abs(oldPatternScale - 1.0) > 1e-9)
                {
                    hatch.PatternScale = oldPatternScale;
                }

                WarnSpacingChanged(ctx, hatch, oldSpacing, warned);
                return;
            }

            if (ctx.Action.PatternScale.HasValue && ctx.Action.PatternScale.Value > 0)
            {
                if (warned.Add("MatchSpacingVsScale"))
                {
                    ctx.Log.Detail("已显式指定 PatternScale，比例以配置值为准，不再保持线距。");
                }

                return;
            }

            if (!oldSpacing.HasValue || oldSpacing.Value <= 0)
            {
                // 源是纯色/渐变：没有"原来的线距"可比，**不能**沿用它的 PatternScale。
                // 纯色填充的 PatternScale 是 0，沿用下去新图案会以 0 比例绘制——
                // 图案线间距被压成 0，AutoCAD 里看起来就是一整块实色（"比例和颜色都不对"）。
                // 这里取 1.0，即新图案在 .pat 里的原始疏密。
                hatch.PatternScale = 1.0;

                if (warned.Add("MatchSpacingNoSource"))
                {
                    ctx.Log.Detail("源填充是纯色/渐变（没有图案线），无从比较线距，比例取 1.0（图案原始疏密）。");
                }

                return;
            }

            double? reference = HatchPatternMetrics.ReferenceSpacing(hatch.Pattern);
            if (!reference.HasValue || reference.Value <= 0)
            {
                if (warned.Add("MatchSpacingNoTarget"))
                {
                    ctx.Log.Detail("新图案取不到基准线距，比例保持原值。");
                }

                return;
            }

            double scale = oldSpacing.Value / reference.Value;

            // 夹到合理区间：图案名写错导致线距差几百倍时，宁可给个极端值并告警，也不要写出 1e-9 这种比例
            if (scale < 1e-4 || scale > 1e4)
            {
                double clamped = scale < 1e-4 ? 1e-4 : 1e4;
                ctx.Log.Warn(string.Format(
                    "保持线距算出的比例 {0:0.######} 超出合理范围（1e-4 ~ 1e4），已夹到 {1:0.######}。请检查源/目标图案是否选对。",
                    scale, clamped));
                scale = clamped;
            }

            scale = Math.Round(scale, 6);
            hatch.PatternScale = scale;

            // 这条必须打 Info 而不是 Detail：
            // 用户打开转换后的图，在属性面板看到"比例 0.384615"时，第一反应是"我没填比例，怎么变了"。
            // 明细行默认可能被 MaxEditorDetailLines 截断、也可能被 Verbose=false 关掉，
            // 于是"为什么变成这个数"的解释就丢了。同一个比例只打一次，不刷屏。
            string key = string.Format("SpacingMatch|{0:0.####}|{1:0.####}|{2:0.######}",
                oldSpacing.Value, reference.Value, scale);

            if (warned.Add(key))
            {
                ctx.Log.Info(string.Format(
                    "保持线距：原线距 {0:0.####} ÷ 新图案基准线距 {1:0.####} = 比例 {2:0.######}"
                    + "（替换后线距仍是 {3:0.####}；这个比例是反算出来的，不是配错）",
                    oldSpacing.Value,
                    reference.Value,
                    scale,
                    reference.Value * scale));
            }
        }

        /// <summary>
        /// 没开「保持线距」时，把"线距确实会变"这件事说出来。
        ///
        /// 为什么必须告警：关掉这个开关后，线距变化是**静默**的——图上看着变稀/变密，
        /// 日志里却一个字都没有，用户只能怀疑是工具算错了。
        /// 实测一次真实误判：配置里 `MatchSpacing=false`（早先试验时选的「否」），
        /// 换完 JIS_LC_8A 线距从 3 变成 7.8，用户看到截图直接报"间距变了"。
        /// 同一个 (原线距, 新线距) 只报一次，不刷屏。
        /// </summary>
        private static void WarnSpacingChanged(
            RuleContext ctx, Hatch hatch, double? oldSpacing, HashSet<string> warned)
        {
            if (!oldSpacing.HasValue || oldSpacing.Value <= 0)
            {
                return;
            }

            double? now = HatchPatternMetrics.ReferenceSpacing(hatch.Pattern);
            if (!now.HasValue || now.Value <= 0)
            {
                return;
            }

            if (Math.Abs(now.Value - oldSpacing.Value) / oldSpacing.Value <= 0.02)
            {
                return;
            }

            string key = string.Format("SpacingChanged|{0:0.####}|{1:0.####}", oldSpacing.Value, now.Value);
            if (!warned.Add(key))
            {
                return;
            }

            ctx.Log.Warn(string.Format(
                "未开启「保持线距」（Action.MatchSpacing=false）：图案线距会从 {0:0.####} 变成 {1:0.####}（{2:0.##} 倍）。"
                + "要线距不变，把该字段设为「是」或直接删掉（默认就是开）。",
                oldSpacing.Value,
                now.Value,
                now.Value / oldSpacing.Value));
        }

        /// <summary>
        /// 结构相同就把方向调回原样。
        ///
        /// 判据见 <see cref="HatchPatternMetrics.TryGetOrientationDelta"/>：线族数相同、
        /// 各族相对角度吻合、虚线结构一致——也就是"同一个图案转了个角度"。
        /// 典型例子：源是 30° 的单线族图案、目标是 ANSI31（45°），旋转 -15° 后两者观感一致。
        ///
        /// 什么时候不动：配置里写了 <c>PatternAngle</c>（用户要的就是那个角度）
        /// 或 <c>PatternScale</c>（用户要的就是那个比例）——显式配置永远优先。
        /// 颜色不受影响：本规则从头到尾不碰 <c>Hatch.Color</c>。
        /// </summary>
        private static void TryMatchOrientation(
            RuleContext ctx, Hatch hatch, HatchPattern oldPattern, HashSet<string> warned)
        {
            if (ctx.Action.PatternAngle.HasValue || ctx.Action.PatternScale.HasValue)
            {
                return;
            }

            double delta;
            bool sameStructure = HatchPatternMetrics.TryGetOrientationDelta(oldPattern, hatch.Pattern, out delta);

            if (!sameStructure)
            {
                // 结构不同（线族数或虚线结构变了）：整体对齐没有意义，但**主方向**仍要对齐。
                // 否则交叉图案换成单线族图案后连方向都变了，看起来像换错了图案。
                if (!HatchPatternMetrics.TryGetDominantDelta(oldPattern, hatch.Pattern, out delta))
                {
                    return;
                }
            }

            double degrees = delta * 180.0 / Math.PI;
            if (Math.Abs(degrees) < 1e-6)
            {
                return;
            }

            // PatternAngle 的语义是"在图案自带角度上叠加"（Update 里是 line.Angle += rotation），
            // 所以这里传的是差值，不是绝对角度。
            hatch.PatternAngle = delta;

            ctx.Log.Info(string.Format(
                sameStructure
                    ? "目标图案与原图案结构相同，已叠加旋转 {0:0.#}°，方向与原图一致。"
                    : "目标图案与原图案结构不同，已把主方向对齐到原图的 {1:0.#}°（叠加旋转 {0:0.#}°）。",
                degrees,
                HatchPatternMetrics.DominantAngleDegrees(hatch.Pattern)));
        }

        /// <summary>
        /// 线族数、主方向、双线缝宽变了就提示一次。
        ///
        /// 为什么值得单独说：用户换完图案看到"跟原图不一样"，第一反应是比例算错了。
        /// 但比例（线距）其实是对的——变的是：
        ///   * 线族数（交叉→单线，线少一半，看起来变淡）；
        ///   * 图案自带方向（每个 .pat 的角度不同，例如 8001_SYSTEMSTYLE_1 是 30°、ANSI31 是 45°）；
        ///   * 双线缝宽（JIS_LC_8A 的缝/距比是 1:11、2_SECONDARY_ESCAPE_ROUTE 是 1:6，
        ///     线距对齐到同一个值后缝必然不一样宽——这是图案自身的几何比例，不是算错）。
        /// </summary>
        private static void WarnAppearanceChange(
            RuleContext ctx, Hatch hatch, HatchPattern oldPattern, HashSet<string> warned)
        {
            string oldName = oldPattern == null ? "(无)" : oldPattern.Name;
            int oldFamilies = oldPattern == null || oldPattern.Lines == null ? 0 : oldPattern.Lines.Count;
            double oldAngle = HatchPatternMetrics.DominantAngleDegrees(oldPattern);

            int newFamilies = hatch.Pattern == null || hatch.Pattern.Lines == null ? 0 : hatch.Pattern.Lines.Count;
            double newAngle = HatchPatternMetrics.DominantAngleDegrees(hatch.Pattern);
            string newName = hatch.Pattern == null ? "(无)" : hatch.Pattern.Name;

            bool familiesChanged = oldFamilies > 0 && newFamilies != oldFamilies;
            bool angleChanged = oldFamilies > 0 && Math.Abs(oldAngle - newAngle) > 0.5;

            double? oldGap = HatchPatternMetrics.PairGap(oldPattern);
            double? newGap = HatchPatternMetrics.PairGap(hatch.Pattern);
            bool gapChanged = oldGap.HasValue && newGap.HasValue && oldGap.Value > 1e-6
                              && Math.Abs(newGap.Value - oldGap.Value) / oldGap.Value > 0.02;

            if (!familiesChanged && !angleChanged && !gapChanged)
            {
                return;
            }

            string key = string.Format("{0}|{1}|{2}|{3}|{4:0.####}",
                oldName, oldFamilies, newName, newFamilies, newGap.HasValue ? newGap.Value : 0.0);
            if (!warned.Add("Appearance:" + key))
            {
                return;
            }

            var parts = new List<string>();
            if (familiesChanged)
            {
                parts.Add(string.Format("线族 {0} → {1}", oldFamilies, newFamilies));
            }

            if (angleChanged)
            {
                parts.Add(string.Format("方向 {0:0.#}° → {1:0.#}°", oldAngle, newAngle));
            }

            if (gapChanged)
            {
                parts.Add(string.Format("双线缝宽 {0:0.####} → {1:0.####}", oldGap.Value, newGap.Value));
            }

            ctx.Log.Info(string.Format(
                "观感会变（不是比例问题）：「{0}」→「{1}」{2}。线距仍按原值保持。",
                oldName, newName, string.Join("，", parts.ToArray())));
        }

        /// <summary>纯色填充：只翻 IsSolid，不动 Pattern（保留图案名便于后续再切回来）。</summary>
        private static void ApplySolid(Hatch hatch)
        {
            hatch.IsSolid = true;
            DisableGradient(hatch);
        }

        /// <summary>转渐变填充。</summary>
        private static void ApplyGradient(RuleContext ctx, Hatch hatch, string gradientName, HashSet<string> warned)
        {
            hatch.IsSolid = true;

            // 不能置 null：写出器会空引用。保留对象、只改字段。
            if (hatch.GradientColor == null)
            {
                hatch.GradientColor = new HatchGradientPattern();
            }

            var g = hatch.GradientColor;
            g.Enabled = true;
            g.Name = gradientName;

            if (ctx.Action.GradientAngle.HasValue)
            {
                g.Angle = ctx.Action.GradientAngle.Value * Math.PI / 180.0;
            }

            if (ctx.Action.GradientShift.HasValue)
            {
                double shift = ctx.Action.GradientShift.Value;
                g.Shift = shift < 0 ? 0 : (shift > 1 ? 1 : shift);
            }

            if (ctx.Action.GradientOneColorMode.HasValue)
            {
                g.IsSingleColorGradient = ctx.Action.GradientOneColorMode.Value;
            }

            // GradientType（预定义 / 用户自定义）在 ACadSharp 里没有对应字段，
            // 渐变名本身已经决定了类型；显式写了但被忽略时说一声。
            if (!string.IsNullOrWhiteSpace(ctx.Action.GradientType) && warned.Add("GradientType"))
            {
                ctx.Log.Detail("GradientType 在离线模式下由渐变名本身决定，该字段已忽略。");
            }
        }

        /// <summary>关闭渐变但保留对象（置 null 会让写出器抛空引用）。</summary>
        private static void DisableGradient(Hatch hatch)
        {
            if (hatch.GradientColor != null)
            {
                hatch.GradientColor.Enabled = false;
            }
        }

        /// <summary>
        /// 合成一条"用户自定义"图案：给定角度与线间距，单线或双线。
        ///
        /// Offset 必须是**世界坐标**下的垂直向量：<c>HatchPattern.Line.Offset</c> 存的是世界坐标位移
        /// （.pat 的原始值在解析时已被旋转过），所以这里按角度算出法向量再乘间距。
        /// 早期版本写死 <c>(0, space)</c>，角度为 0 时恰好等价，角度不为 0 时线距会缩水 cosθ。
        ///
        /// 双线时第二条沿**线方向**错开半个间距（对应 .pat 的 delta-x），做出错缝效果。
        /// </summary>
        private static HatchPattern BuildUserDefinedPattern(double space, double angleRad, bool isDouble)
        {
            var pattern = new HatchPattern("U")
            {
                Description = "User defined",
                Lines = new List<HatchPattern.Line>()
            };

            double ux = Math.Cos(angleRad);
            double uy = Math.Sin(angleRad);

            // 法向量（垂直于线）
            double nx = -uy;
            double ny = ux;

            pattern.Lines.Add(new HatchPattern.Line
            {
                Angle = angleRad,
                BasePoint = new XY(0, 0),
                Offset = new XY(nx * space, ny * space),
                DashLengths = new List<double>()
            });

            if (isDouble)
            {
                pattern.Lines.Add(new HatchPattern.Line
                {
                    Angle = angleRad,
                    BasePoint = new XY(0, 0),
                    Offset = new XY(nx * space + ux * space / 2.0, ny * space + uy * space / 2.0),
                    DashLengths = new List<double>()
                });
            }

            return pattern;
        }

        /// <summary>
        /// 写入图案参数。逐项 try/catch：某个参数对当前图案不适用时只告警，不放弃整次替换。
        /// </summary>
        private static void ApplyPatternSettings(RuleContext ctx, Hatch hatch, HashSet<string> warned)
        {
            RuleAction action = ctx.Action;

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
                // 配置用度，模型用弧度
                double rad = action.PatternAngle.Value * Math.PI / 180.0;
                TrySet(() => hatch.PatternAngle = rad, ctx, warned, "PatternAngle");
            }

            if (action.PatternDouble.HasValue)
            {
                TrySet(() => hatch.IsDouble = action.PatternDouble.Value, ctx, warned, "PatternDouble");
            }

            if (!string.IsNullOrWhiteSpace(action.HatchStyle))
            {
                HatchStyleType style;
                if (TryParseHatchStyle(action.HatchStyle, out style))
                {
                    TrySet(() => hatch.Style = style, ctx, warned, "HatchStyle");
                }
                else
                {
                    ctx.Log.Warn(string.Format("HatchStyle 取值「{0}」无效（可用 Normal / Outer / Ignore），已忽略。", action.HatchStyle));
                }
            }
        }

        private static void TrySet(Action setter, RuleContext ctx, HashSet<string> warned, string name)
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

        // ---------------------------------------------------------------- 匹配与描述

        /// <summary>按源图案名过滤；未配置则全部通过。</summary>
        private static bool MatchesSourcePattern(RuleContext ctx, Hatch hatch)
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

            string current = CurrentPatternName(hatch);
            string gradient = GradientNameOf(hatch);

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
            RuleContext ctx, Hatch hatch, string targetPattern, string targetGradient, bool toGradient, bool toSolid)
        {
            if (toGradient)
            {
                if (!IsGradient(hatch))
                {
                    return false;
                }

                return string.Equals(GradientNameOf(hatch), targetGradient, StringComparison.OrdinalIgnoreCase)
                       && !ctx.Action.GradientAngle.HasValue
                       && !ctx.Action.GradientShift.HasValue;
            }

            if (toSolid)
            {
                return hatch.IsSolid && !IsGradient(hatch);
            }

            if (IsGradient(hatch) || hatch.IsSolid)
            {
                return false;
            }

            if (!string.Equals(CurrentPatternName(hatch), targetPattern, StringComparison.OrdinalIgnoreCase))
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

            if (ctx.Action.PatternDouble.HasValue && hatch.IsDouble != ctx.Action.PatternDouble.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ctx.Action.HatchStyle)
                && !string.Equals(hatch.Style.ToString(), ctx.Action.HatchStyle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string CurrentPatternName(Hatch hatch)
        {
            return hatch.Pattern == null ? string.Empty : (hatch.Pattern.Name ?? string.Empty);
        }

        private static bool IsGradient(Hatch hatch)
        {
            return hatch.GradientColor != null && hatch.GradientColor.Enabled;
        }

        private static string GradientNameOf(Hatch hatch)
        {
            return IsGradient(hatch) ? hatch.GradientColor.Name : null;
        }

        private static string DescribeCurrent(Hatch hatch)
        {
            if (IsGradient(hatch))
            {
                return string.Format("渐变「{0}」", hatch.GradientColor.Name ?? "?");
            }

            if (hatch.IsSolid)
            {
                return "纯色填充";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "图案「{0}」比例 {1:0.###} 角度 {2:0.###}°",
                CurrentPatternName(hatch),
                hatch.PatternScale,
                hatch.PatternAngle * 180.0 / Math.PI);
        }

        // ---------------------------------------------------------------- 枚举解析

        /// <summary>配置里的图案类型（与 AutoCAD 语义一致的三档）。</summary>
        public enum ConfigPatternType
        {
            PreDefined,
            UserDefined,
            CustomDefined
        }

        /// <summary>解析图案类型字符串（大小写不敏感，容忍 Custom/CustomDefined 等写法）。</summary>
        public static ConfigPatternType ParsePatternType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ConfigPatternType.PreDefined;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "USERDEFINED":
                case "USER":
                case "自定义":
                    return ConfigPatternType.UserDefined;

                case "CUSTOMDEFINED":
                case "CUSTOM":
                case "自定义文件":
                    return ConfigPatternType.CustomDefined;

                default:
                    return ConfigPatternType.PreDefined;
            }
        }

        /// <summary>
        /// 配置类型 → 库枚举。
        ///
        /// **注意枚举名是反的**：<c>PatternFill = 0</c> 表示用户自定义，
        /// <c>SolidFill = 1</c> 才表示预定义。按名字直觉写会把预定义图案写成用户自定义，
        /// AutoCAD 里图案会直接消失（实测）。
        /// </summary>
        public static HatchPatternType ToLibraryPatternType(ConfigPatternType type)
        {
            switch (type)
            {
                case ConfigPatternType.UserDefined:
                    return HatchPatternType.PatternFill;

                case ConfigPatternType.CustomDefined:
                    return HatchPatternType.Custom;

                default:
                    return HatchPatternType.SolidFill;
            }
        }

        public static string ParseGradientType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "PreDefinedGradient";
            }

            return string.Equals(text.Trim(), "UserDefinedGradient", StringComparison.OrdinalIgnoreCase)
                ? "UserDefinedGradient"
                : "PreDefinedGradient";
        }

        private static bool TryParseHatchStyle(string text, out HatchStyleType style)
        {
            switch ((text ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "NORMAL":
                case "普通":
                    style = HatchStyleType.Normal;
                    return true;

                case "OUTER":
                case "外部":
                    style = HatchStyleType.Outer;
                    return true;

                case "IGNORE":
                case "忽略":
                    style = HatchStyleType.Ignore;
                    return true;

                default:
                    style = HatchStyleType.Normal;
                    return false;
            }
        }
    }
}
