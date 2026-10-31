using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 几何清理：零长度直线、空文字、重复实体。
    ///
    /// 这是全项目**最危险**的一条规则（删东西不可逆），所以默认值全在安全侧：
    ///   * `CleanupDryRun = true`：默认只报告"会删哪些"，不真删；
    ///   * 重复实体默认 **不清理**（`CleanDuplicateEntities=false`）——同层同类型同包围盒
    ///     也可能是"有意叠加"（比如两遍线宽）；
    ///   * 只处理**顶层空间**（模型/布局）。块定义里的实体删掉会影响所有实例，
    ///     不属于"清理这张图"的语义。
    ///
    /// 重复判定用的是"同类型 + 同图层 + 包围盒一致（容差内）"。
    /// 这是近似：两条端点互换的 LINE 会被判成重复（正确），
    /// 但两条长度相同、位置差一个容差的线也会被当成重复——所以容差要按图纸精度给。
    /// </summary>
    public sealed class GeometryCleanupRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "GeometryCleanup"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            bool cleanZero = RuleContext.Bool(action.CleanZeroLength, true);
            bool cleanEmptyText = RuleContext.Bool(action.CleanEmptyText, true);
            bool cleanDuplicates = RuleContext.Bool(action.CleanDuplicateEntities, false);
            bool dryRun = RuleContext.Bool(action.CleanupDryRun, true);
            double zeroTolerance = RuleContext.Double(action.ZeroLengthTolerance, 0.001);
            double dupTolerance = RuleContext.Double(action.DuplicateTolerance, 0.0001);

            if (!cleanZero && !cleanEmptyText && !cleanDuplicates)
            {
                throw new RuleValidationException("三个清理开关都关着，这条规则不会做任何事。");
            }

            var deletions = new List<KeyValuePair<BlockRecord, Entity>>();
            var reasons = new Dictionary<Entity, string>();

            foreach (BlockRecord space in DatabaseWalker.GetSpaces(context.Document, EntityScanner.ResolveScope(context)))
            {
                // 快照：边遍历边删会抛"集合已修改"
                var entities = new List<Entity>(space.Entities);
                var seen = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);

                foreach (Entity entity in entities)
                {
                    if (entity == null)
                    {
                        continue;
                    }

                    // 先判断"符不符合清理条件"，符合才计数。
                    // 不能对每个实体都 Hit()：那样一张干净的图会得到"匹配 11419、改动 0、跳过 0"，
                    // 匹配数变成一个和规则无关的扫描量（引擎的计数自洽性检查会直接报出来）。
                    string reason = null;

                    if (cleanZero)
                    {
                        var line = entity as Line;
                        if (line != null)
                        {
                            double dx = line.EndPoint.X - line.StartPoint.X;
                            double dy = line.EndPoint.Y - line.StartPoint.Y;
                            double dz = line.EndPoint.Z - line.StartPoint.Z;

                            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= zeroTolerance)
                            {
                                reason = "零长度线";
                            }
                        }
                    }

                    if (reason == null && cleanEmptyText && IsEmptyText(entity))
                    {
                        reason = "空文字";
                    }

                    if (reason == null && cleanDuplicates)
                    {
                        string key = DuplicateKey(entity, dupTolerance);
                        if (key != null)
                        {
                            Entity first;
                            if (seen.TryGetValue(key, out first))
                            {
                                reason = "重复（首件 #" + first.Handle.ToString("X") + "）";
                            }
                            else
                            {
                                seen[key] = entity;
                            }
                        }
                    }

                    if (reason == null)
                    {
                        continue;
                    }

                    result.Hit();

                    if (EntityScanner.ShouldSkipLocked(context, entity))
                    {
                        result.MarkSkipped("锁定图层");
                        continue;
                    }

                    string layer = entity.Layer == null ? string.Empty : entity.Layer.Name;
                    if (string.Equals(layer, "Defpoints", StringComparison.OrdinalIgnoreCase))
                    {
                        result.MarkSkipped("Defpoints 层");
                        continue;
                    }

                    // 只登记，不在这里计跳过：真删时由删除阶段计"改动"，预演时由预演分支计"跳过"。
                    // 两处都计会让每个对象被数两遍（匹配 4 / 改动 4 / 跳过 4）。
                    deletions.Add(new KeyValuePair<BlockRecord, Entity>(space, entity));
                    reasons[entity] = reason;
                }
            }

            if (deletions.Count == 0)
            {
                result.AddNote("没有需要清理的对象。");
                return;
            }

            if (dryRun)
            {
                foreach (KeyValuePair<BlockRecord, Entity> pair in deletions)
                {
                    result.AddNote(string.Format("[预演] 将删除 #{0} {1}：{2}",
                        pair.Value.Handle.ToString("X"),
                        EntityFilter.DxfNameOf(pair.Value),
                        reasons[pair.Value]));

                    result.MarkSkipped("预演");
                    context.RecordSkip(pair.Value, "清理（预演）", reasons[pair.Value]);
                }

                result.AddNote(string.Format(
                    "预演：识别出 {0} 个可清理对象，未删除任何东西。确认无误后把 Action.CleanupDryRun 设为 false。",
                    deletions.Count));
                return;
            }

            int removed = 0;
            foreach (KeyValuePair<BlockRecord, Entity> pair in deletions)
            {
                try
                {
                    // Remove 的返回值类型各版本不一致（bool / 被删对象），只认"删完不在了"
                    pair.Key.Entities.Remove(pair.Value);
                    removed++;

                    // 真删了就必须计"改动"：否则 --skip-unchanged 会把这次删除当成"什么都没变"而丢弃，
                    // 汇总表也会显示"成功 0"，违反 匹配 = 成功 + 跳过 + 失败
                    result.MarkChanged("删除 " + reasons[pair.Value]);
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("删除 #{0} 失败：{1}", pair.Value.Handle.ToString("X"), ex.Message));
                }
            }

            result.AddNote(string.Format("已删除 {0} 个对象（零长度线 / 空文字 / 重复实体）。", removed));
        }

        private static bool IsEmptyText(Entity entity)
        {
            var mtext = entity as MText;
            if (mtext != null)
            {
                return string.IsNullOrWhiteSpace(MTextTokenizer.ToPlainText(mtext.Value));
            }

            var text = entity as TextEntity;
            if (text != null && !(text is AttributeBase))
            {
                return string.IsNullOrWhiteSpace(text.Value);
            }

            return false;
        }

        /// <summary>重复判定键：类型 + 图层 + 包围盒（容差内取整）。</summary>
        private static string DuplicateKey(Entity entity, double tolerance)
        {
            Bounds2d bounds;
            if (!TextBounds.TryGet(entity, out bounds))
            {
                return null;
            }

            double t = tolerance <= 0 ? 0.0001 : tolerance;
            long minX = (long)Math.Round(bounds.MinX / t);
            long minY = (long)Math.Round(bounds.MinY / t);
            long maxX = (long)Math.Round(bounds.MaxX / t);
            long maxY = (long)Math.Round(bounds.MaxY / t);

            return string.Format("{0}|{1}|{2}|{3}|{4}|{5}",
                EntityFilter.DxfNameOf(entity),
                entity.Layer == null ? string.Empty : entity.Layer.Name,
                minX, minY, maxX, maxY);
        }
    }
}
