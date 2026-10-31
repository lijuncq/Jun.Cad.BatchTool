using System;
using System.Collections.Generic;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 扩展规则：批量清理未使用对象（块定义、图层、线型、文字样式、标注样式、注册应用、组、多重引线样式）。
    ///
    /// 实现要点：
    ///   * 用 Database.Purge(ObjectIdCollection) 判定"可清理"，它会剔除仍被引用的对象；
    ///   * Purge 要求候选对象处于【关闭】状态，所以候选收集放在一个独立的短事务里完成，
    ///     收集完立刻提交/关闭，再调用 Purge，最后在环境事务里 Erase；
    ///   * 支持多轮迭代（PurgePasses），处理"块定义被清掉后其独占图层才变空"这类链式依赖。
    ///
    /// 事务边界说明：本规则会开启自己的短事务用于"只读收集"，这与 Sequential 模式下的
    /// 环境事务并存是安全的（AutoCAD 允许事务嵌套，且短事务内不修改数据库）。
    /// </summary>
    public sealed class PurgeUnusedRule : IRuleHandler
    {
        private static readonly string[] AllCategories =
        {
            "Block", "Layer", "Linetype", "TextStyle", "DimStyle", "RegApp", "Group", "MLeaderStyle"
        };

        public string RuleType
        {
            get { return "PurgeUnused"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            var db = ctx.Database;
            var tr = ctx.Transaction;

            HashSet<string> categories = ResolveCategories(ctx.Action.PurgeCategories);
            int passes = RuleExecutionContext.Int(ctx.Action.PurgePasses, 3);
            if (passes < 1)
            {
                passes = 1;
            }

            ctx.Log.Detail("清理类别：" + string.Join(", ", categories));
            ctx.Log.Detail("迭代轮次上限：" + passes);

            var totalByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int grandTotal = 0;

            for (int pass = 1; pass <= passes; pass++)
            {
                List<Candidate> candidates = CollectCandidates(db, categories);

                if (candidates.Count == 0)
                {
                    ctx.Log.Detail(string.Format("第 {0} 轮：没有候选对象，结束。", pass));
                    break;
                }

                var ids = new ObjectIdCollection();
                foreach (Candidate c in candidates)
                {
                    ids.Add(c.Id);
                }

                // 判定哪些真的可以清理（Purge 会就地剔除仍被引用的对象）
                try
                {
                    db.Purge(ids);
                }
                catch (Exception ex)
                {
                    throw new RuleValidationException("调用 PURGE 失败：" + ex.Message, ex);
                }

                if (ids.Count == 0)
                {
                    ctx.Log.Detail(string.Format("第 {0} 轮：{1} 个候选全部仍被引用，结束。", pass, candidates.Count));
                    break;
                }

                var purgedSet = new HashSet<ObjectId>();
                foreach (ObjectId id in ids)
                {
                    purgedSet.Add(id);
                }

                int purgedThisPass = 0;
                foreach (Candidate c in candidates)
                {
                    if (!purgedSet.Contains(c.Id))
                    {
                        continue;
                    }

                    result.Hit();
                    try
                    {
                        var obj = tr.GetObject(c.Id, OpenMode.ForWrite, false);
                        if (obj == null)
                        {
                            continue;
                        }

                        obj.Erase();
                        purgedThisPass++;

                        int n;
                        totalByCategory.TryGetValue(c.Category, out n);
                        totalByCategory[c.Category] = n + 1;

                        ctx.Log.Detail(string.Format("已清理 {0}「{1}」(句柄:{2})", c.Category, c.Name, ObjectUtil.HandleOf(c.Id)));
                    }
                    catch (Exception ex)
                    {
                        string msg = string.Format("清理 {0}「{1}」(句柄:{2}) 失败：{3}", c.Category, c.Name, ObjectUtil.HandleOf(c.Id), ex.Message);
                        result.MarkFailed(msg);
                        ctx.Log.Error(msg);
                    }
                }

                grandTotal += purgedThisPass;
                result.MarkChanged(string.Format("第 {0} 轮清理 {1} 个未使用对象", pass, purgedThisPass));

                if (purgedThisPass == 0)
                {
                    break;
                }
            }

            if (grandTotal == 0)
            {
                result.AddNote("没有可清理的未使用对象（图纸本身很干净，或全部仍被引用）。");
            }
            else
            {
                var parts = totalByCategory
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => string.Format("{0}={1}", kv.Key, kv.Value));
                result.AddNote("清理统计：" + string.Join("，", parts));
                ctx.Log.Info("清理统计：" + string.Join("，", parts));
            }
        }

        private static HashSet<string> ResolveCategories(List<string> configured)
        {
            if (configured == null || configured.Count == 0)
            {
                return new HashSet<string>(AllCategories, StringComparer.OrdinalIgnoreCase);
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in configured.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                string key = c.Trim();
                // 容错：允许写 Blocks/Layers/Textstyles 之类的复数形式
                if (key.EndsWith("s", StringComparison.OrdinalIgnoreCase) && key.Length > 1)
                {
                    key = key.Substring(0, key.Length - 1);
                }

                if (AllCategories.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    set.Add(AllCategories.First(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return set.Count == 0 ? new HashSet<string>(AllCategories, StringComparer.OrdinalIgnoreCase) : set;
        }

        private sealed class Candidate
        {
            public ObjectId Id;
            public string Name;
            public string Category;
        }

        /// <summary>
        /// 在独立短事务中收集候选对象，保证交给 Purge 的对象处于关闭状态。
        /// </summary>
        private static List<Candidate> CollectCandidates(Database db, HashSet<string> categories)
        {
            var list = new List<Candidate>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (categories.Contains("Block"))
                {
                    CollectBlocks(tr, db, list);
                }

                if (categories.Contains("Layer"))
                {
                    CollectLayers(tr, db, list);
                }

                if (categories.Contains("Linetype"))
                {
                    CollectLinetypes(tr, db, list);
                }

                if (categories.Contains("TextStyle"))
                {
                    CollectTextStyles(tr, db, list);
                }

                if (categories.Contains("DimStyle"))
                {
                    CollectDimStyles(tr, db, list);
                }

                if (categories.Contains("RegApp"))
                {
                    CollectRegApps(tr, db, list);
                }

                if (categories.Contains("Group"))
                {
                    CollectDictionaryEntries(tr, db, list, "Group", GetGroupDictionaryId(db));
                }

                if (categories.Contains("MLeaderStyle"))
                {
                    CollectDictionaryEntries(tr, db, list, "MLeaderStyle", GetMLeaderStyleDictionaryId(db));
                }

                tr.Commit();
            }

            return list;
        }

        private static ObjectId GetGroupDictionaryId(Database db)
        {
            try
            {
                return db.GroupDictionaryId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static ObjectId GetMLeaderStyleDictionaryId(Database db)
        {
            try
            {
                return db.MLeaderStyleDictionaryId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static void CollectBlocks(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var btr = tr.GetObject(id, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr == null)
                {
                    continue;
                }

                // 布局块（模型/图纸空间）、匿名块（*U/*D 等）、外参块一律不清理
                if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsFromOverlayReference)
                {
                    continue;
                }

                // 块定义里含属性的（ATTDEF）也可以清理，Purge 会判断是否被引用
                list.Add(new Candidate { Id = id, Name = btr.Name, Category = "块定义" });
            }
        }

        private static void CollectLayers(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var ltr = tr.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (ltr == null)
                {
                    continue;
                }

                if (id == db.Clayer)
                {
                    continue;
                }

                if (string.Equals(ltr.Name, "0", StringComparison.Ordinal)
                    || string.Equals(ltr.Name, "Defpoints", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new Candidate { Id = id, Name = ltr.Name, Category = "图层" });
            }
        }

        private static void CollectLinetypes(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.LinetypeTableId, OpenMode.ForRead, false) as LinetypeTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var ltr = tr.GetObject(id, OpenMode.ForRead, false) as LinetypeTableRecord;
                if (ltr == null)
                {
                    continue;
                }

                if (string.Equals(ltr.Name, "ByLayer", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ltr.Name, "ByBlock", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ltr.Name, "Continuous", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (id == db.Celtype)
                {
                    continue;
                }

                list.Add(new Candidate { Id = id, Name = ltr.Name, Category = "线型" });
            }
        }

        private static void CollectTextStyles(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead, false) as TextStyleTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var str = tr.GetObject(id, OpenMode.ForRead, false) as TextStyleTableRecord;
                if (str == null)
                {
                    continue;
                }

                if (string.Equals(str.Name, "Standard", StringComparison.OrdinalIgnoreCase) || id == db.Textstyle)
                {
                    continue;
                }

                list.Add(new Candidate { Id = id, Name = str.Name, Category = "文字样式" });
            }
        }

        private static void CollectDimStyles(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.DimStyleTableId, OpenMode.ForRead, false) as DimStyleTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var dsr = tr.GetObject(id, OpenMode.ForRead, false) as DimStyleTableRecord;
                if (dsr == null)
                {
                    continue;
                }

                if (string.Equals(dsr.Name, "Standard", StringComparison.OrdinalIgnoreCase) || id == db.Dimstyle)
                {
                    continue;
                }

                list.Add(new Candidate { Id = id, Name = dsr.Name, Category = "标注样式" });
            }
        }

        private static void CollectRegApps(Transaction tr, Database db, List<Candidate> list)
        {
            var table = tr.GetObject(db.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table == null)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                var rec = tr.GetObject(id, OpenMode.ForRead, false) as RegAppTableRecord;
                if (rec == null)
                {
                    continue;
                }

                if (string.Equals(rec.Name, "ACAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new Candidate { Id = id, Name = rec.Name, Category = "注册应用" });
            }
        }

        private static void CollectDictionaryEntries(Transaction tr, Database db, List<Candidate> list, string category, ObjectId dictionaryId)
        {
            if (dictionaryId.IsNull)
            {
                return;
            }

            DBDictionary dict;
            try
            {
                dict = tr.GetObject(dictionaryId, OpenMode.ForRead, false) as DBDictionary;
            }
            catch
            {
                return;
            }

            if (dict == null)
            {
                return;
            }

            foreach (DBDictionaryEntry entry in dict)
            {
                list.Add(new Candidate { Id = entry.Value, Name = entry.Key, Category = category });
            }
        }
    }
}
