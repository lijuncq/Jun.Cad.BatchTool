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
    /// 扩展规则：批量修改对象线宽。
    ///
    /// 线宽取值（单位 0.01mm，与 DXF 组码 370 一致）：
    ///   -1 = ByLayer，-2 = ByBlock，-3 = Default，
    ///   0/5/9/13/15/18/20/25/30/35/40/50/53/60/70/80/90/100/106/120/140/158/200/211。
    /// ForceByLayer=true 时统一置为随层。
    /// </summary>
    public sealed class EntityLineweightChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLineweightChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            var tr = ctx.Transaction;
            bool forceByLayer = RuleExecutionContext.Bool(ctx.Action.ForceByLayer, false);

            int value;
            if (forceByLayer)
            {
                value = (int)LineWeight.ByLayer;
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
                    "线宽值 {0} 不是 AutoCAD 认可的取值（任意整数强转会触发 eInvalidInput）。合法值：{1}，单位 0.01mm。",
                    value, LineWeightUtil.EntityValuesText));
            }

            var lineWeight = (LineWeight)value;

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLineweightChange 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            ctx.Log.Detail(string.Format("目标线宽：{0}", DescribeLineWeight(lineWeight)));

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

                LineWeight current;
                try
                {
                    current = ent.LineWeight;
                }
                catch
                {
                    result.MarkSkipped(desc + " 不支持线宽属性，跳过。");
                    continue;
                }

                if (current == lineWeight)
                {
                    result.MarkSkipped(string.Format("{0} 线宽已是 {1}，跳过。", desc, DescribeLineWeight(lineWeight)));
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
                    ent.LineWeight = lineWeight;

                    string detail = string.Format("{0}：线宽 {1} → {2}", desc, DescribeLineWeight(current), DescribeLineWeight(lineWeight));
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

        public static string DescribeLineWeight(LineWeight lw)
        {
            switch ((int)lw)
            {
                case -1:
                    return "ByLayer";
                case -2:
                    return "ByBlock";
                case -3:
                    return "Default";
                default:
                    return string.Format("{0:0.00}mm", (int)lw / 100.0);
            }
        }
    }
}
