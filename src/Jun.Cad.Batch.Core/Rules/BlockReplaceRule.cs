using System;
using System.Collections.Generic;
using System.Globalization;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量替换块参照。
    ///
    /// 处理链路：
    ///   1. 按有效块名筛出候选 INSERT（动态块回溯到动态块定义名）；
    ///   2. 抓下属性值（Tag → 文本）；
    ///   3. 新建目标块参照，复制通用属性（图层/颜色/线型/线宽/线型比例/可见性/透明度）；
    ///   4. 调 <see cref="Insert.UpdateAttributes"/> 让库按目标块的 ATTDEF 建出 ATTRIB，再按 Tag 赋值；
    ///   5. 用新参照替换原所有者集合里的旧参照。
    ///
    /// 与 AutoCAD 版的**功能降级**：
    ///   * 动态参数（DynamicBlockReferenceProperty）在 ACadSharp 里没有建模，
    ///     <c>AllowDynamicBlock=true</c> 时能替换但**参数值会丢**，日志会明确警告；
    ///   * 属性迁移不再需要手工 <c>new AttributeEntity</c>：<c>UpdateAttributes()</c> 已经按
    ///     ATTDEF 建好了属性实例（实测手工创建会出现同 Tag 两条记录）。
    /// </summary>
    public sealed class BlockReplaceRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "BlockReplace"; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            string sourceName = ctx.Filter.SourceBlockName == null ? null : ctx.Filter.SourceBlockName.Trim();
            string targetName = ctx.Action.TargetBlockName == null ? null : ctx.Action.TargetBlockName.Trim();

            if (string.IsNullOrEmpty(sourceName))
            {
                throw new RuleValidationException("BlockReplace 规则缺少 Filter.SourceBlockName。");
            }

            if (string.IsNullOrEmpty(targetName))
            {
                throw new RuleValidationException("BlockReplace 规则缺少 Action.TargetBlockName。");
            }

            BlockRecord target;
            if (!ctx.Document.BlockRecords.TryGetValue(targetName, out target) || target == null)
            {
                throw new RuleValidationException(string.Format(
                    "目标块「{0}」在当前图纸中不存在，规则中止。请先把该块定义引入图纸。", targetName));
            }

            BlockRecord source;
            if (ctx.Document.BlockRecords.TryGetValue(sourceName, out source) && source != null)
            {
                if (ReferenceEquals(source, target))
                {
                    throw new RuleValidationException(string.Format("源块与目标块相同（{0}），无需替换。", sourceName));
                }
            }
            else
            {
                ctx.Log.Warn(string.Format(
                    "块表中没有名为「{0}」的块定义（可能是动态块名或拼写不同），将继续按块参照的有效块名匹配。",
                    sourceName));
            }

            bool allowDynamic = RuleContext.Bool(ctx.Action.AllowDynamicBlock, false);
            bool migrateAttributes = RuleContext.Bool(ctx.Action.MigrateAttributes, true);

            ctx.Log.Detail(string.Format("源块：{0} → 目标块：{1}；允许动态块：{2}；属性迁移：{3}",
                sourceName, targetName, allowDynamic ? "是" : "否", migrateAttributes ? "是" : "否"));

            EntityFilter filter = EntityFilter.Build(ctx.Filter, sourceName);
            ctx.Log.Detail("过滤条件：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("扫描到 {0} 个候选对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                var src = hit.Entity as Insert;
                if (src == null || src is TableEntity)
                {
                    continue;
                }

                // 双保险：过滤层已按有效块名筛过，这里再确认一次
                if (!string.Equals(EntityFilter.EffectiveBlockName(src.Block), sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Hit();

                try
                {
                    ReplaceOne(ctx, result, hit, src, target, targetName, allowDynamic, migrateAttributes);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 替换失败：{1}", EntityDescribe.Describe(hit), ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        private static void ReplaceOne(
            RuleContext ctx,
            RuleExecutionResult result,
            ScanHit hit,
            Insert src,
            BlockRecord target,
            string targetName,
            bool allowDynamic,
            bool migrateAttributes)
        {
            string srcDesc = EntityDescribe.Describe(hit);

            // 已经是目标块：跳过，避免自替换
            if (ReferenceEquals(src.Block, target))
            {
                result.MarkSkipped(string.Format("{0} 已是目标块，跳过。", srcDesc));
                return;
            }

            BlockRecord srcDef = src.Block;
            bool isDynamic = srcDef != null && srcDef.IsAnonymous && srcDef.Source != null;

            // ---- 动态块边界 ----
            if (isDynamic && !allowDynamic)
            {
                string msg = string.Format(
                    "{0} 是动态块（定义：{1}），AllowDynamicBlock=false，已跳过。",
                    srcDesc, EntityFilter.EffectiveBlockName(srcDef));
                result.MarkSkipped(msg);
                ctx.Log.Warn(msg);
                return;
            }

            BlockRecord owner = hit.Owner;
            if (owner == null)
            {
                result.MarkFailed(string.Format("{0} 无法确定所属容器，跳过。", srcDesc));
                return;
            }

            if (EntityScanner.ShouldSkipLocked(ctx, src))
            {
                string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", srcDesc, LayerService.LayerNameOf(src));
                result.MarkSkipped(msg);
                ctx.Log.Warn(msg);
                return;
            }

            // 先把需要迁移的数据抓下来
            Dictionary<string, string> attributeValues = migrateAttributes ? CollectAttributeValues(src) : null;

            var newRef = new Insert(target)
            {
                InsertPoint = src.InsertPoint,
                Rotation = src.Rotation,
                XScale = src.XScale,
                YScale = src.YScale,
                ZScale = src.ZScale,
                Normal = src.Normal,
                Layer = src.Layer,
                Color = src.Color,
                LineType = src.LineType,
                LineTypeScale = src.LineTypeScale,
                LineWeight = src.LineWeight,
                IsInvisible = src.IsInvisible,
                Transparency = src.Transparency,
                RowCount = src.RowCount,
                ColumnCount = src.ColumnCount,
                RowSpacing = src.RowSpacing,
                ColumnSpacing = src.ColumnSpacing
            };

            // 让库按目标块的 ATTDEF 建出 ATTRIB，再按 Tag 赋值
            int attMigrated = 0;
            int attMissing = 0;
            if (migrateAttributes)
            {
                MigrateAttributes(ctx, result, newRef, attributeValues, out attMigrated, out attMissing);
            }

            // ---- 替换：从原容器里摘掉旧参照、放入新参照 ----
            owner.Entities.Remove(src);
            owner.Entities.Add(newRef);

            if (isDynamic && allowDynamic)
            {
                ctx.Log.Warn(string.Format(
                    "{0}：动态块已替换，但动态参数值无法迁移（库未建模 DynamicBlockReferenceProperty），" +
                    "替换后需在 CAD 里重新设参数。", srcDesc));
            }

            string detail = string.Format(
                "{0} → {1}（插入点 {2}，旋转 {3:0.###}°，缩放 {4}/{5}/{6}）{7}{8}",
                srcDesc,
                targetName,
                string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", src.InsertPoint.X, src.InsertPoint.Y),
                src.Rotation * 180.0 / Math.PI,
                src.XScale.ToString("0.###", CultureInfo.InvariantCulture),
                src.YScale.ToString("0.###", CultureInfo.InvariantCulture),
                src.ZScale.ToString("0.###", CultureInfo.InvariantCulture),
                hit.IsNested ? string.Format("，嵌套于「{0}」深度 {1}", hit.OwnerName, hit.Depth) : string.Empty,
                attMigrated > 0 ? string.Format("，迁移属性 {0} 个", attMigrated) : string.Empty);

            result.MarkChanged(detail);
            ctx.Log.Detail(detail);
        }

        /// <summary>抓取源块参照上的属性值（Tag → 文本）。</summary>
        private static Dictionary<string, string> CollectAttributeValues(Insert ins)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (AttributeEntity att in ins.Attributes)
                {
                    if (att == null || string.IsNullOrEmpty(att.Tag))
                    {
                        continue;
                    }

                    if (!dict.ContainsKey(att.Tag))
                    {
                        dict[att.Tag] = att.Value;
                    }
                }
            }
            catch
            {
                // 属性集合读取失败不影响替换主流程
            }

            return dict;
        }

        /// <summary>
        /// 按 Tag 名称把属性值迁移到新块参照。
        ///
        /// 关键点：<c>UpdateAttributes()</c> 会按目标块定义的 ATTDEF 建出属性实例
        /// （AutoCAD 不会因为块定义带属性就自动创建 AttributeEntity，手工 new 会出现同 Tag 两条）。
        /// 名称不匹配的属性只记笔记，不迁移。
        /// </summary>
        private static void MigrateAttributes(
            RuleContext ctx,
            RuleExecutionResult result,
            Insert newRef,
            Dictionary<string, string> sourceValues,
            out int migrated,
            out int missing)
        {
            migrated = 0;
            missing = 0;

            try
            {
                newRef.UpdateAttributes();
            }
            catch (Exception ex)
            {
                ctx.Log.Warn("按目标块定义生成属性失败：" + ex.Message);
                return;
            }

            if (!newRef.HasAttributes)
            {
                return;
            }

            foreach (AttributeEntity att in newRef.Attributes)
            {
                if (att == null || string.IsNullOrEmpty(att.Tag))
                {
                    continue;
                }

                string value;
                if (sourceValues != null && sourceValues.TryGetValue(att.Tag, out value))
                {
                    att.Value = value;
                    migrated++;
                }
                else
                {
                    missing++;
                    string msg = string.Format(
                        "属性 Tag「{0}」在源块中不存在，未迁移（保留目标块默认值「{1}」）。",
                        att.Tag, att.Value);
                    result.AddNote(msg);
                    ctx.Log.Detail(msg);
                }
            }
        }
    }
}
