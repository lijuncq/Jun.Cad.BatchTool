using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.Geometry;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 规则 1：批量替换块参照。
    ///
    /// 处理链路：
    ///   1. 用 DXF 组码 2（块名）筛出候选 INSERT；
    ///   2. 逐个确认有效块名（动态块用动态块定义名比较）；
    ///   3. 新建目标块参照 → CopyFrom 复制通用属性 → 显式重设插入点/旋转/缩放/法向；
    ///   4. 追加到原所有者 BlockTableRecord（模型空间、布局、或块定义内部），
    ///      再迁移属性值、动态参数；
    ///   5. Erase 旧参照。
    ///
    /// 嵌套块：由 DatabaseWalker 递归进入块定义，因此"块中块"里的引用同样会被替换；
    ///          替换发生在块定义内部时，所有引用该定义的插入会同步生效（这正是批量治理要的效果）。
    ///
    /// 动态块边界：AllowDynamicBlock=false（默认）时，遇到动态块直接跳过并记录警告——
    ///          因为动态块替换涉及匿名块 *U 实例与参数约束，跨定义替换容易造成参数丢失；
    ///          设为 true 时按参数名逐个迁移动态参数值（见 MigrateDynamicProperties）。
    /// </summary>
    public sealed class BlockReplaceRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "BlockReplace"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
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

            var db = ctx.Database;
            var tr = ctx.Transaction;

            var blockTable = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (blockTable == null)
            {
                throw new RuleValidationException("无法打开块表。");
            }

            if (!blockTable.Has(targetName))
            {
                throw new RuleValidationException(string.Format(
                    "目标块「{0}」在当前图纸中不存在，规则中止。请先插入或定义该块（可用 PURGE 之外的方式先引入）。", targetName));
            }

            if (blockTable.Has(sourceName))
            {
                ObjectId srcDefId = blockTable[sourceName];
                if (srcDefId == blockTable[targetName])
                {
                    throw new RuleValidationException(string.Format("源块与目标块相同（{0}），无需替换。", sourceName));
                }
            }
            else
            {
                ctx.Log.Warn(string.Format("块表中没有名为「{0}」的块定义（可能是动态块名或拼写不同），将继续按块参照的有效块名匹配。", sourceName));
            }

            ObjectId targetBtrId = blockTable[targetName];
            bool allowDynamic = RuleExecutionContext.Bool(ctx.Action.AllowDynamicBlock, false);
            bool migrateAttributes = RuleExecutionContext.Bool(ctx.Action.MigrateAttributes, true);

            ctx.Log.Detail(string.Format("源块：{0} → 目标块：{1}；允许动态块：{2}；属性迁移：{3}",
                sourceName, targetName, allowDynamic ? "是" : "否", migrateAttributes ? "是" : "否"));

            var filter = DxfFilter.Build(ctx.Filter, sourceName, tr);
            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("扫描到 {0} 个候选对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                BlockReference src = null;
                try
                {
                    src = tr.GetObject(hit.Id, OpenMode.ForRead, false) as BlockReference;
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("{0} 打开失败：{1}", ObjectUtil.HandleOf(hit.Id), ex.Message));
                    continue;
                }

                if (src == null || src.IsErased)
                {
                    continue;
                }

                // 双保险：DXF 组码 2 只认块名，动态块的匿名名会漏，这里按有效块名再判一次
                if (!BlockNameUtil.Matches(src, sourceName, tr))
                {
                    continue;
                }

                result.Hit();

                try
                {
                    ReplaceOne(ctx, result, hit, src, targetBtrId, targetName, allowDynamic, migrateAttributes);
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("{0} 替换失败：{1}", ObjectUtil.Describe(src), ex.Message));
                    ctx.Log.Error(string.Format("{0} 替换失败：{1}", ObjectUtil.Describe(src), ex.Message));
                }
            }
        }

        private void ReplaceOne(
            RuleExecutionContext ctx,
            RuleExecutionResult result,
            ScanHit hit,
            BlockReference src,
            ObjectId targetBtrId,
            string targetName,
            bool allowDynamic,
            bool migrateAttributes)
        {
            var tr = ctx.Transaction;
            string srcDesc = ObjectUtil.Describe(src);

            // 已经是目标块：跳过，避免自替换
            if (src.BlockTableRecord == targetBtrId)
            {
                result.MarkSkipped(string.Format("{0} 已是目标块，跳过。", srcDesc));
                return;
            }

            bool isDynamic = src.IsDynamicBlock;

            // ---- 动态块边界 ----
            if (isDynamic && !allowDynamic)
            {
                string msg = string.Format(
                    "{0} 是动态块（定义：{1}），AllowDynamicBlock=false，已跳过。",
                    srcDesc, BlockNameUtil.GetEffectiveName(src, tr));
                result.MarkSkipped(msg);
                ctx.Log.Warn(msg);
                return;
            }

            var owner = tr.GetObject(src.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
            if (owner == null)
            {
                result.MarkFailed(string.Format("{0} 无法打开所属块表记录，跳过。", srcDesc));
                return;
            }

            // 先把需要迁移的数据抓下来——一旦 Erase 旧参照，属性/参数都读不到了
            Dictionary<string, string> attributeValues = migrateAttributes
                ? CollectAttributeValues(tr, src)
                : null;

            List<KeyValuePair<string, object>> dynamicValues = isDynamic
                ? CollectDynamicValues(tr, src)
                : null;

            Point3d position = src.Position;
            double rotation = src.Rotation;
            Scale3d scale = src.ScaleFactors;
            Vector3d normal = src.Normal;

            // ---- 创建新参照：构造函数即带插入点与目标块定义 ----
            var newRef = new BlockReference(position, targetBtrId);

            // CopyFrom 复制"通用属性"：图层、颜色、线型、线宽、线型比例、可见性、透明度、材质等
            newRef.CopyFrom(src);

            // 防御性重设：不同版本的 CopyFrom 覆盖范围略有差异，
            // 这里显式恢复块定义指针与变换属性，确保"插入点/旋转/缩放"严格保留
            newRef.BlockTableRecord = targetBtrId;
            newRef.Position = position;
            newRef.Rotation = rotation;
            newRef.ScaleFactors = scale;
            newRef.Normal = normal;

            // ---- 事务边界：新增对象必须显式登记，否则图形数据库不会保存 ----
            owner.AppendEntity(newRef);
            tr.AddNewlyCreatedDBObject(newRef, true);

            // ---- 属性迁移（必须在 Append 之后：BlockTransform 依赖已插入的位置） ----
            int attMigrated = 0;
            int attMissing = 0;
            if (migrateAttributes)
            {
                MigrateAttributes(ctx, result, newRef, targetBtrId, attributeValues, out attMigrated, out attMissing);
            }

            // ---- 动态参数迁移（仅在允许动态块时） ----
            int dynMigrated = 0;
            if (isDynamic && allowDynamic && dynamicValues != null)
            {
                dynMigrated = MigrateDynamicProperties(ctx, result, newRef, dynamicValues);
            }

            // ---- 删除旧参照（同一事务内，旧对象 Erase 后仍可用句柄追溯） ----
            src.UpgradeOpen();
            src.Erase();

            string detail = string.Format(
                "{0} → {1}（插入点 {2}，旋转 {3:0.###}°，缩放 {4}）{5}{6}{7}",
                srcDesc,
                targetName,
                position.ToString(),
                rotation * 180.0 / Math.PI,
                scale.ToString(),
                hit.IsNested ? string.Format("，嵌套于「{0}」深度 {1}", hit.OwnerName, hit.Depth) : string.Empty,
                attMigrated > 0 ? string.Format("，迁移属性 {0} 个", attMigrated) : string.Empty,
                dynMigrated > 0 ? string.Format("，迁移动态参数 {0} 个", dynMigrated) : string.Empty);

            result.MarkChanged(detail);
            ctx.Log.Detail(detail);
        }

        /// <summary>抓取源块参照上的属性值（Tag → 文本）。</summary>
        private static Dictionary<string, string> CollectAttributeValues(Transaction tr, BlockReference br)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    var att = tr.GetObject(attId, OpenMode.ForRead, false) as AttributeReference;
                    if (att == null)
                    {
                        continue;
                    }

                    if (!dict.ContainsKey(att.Tag))
                    {
                        dict[att.Tag] = att.TextString;
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
        /// 新块参照的属性需要"从目标块定义的 AttributeDefinition 生成"，
        /// AutoCAD 不会因为块定义带属性就自动创建 AttributeReference。
        /// 名称不匹配的属性只记日志，不迁移。
        /// </summary>
        private static void MigrateAttributes(
            RuleExecutionContext ctx,
            RuleExecutionResult result,
            BlockReference newRef,
            ObjectId targetBtrId,
            Dictionary<string, string> sourceValues,
            out int migrated,
            out int missing)
        {
            migrated = 0;
            missing = 0;

            var tr = ctx.Transaction;
            BlockTableRecord targetBtr;
            try
            {
                targetBtr = tr.GetObject(targetBtrId, OpenMode.ForRead, false) as BlockTableRecord;
            }
            catch
            {
                return;
            }

            if (targetBtr == null)
            {
                return;
            }

            var defIds = new List<ObjectId>();
            foreach (ObjectId id in targetBtr)
            {
                defIds.Add(id);
            }

            foreach (ObjectId id in defIds)
            {
                AttributeDefinition attDef;
                try
                {
                    attDef = tr.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                }
                catch
                {
                    continue;
                }

                // 常量属性由块定义固定，不需要实例属性
                if (attDef == null || attDef.Constant)
                {
                    continue;
                }

                var newAtt = new AttributeReference();
                newAtt.SetAttributeFromBlock(attDef, newRef.BlockTransform);

                string value;
                if (sourceValues != null && sourceValues.TryGetValue(attDef.Tag, out value))
                {
                    newAtt.TextString = value;
                    migrated++;
                }
                else
                {
                    missing++;
                    string msg = string.Format(
                        "属性 Tag「{0}」在源块中不存在，未迁移（保留目标块默认值「{1}」）。",
                        attDef.Tag, attDef.TextString);
                    result.AddNote(msg);
                    ctx.Log.Detail(msg);
                }

                newRef.AttributeCollection.AppendAttribute(newAtt);
                tr.AddNewlyCreatedDBObject(newAtt, true);
            }
        }

        /// <summary>抓取动态块参数值（参数名 → 值）。</summary>
        private static List<KeyValuePair<string, object>> CollectDynamicValues(Transaction tr, BlockReference br)
        {
            var list = new List<KeyValuePair<string, object>>();

            try
            {
                var props = br.DynamicBlockReferencePropertyCollection;
                foreach (DynamicBlockReferenceProperty prop in props)
                {
                    if (prop == null || string.IsNullOrEmpty(prop.PropertyName))
                    {
                        continue;
                    }

                    list.Add(new KeyValuePair<string, object>(prop.PropertyName, prop.Value));
                }
            }
            catch
            {
                // 读取失败时返回空列表，替换照常进行
            }

            return list;
        }

        /// <summary>
        /// 按参数名迁移动态块参数值。
        /// 只迁移"目标块也存在、且可写"的参数；只读参数（如某些查寻参数）跳过并记日志。
        /// 值类型沿用源参数的原生类型（short/double/string），避免强制转换导致约束失效。
        /// </summary>
        private static int MigrateDynamicProperties(
            RuleExecutionContext ctx,
            RuleExecutionResult result,
            BlockReference newRef,
            List<KeyValuePair<string, object>> values)
        {
            int migrated = 0;
            if (values == null || values.Count == 0)
            {
                return 0;
            }

            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                lookup[pair.Key] = pair.Value;
            }

            try
            {
                var targetProps = newRef.DynamicBlockReferencePropertyCollection;
                foreach (DynamicBlockReferenceProperty prop in targetProps)
                {
                    if (prop == null || string.IsNullOrEmpty(prop.PropertyName))
                    {
                        continue;
                    }

                    object value;
                    if (!lookup.TryGetValue(prop.PropertyName, out value))
                    {
                        continue;
                    }

                    if (prop.ReadOnly)
                    {
                        result.AddNote(string.Format("动态参数「{0}」为只读，未迁移。", prop.PropertyName));
                        continue;
                    }

                    try
                    {
                        prop.Value = value;
                        migrated++;
                    }
                    catch (Exception ex)
                    {
                        string msg = string.Format("动态参数「{0}」迁移失败：{1}", prop.PropertyName, ex.Message);
                        result.AddNote(msg);
                        ctx.Log.Warn(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                ctx.Log.Warn("读取目标块动态参数失败：" + ex.Message);
            }

            return migrated;
        }
    }
}
