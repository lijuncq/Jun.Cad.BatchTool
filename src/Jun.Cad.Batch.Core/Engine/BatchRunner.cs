using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ACadSharp;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine.Preview;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 执行模式。
    ///
    /// 离线版只有 <see cref="Sequential"/> 一种真实语义——没有事务就没有"逐条回滚"。
    /// 保留这个枚举是为了让旧配置读得进来，并在降级时给出明确告警。
    /// </summary>
    public enum ExecutionMode
    {
        Sequential,
        Isolated
    }

    /// <summary>
    /// 批处理执行引擎：规则调度、异常兜底、统计汇总。
    ///
    /// 与 AutoCAD 版最大的差别：**没有事务**。
    ///   * 规则直接改内存模型，没有"提交"这一步；
    ///   * 因此 <c>ExecutionMode=Isolated</c> 的"单条失败只回滚该条"无法实现——
    ///     这里会明确告警并按 Sequential 语义执行，而不是假装支持；
    ///   * 真正的回滚边界在**文件级**：整份文档处理失败就丢弃、不写盘（见 <see cref="CadFileSession"/>），
    ///     源文件一个字节都不会被动。
    ///
    /// 这条差别必须在日志里说清楚：用户看到 "Isolated" 却没得到回滚，会以为规则出问题时图纸是安全的。
    /// </summary>
    public sealed class BatchRunner
    {
        private readonly BatchConfig _config;
        private readonly string _configPath;
        private readonly BatchLogger _log;
        private readonly ResourceLocator _resources;
        private readonly IPreviewSink _preview;

        /// <summary>当前规则是否自己上报改动（决定能不能输出"均已是目标状态"这类兜底结论）。</summary>
        private bool _currentReportsOwnChanges;

        public BatchRunner(BatchConfig config, string configPath, BatchLogger log, ResourceLocator resources)
            : this(config, configPath, log, resources, null)
        {
        }

        public BatchRunner(BatchConfig config, string configPath, BatchLogger log, ResourceLocator resources, IPreviewSink preview)
        {
            _config = config ?? new BatchConfig();
            _configPath = configPath;
            _log = log;
            _resources = resources ?? new ResourceLocator();
            _preview = preview;
            _log.MaxConsoleDetailLines = config == null ? 100 : config.MaxEditorDetailLines;
        }

        /// <summary>预览里显示的文件名（由调用方设置，引擎不关心路径）。</summary>
        public string FileLabel { get; set; }

        /// <summary>是否处于差异预览模式（预演：规则照跑，但不写盘由调用方保证）。</summary>
        public bool IsPreview
        {
            get { return _preview != null; }
        }

        /// <summary>在给定文档上执行全部启用规则。</summary>
        public BatchRunSummary Run(CadDocument doc)
        {
            var summary = new BatchRunSummary();
            var totalWatch = Stopwatch.StartNew();

            if (doc == null)
            {
                summary.Warnings.Add("文档为空，未执行任何规则。");
                summary.Elapsed = totalWatch.Elapsed;
                return summary;
            }

            ExecutionMode mode = ParseMode(_config.ExecutionMode, summary.Warnings);
            summary.ExecutionMode = mode.ToString();

            var rules = _config.Rules.Where(r => r != null && r.Enabled).ToList();

            _log.Section("开始执行批处理");
            if (!string.IsNullOrEmpty(_configPath))
            {
                _log.Info(string.Format("配置文件：{0}", _configPath));
            }

            _log.Info(string.Format("执行模式：{0}", DescribeMode(mode)));
            _log.Info(string.Format("启用规则：{0} 条", rules.Count));

            foreach (string w in summary.Warnings)
            {
                _log.Warn(w);
            }

            if (rules.Count == 0)
            {
                _log.Warn("没有启用的规则，批处理结束。");
                summary.Elapsed = totalWatch.Elapsed;
                return summary;
            }

            bool continueOnError = _config.ContinueOnError;

            if (_preview != null)
            {
                _preview.CurrentFile = FileLabel;
            }

            for (int index = 0; index < rules.Count; index++)
            {
                RuleConfig rule = rules[index];

                if (_preview != null)
                {
                    // 每条规则各自一套句柄去重集合，避免前一条规则的"跳过"吞掉后一条规则的改动
                    _preview.BeginRule(index);
                }

                // 预览模式：规则执行前后各抓一次快照，用 diff 代替"规则自报"
                Dictionary<string, EntityState> before = _preview == null
                    ? null
                    : EntitySnapshot.Take(doc, 8, true);

                RuleExecutionResult result = ExecuteRule(doc, rule, index);
                summary.Results.Add(result);

                if (_preview != null)
                {
                    CapturePreview(doc, rule, index, result, before);
                }

                bool ruleContinue = rule.ContinueOnError.HasValue ? rule.ContinueOnError.Value : continueOnError;
                if (result.Aborted && !ruleContinue)
                {
                    _log.Warn(string.Format(
                        "规则「{0}」中止且 ContinueOnError=false，后续规则不再执行（已完成规则的修改仍会写出）。",
                        rule.Name));
                    break;
                }
            }

            // ---- 文档级收尾：保证写出去的填充图案名 AutoCAD 认得出 ----
            // 放在所有规则之后：图案名是规则改出来的，改完才知道要不要动 $MEASUREMENT。
            // 失败不能影响主流程——这只是"名字能不能被解析"的兼容性修正，不是业务规则。
            try
            {
                bool hatchRules = rules.Any(r =>
                    string.Equals((r.Type ?? string.Empty).Trim(), "HatchPatternChange", StringComparison.OrdinalIgnoreCase));

                string unitsNote = DrawingUnits.Apply(
                    doc, _resources, _config.MeasurementUnits, IsPreview, _log, hatchRules);

                if (!string.IsNullOrEmpty(unitsNote))
                {
                    summary.Notes.Add(unitsNote);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("修正图形测量单位（$MEASUREMENT）失败，已跳过：" + ex.Message);
            }

            totalWatch.Stop();
            summary.Elapsed = totalWatch.Elapsed;
            return summary;
        }

        private static ExecutionMode ParseMode(string text, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ExecutionMode.Sequential;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "SEQUENTIAL":
                case "SERIAL":
                    return ExecutionMode.Sequential;

                case "ISOLATED":
                case "PERRULE":
                case "PER-RULE":
                    warnings.Add("ExecutionMode=Isolated 在离线模式下无法实现（没有事务可回滚），已按 Sequential 执行；"
                                 + "回滚边界退化为\"整个文件\"：任一步骤崩溃则不写出，源文件保持原样。");
                    return ExecutionMode.Sequential;

                case "PARALLEL":
                    warnings.Add("ExecutionMode=Parallel 不受支持（同一文档不能被并发修改），已按 Sequential 执行。");
                    return ExecutionMode.Sequential;

                default:
                    warnings.Add(string.Format("未知的 ExecutionMode「{0}」，已回退为 Sequential。", text));
                    return ExecutionMode.Sequential;
            }
        }

        private static string DescribeMode(ExecutionMode mode)
        {
            return mode == ExecutionMode.Sequential
                ? "Sequential（顺序执行，无事务）"
                : mode.ToString();
        }

        /// <summary>
        /// 把一条规则的执行结果转成预览行。
        ///
        /// 三条来源：
        ///   1. 快照 diff → <c>Will change</c>（引擎算，规则不用配合）；
        ///   2. 规则自己上报的 <see cref="RuleContext.RecordChange"/>（带降级原因）；
        ///   3. 失败/中止 → <c>Blocked</c>（附原因）。
        /// </summary>
        private void CapturePreview(
            CadDocument doc, RuleConfig rule, int index, RuleExecutionResult result,
            Dictionary<string, EntityState> before)
        {
            if (_preview == null)
            {
                return;
            }

            _preview.CurrentRule = rule.Name;
            _preview.CurrentRuleIndex = index;

            Dictionary<string, EntityState> after = EntitySnapshot.Take(doc, 8, true);
            List<ChangeRecord> diff = EntitySnapshot.Diff(before, after);

            foreach (ChangeRecord record in diff)
            {
                // 规则自己已经报过这个句柄（带降级原因）——不要再用快照 diff 重复一行
                if (_preview.HasRecordFor(record.Handle))
                {
                    continue;
                }

                record.RuleName = rule.Name;
                record.RuleIndex = index;
                record.File = FileLabel;
                _preview.Add(record);
            }

            // 自报的规则：diff 为空不代表"没改"，只是"改的东西快照看不见"，
            // 这时绝不能说"均已是目标状态"——那是把"改了很多"说成"什么都没改"。
            if (diff.Count == 0 && result.Matched > 0 && !result.Aborted && !_currentReportsOwnChanges)
            {
                _preview.Add(new ChangeRecord
                {
                    RuleName = rule.Name,
                    RuleIndex = index,
                    File = FileLabel,
                    Operation = "整条规则",
                    Status = ChangeStatus.NoChange,
                    Note = string.Format("命中 {0} 处，均已是目标状态", result.Matched)
                });
            }

            foreach (string failure in result.Failures)
            {
                _preview.Add(new ChangeRecord
                {
                    RuleName = rule.Name,
                    RuleIndex = index,
                    File = FileLabel,
                    Operation = "失败",
                    Status = ChangeStatus.Blocked,
                    Note = failure
                });
            }

            if (result.Aborted)
            {
                _preview.Add(new ChangeRecord
                {
                    RuleName = rule.Name,
                    RuleIndex = index,
                    File = FileLabel,
                    Operation = "规则中止",
                    Status = ChangeStatus.Blocked,
                    Note = result.AbortReason
                });
            }
        }

        /// <summary>执行单条规则并记录日志。任何异常都不会向外抛出。</summary>
        private RuleExecutionResult ExecuteRule(CadDocument doc, RuleConfig rule, int index = 0)
        {
            var result = new RuleExecutionResult(rule.Name, rule.Type);
            var watch = Stopwatch.StartNew();

            _log.BeginRule();
            _log.Section(string.Format("规则：{0}（{1}）", rule.Name, rule.Type));

            IRuleHandler handler = RuleFactory.Create(rule.Type);
            if (handler == null)
            {
                result.Aborted = true;
                result.AbortReason = string.Format("规则类型「{0}」未注册。", rule.Type);
                watch.Stop();
                result.Elapsed = watch.Elapsed;
                _log.Error(result.AbortReason + " 已注册类型：" + string.Join(", ", RuleFactory.GetRegisteredTypes().ToArray()));
                return result;
            }

            try
            {
                var ctx = new RuleContext(doc, _config, rule, _log, _resources) { FileLabel = FileLabel };
                _currentReportsOwnChanges = handler.ReportsOwnChanges;

                if (_preview != null)
                {
                    _preview.CurrentRule = rule.Name;
                    _preview.CurrentRuleIndex = index;
                    ctx.Preview = _preview;
                }

                handler.Execute(ctx, result);
            }
            catch (RuleValidationException ex)
            {
                result.Aborted = true;
                result.AbortReason = ex.Message;
                _log.Error(string.Format("规则「{0}」中止：{1}", rule.Name, ex.Message));
            }
            catch (Exception ex)
            {
                result.Aborted = true;
                result.AbortReason = ex.Message;
                _log.Error(string.Format("规则「{0}」异常：{1}", rule.Name, ex));
            }

            watch.Stop();
            result.Elapsed = watch.Elapsed;

            // 计数自洽性检查（契约：匹配 = 改动 + 跳过 + 失败）。
            // 只告警不中止——这是规则实现的疏漏，不是数据问题，中止反而会掩盖它。
            if (!result.Aborted && result.CountGap != 0)
            {
                result.CountMismatch = true;
                _log.Warn(string.Format(
                    "  计数不自洽：匹配 {0} ≠ 改动 {1} + 跳过 {2} + 失败 {3}（差 {4}）。"
                    + "有分支命中了却没有计入改动/跳过/失败，该规则的明细不可全信。",
                    result.Matched, result.Changed, result.Skipped, result.Failed, result.CountGap));
            }

            // 失败明细逐条输出（含句柄），便于用户按句柄定位
            foreach (string failure in result.Failures)
            {
                _log.Error("  失败明细：" + failure);
            }

            foreach (string note in result.Notes)
            {
                _log.Detail("  说明：" + note);
            }

            _log.Info("  " + result.ToSummaryLine());
            return result;
        }

        /// <summary>输出整批处理的汇总表。</summary>
        public void WriteSummary(BatchRunSummary summary)
        {
            if (summary == null)
            {
                return;
            }

            _log.Section("批处理汇总");
            _log.Info(FormatRow("规则", "匹配", "改动", "跳过", "失败", "耗时(ms)"));

            foreach (RuleExecutionResult r in summary.Results)
            {
                _log.Info(FormatRow(r.RuleName, r.Matched.ToString(), r.Changed.ToString(),
                    r.Skipped.ToString(), r.Failed.ToString(), ((int)r.Elapsed.TotalMilliseconds).ToString()));
            }

            _log.Info(new string('-', 70));
            _log.Info(FormatRow("合计", summary.TotalMatched.ToString(), summary.TotalChanged.ToString(),
                summary.TotalSkipped.ToString(), summary.TotalFailed.ToString(),
                ((int)summary.Elapsed.TotalMilliseconds).ToString()));

            if (!string.IsNullOrEmpty(_log.LogFilePath))
            {
                _log.Info("日志文件：" + _log.LogFilePath);
            }

            // ---- 文档级改动集中回放（$MEASUREMENT 这类"不在任何规则名下"的修改）----
            if (summary.Notes.Count > 0)
            {
                _log.Section("文档级改动（不属于任何规则）");
                foreach (string note in summary.Notes)
                {
                    _log.Info("  · " + note);
                }
            }

            // ---- 中止原因集中回放：规则为什么没干活，必须说清楚 ----
            var aborted = summary.Results.Where(r => r.Aborted).ToList();
            if (aborted.Count > 0)
            {
                _log.Warn("以下规则已中止（未执行完成）：");
                foreach (RuleExecutionResult r in aborted)
                {
                    _log.Warn(string.Format("  · {0}：{1}", r.RuleName, r.AbortReason));
                }
            }

            // ---- 计数不自洽的规则集中回放：明细不可全信，必须让人看见 ----
            var mismatched = summary.Results.Where(r => r.CountMismatch).ToList();
            if (mismatched.Count > 0)
            {
                _log.Warn("以下规则的计数不自洽（匹配 ≠ 改动 + 跳过 + 失败），明细可能缺项：");
                foreach (RuleExecutionResult r in mismatched)
                {
                    _log.Warn(string.Format("  · {0}：匹配 {1}，改动 {2}，跳过 {3}，失败 {4}（差 {5}）",
                        r.RuleName, r.Matched, r.Changed, r.Skipped, r.Failed, r.CountGap));
                }
            }

            // ---- 零改动时给出可执行的排查指引（一条规则都没跑时不打，那是配置问题不是过滤问题）----
            if (summary.TotalChanged == 0 && summary.Results.Count > 0)
            {
                _log.Section("本次没有任何对象被修改 —— 按下面几条排查");
                _log.Info("1) Filter 里的名字必须与图纸一致：块名 / 图层名 / 文字样式名 / 填充图案名。");
                _log.Info("   示例配置用的是 A1_TITLE、EQUIPMENT 这类演示名字，图纸里没有就会 0 命中。");
                _log.Info("2) 颜色索引：随层实体是 256；写 1 只会命中\"显式设为红色\"的实体，随层的红线不会命中。");
                _log.Info("3) 规则是否被 Enabled=false 关闭（示例配置里大部分扩展规则默认关闭）。");
                _log.Info("4) 对象是否在扫描范围内：Scope=ModelAndLayouts 会覆盖布局与块定义内部；");
                _log.Info("   Scope=Selection 在离线模式下不可用，会被降级为整张图。");
                _log.Info("5) 目标不存在时规则会主动中止（如目标块/线型/图案未定义），原因见上面的\"已中止\"列表。");
                _log.Info("6) 逐条明细在日志文件里。");
            }
        }

        /// <summary>
        /// 一行汇总。列宽按**显示列宽**算（汉字占两列），否则中文规则名会把整张表顶歪——
        /// 详见 <see cref="TextColumns"/>。
        /// </summary>
        private static string FormatRow(string name, string matched, string changed,
            string skipped, string failed, string elapsed)
        {
            return TextColumns.Pad(name, 28, false)
                 + TextColumns.Pad(matched, 8, true)
                 + TextColumns.Pad(changed, 8, true)
                 + TextColumns.Pad(skipped, 8, true)
                 + TextColumns.Pad(failed, 8, true)
                 + TextColumns.Pad(elapsed, 10, true);
        }
    }
}
