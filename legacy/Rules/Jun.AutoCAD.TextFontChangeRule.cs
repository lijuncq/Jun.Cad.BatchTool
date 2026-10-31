using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 规则 3：批量修改指定类型及颜色的文字字体。
    ///
    /// 覆盖实体：TEXT / MTEXT / ATTDEF / ATTRIB / DIMENSION / ACAD_TABLE / LEADER / MULTILEADER / TOLERANCE。
    ///
    /// 修改方式（文字样式重映射，而不是逐个实体改字体）：
    ///   1. 定位/创建目标 TextStyleTableRecord，写入 FileName、BigFontFileName、
    ///      XScale（宽度因子）、ObliqueAngle（倾斜角）、TextSize（固定字高）；
    ///   2. 把命中实体的 TextStyleId 指向该样式；
    ///   3. 标注（Dimension）的文字样式由标注样式控制，改 DimStyleTableRecord.TextStyleId；
    ///   4. MText 的 \F 内联字体码按 HandleMTextInlineFont 决定是否一并替换。
    /// </summary>
    public sealed class TextFontChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextFontChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            string styleName = ctx.Action.TargetTextStyle == null ? null : ctx.Action.TargetTextStyle.Trim();
            if (string.IsNullOrEmpty(styleName))
            {
                throw new RuleValidationException("TextFontChange 规则缺少 Action.TargetTextStyle。");
            }

            var tr = ctx.Transaction;

            // ---- 1) 目标文字样式：不存在则创建，存在则按配置刷新字体属性 ----
            ObjectId styleId = TextStyleService.EnsureTextStyle(tr, ctx.Database, styleName, ctx.Action, ctx.Log);
            if (styleId.IsNull)
            {
                throw new RuleValidationException(string.Format("无法创建或打开文字样式「{0}」。", styleName));
            }

            bool handleInline = RuleExecutionContext.Bool(ctx.Action.HandleMTextInlineFont, true);
            bool alsoUpdateDimStyle = RuleExecutionContext.Bool(ctx.Action.AlsoUpdateDimensionStyle, true);
            string fontName = ctx.Action.FontFileName == null ? null : ctx.Action.FontFileName.Trim();

            ctx.Log.Detail(string.Format(
                "目标样式：{0}；主字体：{1}；大字体：{2}；MText 内联字体处理：{3}；同步标注样式：{4}",
                styleName,
                string.IsNullOrEmpty(fontName) ? "(不变)" : fontName,
                string.IsNullOrEmpty(ctx.Action.BigFont) ? "(不变)" : ctx.Action.BigFont,
                handleInline ? "是" : "否",
                alsoUpdateDimStyle ? "是" : "否"));

            // ---- 2) 过滤：未显式指定 EntityTypes 时用"文字类"默认集合 ----
            RuleFilterAdapter.PrepareTextFilter(ctx.Filter);

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "TextFontChange 规则没有任何过滤条件，为避免把整张图的文字都改掉，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

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

                if (ent == null || ent.IsErased || !TextEntityHelper.IsTextCarrier(ent))
                {
                    continue;
                }

                // 当前文字样式名过滤（Dimension 走标注样式的文字样式）
                if (!string.IsNullOrWhiteSpace(ctx.Filter.TextStyleName))
                {
                    ObjectId currentStyleId = TextEntityHelper.GetTextStyleId(tr, ent);
                    string currentName = TextStyleService.GetTextStyleName(tr, currentStyleId);
                    if (!string.Equals(currentName, ctx.Filter.TextStyleName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                result.Hit();
                string desc = ObjectUtil.Describe(ent);

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, ent.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, ent.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    ApplyToEntity(ctx, result, ent, desc, styleId, styleName, fontName, handleInline, alsoUpdateDimStyle);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改字体失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        private static void ApplyToEntity(
            RuleExecutionContext ctx,
            RuleExecutionResult result,
            Entity ent,
            string desc,
            ObjectId styleId,
            string styleName,
            string fontName,
            bool handleInline,
            bool alsoUpdateDimStyle)
        {
            var tr = ctx.Transaction;

            // ---- 标注：文字样式挂在标注样式上 ----
            var dim = ent as Dimension;
            if (dim != null)
            {
                if (!alsoUpdateDimStyle)
                {
                    result.MarkSkipped(desc + "：标注文字样式由标注样式控制，AlsoUpdateDimensionStyle=false，跳过。");
                    return;
                }

                string dimStyleName = DimStyleService.GetName(tr, dim.DimensionStyle);
                if (DimStyleService.SetTextStyle(tr, dim.DimensionStyle, styleId, ctx.Log))
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

            // ---- 其他文字类实体：改实体上的 TextStyleId ----
            string reason;
            bool changed = TextEntityHelper.SetTextStyleId(tr, ent, styleId, out reason);

            if (!changed && reason != null)
            {
                string msg = string.Format("{0} 无法重映射文字样式：{1}", desc, reason);
                result.MarkFailed(msg);
                ctx.Log.Error(msg);
                return;
            }

            // ---- MText 内联字体：解析 \F 格式码 ----
            bool inlineChanged = false;
            var mtext = ent as MText;
            if (mtext != null && handleInline && !string.IsNullOrEmpty(fontName))
            {
                inlineChanged = ApplyMTextInlineFont(ctx, mtext, fontName);
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
        private static bool ApplyMTextInlineFont(RuleExecutionContext ctx, MText mtext, string fontName)
        {
            string contents;
            try
            {
                contents = mtext.Contents;
            }
            catch
            {
                return false;
            }

            if (!TextStyleService.HasForeignInlineFont(contents, fontName))
            {
                return false;
            }

            int replaced;
            string newContents = TextStyleService.ReplaceMTextInlineFont(contents, fontName, out replaced);
            if (replaced == 0 || string.Equals(contents, newContents, StringComparison.Ordinal))
            {
                return false;
            }

            mtext.UpgradeOpen();
            mtext.Contents = newContents;
            ctx.Log.Detail(string.Format("  已替换 {0} 处 \\F 内联字体。", replaced));
            return true;
        }
    }

    /// <summary>
    /// 过滤器适配小工具：把"文字类规则"的默认类型集合补进 Filter，
    /// 让 DXF 组码 0（Start）能一次性表达多种类型。
    /// </summary>
    internal static class RuleFilterAdapter
    {
        public static void PrepareTextFilter(Config.RuleFilter filter)
        {
            if (filter == null)
            {
                return;
            }

            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                filter.EntityTypes = TextEntityHelper.ResolveTypes(filter);
            }
        }
    }
}
