using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp.Blocks;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Engine.Purge;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 规则：批量清理未使用对象（块定义、图层、线型、文字样式、标注样式）。
    ///
    /// 与 AutoCAD 版的**重要差别**：没有 <c>Database.Purge()</c> 这个内核 API，
    /// "是否还被引用"必须自己算（见 <see cref="ReferenceScanner"/>）。
    /// 因此本规则的设计原则是**宁可少删**：
    ///   * 只从"活的容器"出发（布局块 + 被引用到的块定义）；
    ///   * 遇到不认识的实体类型时，图层/线型/文字样式/标注样式这四类**整体放弃**本轮清理
    ///     （无法确定它们是否引用了这些表项），只保留块定义清理；
    ///   * 标准项与当前项（0 层、Defpoints、Continuous、Standard、Header 里的当前项）永不列入候选。
    ///
    /// 与 AutoCAD 版的功能降级：RegApp / Group / MLeaderStyle 三类**不支持**。
    ///   XData 注册应用与组内成员的引用关系在 ACadSharp 里没有可用的扫描入口，
    ///   硬猜会误删，所以显式跳过并告警。
    ///
    /// 支持多轮迭代（PurgePasses），处理"块定义被清掉后其独占图层才变空"这类链式依赖。
    /// </summary>
    public sealed class PurgeUnusedRule : IRuleHandler
    {
        private static readonly string[] AllCategories =
        {
            "Block", "Layer", "Linetype", "TextStyle", "DimStyle", "RegApp", "Group", "MLeaderStyle"
        };

        private static readonly string[] SupportedCategories =
        {
            "Block", "Layer", "Linetype", "TextStyle", "DimStyle"
        };

        public string RuleType
        {
            get { return "PurgeUnused"; }
        }

        /// <summary>
        /// 删的是**表项**（块定义 / 图层 / 线型 / 样式），实体快照完全看不见。
        /// 本规则自己上报，否则预览会把"删了 3 个块定义"说成"均已是目标状态"。
        /// </summary>
        public bool ReportsOwnChanges
        {
            get { return true; }
        }

        public void Execute(RuleContext ctx, RuleExecutionResult result)
        {
            HashSet<string> categories = ResolveCategories(ctx.Action.PurgeCategories);
            int passes = RuleContext.Int(ctx.Action.PurgePasses, 3);
            if (passes < 1)
            {
                passes = 1;
            }

            // 不支持的三类：显式告警，别让用户以为清干净了
            var unsupported = categories.Where(c => Array.IndexOf(SupportedCategories, c) < 0).ToList();
            if (unsupported.Count > 0)
            {
                ctx.Log.Warn(string.Format(
                    "以下清理类别在离线模式下不支持，已跳过：{0}。" +
                    "（XData 注册应用 / 组 / 多重引线样式的引用关系没有可用的扫描入口，硬猜会误删。）",
                    string.Join("、", unsupported.ToArray())));
            }

            var active = categories.Where(c => Array.IndexOf(SupportedCategories, c) >= 0).ToList();
            if (active.Count == 0)
            {
                throw new RuleValidationException(
                    "PurgeUnused 规则配置的类别在离线模式下全部不支持（RegApp / Group / MLeaderStyle），规则中止。");
            }

            ctx.Log.Detail("清理类别：" + string.Join(", ", active.ToArray()));
            ctx.Log.Detail("迭代轮次上限：" + passes);

            var totalByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int grandTotal = 0;

            for (int pass = 1; pass <= passes; pass++)
            {
                PurgeAnalysis analysis = ReferenceScanner.Analyze(ctx.Document);

                // 不认识的实体类型 → 保守处理：只清块定义
                bool restrictToBlocks = analysis.UnresolvedEntityTypes.Count > 0;
                if (restrictToBlocks && pass == 1)
                {
                    ctx.Log.Warn(string.Format(
                        "图纸里有本工具不认识的实体类型（{0}），无法确定它们是否引用了图层/线型/样式，" +
                        "本次只清理未使用的块定义。",
                        string.Join("、", analysis.UnresolvedEntityTypes.ToArray())));
                }

                int purgedThisPass = 0;

                if (active.Contains("Block"))
                {
                    purgedThisPass += PurgeBlocks(ctx, result, analysis, totalByCategory);
                }

                if (!restrictToBlocks)
                {
                    if (active.Contains("Layer"))
                    {
                        purgedThisPass += PurgeLayers(ctx, result, analysis, totalByCategory);
                    }

                    if (active.Contains("Linetype"))
                    {
                        purgedThisPass += PurgeLinetypes(ctx, result, analysis, totalByCategory);
                    }

                    if (active.Contains("TextStyle"))
                    {
                        purgedThisPass += PurgeTextStyles(ctx, result, analysis, totalByCategory);
                    }

                    if (active.Contains("DimStyle"))
                    {
                        purgedThisPass += PurgeDimStyles(ctx, result, analysis, totalByCategory);
                    }
                }

                grandTotal += purgedThisPass;
                ctx.Log.Detail(string.Format("第 {0} 轮清理 {1} 个未使用对象。", pass, purgedThisPass));

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

                string text = "清理统计：" + string.Join("，", parts.ToArray());
                result.AddNote(text);
                ctx.Log.Info(text);
            }
        }

        // ---------------------------------------------------------------- 各类别

        private static int PurgeBlocks(
            RuleContext ctx, RuleExecutionResult result, PurgeAnalysis analysis, Dictionary<string, int> totals)
        {
            var candidates = new List<string>();

            foreach (BlockRecord block in ctx.Document.BlockRecords)
            {
                if (block == null || analysis.LiveBlocks.Contains(block))
                {
                    continue;
                }

                // 布局块 / 匿名块（*U/*D/*T）/ 外参块一律不清理
                if (DatabaseWalker.IsLayoutBlock(block) || block.IsAnonymous)
                {
                    continue;
                }

                if (DatabaseWalker.IsExternalReference(block))
                {
                    continue;
                }

                if (((int)block.Flags & (int)BlockTypeFlags.XRefDependent) != 0)
                {
                    continue;
                }

                if (block.Name == null || block.Name.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                candidates.Add(block.Name);
            }

            return RemoveAll(ctx, result, candidates, "块定义", name => ctx.Document.BlockRecords.Remove(name), totals);
        }

        private static int PurgeLayers(
            RuleContext ctx, RuleExecutionResult result, PurgeAnalysis analysis, Dictionary<string, int> totals)
        {
            var candidates = new List<string>();

            foreach (Layer layer in ctx.Document.Layers)
            {
                if (layer == null || layer.Name == null)
                {
                    continue;
                }

                if (analysis.UsedLayers.Contains(layer.Name))
                {
                    continue;
                }

                candidates.Add(layer.Name);
            }

            return RemoveAll(ctx, result, candidates, "图层", name => ctx.Document.Layers.Remove(name), totals);
        }

        private static int PurgeLinetypes(
            RuleContext ctx, RuleExecutionResult result, PurgeAnalysis analysis, Dictionary<string, int> totals)
        {
            var candidates = new List<string>();

            foreach (LineType lt in ctx.Document.LineTypes)
            {
                if (lt == null || lt.Name == null)
                {
                    continue;
                }

                if (analysis.UsedLinetypes.Contains(lt.Name))
                {
                    continue;
                }

                candidates.Add(lt.Name);
            }

            return RemoveAll(ctx, result, candidates, "线型", name => ctx.Document.LineTypes.Remove(name), totals);
        }

        private static int PurgeTextStyles(
            RuleContext ctx, RuleExecutionResult result, PurgeAnalysis analysis, Dictionary<string, int> totals)
        {
            var candidates = new List<string>();

            foreach (TextStyle style in ctx.Document.TextStyles)
            {
                if (style == null || style.Name == null)
                {
                    continue;
                }

                if (analysis.UsedTextStyles.Contains(style.Name))
                {
                    continue;
                }

                candidates.Add(style.Name);
            }

            return RemoveAll(ctx, result, candidates, "文字样式", name => ctx.Document.TextStyles.Remove(name), totals);
        }

        private static int PurgeDimStyles(
            RuleContext ctx, RuleExecutionResult result, PurgeAnalysis analysis, Dictionary<string, int> totals)
        {
            var candidates = new List<string>();

            foreach (DimensionStyle style in ctx.Document.DimensionStyles)
            {
                if (style == null || style.Name == null)
                {
                    continue;
                }

                if (analysis.UsedDimStyles.Contains(style.Name))
                {
                    continue;
                }

                candidates.Add(style.Name);
            }

            return RemoveAll(ctx, result, candidates, "标注样式", name => ctx.Document.DimensionStyles.Remove(name), totals);
        }

        /// <summary>
        /// 逐个删除并记账。单个失败只记日志，不中断本轮（其余对象仍然可以清掉）。
        /// </summary>
        private static int RemoveAll(
            RuleContext ctx,
            RuleExecutionResult result,
            List<string> names,
            string category,
            Action<string> remove,
            Dictionary<string, int> totals)
        {
            int purged = 0;

            foreach (string name in names)
            {
                result.Hit();

                try
                {
                    remove(name);
                    purged++;

                    int n;
                    totals.TryGetValue(category, out n);
                    totals[category] = n + 1;

                    // 逐项 MarkChanged：汇总行的"成功"必须等于实际删掉的个数，
                    // 否则会出现"匹配 4、成功 1"这种自相矛盾的报表。
                    result.MarkChanged(string.Format("清理 {0}「{1}」", category, name));
                    ctx.RecordTableChange(name, category, "清理（删除表项）", name, "(已删除)");
                    ctx.Log.Detail(string.Format("已清理 {0}「{1}」", category, name));
                }
                catch (Exception ex)
                {
                    string msg = string.Format("清理 {0}「{1}」失败：{2}", category, name, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }

            return purged;
        }

        // ---------------------------------------------------------------- 类别解析

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

                // 容错：允许写 Blocks / Layers / TextStyles 之类的复数形式
                if (key.EndsWith("s", StringComparison.OrdinalIgnoreCase) && key.Length > 1)
                {
                    key = key.Substring(0, key.Length - 1);
                }

                if (AllCategories.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    set.Add(AllCategories.First(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)));
                }
            }

            // 全部都没识别出来时**不能**退回"清全部"：
            // 配置写 "Layres" 的后果不该是"把块定义、图层、线型、样式全清一遍"。
            if (set.Count == 0)
            {
                throw new RuleValidationException(string.Format(
                    "PurgeCategories 里的类别一个都无法识别（{0}）。可用：{1}。",
                    string.Join("/", configured.ToArray()),
                    string.Join("/", AllCategories)));
            }

            return set;
        }
    }
}
