using System;
using System.Collections.Generic;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
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
    ///   1. 按块名筛出候选 INSERT（动态块按"有效块名"回溯到动态块定义名）；
    ///   2. 锁定图层 → 按 SkipLockedLayers 跳过并告警；
    ///   3. 改块参照自身的 Layer；AlsoMoveAttributes=true 时连同 ATTRIB 一起改；
    ///   4. AlsoMoveBlockContent=true（默认）时，把**块定义内部的对象**也改到目标图层——
    ///      只改参照的图层是不够的：块内实体自带图层，不随参照图层走。
    ///
    /// 与 AutoCAD 版的差别：
    ///   * 动态块在离线模型里表现为"实例指向匿名块、匿名块 Source 指回动态块定义"，
    ///     因此要同时改匿名实例块与 Source 定义（否则重生成后图层会被重建回原样）；
    ///   * 不再有 IsDependent 的独立属性，改用 <see cref="BlockTypeFlags.XRefDependent"/> 判定。
    /// </summary>
    public sealed class BlockLayerChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "BlockLayerChange"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
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

            Layer target = LayerService.EnsureLayer(ctx.Document, targetLayer, ctx.Action, ctx.Log, ctx.Resources);
            if (target == null)
            {
                throw new RuleValidationException(string.Format(
                    "目标图层「{0}」不存在且未创建，规则中止。", targetLayer));
            }

            // 默认 true：只挪块参照、块里的内容不动，用户看到的就是"改了但图上没变"
            bool contentWanted = RuleContext.Bool(ctx.Action.AlsoMoveBlockContent, true);

            // 属性默认跟着"块内对象"走：ATTDEF 都改到目标图层了，已有 ATTRIB 却留在原图层，等于只修一半
            bool alsoMoveAttributes = RuleContext.Bool(ctx.Action.AlsoMoveAttributes, contentWanted);
            bool alsoMoveContent = contentWanted;

            // 不改块定义内部（IncludeNestedBlocks=false）时，块内对象归层无从谈起
            if (alsoMoveContent && !ctx.IncludeNestedBlocks)
            {
                alsoMoveContent = false;
                ctx.Log.Warn("Filter.IncludeNestedBlocks=false：本次不改块定义内部对象" +
                             (alsoMoveAttributes
                                 ? "（块参照上的属性仍按 AlsoMoveAttributes 归层；要让块内一起归层请把该开关设为 true）。"
                                 : "（要让块内一起归层请把该开关设为 true）。"));
            }

            EntityFilter filter = EntityFilter.Build(ctx.Filter, null);
            ctx.Log.Detail(string.Format("块名：{0} → 目标图层：{1}；属性一并归层：{2}；块内对象一并归层：{3}",
                string.Join(", ", blockNames.ToArray()), targetLayer,
                alsoMoveAttributes ? "是" : "否", alsoMoveContent ? "是" : "否"));
            ctx.Log.Detail("过滤条件：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("扫描到 {0} 个候选对象。", hits.Count));

            // 同一个块定义可能被多个 INSERT 引用，块内对象只有一份：每个定义只处理一次
            var processedDefinitions = new HashSet<BlockRecord>();

            // 锁定判断要查图层表，块内对象动辄上千个，按图层名缓存一次
            var lockedLayers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (ScanHit hit in hits)
            {
                var ins = hit.Entity as Insert;
                if (ins == null || ins is TableEntity)
                {
                    continue;
                }

                // 双保险：过滤层已按有效块名筛过，这里再确认一次（嵌套扫描时命中项可能来自别的容器）
                if (!MatchesAny(ins, blockNames))
                {
                    continue;
                }

                result.Hit();
                string desc = EntityDescribe.Describe(hit);

                string currentLayerName = LayerService.LayerNameOf(ins);
                bool layerNeedsMove = !string.Equals(currentLayerName, targetLayer, StringComparison.OrdinalIgnoreCase);

                if (layerNeedsMove && IsLayerLocked(ctx, currentLayerName, lockedLayers))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，按 SkipLockedLayers=true 跳过。", desc, currentLayerName);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                string oldLayer = currentLayerName;
                bool layerMoved = false;

                if (layerNeedsMove)
                {
                    try
                    {
                        ins.Layer = target;
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

                // 块参照已在目标图层时，属性/块内对象仍可能留在原图层，所以这两步不受 layerMoved 限制
                int attMoved = alsoMoveAttributes
                    ? MoveAttributes(ins, target, ctx, lockedLayers)
                    : 0;

                ContentMoveStats content = alsoMoveContent
                    ? MoveBlockContent(ins, target, ctx, processedDefinitions, lockedLayers)
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

        /// <summary>块内对象归层的统计（按一次 INSERT 汇总）。</summary>
        private sealed class ContentMoveStats
        {
            public int Moved;
            public int Locked;
            public int Failed;
            public readonly List<string> Definitions = new List<string>();

            /// <summary>本次要处理的块定义在本轮规则里已经被别的 INSERT 处理过了。</summary>
            public bool Reused;
        }

        /// <summary>
        /// 把块参照背后的块定义里的对象改到目标图层。
        ///
        /// 为什么改"块定义"而不是"每个实例的副本"：
        ///   块内对象在所有 INSERT 之间共享（同一个 BlockRecord），
        ///   逐个实例复制会破坏块的复用，而且没有"只改这一个实例内部几何"的入口。
        ///   改定义会作用于所有引用——批量治理场景下这正是想要的。
        /// </summary>
        private static ContentMoveStats MoveBlockContent(
            Insert ins, Layer target, RuleContext ctx,
            HashSet<BlockRecord> processed, Dictionary<string, bool> lockedLayers)
        {
            var stats = new ContentMoveStats();

            BlockRecord ownDef = ins.Block;
            bool isDynamic = ownDef != null && ownDef.IsAnonymous && ownDef.Source != null;

            // 1) 参照自身的块定义：动态块的匿名块（*U##）里才是实际显示的几何
            MoveDefinitionContent(ownDef, target, ctx, processed, lockedLayers, 0, isDynamic, stats);

            // 2) 动态块还要改原始动态块定义：匿名块由库/宿主按参数重建，
            //    只改匿名块的话，参数一变或重生成后图层会被重建回原样。
            if (isDynamic)
            {
                MoveDefinitionContent(ownDef.Source, target, ctx, processed, lockedLayers, 0, true, stats);
            }

            return stats;
        }

        /// <summary>
        /// 递归把某个块定义内部的对象改到目标图层。
        /// </summary>
        /// <param name="allowAnonymous">
        /// 是否允许处理匿名块定义。动态块的匿名实例块（*U##）必须处理（几何在里面）；
        /// 而标注块（*D）、表格块（*T）这类匿名块由宿主按需重建，改了没意义还可能干扰重建，
        /// 因此递归遇到它们时直接跳过。
        /// </param>
        private static void MoveDefinitionContent(
            BlockRecord block, Layer target, RuleContext ctx,
            HashSet<BlockRecord> processed, Dictionary<string, bool> lockedLayers,
            int depth, bool allowAnonymous, ContentMoveStats stats)
        {
            if (block == null)
            {
                return;
            }

            if (DatabaseWalker.IsLayoutBlock(block))
            {
                return;
            }

            if (DatabaseWalker.IsExternalReference(block) || IsXRefDependent(block))
            {
                ctx.Log.Warn(string.Format("块定义「{0}」来自外部参照，其内部对象不做归层。", block.Name));
                return;
            }

            if (block.IsAnonymous && !allowAnonymous)
            {
                return;
            }

            if (!processed.Add(block))
            {
                // 多个 INSERT 引用同一定义时只做一次
                stats.Reused = true;
                return;
            }

            string blockName = block.Name;

            // 先快照：递归中会继续改实体，直接遍历原集合不安全
            var children = new List<Entity>();
            foreach (Entity e in block.Entities)
            {
                children.Add(e);
            }

            int movedHere = 0;
            int lockedHere = 0;

            foreach (Entity ent in children)
            {
                if (ent == null)
                {
                    continue;
                }

                string currentLayer = LayerService.LayerNameOf(ent);

                if (!string.Equals(currentLayer, target.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsLayerLocked(ctx, currentLayer, lockedLayers))
                    {
                        // 逐个实体告警会把日志刷满，按定义汇总一次即可
                        stats.Locked++;
                        lockedHere++;
                    }
                    else
                    {
                        try
                        {
                            ent.Layer = target;
                            movedHere++;
                            stats.Moved++;
                        }
                        catch (Exception ex)
                        {
                            stats.Failed++;
                            ctx.Log.Warn(string.Format("块定义「{0}」内的 {1} 归层失败：{2}",
                                blockName, EntityDescribe.Describe(ent), ex.Message));
                        }
                    }
                }

                // 递归：块定义里还可能嵌着块参照（块中块）
                var nested = ent as Insert;
                if (nested == null || nested is TableEntity || depth >= ctx.MaxNestingDepth)
                {
                    continue;
                }

                BlockRecord nestedDef = nested.Block;
                bool nestedDynamic = nestedDef != null && nestedDef.IsAnonymous && nestedDef.Source != null;

                MoveDefinitionContent(nestedDef, target, ctx, processed, lockedLayers, depth + 1, nestedDynamic, stats);

                if (nestedDynamic)
                {
                    MoveDefinitionContent(nestedDef.Source, target, ctx, processed, lockedLayers, depth + 1, true, stats);
                }
            }

            if (movedHere > 0)
            {
                stats.Definitions.Add(blockName);
                ctx.Log.Detail(string.Format("块定义「{0}」内 {1} 个对象已归层到「{2}」。", blockName, movedHere, target.Name));
            }

            if (lockedHere > 0)
            {
                ctx.Log.Warn(string.Format(
                    "块定义「{0}」内 {1} 个对象位于锁定图层，按 SkipLockedLayers=true 跳过。", blockName, lockedHere));
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
        /// </summary>
        private static int MoveAttributes(Insert ins, Layer target, RuleContext ctx, Dictionary<string, bool> lockedLayers)
        {
            int moved = 0;
            int locked = 0;

            List<AttributeEntity> attributes;
            try
            {
                attributes = new List<AttributeEntity>(ins.Attributes);
            }
            catch
            {
                return 0;
            }

            foreach (AttributeEntity att in attributes)
            {
                if (att == null)
                {
                    continue;
                }

                string currentLayer = LayerService.LayerNameOf(att);
                if (string.Equals(currentLayer, target.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsLayerLocked(ctx, currentLayer, lockedLayers))
                {
                    locked++;
                    continue;
                }

                try
                {
                    att.Layer = target;
                    moved++;
                }
                catch (Exception ex)
                {
                    ctx.Log.Warn(string.Format("属性 {0} 归层失败：{1}", EntityDescribe.HandleOf(att), ex.Message));
                }
            }

            if (locked > 0)
            {
                ctx.Log.Warn(string.Format("块参照上有 {0} 个属性位于锁定图层，按 SkipLockedLayers=true 跳过。", locked));
            }

            return moved;
        }

        /// <summary>是否 xref 依赖块（XR|NAME）：只读，不应改动。</summary>
        private static bool IsXRefDependent(BlockRecord block)
        {
            return ((int)block.Flags & (int)BlockTypeFlags.XRefDependent) != 0;
        }

        /// <summary>
        /// 图层是否锁定（带缓存）。SkipLockedLayers=false 时直接返回 false，连图层表都不用查。
        /// </summary>
        private static bool IsLayerLocked(RuleContext ctx, string layerName, Dictionary<string, bool> cache)
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

            locked = LayerService.IsLocked(ctx.Document, layerName);
            cache[layerName] = locked;
            return locked;
        }

        /// <summary>收集配置里的块名（SourceBlockName + SourceBlockNames，去重、保序）。</summary>
        public static List<string> CollectBlockNames(RuleFilter filter)
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

        private static bool MatchesAny(Insert ins, List<string> blockNames)
        {
            string effective = EntityFilter.EffectiveBlockName(ins.Block);
            if (effective == null)
            {
                return false;
            }

            foreach (string name in blockNames)
            {
                if (string.Equals(effective, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
