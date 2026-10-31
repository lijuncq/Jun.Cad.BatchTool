using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量把对象特性置为 ByLayer（随层）。
    ///
    /// 典型场景：外来图纸规范化——对方把所有图元的颜色/线型/线宽都"写死"在实体上，
    /// 导致按图层控制出图失效。本规则把命中实体的这些特性强制改回随层，
    /// 之后只改图层特性即可整体控制。
    ///
    /// 与 AutoCAD 版的**功能降级**：
    ///   <c>ForcePlotStyleByLayer</c> 无法实现。打印样式（PlotStyleName）在 DWG 里挂在
    ///   实体的扩展数据上，ACadSharp 的 <see cref="Entity"/> 没有对应属性
    ///   （只有文件头的默认打印样式类型）。该开关被打开时给一次警告，
    ///   其余三项照常执行——不静默吞掉，也不因此让整条规则中止。
    /// </summary>
    public sealed class SetByLayerRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "SetByLayer"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            bool color = RuleContext.Bool(ctx.Action.ForceColorByLayer, true);
            bool linetype = RuleContext.Bool(ctx.Action.ForceLinetypeByLayer, true);
            bool lineWeight = RuleContext.Bool(ctx.Action.ForceLineWeightByLayer, true);
            bool plotStyle = RuleContext.Bool(ctx.Action.ForcePlotStyleByLayer, true);

            if (!color && !linetype && !lineWeight && !plotStyle)
            {
                throw new RuleValidationException("SetByLayer 规则四个 Force*ByLayer 开关全为 false，没有可执行的操作。");
            }

            if (plotStyle)
            {
                ctx.Log.Warn("「打印样式随层」在离线模式下不支持（ACadSharp 未建模实体的 PlotStyleName），该项已跳过，其余特性照常处理。");
            }

            if (!color && !linetype && !lineWeight)
            {
                throw new RuleValidationException(
                    "SetByLayer 规则实际可执行的开关（颜色/线型/线宽）全为 false，只有不支持的「打印样式随层」被打开，规则中止。");
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "SetByLayer 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("过滤条件：" + filter.Describe());
            ctx.Log.Detail(string.Format("置为随层：颜色={0} 线型={1} 线宽={2}（打印样式不支持）", color, linetype, lineWeight));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            var byLayerLineType = ctx.Document.LineTypes.ByLayer;

            foreach (ScanHit hit in hits)
            {
                Entity ent = hit.Entity;
                if (ent == null)
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

                var changes = new List<string>();

                if (color)
                {
                    try
                    {
                        if (!ent.Color.IsByLayer)
                        {
                            ent.Color = Color.ByLayer;
                            changes.Add("颜色→随层");
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Log.Warn(string.Format("{0} 颜色置随层失败：{1}", desc, ex.Message));
                    }
                }

                if (linetype)
                {
                    try
                    {
                        if (byLayerLineType != null && !ReferenceEquals(ent.LineType, byLayerLineType))
                        {
                            ent.LineType = byLayerLineType;
                            changes.Add("线型→随层");
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Log.Warn(string.Format("{0} 线型置随层失败：{1}", desc, ex.Message));
                    }
                }

                if (lineWeight)
                {
                    try
                    {
                        if (ent.LineWeight != LineWeightType.ByLayer)
                        {
                            ent.LineWeight = LineWeightType.ByLayer;
                            changes.Add("线宽→随层");
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Log.Warn(string.Format("{0} 线宽置随层失败：{1}", desc, ex.Message));
                    }
                }

                if (changes.Count == 0)
                {
                    result.MarkSkipped(desc + "：全部特性已是随层，跳过。");
                }
                else
                {
                    string detail = string.Format("{0}：{1}", desc, string.Join("，", changes));
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
            }
        }
    }
}
