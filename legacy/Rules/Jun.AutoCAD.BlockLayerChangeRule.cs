using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 规则：批量把指定块（一个或多个块名）归到指定图层。
    ///
    /// 与 EntityLayerChange 的分工：
    ///   EntityLayerChange  按"实体类型 + 颜色 + 图层"筛选，适合"所有红色线归层"这类通用场景；
    ///   BlockLayerChange   按"块名"筛选，专门解决"图框块/标题块/设备块散落在各图层"的问题——
    ///                      这类需求用 EntityLayerChange 表达不了（INSERT 没有颜色/类型上的区分度）。
    ///
    /// 处理链路：
    ///   1. 用 DXF 组码 2（块名，多块名走 &lt;OR ... OR&gt;）筛出候选 INSERT；
    ///   2. 逐个确认有效块名（动态块用动态块定义名比较），排除已删除对象；
    ///   3. 锁定图层 → 按 SkipLockedLayers 跳过并告警；
    ///   4. 改块参照自身的 Layer；AlsoMoveAttributes=true 时连同块参照上的 ATTRIB 一起改；
    ///   5. AlsoMoveBlockContent=true（默认）时，把**块定义内部的对象**也改到目标图层——
    ///      只改参照的图层是不够的：块内实体自带图层，不随参照图层走，
    ///      所以"块归层了但块里的线还是老图层"（2026-10-30 修复的功能缺口）。
    ///
    /// 目标图层不存在时按 Action 里的 LayerColorIndex / LayerLinetype / LayerLineWeight / LayerPlottable 自动创建
    /// （CreateIfMissing=false 时改为跳过并告警）。
    ///
    /// 嵌套块：由 DatabaseWalker 递归进入块定义，因此"块中块"里的引用同样会被归层；
    ///          在块定义内部改动会同时作用于所有引用该定义的插入——这正是批量治理想要的效果。
    /// </summary>
    public sealed class BlockLayerChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "BlockLayerChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            List<string> blockNames = CollectBlockNames(ctx.Filter);
            if (blockNames.Count == 0)
            {
                throw new RuleValidationException(
                    "BlockLayerChange 规则缺少 Filter.SourceBlockName / Filter.SourceBlockNames。" +
                    "为避免把整张图的块都挪走，规则中止。");
            }

            string targetLayer = ctx.Action.TargetLayer == null ? null : ctx.Action.TargetLayer.Trim();
            if (string.IsNullOrEmpty(targetLayer))
            {
                throw new RuleValidationException("BlockLayerChange 规则缺少 Action.TargetLayer。");
            }

            var tr = ctx.Transaction;
            var db = ctx.Database;

            // 目标图层（不存在则按配置创建）
            ObjectId targetLayerId = LayerService.EnsureLayer(tr, db, targetLayer, ctx.Action, ctx.Log);
            if (targetLayerId.IsNull)
            {
                throw new RuleValidationException(string.Format(
                    "目标图层「{0}」不存在且未创建，规则中止。", targetLayer));
            }

            // 默认 true：只挪块参照、块里的内容不动，用户看到的就是"改了但图上没变"
            bool contentWanted = RuleExecutionContext.Bool(ctx.Action.AlsoMoveBlockContent, true);

            // 属性默认跟着"块内对象"走：ATTDEF 都改到目标图层了，已有的 ATTRIB 却留在原图层，
            // 标题块的文字仍然"没归层"，等于修复只做了一半。显式写 false 才保留属性原图层。
            bool alsoMoveAttributes = RuleExecutionContext.Bool(ctx.Action.AlsoMoveAttributes, contentWanted);

            bool alsoMoveContent = contentWanted;

            // 不改块定义内部（IncludeNestedBlocks=false）时，块内对象归层无从谈起——
            // 这个开关的语义就是"只处理顶层实体，不进入任何块定义"，这里必须尊重它。
            // 属性（ATTRIB）是块参照的兄弟实体、不在块定义里，因此不受这个开关影响。
            if (alsoMoveContent && !ctx.IncludeNestedBlocks)
            {
                alsoMoveContent = false;
                ctx.Log.Warn("Filter.IncludeNestedBlocks=false：本次不改块定义内部对象" +
                             (alsoMoveAttributes
                                 ? "（块参照上的属性仍按 AlsoMoveAttributes 归层；要让块内一起归层请把该开关设为 true）。"
                                 : "（要让块内一起归层请把该开关设为 true）。"));
            }

            // 改的是块定义，会作用于所有引用；只选了部分对象时先把话说清楚
            if (alsoMoveContent
                && DatabaseWalker.ParseScope(ctx.Filter.Scope, ScanScope.ModelAndLayouts) == ScanScope.Selection)
            {
                ctx.Log.Warn("Scope=Selection：选中的块参照会改到它的**块定义**，" +
                             "因此该块在其它位置的所有引用都会一起归层。");
            }

            // 块名本身就是过滤条件，不需要再补 EntityTypes：
            // DxfFilter 的块名判定只对 BlockReference 成立，非块实体自然被排除。
            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            ctx.Log.Detail(string.Format("块名：{0} → 目标图层：{1}；属性一并归层：{2}；块内对象一并归层：{3}",
                string.Join(", ", blockNames.ToArray()), targetLayer,
                alsoMoveAttributes ? "是" : "否", alsoMoveContent ? "是" : "否"));
            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("扫描到 {0} 个候选对象。", hits.Count));

            // 同一个块定义可能被多个 INSERT 引用，块内对象只有一份：
            // 每个定义只处理一次，否则同一批实体被反复"归层"，统计也会翻倍。
            var processedDefinitions = new HashSet<ObjectId>();

            // 锁定判断要打开图层表，块内对象动辄上千个，按图层名缓存一次
            var lockedLayers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (ScanHit hit in hits)
            {
                BlockReference br = null;
                try
                {
                    br = tr.GetObject(hit.Id, OpenMode.ForRead, false) as BlockReference;
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("{0} 打开失败：{1}", ObjectUtil.HandleOf(hit.Id), ex.Message));
                    continue;
                }

                if (br == null || br.IsErased)
                {
                    continue;
                }

                // 双保险：DXF 组码 2 只认块名，动态块的匿名名会漏，这里按有效块名再判一次
                if (!MatchesAny(br, blockNames, tr))
                {
                    continue;
                }

                result.Hit();
                string desc = ObjectUtil.Describe(br);

                bool layerNeedsMove = !string.Equals(br.Layer, targetLayer, StringComparison.OrdinalIgnoreCase);

                if (layerNeedsMove && IsLayerLocked(tr, db, br.Layer, ctx, lockedLayers))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，按 SkipLockedLayers=true 跳过。", desc, br.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                string oldLayer = br.Layer;
                bool layerMoved = false;

                if (layerNeedsMove)
                {
                    try
                    {
                        br.UpgradeOpen();
                        br.Layer = targetLayer;
                        layerMoved = true;
                    }
                    catch (Exception ex)
                    {
                        string msg = string.Format("{0} 修改图层失败：{1}", desc, ex.Message);
                        result.MarkFailed(msg);
                        ctx.Log.Error(msg);
                        continue;
                    }
                }

                // 块参照已经就在目标图层时，属性/块内对象仍可能留在原图层上，所以这两步不受 layerMoved 限制
                int attMoved = alsoMoveAttributes
                    ? MoveAttributes(tr, db, br, targetLayer, ctx, lockedLayers)
                    : 0;

                ContentMoveStats content = alsoMoveContent
                    ? MoveBlockContent(tr, db, br, targetLayer, ctx, processedDefinitions, lockedLayers)
                    : null;

                int contentMoved = content == null ? 0 : content.Moved;

                if (!layerMoved && attMoved == 0 && contentMoved == 0)
                {
                    if (content != null && content.Failed > 0)
                    {
                        // 有对象改不动：算"失败"而不是"跳过"，否则汇总行会显示一切正常
                        string failMsg = string.Format("{0} 块内对象归层失败 {1} 个，详见日志。", desc, content.Failed);
                        result.MarkFailed(failMsg);
                        ctx.Log.Error(failMsg);
                        continue;
                    }

                    var why = new List<string>();
                    if (alsoMoveContent)
                    {
                        // 定义被本轮前面的 INSERT 处理过时，"块内对象也已在目标图层"是刚才改的结果，
                        // 不是本来就干净，日志要说清楚，免得用户以为规则没生效
                        why.Add(content != null && content.Reused
                            ? "已在目标图层，块定义本轮已处理过"
                            : "已在目标图层，块内对象也已在目标图层");
                    }
                    else
                    {
                        why.Add("已在目标图层");
                    }

                    if (content != null && content.Locked > 0)
                    {
                        why.Add(string.Format("块内 {0} 个位于锁定图层", content.Locked));
                    }

                    result.MarkSkipped(desc + " " + string.Join("，", why.ToArray()) + "，跳过。");
                    continue;
                }

                string detail = string.Format(
                    "{0}：{1}{2}{3}{4}",
                    desc,
                    layerMoved ? string.Format("图层 {0} → {1}", oldLayer, targetLayer) : "已在目标图层",
                    attMoved > 0 ? string.Format("，属性 {0} 个", attMoved) : string.Empty,
                    DescribeContent(content),
                    hit.IsNested ? string.Format("，嵌套于「{0}」深度 {1}", hit.OwnerName, hit.Depth) : string.Empty);

                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
            }
        }

        // ---------------------------------------------------------------- 块内对象归层

        /// <summary>块内对象归层的统计（按一次 INSERT 汇总，不含跨 INSERT 的累计）。</summary>
        private sealed class ContentMoveStats
        {
            /// <summary>成功改到目标图层的块内对象数。</summary>
            public int Moved;

            /// <summary>位于锁定图层、按 SkipLockedLayers 跳过的数量。</summary>
            public int Locked;

            /// <summary>
            /// 单个对象改图层失败的数量。明细里会写出来；若这个 INSERT 别的什么都没改成，
            /// 主循环会把它记成"失败"，避免汇总行显示一切正常（见 Execute）。
            /// </summary>
            public int Failed;

            /// <summary>实际动过的块定义名（嵌套时会有多个），用于日志定位。</summary>
            public readonly List<string> Definitions = new List<string>();

            /// <summary>
            /// 本次要处理的块定义在**本轮规则**里已经被别的 INSERT 处理过了。
            /// 用来区分"块内对象本来就在目标图层"和"刚被前一个引用改过"，避免日志说反话。
            /// </summary>
            public bool Reused;
        }

        /// <summary>
        /// 把块参照背后的块定义里的对象改到目标图层，返回统计。
        ///
        /// 为什么改"块定义"而不是"每个实例的副本"：
        ///   块内对象在所有 INSERT 之间是共享的（同一个 BlockTableRecord），
        ///   逐个实例复制会破坏块的复用，而且 AutoCAD 没有"只改这一个实例内部几何"的公开 API。
        ///   改定义会作用于所有引用——批量治理场景下这正是想要的。
        /// </summary>
        private static ContentMoveStats MoveBlockContent(
            Transaction tr, Database db, BlockReference br, string targetLayer,
            RuleExecutionContext ctx, HashSet<ObjectId> processed, Dictionary<string, bool> lockedLayers)
        {
            var stats = new ContentMoveStats();

            bool isDynamic = false;
            try
            {
                isDynamic = br.IsDynamicBlock;
            }
            catch
            {
                // 属性读取失败按普通块处理
            }

            // 1) 参照自身的块定义：动态块的匿名块（*U##）里才是实际显示的几何
            ObjectId ownDefId = ObjectId.Null;
            try
            {
                ownDefId = br.BlockTableRecord;
            }
            catch (Exception ex)
            {
                ctx.Log.Warn(string.Format("读取块定义失败：{0}", ex.Message));
            }

            MoveDefinitionContent(tr, db, ownDefId, targetLayer, ctx, processed, lockedLayers, 0, isDynamic, stats);

            // 2) 动态块还要改原始动态块定义：匿名块由宿主按参数/可见性重建，
            //    只改匿名块的话，参数一变或重生成后图层会被重建回原样。
            if (isDynamic)
            {
                ObjectId dynamicDefId = ObjectId.Null;
                try
                {
                    dynamicDefId = br.DynamicBlockTableRecord;
                }
                catch (Exception ex)
                {
                    ctx.Log.Warn(string.Format("读取动态块定义失败：{0}", ex.Message));
                }

                MoveDefinitionContent(tr, db, dynamicDefId, targetLayer, ctx, processed, lockedLayers, 0, true, stats);
            }

            return stats;
        }

        /// <summary>
        /// 递归把某个块定义内部的对象改到目标图层。
        /// </summary>
        /// <param name="allowAnonymous">
        /// 是否允许处理匿名块定义。动态块的匿名实例块（*U##）必须处理（几何在里面）；
        /// 而标注块（*D）、表格块（*T）这类匿名块由宿主按需重建，改了没有意义还可能干扰重建，
        /// 因此递归遇到它们时直接跳过。
        /// </param>
        private static void MoveDefinitionContent(
            Transaction tr, Database db, ObjectId btrId, string targetLayer,
            RuleExecutionContext ctx, HashSet<ObjectId> processed, Dictionary<string, bool> lockedLayers,
            int depth, bool allowAnonymous, ContentMoveStats stats)
        {
            if (btrId.IsNull)
            {
                return;
            }

            BlockTableRecord btr;
            try
            {
                btr = tr.GetObject(btrId, OpenMode.ForRead, false) as BlockTableRecord;
            }
            catch (Exception ex)
            {
                ctx.Log.Warn(string.Format("打开块定义 {0} 失败：{1}", ObjectUtil.HandleOf(btrId), ex.Message));
                stats.Failed++;
                return;
            }

            if (btr == null || btr.IsErased || btr.IsLayout)
            {
                return;
            }

            if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
            {
                // IsDependent 是 xref 依赖块（XR|NAME）：只读，逐实体 UpgradeOpen 必抛
                ctx.Log.Warn(string.Format("块定义「{0}」来自外部参照，其内部对象不做归层。", SafeName(btr)));
                return;
            }

            if (btr.IsAnonymous && !allowAnonymous)
            {
                return;
            }

            if (!processed.Add(btrId))
            {
                // 已经处理过：多个 INSERT 引用同一定义时只做一次
                stats.Reused = true;
                return;
            }

            string btrName = SafeName(btr);

            // 先快照 ObjectId：遍历中会 UpgradeOpen 修改实体，直接 foreach 打开中的 BTR 会抛异常
            var childIds = new List<ObjectId>();
            try
            {
                foreach (ObjectId id in btr)
                {
                    childIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                ctx.Log.Warn(string.Format("遍历块定义「{0}」失败：{1}", btrName, ex.Message));
                return;
            }

            int movedHere = 0;
            int lockedHere = 0;

            foreach (ObjectId id in childIds)
            {
                Entity ent;
                try
                {
                    ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch (Exception ex)
                {
                    stats.Failed++;
                    ctx.Log.Warn(string.Format("块定义「{0}」内的 {1} 打开失败：{2}",
                        btrName, ObjectUtil.HandleOf(id), ex.Message));
                    continue;
                }

                if (ent == null || ent.IsErased)
                {
                    continue;
                }

                string currentLayer = null;
                try
                {
                    currentLayer = ent.Layer;
                }
                catch
                {
                    // 读不到图层名时按"需要改"处理，下面会再失败一次并记日志
                }

                if (!string.Equals(currentLayer, targetLayer, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsLayerLocked(tr, db, currentLayer, ctx, lockedLayers))
                    {
                        // 逐个实体告警会把日志刷满，按定义汇总一次即可（明细见统计行）
                        stats.Locked++;
                        lockedHere++;
                    }
                    else
                    {
                        try
                        {
                            ent.UpgradeOpen();
                            ent.Layer = targetLayer;
                            movedHere++;
                            stats.Moved++;
                        }
                        catch (Exception ex)
                        {
                            stats.Failed++;
                            ctx.Log.Warn(string.Format("块定义「{0}」内的 {1} 归层失败：{2}",
                                btrName, ObjectUtil.Describe(ent), ex.Message));
                        }
                    }
                }

                // 递归：块定义里还可能嵌着块参照（块中块）
                var nested = ent as BlockReference;
                if (nested == null || depth >= ctx.MaxNestingDepth)
                {
                    continue;
                }

                ObjectId nestedDefId = ObjectId.Null;
                bool nestedDynamic = false;
                try
                {
                    nestedDefId = nested.BlockTableRecord;
                    nestedDynamic = nested.IsDynamicBlock;
                }
                catch
                {
                    continue;
                }

                MoveDefinitionContent(tr, db, nestedDefId, targetLayer, ctx, processed, lockedLayers, depth + 1, nestedDynamic, stats);

                if (nestedDynamic)
                {
                    ObjectId nestedDynamicDefId = ObjectId.Null;
                    try
                    {
                        nestedDynamicDefId = nested.DynamicBlockTableRecord;
                    }
                    catch
                    {
                        // 忽略：动态块定义读不到时至少匿名块已经改过了
                    }

                    MoveDefinitionContent(tr, db, nestedDynamicDefId, targetLayer, ctx, processed, lockedLayers, depth + 1, true, stats);
                }
            }

            if (movedHere > 0)
            {
                stats.Definitions.Add(btrName);
                ctx.Log.Detail(string.Format("块定义「{0}」内 {1} 个对象已归层到「{2}」。", btrName, movedHere, targetLayer));
            }

            if (lockedHere > 0)
            {
                ctx.Log.Warn(string.Format(
                    "块定义「{0}」内 {1} 个对象位于锁定图层，按 SkipLockedLayers=true 跳过。", btrName, lockedHere));
            }
        }

        private static string DescribeContent(ContentMoveStats content)
        {
            if (content == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            if (content.Moved > 0)
            {
                // 只有一个定义时不必再重复一遍数量；多个（块中块）时把名字都列出来
                string where = content.Definitions.Count <= 1
                    ? string.Format("「{0}」", content.Definitions.Count == 0 ? "?" : content.Definitions[0])
                    : string.Join("、", content.Definitions.ToArray());

                parts.Add(string.Format("块内对象 {0} 个（{1}）", content.Moved, where));
            }

            if (content.Locked > 0)
            {
                parts.Add(string.Format("块内 {0} 个位于锁定图层跳过", content.Locked));
            }

            if (content.Failed > 0)
            {
                parts.Add(string.Format("块内 {0} 个失败", content.Failed));
            }

            return parts.Count == 0 ? string.Empty : "，" + string.Join("，", parts.ToArray());
        }

        /// <summary>
        /// 把块参照上的属性（ATTRIB）一起挪到目标图层，返回成功个数。
        /// 属性是独立实体、各自带图层，只改块参照的图层不会带走它们。
        /// 与块参照本身一致：锁定图层上的属性按 SkipLockedLayers 跳过。
        /// 单个属性失败只记日志，不影响整条规则的统计口径。
        /// </summary>
        private static int MoveAttributes(Transaction tr, Database db, BlockReference br, string targetLayer,
            RuleExecutionContext ctx, Dictionary<string, bool> lockedLayers)
        {
            int moved = 0;
            int locked = 0;

            List<ObjectId> attIds = null;
            try
            {
                attIds = new List<ObjectId>();
                foreach (ObjectId id in br.AttributeCollection)
                {
                    attIds.Add(id);
                }
            }
            catch
            {
                // 属性集合读不到（块已删除等）→ 忽略
                return 0;
            }

            foreach (ObjectId id in attIds)
            {
                try
                {
                    var att = tr.GetObject(id, OpenMode.ForRead, false) as AttributeReference;
                    if (att == null || att.IsErased)
                    {
                        continue;
                    }

                    if (string.Equals(att.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsLayerLocked(tr, db, att.Layer, ctx, lockedLayers))
                    {
                        locked++;
                        continue;
                    }

                    att.UpgradeOpen();
                    att.Layer = targetLayer;
                    moved++;
                }
                catch (Exception ex)
                {
                    ctx.Log.Warn(string.Format("属性 {0} 归层失败：{1}", ObjectUtil.HandleOf(id), ex.Message));
                }
            }

            if (locked > 0)
            {
                ctx.Log.Warn(string.Format("块参照上有 {0} 个属性位于锁定图层，按 SkipLockedLayers=true 跳过。", locked));
            }

            return moved;
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

        /// <summary>
        /// 图层是否锁定（带缓存）。SkipLockedLayers=false 时直接返回 false，连图层表都不用打开。
        /// 块内对象可能成千上万，逐个打开图层表会成为瓶颈，所以按图层名缓存一次结果。
        /// </summary>
        private static bool IsLayerLocked(Transaction tr, Database db, string layerName,
            RuleExecutionContext ctx, Dictionary<string, bool> cache)
        {
            if (string.IsNullOrWhiteSpace(layerName) || !ctx.SkipLockedLayers)
            {
                return false;
            }

            bool locked;
            if (cache.TryGetValue(layerName, out locked))
            {
                return locked;
            }

            locked = LayerService.IsLocked(tr, db, layerName);
            cache[layerName] = locked;
            return locked;
        }

        /// <summary>收集配置里的块名（SourceBlockName + SourceBlockNames，去重、保序）。</summary>
        private static List<string> CollectBlockNames(RuleFilter filter)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (filter != null)
            {
                if (!string.IsNullOrWhiteSpace(filter.SourceBlockName))
                {
                    string single = filter.SourceBlockName.Trim();
                    if (seen.Add(single))
                    {
                        names.Add(single);
                    }
                }

                if (filter.SourceBlockNames != null)
                {
                    foreach (string raw in filter.SourceBlockNames)
                    {
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            continue;
                        }

                        string name = raw.Trim();
                        if (seen.Add(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }

            return names;
        }

        private static bool MatchesAny(BlockReference br, List<string> blockNames, Transaction tr)
        {
            foreach (string name in blockNames)
            {
                if (BlockNameUtil.Matches(br, name, tr))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
