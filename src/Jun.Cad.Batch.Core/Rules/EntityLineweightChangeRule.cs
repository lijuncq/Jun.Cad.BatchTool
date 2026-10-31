using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改对象线宽。
    ///
    /// 线宽取值（单位 0.01mm，与 DXF 组码 370 一致）：
    ///   -1 = ByLayer，-2 = ByBlock，-3 = Default，
    ///   0/5/9/13/15/18/20/25/30/35/40/50/53/60/70/80/90/100/106/120/140/158/200/211。
    ///
    /// 与 AutoCAD 版的差别：没有内核抛 eInvalidInput 兜底，
    /// 所以非法值必须在**写进模型之前**拦住（<see cref="LineWeightUtil.IsValidEntityValue"/>）。
    /// </summary>
    public sealed class EntityLineweightChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLineweightChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            bool forceByLayer = RuleContext.Bool(ctx.Action.ForceByLayer, false);

            int value;
            if (forceByLayer)
            {
                value = -1;
            }
            else if (ctx.Action.LineWeight.HasValue)
            {
                value = ctx.Action.LineWeight.Value;
            }
            else
            {
                throw new RuleValidationException("EntityLineweightChange 规则缺少 Action.LineWeight（或设置 ForceByLayer=true）。");
            }

            if (!LineWeightUtil.IsValidEntityValue(value))
            {
                throw new RuleValidationException(string.Format(
                    "线宽值 {0} 不是 AutoCAD 认可的取值。合法值：{1}，单位 0.01mm。",
                    value, LineWeightUtil.EntityValuesText));
            }

            LineWeightType target;
            if (!LineWeightUtil.TryToLineWeightType(value, out target))
            {
                throw new RuleValidationException(string.Format("线宽值 {0} 无法映射到库的线宽枚举。", value));
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLineweightChange 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail("目标线宽：" + LineWeightUtil.Describe(value));

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

                if (ent.LineWeight == target)
                {
                    result.MarkSkipped(string.Format("{0} 线宽已是 {1}，跳过。", desc, LineWeightUtil.Describe(value)));
                    continue;
                }

                if (EntityScanner.ShouldSkipLocked(ctx, ent))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, LayerService.LayerNameOf(ent));
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    LineWeightType current = ent.LineWeight;
                    ent.LineWeight = target;

                    string detail = string.Format("{0}：线宽 {1} → {2}", desc, current, target);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改线宽失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }
    }
}
