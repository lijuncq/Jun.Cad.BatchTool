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
    /// 扩展规则：批量修改对象线型。
    ///
    /// 过滤：实体类型 + 颜色索引 + 图层（DXF 组码组合）
    /// 操作：LinetypeName 指定目标线型；ForceByLayer=true 时强制置为 ByLayer（外来图纸规范化常用）。
    ///       目标线型不存在时尝试从 acad.lin / acadiso.lin 加载，仍失败则整条规则中止并提示。
    /// </summary>
    public sealed class EntityLinetypeChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLinetypeChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            var tr = ctx.Transaction;
            bool forceByLayer = RuleExecutionContext.Bool(ctx.Action.ForceByLayer, false);
            string target = ctx.Action.LinetypeName == null ? null : ctx.Action.LinetypeName.Trim();

            if (forceByLayer)
            {
                target = "ByLayer";
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new RuleValidationException("EntityLinetypeChange 规则缺少 Action.LinetypeName（或设置 ForceByLayer=true）。");
            }

            if (!LinetypeService.IsSymbolicLinetype(target))
            {
                ObjectId ltId = LinetypeService.EnsureLinetype(tr, ctx.Database, target, ctx.Log);
                if (ltId.IsNull)
                {
                    throw new RuleValidationException(string.Format(
                        "线型「{0}」不可用，规则中止。可先把该线型定义加载到 acad.lin 中。", target));
                }
            }

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLinetypeChange 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            ctx.Log.Detail("目标线型：" + target + (forceByLayer ? "（强制随层）" : string.Empty));

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

                string current = null;
                try
                {
                    current = ent.Linetype;
                }
                catch
                {
                    // 某些实体可能不支持线型属性
                }

                if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    result.MarkSkipped(string.Format("{0} 线型已是「{1}」，跳过。", desc, target));
                    continue;
                }

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, ent.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, ent.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    ent.UpgradeOpen();
                    ent.Linetype = target;

                    string detail = string.Format("{0}：线型 {1} → {2}", desc, current ?? "(未知)", target);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改线型失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }
    }
}
