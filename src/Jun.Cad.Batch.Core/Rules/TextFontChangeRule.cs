using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改文字字体。
    ///
    /// 修改方式（文字样式重映射，而不是逐个实体改字体）：
    ///   1. 定位/创建目标 <see cref="TextStyle"/>，写入 Filename、BigFontFilename、
    ///      Width（宽度因子）、ObliqueAngle（倾斜角）、Height（固定字高）；
    ///   2. 把命中实体的 Style 指向该样式；
    ///   3. 标注（Dimension）的文字样式由标注样式控制，改 <c>DimensionStyle.Style</c>；
    ///   4. MText 的 \F 内联字体码按 <c>HandleMTextInlineFont</c> 决定是否一并替换。
    /// </summary>
    public sealed class TextFontChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextFontChange"; }
        }

        /// <summary>
        /// 改的是**文字样式定义**（字体/大字体/宽度/倾斜/固定字高），实体快照只记样式名，
        /// 看不见这些改动——所以本规则自己上报，否则预览会把"换了整张图的字体"说成"均已是目标状态"。
        /// </summary>
        public bool ReportsOwnChanges
        {
            get { return true; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            string styleName = ctx.Action.TargetTextStyle == null ? null : ctx.Action.TargetTextStyle.Trim();
            if (string.IsNullOrEmpty(styleName))
            {
                throw new RuleValidationException("TextFontChange 规则缺少 Action.TargetTextStyle。");
            }

            // ---- 1) 目标文字样式：不存在则创建，存在则按配置刷新字体属性 ----
            TextStyle existingStyle = null;
            ctx.Document.TextStyles.TryGetValue(styleName, out existingStyle);
            string styleBefore = DescribeStyle(existingStyle);

            TextStyle style = TextStyleService.EnsureTextStyle(ctx.Document, styleName, ctx.Action, ctx.Log);
            if (style == null)
            {
                throw new RuleValidationException(string.Format("无法创建或打开文字样式「{0}」。", styleName));
            }

            string styleAfter = DescribeStyle(style);
            if (!string.Equals(styleBefore, styleAfter, StringComparison.Ordinal))
            {
                ctx.RecordTableChange(styleName, "TEXTSTYLE", "改文字样式", styleBefore, styleAfter);
            }

            bool handleInline = RuleContext.Bool(ctx.Action.HandleMTextInlineFont, true);
            bool alsoUpdateDimStyle = RuleContext.Bool(ctx.Action.AlsoUpdateDimensionStyle, true);
            string fontToken = TextStyleService.FontToken(ctx.Action);

            ctx.Log.Detail(string.Format(
                "目标样式：{0}；主字体：{1}；大字体：{2}；MText 内联字体处理：{3}；同步标注样式：{4}",
                styleName,
                string.IsNullOrEmpty(ctx.Action.FontFileName) ? "(不变)" : ctx.Action.FontFileName,
                string.IsNullOrEmpty(ctx.Action.BigFont) ? "(不变)" : ctx.Action.BigFont,
                handleInline ? "是" : "否",
                alsoUpdateDimStyle ? "是" : "否"));

            // ---- 2) 过滤：未显式指定 EntityTypes 时用"文字类"默认集合 ----
            TextEntityHelper.PrepareTextFilter(ctx.Filter);

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "TextFontChange 规则没有任何过滤条件，为避免把整张图的文字都改掉，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            string requiredStyle = ctx.Filter.TextStyleName == null ? null : ctx.Filter.TextStyleName.Trim();

            foreach (ScanHit hit in hits)
            {
                Entity ent = hit.Entity;
                if (ent == null || !TextEntityHelper.IsTextCarrier(ent))
                {
                    continue;
                }

                // 当前文字样式名过滤（Dimension 走标注样式的文字样式）
                if (!string.IsNullOrEmpty(requiredStyle))
                {
                    string current = TextEntityHelper.CurrentStyleName(ent);
                    if (!string.Equals(current, requiredStyle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                result.Hit();
                string desc = EntityDescribe.Describe(hit);

                if (EntityScanner.ShouldSkipLocked(ctx, ent))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, LayerService.LayerNameOf(ent));
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    ApplyToEntity(ctx, result, ent, desc, style, styleName, fontToken, handleInline, alsoUpdateDimStyle);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改字体失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        /// <summary>样式的可比较指纹（字体 / 大字体 / 宽度 / 倾斜 / 固定字高）。</summary>
        private static string DescribeStyle(TextStyle style)
        {
            if (style == null)
            {
                return "(不存在)";
            }

            return string.Format("{0}|{1}|w={2:0.###}|o={3:0.###}°|h={4:0.###}",
                string.IsNullOrEmpty(style.Filename) ? "(无)" : style.Filename,
                string.IsNullOrEmpty(style.BigFontFilename) ? "(无)" : style.BigFontFilename,
                style.Width,
                style.ObliqueAngle * 180.0 / Math.PI,
                style.Height);
        }

        private static void ApplyToEntity(
            RuleContext ctx,
            RuleExecutionResult result,
            Entity ent,
            string desc,
            TextStyle style,
            string styleName,
            string fontToken,
            bool handleInline,
            bool alsoUpdateDimStyle)
        {
            // ---- 标注：文字样式挂在标注样式上 ----
            var dim = ent as Dimension;
            if (dim != null)
            {
                if (!alsoUpdateDimStyle)
                {
                    result.MarkSkipped(desc + "：标注文字样式由标注样式控制，AlsoUpdateDimensionStyle=false，跳过。");
                    return;
                }

                DimensionStyle dimStyle = dim.Style;
                string dimStyleName = DimStyleService.NameOf(dimStyle);
                if (DimStyleService.SetTextStyle(dimStyle, style, ctx.Log))
                {
                    string detail = string.Format("{0}：标注样式「{1}」的文字样式 → {2}", desc, dimStyleName, styleName);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                else
                {
                    result.MarkSkipped(string.Format("{0}：标注样式「{1}」的文字样式已是「{2}」，无需修改。", desc, dimStyleName, styleName));
                }

                return;
            }

            // ---- 表格 / 多重引线 / 形位公差：离线版没有可写入口 ----
            if (!TextEntityHelper.SupportsDirectTextProperties(ent))
            {
                string reason = TextEntityHelper.UnsupportedReason(ent);
                result.MarkSkipped(string.Format("{0}：{1}，跳过。", desc, reason));
                ctx.Log.Detail(string.Format("{0}：{1}。", desc, reason));
                return;
            }

            // ---- 其他文字类实体：改实体上的 Style ----
            bool changed = !ReferenceEquals(TextEntityHelper.CurrentStyleName(ent), styleName)
                           && !string.Equals(TextEntityHelper.CurrentStyleName(ent), styleName, StringComparison.OrdinalIgnoreCase);

            TextEntityHelper.SetStyle(ent, style);

            // ---- MText 内联字体：解析 \F 格式码 ----
            bool inlineChanged = false;
            if (ent is MText && handleInline && !string.IsNullOrEmpty(fontToken))
            {
                inlineChanged = ApplyMTextInlineFont(ctx, (MText)ent, fontToken);
            }

            if (changed || inlineChanged)
            {
                string detail = string.Format(
                    "{0}：文字样式 → {1}{2}",
                    desc,
                    styleName,
                    inlineChanged ? "（并替换了 \\F 内联字体）" : string.Empty);
                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
            }
            else
            {
                result.MarkSkipped(string.Format("{0}：文字样式已是「{1}」且无内联字体需替换。", desc, styleName));
            }
        }

        /// <summary>替换 MText 内容中的 \F 内联字体名（保留 |b0|i0|c0 等后缀）。</summary>
        private static bool ApplyMTextInlineFont(RuleContext ctx, MText mtext, string fontToken)
        {
            string contents = mtext.Value;
            if (string.IsNullOrEmpty(contents))
            {
                return false;
            }

            if (!TextStyleService.HasForeignInlineFont(contents, fontToken))
            {
                return false;
            }

            int replaced;
            string newContents = TextStyleService.ReplaceMTextInlineFont(contents, fontToken, out replaced);
            if (replaced == 0 || string.Equals(contents, newContents, StringComparison.Ordinal))
            {
                return false;
            }

            mtext.Value = newContents;
            ctx.Log.Detail(string.Format("  已替换 {0} 处 \\F 内联字体。", replaced));
            return true;
        }
    }
}
