using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>扫描范围。</summary>
    public enum ScanScope
    {
        ModelAndLayouts,
        ModelOnly,
        LayoutsOnly,
        Selection
    }

    /// <summary>一次扫描命中的实体。</summary>
    public sealed class ScanHit
    {
        public ObjectId Id { get; set; }

        /// <summary>嵌套深度：0 = 顶层空间，1 = 块定义内第一层，依此类推。</summary>
        public int Depth { get; set; }

        /// <summary>所属容器（布局名或块定义名），用于日志定位。</summary>
        public string OwnerName { get; set; }

        public bool IsNested
        {
            get { return Depth > 0; }
        }

        public override string ToString()
        {
            return string.Format("{0}@{1}(深度{2})", ObjectUtil.HandleOf(Id), OwnerName, Depth);
        }
    }

    /// <summary>
    /// 数据库遍历器：模型空间 + 全部布局空间 + 递归块定义内部。
    ///
    /// 关键点（嵌套块）：
    ///   AutoCAD 的块定义本身也是一个 BlockTableRecord，内部可以再放 BlockReference。
    ///   只扫描顶层空间会漏掉"图框块里嵌的标题块"这类结构，因此这里用 BFS 递归进入
    ///   每一个被引用的块定义；同一块定义只进入一次（visited 去重），
    ///   既避免重复处理，也避免自引用/循环引用导致死循环。
    ///
    /// 不进入的容器：
    ///   * 外部参照（IsFromExternalReference / IsFromOverlayReference）——不允许修改；
    ///   * 匿名块（IsAnonymous，典型是动态块的 *U 实例块）——按"动态块边界"策略跳过，
    ///     保证不会误改某个动态块实例的私有几何。
    /// </summary>
    public static class DatabaseWalker
    {
        /// <summary>
        /// 按扫描范围收集实体。
        /// </summary>
        /// <param name="includeNested">
        /// 是否递归进入块定义内部（对应配置里的 <c>Filter.IncludeNestedBlocks</c>，默认 true）。
        /// 为 false 时只处理模型空间与布局里的顶层实体，不进入任何块定义。
        /// 2026-10-29 之前该配置项无人读取，属于死字段；本次接上，默认值与历史行为一致。
        /// </param>
        /// <param name="predicate">
        /// 命中判定（通常传 DxfFilter.Matches）。注意：谓词只决定"是否计入结果"，
        /// 不影响递归——即使块参照本身不匹配，也要继续进入它的块定义找内部匹配项。
        /// </param>
        public static List<ScanHit> Collect(Transaction tr, Database db, ScanScope scope, int maxDepth, bool includeNested, Func<Entity, bool> predicate)
        {
            var hits = new List<ScanHit>();
            var visited = new HashSet<ObjectId>();
            var queue = new Queue<KeyValuePair<ObjectId, int>>();

            // 1) 顶层种子：模型空间 + 各布局空间
            foreach (var seed in GetSpaceIds(tr, db, scope))
            {
                if (visited.Add(seed))
                {
                    queue.Enqueue(new KeyValuePair<ObjectId, int>(seed, 0));
                }
            }

            while (queue.Count > 0)
            {
                var pair = queue.Dequeue();
                ObjectId btrId = pair.Key;
                int depth = pair.Value;

                BlockTableRecord btr;
                try
                {
                    btr = tr.GetObject(btrId, OpenMode.ForRead, false) as BlockTableRecord;
                }
                catch
                {
                    continue;
                }

                if (btr == null)
                {
                    continue;
                }

                // 先快照 ObjectId 列表：规则可能在遍历过程中新增/删除实体，
                // 直接 foreach 打开中的 BTR 会抛 eWasOpenForRead / 迭代器失效。
                var childIds = new List<ObjectId>();
                foreach (ObjectId id in btr)
                {
                    childIds.Add(id);
                }

                foreach (ObjectId id in childIds)
                {
                    Entity ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    bool matched;
                    try
                    {
                        matched = predicate == null || predicate(ent);
                    }
                    catch
                    {
                        matched = false;
                    }

                    if (matched)
                    {
                        hits.Add(new ScanHit { Id = id, Depth = depth, OwnerName = SafeName(btr) });
                    }

                    // 2) 递归：块参照 → 其块定义
                    var br = ent as BlockReference;
                    if (br == null || !includeNested || depth >= maxDepth)
                    {
                        continue;
                    }

                    ObjectId nestedBtrId;
                    try
                    {
                        nestedBtrId = br.BlockTableRecord;
                    }
                    catch
                    {
                        continue;
                    }

                    if (nestedBtrId.IsNull || visited.Contains(nestedBtrId))
                    {
                        continue;
                    }

                    BlockTableRecord nested;
                    try
                    {
                        nested = tr.GetObject(nestedBtrId, OpenMode.ForRead, false) as BlockTableRecord;
                    }
                    catch
                    {
                        continue;
                    }

                    if (nested == null)
                    {
                        continue;
                    }

                    if (nested.IsAnonymous || nested.IsFromExternalReference || nested.IsFromOverlayReference)
                    {
                        // 动态块匿名实例块 / 外参块：不进入
                        continue;
                    }

                    if (visited.Add(nestedBtrId))
                    {
                        queue.Enqueue(new KeyValuePair<ObjectId, int>(nestedBtrId, depth + 1));
                    }
                }
            }

            return hits;
        }

        /// <summary>按 Scope 取顶层空间的 BlockTableRecord ObjectId。</summary>
        public static List<ObjectId> GetSpaceIds(Transaction tr, Database db, ScanScope scope)
        {
            var list = new List<ObjectId>();

            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null)
            {
                return list;
            }

            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord btr;
                try
                {
                    btr = tr.GetObject(btrId, OpenMode.ForRead, false) as BlockTableRecord;
                }
                catch
                {
                    continue;
                }

                if (btr == null || !btr.IsLayout)
                {
                    continue;
                }

                bool isModel = string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);

                if (scope == ScanScope.ModelOnly && !isModel)
                {
                    continue;
                }

                if (scope == ScanScope.LayoutsOnly && isModel)
                {
                    continue;
                }

                list.Add(btrId);
            }

            return list;
        }

        /// <summary>解析配置里的 Scope 字符串。</summary>
        public static ScanScope ParseScope(string scope, ScanScope defaultScope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return defaultScope;
            }

            switch (scope.Trim().ToUpperInvariant())
            {
                case "MODELANDLAYOUTS":
                case "ALL":
                case "MODEL_AND_LAYOUTS":
                    return ScanScope.ModelAndLayouts;

                case "MODELONLY":
                case "MODEL":
                    return ScanScope.ModelOnly;

                case "LAYOUTSONLY":
                case "LAYOUTS":
                case "PAPER":
                    return ScanScope.LayoutsOnly;

                case "SELECTION":
                case "SELECT":
                    return ScanScope.Selection;

                default:
                    return defaultScope;
            }
        }

        private static string SafeName(BlockTableRecord btr)
        {
            try
            {
                return btr.Name;
            }
            catch
            {
                return "?";
            }
        }
    }
}
