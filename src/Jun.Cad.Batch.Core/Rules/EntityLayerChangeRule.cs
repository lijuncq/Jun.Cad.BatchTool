using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改指定类型及颜色图元的图层。
    ///
    /// 过滤：实体类型 + 颜色索引 + 图层 + 块名（见 <see cref="EntityFilter"/>）
    /// 操作：把命中的实体整体迁移到 <c>Action.TargetLayer</c>；图层不存在时按配置自动创建
    ///       （继承 LayerColorIndex / LayerLinetype / LayerLineWeight / LayerPlottable）。
    ///
    /// 与 AutoCAD 版的差别：不再有"打开/升级"这一步，直接 <c>entity.Layer = target</c>。
    /// 但**必须保留"至少一个过滤条件"的护栏**——离线版没有内核的 UNDO，
    /// 一条没写过滤条件的规则会把整张图挪到一个图层上，而且写盘后不可挽回。
    /// </summary>
    public sealed class EntityLayerChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLayerChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            string targetLayer = ctx.Action.TargetLayer == null ? null : ctx.Action.TargetLayer.Trim();
            if (string.IsNullOrEmpty(targetLayer))
            {
                throw new RuleValidationException("EntityLayerChange 规则缺少 Action.TargetLayer。");
            }

            Layer target = LayerService.EnsureLayer(ctx.Document, targetLayer, ctx.Action, ctx.Log, ctx.Resources);
            if (target == null)
            {
                throw new RuleValidationException(string.Format(
                    "目标图层「{0}」不存在且未创建，规则中止。", targetLayer));
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLayerChange 规则没有任何过滤条件（EntityTypes / ColorIndex / LayerName 全为空），" +
                    "为避免误改整张图，规则中止。请显式指定至少一个过滤条件。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail("扫描范围：" + DatabaseWalker.DescribeScope(EntityScanner.ResolveScope(ctx)));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                Entity ent = hit.Entity;
                if (ent == null)
                {
                    continue;
                }

                result.Hit();
                string desc = EntityDescribe.Describe(hit);

                if (ent.Layer != null && string.Equals(ent.Layer.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.MarkSkipped(desc + " 已在目标图层，跳过。");
                    continue;
                }

                if (EntityScanner.ShouldSkipLocked(ctx, ent))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，按 SkipLockedLayers=true 跳过。", desc, LayerService.LayerNameOf(ent));
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    string oldLayer = LayerService.LayerNameOf(ent);
                    ent.Layer = target;

                    string detail = string.Format("{0}：图层 {1} → {2}", desc, oldLayer, target.Name);
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
