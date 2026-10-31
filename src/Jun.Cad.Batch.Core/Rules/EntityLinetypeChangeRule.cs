using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量修改对象线型。
    ///
    /// 操作：<c>Action.LinetypeName</c> 指定目标线型；<c>ForceByLayer=true</c> 时强制置为 ByLayer。
    /// 目标线型不存在时尝试从线型库（.lin）建出来，仍失败则整条规则中止并提示——
    /// 静默跳过会让用户以为"改完了"。
    /// </summary>
    public sealed class EntityLinetypeChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "EntityLinetypeChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            bool forceByLayer = RuleContext.Bool(ctx.Action.ForceByLayer, false);
            string targetName = ctx.Action.LinetypeName == null ? null : ctx.Action.LinetypeName.Trim();

            if (forceByLayer)
            {
                targetName = "ByLayer";
            }

            if (string.IsNullOrEmpty(targetName))
            {
                throw new RuleValidationException("EntityLinetypeChange 规则缺少 Action.LinetypeName（或设置 ForceByLayer=true）。");
            }

            LineType target = LinetypeService.EnsureLinetype(ctx.Document, targetName, ctx.Log, ctx.Resources);
            if (target == null)
            {
                throw new RuleValidationException(string.Format(
                    "线型「{0}」不可用，规则中止。请把该线型定义补进线型库（.lin）后重试。", targetName));
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "EntityLinetypeChange 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail("目标线型：" + target.Name + (forceByLayer ? "（强制随层）" : string.Empty));

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

                string current = EntityDescribe.DescribeLinetype(ent);
                if (string.Equals(current, target.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.MarkSkipped(string.Format("{0} 线型已是「{1}」，跳过。", desc, target.Name));
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
                    ent.LineType = target;

                    string detail = string.Format("{0}：线型 {1} → {2}", desc, current, target.Name);
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
