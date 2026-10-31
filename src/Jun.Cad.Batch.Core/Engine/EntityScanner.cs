using System;
using System.Collections.Generic;
using ACadSharp.Entities;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 扫描入口：把"配置里的 Filter + 遍历行为"翻译成一次具体的实体收集。
    ///
    /// 与 AutoCAD 版的差别：原来还有 <c>Scope=Selection</c> 走当前选择集这条路，
    /// 离线工具没有交互选择，这个值会被降级并在日志里说清楚——
    /// 静默当成全选最危险：用户以为只改选中的几个，结果改了整张图。
    /// </summary>
    public static class EntityScanner
    {
        /// <summary>
        /// 按规则的过滤条件扫描实体。
        ///
        /// 两步：
        ///   1. 用 <see cref="SpatialFilter"/> 解析出的**种子空间**收集（布局通配符在这里生效，
        ///      没被选中的布局根本不会被遍历）；
        ///   2. 收集完再按图框容器 / 几何区域后置筛一遍（依赖实体包围盒，只能事后算）。
        /// </summary>
        public static List<ScanHit> Scan(RuleContext ctx, EntityFilter filter)
        {
            if (ctx == null || ctx.Document == null)
            {
                return new List<ScanHit>();
            }

            ScanScope scope = ResolveScope(ctx);
            SpatialFilter spatial = ctx.Spatial;

            List<ScanHit> hits = spatial != null && spatial.Spaces != null && spatial.Spaces.Count > 0
                ? DatabaseWalker.CollectFrom(ctx.Document, spatial.Spaces, ctx.MaxNestingDepth,
                    ctx.IncludeNestedBlocks, filter == null ? (Func<Entity, bool>)null : filter.Matches)
                : DatabaseWalker.Collect(ctx.Document, scope, ctx.MaxNestingDepth,
                    ctx.IncludeNestedBlocks, filter == null ? (Func<Entity, bool>)null : filter.Matches);

            if (spatial == null)
            {
                return hits;
            }

            // 配了图框/区域但一个都没匹配上：直接空结果。
            // 不能因为 HasAreaFilter=false 就当成"没过滤"——那会退化成整张图。
            if (spatial.MatchedNothing)
            {
                return new List<ScanHit>();
            }

            if (!spatial.HasAreaFilter)
            {
                return hits;
            }

            var filtered = new List<ScanHit>(hits.Count);
            foreach (ScanHit hit in hits)
            {
                if (spatial.Accepts(hit))
                {
                    filtered.Add(hit);
                }
            }

            if (spatial.UndecidableCount > 0 && ctx.Log != null)
            {
                ctx.Log.Warn(string.Format(
                    "区域/图框过滤：{0} 个对象没有包围盒（表格或未知实体），无法判定是否在区域内，已排除。",
                    spatial.UndecidableCount));
            }

            return filtered;
        }

        /// <summary>解析规则生效的扫描范围，并把降级情况写进日志。</summary>
        public static ScanScope ResolveScope(RuleContext ctx)
        {
            ScanScope scope = DatabaseWalker.ParseScope(ctx.Filter.Scope, ScanScope.ModelAndLayouts);

            if (scope == ScanScope.Selection)
            {
                if (ctx.Log != null)
                {
                    ctx.Log.Warn("Filter.Scope=Selection 在离线模式下不可用（没有交互选择集），已按 ModelAndLayouts 处理整张图。");
                }

                return ScanScope.ModelAndLayouts;
            }

            return scope;
        }

        /// <summary>命中对象所在空间的显示名（模型 / 布局名 / 块:xxx）。</summary>
        public static string SpaceNameOf(ScanHit hit)
        {
            if (hit == null || hit.Owner == null)
            {
                return string.Empty;
            }

            if (hit.Depth == 0)
            {
                return hit.Owner.Layout != null ? hit.Owner.Layout.Name : hit.Owner.Name;
            }

            return "块:" + hit.Owner.Name;
        }

        /// <summary>
        /// 跳过锁定图层上的实体？命中但位于锁定图层时统一走这里判定，
        /// 保证所有规则的"跳过"语义一致。
        /// </summary>
        public static bool ShouldSkipLocked(RuleContext ctx, Entity entity)
        {
            if (ctx == null || !ctx.SkipLockedLayers || entity == null)
            {
                return false;
            }

            string layer = LayerService.LayerNameOf(entity);
            return LayerService.IsLocked(ctx.Document, layer);
        }
    }
}
