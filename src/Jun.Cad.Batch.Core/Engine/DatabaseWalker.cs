using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>扫描范围。</summary>
    public enum ScanScope
    {
        ModelAndLayouts,
        ModelOnly,
        LayoutsOnly,

        /// <summary>
        /// 兼容旧配置保留的枚举值。离线工具没有"当前选择集"，执行时会被降级为
        /// <see cref="ModelAndLayouts"/> 并给出警告——静默当成全选会让人以为筛选生效了。
        /// </summary>
        Selection
    }

    /// <summary>一次扫描命中的实体。</summary>
    public sealed class ScanHit
    {
        public Entity Entity { get; set; }

        /// <summary>所属容器（模型空间 / 布局块 / 块定义），用于日志定位。</summary>
        public BlockRecord Owner { get; set; }

        /// <summary>嵌套深度：0 = 顶层空间，1 = 块定义内第一层，依此类推。</summary>
        public int Depth { get; set; }

        public string OwnerName
        {
            get { return Owner == null ? "?" : Owner.Name; }
        }

        public bool IsNested
        {
            get { return Depth > 0; }
        }

        /// <summary>句柄（ACadSharp 里就是 DXF 组码 5 的值），日志里用它定位对象。</summary>
        public string HandleText
        {
            get { return Entity == null ? "0" : Entity.Handle.ToString("X"); }
        }

        public override string ToString()
        {
            return string.Format("{0}@{1}(深度{2})", HandleText, OwnerName, Depth);
        }
    }

    /// <summary>
    /// 数据库遍历器：模型空间 + 全部布局空间 + 递归块定义内部。
    ///
    /// 与 AutoCAD 版的结构一致，但去掉了 <c>Transaction</c>/<c>ObjectId</c>：
    /// ACadSharp 里 <see cref="BlockRecord.Entities"/> 就是内存集合，直接拿引用即可。
    /// 因此"去重"从 <c>HashSet&lt;ObjectId&gt;</c> 换成了 <c>HashSet&lt;BlockRecord&gt;</c>（引用相等）。
    ///
    /// 为什么仍然要 BFS 而不是递归：
    ///   * 深度由配置 <c>MaxNestingDepth</c> 控制，队列比递归好截断；
    ///   * 自引用/循环引用（块 A 里插块 A）不会把栈打爆。
    ///
    /// 不进入的容器（与 AutoCAD 版策略一致）：
    ///   * 外部参照（<see cref="BlockTypeFlags.XRef"/> / <see cref="BlockTypeFlags.XRefOverlay"/>）——不允许改别人的图；
    ///   * 匿名块（<see cref="BlockRecord.IsAnonymous"/>，典型是动态块的 *U 实例块）——按"动态块边界"跳过。
    /// </summary>
    public static class DatabaseWalker
    {
        /// <summary>
        /// 按扫描范围收集实体。
        /// </summary>
        /// <param name="includeNested">
        /// 是否递归进入块定义内部（对应配置 <c>Filter.IncludeNestedBlocks</c>，默认 true）。
        /// </param>
        /// <param name="predicate">
        /// 命中判定（通常传 <see cref="EntityFilter.Matches"/>）。谓词只决定"是否计入结果"，
        /// 不影响递归——块参照本身不匹配，也要继续进入它的块定义找内部匹配项。
        /// </param>
        public static List<ScanHit> Collect(
            CadDocument doc,
            ScanScope scope,
            int maxDepth,
            bool includeNested,
            Func<Entity, bool> predicate)
        {
            return CollectFrom(doc, GetSpaces(doc, scope), maxDepth, includeNested, predicate);
        }

        /// <summary>
        /// 从指定的种子空间收集实体（用于"只处理某些布局"的场景）。
        /// 种子由调用方给，本方法不再解析 Scope。
        /// </summary>
        public static List<ScanHit> CollectFrom(
            CadDocument doc,
            IEnumerable<BlockRecord> seeds,
            int maxDepth,
            bool includeNested,
            Func<Entity, bool> predicate)
        {
            var hits = new List<ScanHit>();
            if (doc == null || seeds == null)
            {
                return hits;
            }

            if (maxDepth < 1)
            {
                maxDepth = 1;
            }

            var visited = new HashSet<BlockRecord>();
            var queue = new Queue<KeyValuePair<BlockRecord, int>>();

            foreach (BlockRecord seed in seeds)
            {
                if (seed != null && visited.Add(seed))
                {
                    queue.Enqueue(new KeyValuePair<BlockRecord, int>(seed, 0));
                }
            }

            while (queue.Count > 0)
            {
                KeyValuePair<BlockRecord, int> pair = queue.Dequeue();
                BlockRecord owner = pair.Key;
                int depth = pair.Value;

                // 快照：规则执行中可能增删实体，直接遍历原集合会抛"集合已修改"
                var children = new List<Entity>();
                foreach (Entity e in owner.Entities)
                {
                    children.Add(e);
                }

                foreach (Entity ent in children)
                {
                    if (ent == null)
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
                        // 单个对象的判定异常不能中断整轮扫描
                        matched = false;
                    }

                    if (matched)
                    {
                        hits.Add(new ScanHit { Entity = ent, Owner = owner, Depth = depth });
                    }

                    if (!includeNested || depth >= maxDepth)
                    {
                        continue;
                    }

                    // TableEntity 继承 Insert，但它的 Block 是匿名块，下面的 IsAnonymous 判定会跳过
                    var ins = ent as Insert;
                    if (ins == null)
                    {
                        continue;
                    }

                    BlockRecord nested = ins.Block;
                    if (nested == null || visited.Contains(nested))
                    {
                        continue;
                    }

                    if (nested.IsAnonymous || IsExternalReference(nested))
                    {
                        continue;
                    }

                    visited.Add(nested);
                    queue.Enqueue(new KeyValuePair<BlockRecord, int>(nested, depth + 1));
                }
            }

            return hits;
        }

        /// <summary>
        /// 按 Scope 取顶层空间的块记录。
        ///
        /// 布局块的识别用 <see cref="BlockRecord.Layout"/> 非空——比按名字前缀
        /// （*Model_Space / *Paper_Space0）判断可靠：多布局图纸里第二个布局块名是
        /// *Paper_Space0，前缀匹配容易连匿名块一起捞进来。
        /// </summary>
        public static List<BlockRecord> GetSpaces(CadDocument doc, ScanScope scope)
        {
            var list = new List<BlockRecord>();
            if (doc == null)
            {
                return list;
            }

            var seen = new HashSet<BlockRecord>();

            if (scope == ScanScope.ModelAndLayouts || scope == ScanScope.ModelOnly || scope == ScanScope.Selection)
            {
                if (doc.ModelSpace != null && seen.Add(doc.ModelSpace))
                {
                    list.Add(doc.ModelSpace);
                }
            }

            if (scope == ScanScope.ModelAndLayouts || scope == ScanScope.LayoutsOnly || scope == ScanScope.Selection)
            {
                if (doc.PaperSpace != null && seen.Add(doc.PaperSpace))
                {
                    list.Add(doc.PaperSpace);
                }

                foreach (BlockRecord b in doc.BlockRecords)
                {
                    if (b == null || b.Layout == null)
                    {
                        continue;
                    }

                    if (seen.Add(b))
                    {
                        list.Add(b);
                    }
                }
            }

            return list;
        }

        /// <summary>是否是外部参照（含覆盖参照）。</summary>
        public static bool IsExternalReference(BlockRecord block)
        {
            if (block == null)
            {
                return false;
            }

            int flags = (int)block.Flags;
            return (flags & (int)BlockTypeFlags.XRef) != 0
                   || (flags & (int)BlockTypeFlags.XRefOverlay) != 0;
        }

        /// <summary>是否是布局块（模型空间 / 图纸空间）。</summary>
        public static bool IsLayoutBlock(BlockRecord block)
        {
            return block != null && block.Layout != null;
        }

        /// <summary>解析配置里的 Scope 字符串。无法识别时返回默认值并把原文交给调用方记录。</summary>
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

        /// <summary>把 Scope 渲染成人话（日志用）。</summary>
        public static string DescribeScope(ScanScope scope)
        {
            switch (scope)
            {
                case ScanScope.ModelOnly:
                    return "仅模型空间";
                case ScanScope.LayoutsOnly:
                    return "仅布局";
                case ScanScope.Selection:
                    return "当前选择集（离线不可用，已降级为模型+布局）";
                default:
                    return "模型空间 + 全部布局";
            }
        }
    }
}
