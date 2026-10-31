using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改文字高度。
    ///
    /// 覆盖：TEXT / MTEXT / ATTDEF / ATTRIB / DIMENSION（改标注样式 TextHeight）。
    ///
    /// 关于 MText 的行距联动：
    ///   MText 行距 = 字高 × 行距系数，改字高不会自动改系数。
    ///   本规则默认不动系数（视觉上行距随字高线性变化）；
    ///   若图纸要求固定系数，在 <c>Action.LineSpacingFactor</c> 里显式指定即可。
    ///
    /// 与 AutoCAD 版的**功能降级**：ACAD_TABLE / MULTILEADER / TOLERANCE 的字高在离线模式下不可写，
    /// 命中后记为"跳过"并写明原因——不静默吞掉。
    /// </summary>
    public sealed class TextHeightChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextHeightChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            if (!ctx.Action.Height.HasValue || ctx.Action.Height.Value <= 0)
            {
                throw new RuleValidationException("TextHeightChange 规则缺少有效的 Action.Height（必须为正数）。");
            }

            double height = ctx.Action.Height.Value;
            bool alsoUpdateDimStyle = RuleContext.Bool(ctx.Action.AlsoUpdateDimensionStyle, true);

            TextEntityHelper.PrepareTextFilter(ctx.Filter);

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "TextHeightChange 规则没有任何过滤条件，为避免误改整张图的文字，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail(string.Format("目标字高：{0}；同步标注样式：{1}", height, alsoUpdateDimStyle ? "是" : "否"));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                Entity ent = hit.Entity;
                if (ent == null || !TextEntityHelper.IsTextCarrier(ent))
                {
                    continue;
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
                    ApplyHeight(ctx, result, ent, desc, height, alsoUpdateDimStyle);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改字高失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        private static void ApplyHeight(
            RuleContext ctx,
            RuleExecutionResult result,
            Entity ent,
            string desc,
            double height,
            bool alsoUpdateDimStyle)
        {
            // ---- 标注：字高写在标注样式的 TextHeight ----
            var dim = ent as Dimension;
            if (dim != null)
            {
                if (!alsoUpdateDimStyle)
                {
                    result.MarkSkipped(desc + "：标注字高由标注样式控制，AlsoUpdateDimensionStyle=false，跳过。");
                    return;
                }

                DimensionStyle style = dim.Style;
                if (style == null)
                {
                    result.MarkSkipped(desc + "：标注样式为空，跳过。");
                    return;
                }

                if (Math.Abs(style.TextHeight - height) < 1e-9)
                {
                    result.MarkSkipped(string.Format("{0}：标注样式「{1}」字高已是 {2}，跳过。", desc, style.Name, height));
                    return;
                }

                double old = style.TextHeight;
                style.TextHeight = height;

                string detail = string.Format("{0}：标注样式「{1}」字高 {2} → {3}", desc, style.Name, old, height);
                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
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

            double? current = TextEntityHelper.GetHeight(ent);
            if (current.HasValue && Math.Abs(current.Value - height) < 1e-9)
            {
                // 字高已达标，但如果显式要求了行距系数，MText 仍需处理
                var mtextSame = ent as MText;
                if (mtextSame == null || !ctx.Action.LineSpacingFactor.HasValue)
                {
                    result.MarkSkipped(string.Format("{0} 字高已是 {1}，跳过。", desc, height));
                    return;
                }
            }

            string note = null;
            if (!TextEntityHelper.SetHeight(ent, height))
            {
                result.MarkSkipped(string.Format("{0} 不支持字高修改，跳过。", desc));
                return;
            }

            var mtext = ent as MText;
            if (mtext != null && ctx.Action.LineSpacingFactor.HasValue)
            {
                double factor = ctx.Action.LineSpacingFactor.Value;
                TextEntityHelper.SetLineSpacingFactor(mtext, factor);
                note = string.Format("（行距系数设为 {0:0.##}）", factor < 0.25 ? 0.25 : (factor > 4.0 ? 4.0 : factor));
            }

            string text = string.Format("{0}：字高 → {1}{2}", desc, height, note ?? string.Empty);
            result.MarkChanged(text);
            ctx.Log.Detail(text);
        }
    }
}
