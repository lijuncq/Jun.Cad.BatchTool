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
    /// 规则 2：批量修改指定类型及颜色图元的图层。
    ///
    /// 过滤：DxfCode.Start（实体类型）+ DxfCode.Color（颜色索引）+ DxfCode.LayerName（图层）
    ///       组合成 SelectionFilter；遍历路径下由 DxfFilter.Matches 做等价判定。
    /// 操作：把命中的实体整体迁移到 Action.TargetLayer；图层不存在时按配置自动创建
    ///       （继承 LayerColorIndex / LayerLinetype / LayerLineWeight / LayerPlottable），不报错终止。
    /// </summary>
    public sealed class EntityLayerChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLayerChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            string targetLayer = ctx.Action.TargetLayer == null ? null : ctx.Action.TargetLayer.Trim();
            if (string.IsNullOrEmpty(targetLayer))
            {
                throw new RuleValidationException("EntityLayerChange 规则缺少 Action.TargetLayer。");
            }

            var tr = ctx.Transaction;

            // 目标图层（不存在则创建）
            ObjectId targetLayerId = LayerService.EnsureLayer(tr, ctx.Database, targetLayer, ctx.Action, ctx.Log);
            if (targetLayerId.IsNull)
            {
                throw new RuleValidationException(string.Format(
                    "目标图层「{0}」不存在且未创建，规则中止。", targetLayer));
            }

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLayerChange 规则没有任何过滤条件（EntityTypes / ColorIndex / LayerName 全为空），" +
                    "为避免误改整张图，规则中止。请显式指定至少一个过滤条件。");
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

                if (ent == null || ent.IsErased)
                {
                    continue;
                }

                result.Hit();
                string desc = ObjectUtil.Describe(ent);

                if (string.Equals(ent.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                {
                    result.MarkSkipped(desc + " 已在目标图层，跳过。");
                    continue;
                }

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, ent.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，按 SkipLockedLayers=true 跳过。", desc, ent.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    string oldLayer = ent.Layer;
                    ent.UpgradeOpen();
                    ent.Layer = targetLayer;

                    string detail = string.Format("{0}：图层 {1} → {2}", desc, oldLayer, targetLayer);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改图层失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }
    }
}
