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
    /// 扩展规则：批量修改标注样式。
    ///
    /// 过滤：
    ///   * EntityTypes 里写 DIMENSION 即可覆盖全部标注子类（线性/对齐/半径/直径/角度/坐标/弧长）；
    ///   * DimensionStyleName 可按"当前标注样式名"再收窄。
    /// 操作：
    ///   * 把命中的 Dimension 的 DimensionStyle 指向目标标注样式；
    ///   * 目标样式不存在时以 Standard 为模板克隆创建，并按需写入箭头大小（Dimasz）与文字高度（Dimtxt）。
    ///
    /// 提示：标注的"文字样式"也挂在标注样式上，若同时要统一字体，
    ///       请配合 TextFontChange 规则的 AlsoUpdateDimensionStyle=true。
    /// </summary>
    public sealed class DimensionStyleChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "DimensionStyleChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            string targetName = ctx.Action.TargetDimensionStyle == null ? null : ctx.Action.TargetDimensionStyle.Trim();
            if (string.IsNullOrEmpty(targetName))
            {
                throw new RuleValidationException("DimensionStyleChange 规则缺少 Action.TargetDimensionStyle。");
            }

            var tr = ctx.Transaction;

            bool created;
            ObjectId targetId = DimStyleService.EnsureDimStyle(tr, ctx.Database, targetName, ctx.Action, ctx.Log, out created);
            if (targetId.IsNull)
            {
                throw new RuleValidationException(string.Format("无法创建或打开标注样式「{0}」。", targetName));
            }

            // 未指定 EntityTypes 时默认只看标注
            if (ctx.Filter.EntityTypes == null || ctx.Filter.EntityTypes.Count == 0)
            {
                ctx.Filter.EntityTypes = new List<string> { "DIMENSION" };
            }

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            ctx.Log.Detail(string.Format("目标标注样式：{0}{1}", targetName, created ? "（本次新建）" : string.Empty));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                var dim = tr.GetObject(hit.Id, OpenMode.ForRead, false) as Dimension;
                if (dim == null || dim.IsErased)
                {
                    continue;
                }

                // 按当前标注样式名过滤
                if (!string.IsNullOrWhiteSpace(ctx.Filter.DimensionStyleName))
                {
                    string currentName = DimStyleService.GetName(tr, dim.DimensionStyle);
                    if (!string.Equals(currentName, ctx.Filter.DimensionStyleName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                result.Hit();
                string desc = ObjectUtil.Describe(dim);

                if (dim.DimensionStyle == targetId)
                {
                    result.MarkSkipped(string.Format("{0} 已是标注样式「{1}」，跳过。", desc, targetName));
                    continue;
                }

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, dim.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, dim.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    string oldName = DimStyleService.GetName(tr, dim.DimensionStyle);

                    dim.UpgradeOpen();
                    dim.DimensionStyle = targetId;

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
    }
}
