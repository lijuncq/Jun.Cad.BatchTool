using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改标注样式。
    ///
    /// 过滤：
    ///   * EntityTypes 里写 DIMENSION 即可覆盖全部标注子类（线性/对齐/半径/直径/角度/坐标/弧长）；
    ///   * DimensionStyleName 可按"当前标注样式名"再收窄。
    /// 操作：
    ///   * 把命中的 Dimension 的 Style 指向目标标注样式；
    ///   * 目标样式不存在时以 Standard 为模板克隆创建，并按需写入箭头大小与文字高度。
    ///
    /// 提示：标注的"文字样式"也挂在标注样式上。要统一字体，
    ///       请配合 TextFontChange 规则的 AlsoUpdateDimensionStyle=true。
    /// </summary>
    public sealed class DimensionStyleChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "DimensionStyleChange"; }
        }

        /// <summary>
        /// 改的是**标注样式定义**（箭头大小 / 文字高度）与新建样式，实体快照只记样式名。
        /// 本规则自己上报，避免"改了样式却报无改动"。
        /// </summary>
        public bool ReportsOwnChanges
        {
            get { return true; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            string targetName = ctx.Action.TargetDimensionStyle == null ? null : ctx.Action.TargetDimensionStyle.Trim();
            if (string.IsNullOrEmpty(targetName))
            {
                throw new RuleValidationException("DimensionStyleChange 规则缺少 Action.TargetDimensionStyle。");
            }

            DimensionStyle existingStyle = null;
            ctx.Document.DimensionStyles.TryGetValue(targetName, out existingStyle);
            string styleBefore = DescribeStyle(existingStyle);

            bool created;
            DimensionStyle target = DimStyleService.EnsureDimStyle(
                ctx.Document, targetName, ctx.Action, ctx.Log, out created);

            if (target == null)
            {
                throw new RuleValidationException(string.Format("无法创建或打开标注样式「{0}」。", targetName));
            }

            string styleAfter = DescribeStyle(target);
            if (!string.Equals(styleBefore, styleAfter, StringComparison.Ordinal))
            {
                ctx.RecordTableChange(targetName, "DIMSTYLE",
                    created ? "新建标注样式" : "改标注样式", styleBefore, styleAfter);
            }

            // 未指定 EntityTypes 时默认只看标注
            if (ctx.Filter.EntityTypes == null || ctx.Filter.EntityTypes.Count == 0)
            {
                ctx.Filter.EntityTypes = new List<string> { "DIMENSION" };
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail(string.Format("目标标注样式：{0}{1}", targetName, created ? "（本次新建）" : string.Empty));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            string requiredStyle = ctx.Filter.DimensionStyleName == null ? null : ctx.Filter.DimensionStyleName.Trim();

            foreach (ScanHit hit in hits)
            {
                var dim = hit.Entity as Dimension;
                if (dim == null)
                {
                    continue;
                }

                // 按当前标注样式名过滤
                if (!string.IsNullOrEmpty(requiredStyle))
                {
                    string currentName = DimStyleService.NameOf(dim.Style);
                    if (!string.Equals(currentName, requiredStyle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                result.Hit();
                string desc = EntityDescribe.Describe(hit);

                if (ReferenceEquals(dim.Style, target))
                {
                    result.MarkSkipped(string.Format("{0} 已是标注样式「{1}」，跳过。", desc, targetName));
                    continue;
                }

                if (EntityScanner.ShouldSkipLocked(ctx, dim))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, LayerService.LayerNameOf(dim));
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    string oldName = DimStyleService.NameOf(dim.Style);
                    dim.Style = target;

                    string detail = string.Format("{0}：标注样式「{1}」→「{2}」", desc, oldName, targetName);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改标注样式失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }
        /// <summary>标注样式的可比较指纹（箭头大小 / 文字高度）。</summary>
        private static string DescribeStyle(DimensionStyle style)
        {
            if (style == null)
            {
                return "(不存在)";
            }

            return string.Format("arrow={0:0.###}|text={1:0.###}", style.ArrowSize, style.TextHeight);
        }

    }
}
