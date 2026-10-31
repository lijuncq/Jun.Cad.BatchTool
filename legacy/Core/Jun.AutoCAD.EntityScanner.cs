using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 实体扫描入口：把"DXF 过滤 + 扫描范围"统一成一份命中列表。
    ///
    /// 两条路径：
    ///   Scope = Selection   → 走 SelectionFilter（Editor.SelectImplied / SelectAll），
    ///                         过滤条件完全由 DXF 组码交给 AutoCAD 内核处理；
    ///   Scope = 其他        → 走 DatabaseWalker（模型空间 + 布局 + 嵌套块），
    ///                         用 DxfFilter.Matches 做等价判定。
    /// </summary>
    public static class EntityScanner
    {
        public static List<ScanHit> Scan(RuleExecutionContext ctx, DxfFilter filter)
        {
            var scope = DatabaseWalker.ParseScope(ctx.Filter.Scope, ScanScope.ModelAndLayouts);
            if (scope == ScanScope.Selection)
            {
                return ScanSelection(ctx, filter);
            }

            return DatabaseWalker.Collect(ctx.Transaction, ctx.Database, scope, ctx.MaxNestingDepth, ctx.IncludeNestedBlocks, ent => filter.Matches(ent));
        }

        /// <summary>当前选择集 + DXF 组码过滤。</summary>
        private static List<ScanHit> ScanSelection(RuleExecutionContext ctx, DxfFilter filter)
        {
            var hits = new List<ScanHit>();
            var editor = ctx.Editor;
            if (editor == null)
            {
                return hits;
            }

            SelectionFilter selectionFilter = filter.ToSelectionFilter();

            PromptSelectionResult psr = editor.SelectImplied();
            if (psr.Status != PromptStatus.OK)
            {
                psr = selectionFilter == null ? editor.SelectAll() : editor.SelectAll(selectionFilter);
            }

            if (psr.Status != PromptStatus.OK || psr.Value == null)
            {
                return hits;
            }

            foreach (SelectedObject so in psr.Value)
            {
                if (so == null || so.ObjectId.IsNull)
                {
                    continue;
                }

                // 已有选择集时再补一次托管判定，保证与遍历路径语义一致
                try
                {
                    var ent = ctx.Transaction.GetObject(so.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (ent == null || ent.IsErased || !filter.Matches(ent))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                hits.Add(new ScanHit { Id = so.ObjectId, Depth = 0, OwnerName = "选择集" });
            }

            return hits;
        }
    }
}
