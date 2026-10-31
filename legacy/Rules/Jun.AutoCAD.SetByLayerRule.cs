using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.Colors;
#else
using Autodesk.AutoCAD.Colors;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 扩展规则：批量把对象特性置为 ByLayer（随层）。
    ///
    /// 典型场景：外来图纸规范化——对方把所有图元的颜色/线型/线宽都"写死"在实体上，
    /// 导致按图层控制出图失效。本规则把命中实体的颜色、线型、线宽、打印样式强制改回随层，
    /// 之后只改图层特性即可整体控制。
    ///
    /// 注意：实体颜色改为随层后，图层本身的颜色才生效；
    ///       若图层颜色是 7 号（白/黑），出图时看起来会"变色"，这是预期行为。
    /// </summary>
    public sealed class SetByLayerRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "SetByLayer"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            var tr = ctx.Transaction;

            bool color = RuleExecutionContext.Bool(ctx.Action.ForceColorByLayer, true);
            bool linetype = RuleExecutionContext.Bool(ctx.Action.ForceLinetypeByLayer, true);
            bool lineWeight = RuleExecutionContext.Bool(ctx.Action.ForceLineWeightByLayer, true);
            bool plotStyle = RuleExecutionContext.Bool(ctx.Action.ForcePlotStyleByLayer, true);

            if (!color && !linetype && !lineWeight && !plotStyle)
            {
                throw new RuleValidationException("SetByLayer 规则四个 Force*ByLayer 开关全为 false，没有可执行的操作。");
            }

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "SetByLayer 规则没有任何过滤条件，为避免误改整张图，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            ctx.Log.Detail(string.Format("置为随层：颜色={0} 线型={1} 线宽={2} 打印样式={3}",
                color, linetype, lineWeight, plotStyle));

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

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, ent.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, ent.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                var changes = new List<string>();

                try
                {
                    ent.UpgradeOpen();

                    if (color)
                    {
                        try
                        {
                            if (ent.Color.ColorIndex != 256)
                            {
                                ent.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
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
                            if (!string.Equals(ent.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                            {
                                ent.Linetype = "ByLayer";
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
                            if (ent.LineWeight != LineWeight.ByLayer)
                            {
                                ent.LineWeight = LineWeight.ByLayer;
                                changes.Add("线宽→随层");
                            }
                        }
                        catch (Exception ex)
                        {
                            ctx.Log.Warn(string.Format("{0} 线宽置随层失败：{1}", desc, ex.Message));
                        }
                    }

                    if (plotStyle)
                    {
                        try
                        {
                            // 打印样式名在部分实体上不可写，失败仅告警
                            if (!string.Equals(ent.PlotStyleName, "ByLayer", StringComparison.OrdinalIgnoreCase))
                            {
                                ent.PlotStyleName = "ByLayer";
                                changes.Add("打印样式→随层");
                            }
                        }
                        catch (Exception ex)
                        {
                            ctx.Log.Detail(string.Format("{0} 打印样式置随层失败（可忽略）：{1}", desc, ex.Message));
                        }
                    }
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 置随层失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                    continue;
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
