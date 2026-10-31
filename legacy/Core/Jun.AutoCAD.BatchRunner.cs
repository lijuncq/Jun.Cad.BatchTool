using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using HostException = ZwSoft.ZwCAD.Runtime.Exception;
#else
using HostException = Autodesk.AutoCAD.Runtime.Exception;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>执行模式。</summary>
    public enum ExecutionMode
    {
        /// <summary>所有规则共用一个事务，顺序执行，后一条规则看到前一条的结果。</summary>
        Sequential,

        /// <summary>每条规则独立事务并立即提交，单条失败可整体回滚该条规则。</summary>
        Isolated
    }

    /// <summary>
    /// 批处理执行引擎：负责事务边界、规则调度、异常兜底与统计汇总。
    ///
    /// 事务边界（关键约束）：
    ///   * Sequential：整个批处理只有一个事务，最后统一 Commit；
    ///   * Isolated  ：每条规则一个事务，成功即 Commit，异常时 Dispose 自动回滚；
    ///   * 任何处理器内部都不允许自己 Commit/Dispose 环境事务。
    /// </summary>
    public sealed class BatchRunner
    {
        private readonly Document _doc;
        private readonly BatchConfig _config;
        private readonly string _configPath;
        private readonly BatchLogger _log;

        public BatchRunner(Document doc, BatchConfig config, string configPath, BatchLogger log)
        {
            _doc = doc;
            _config = config;
            _configPath = configPath;
            _log = log;
            _log.MaxEditorDetailLines = config.MaxEditorDetailLines;
        }

        public BatchRunSummary Run()
        {
            var summary = new BatchRunSummary();
            var totalWatch = Stopwatch.StartNew();

            ExecutionMode mode = ParseMode(_config.ExecutionMode, summary.Warnings);
            summary.ExecutionMode = mode.ToString();

            var rules = _config.Rules.Where(r => r != null && r.Enabled).ToList();

            _log.Section("开始执行批处理");
            _log.Info(string.Format("配置文件：{0}", _configPath));
            _log.Info(string.Format("执行模式：{0}", mode == ExecutionMode.Sequential ? "Sequential（单事务）" : "Isolated（逐条提交）"));
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

            if (mode == ExecutionMode.Sequential)
            {
                RunSequential(rules, summary);
            }
            else
            {
                RunIsolated(rules, summary);
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
                    return ExecutionMode.Isolated;

                case "PARALLEL":
                    warnings.Add("ExecutionMode=Parallel 不受支持：AutoCAD 图形数据库不支持多线程并发写入，已降级为 Isolated 顺序执行。");
                    return ExecutionMode.Isolated;

                default:
                    warnings.Add(string.Format("未知的 ExecutionMode「{0}」，已回退为 Sequential。", text));
                    return ExecutionMode.Sequential;
            }
        }

        /// <summary>顺序模式：单事务，规则依次执行。</summary>
        private void RunSequential(List<RuleConfig> rules, BatchRunSummary summary)
        {
            bool continueOnError = _config.ContinueOnError;

            using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
            {
                foreach (RuleConfig rule in rules)
                {
                    RuleExecutionResult result = ExecuteRule(tr, rule);
                    summary.Results.Add(result);

                    bool ruleContinue = rule.ContinueOnError.HasValue ? rule.ContinueOnError.Value : continueOnError;
                    if (result.Aborted && !ruleContinue)
                    {
                        _log.Warn(string.Format(
                            "规则「{0}」中止且 ContinueOnError=false，后续规则不再执行（已完成规则的修改仍会提交）。",
                            rule.Name));
                        break;
                    }
                }

                // 统一提交：Sequential 模式下所有规则的修改一起生效
                tr.Commit();
            }
        }

        /// <summary>隔离模式：每条规则独立事务，失败只回滚该条规则。</summary>
        private void RunIsolated(List<RuleConfig> rules, BatchRunSummary summary)
        {
            bool continueOnError = _config.ContinueOnError;

            foreach (RuleConfig rule in rules)
            {
                RuleExecutionResult result;

                using (Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    result = ExecuteRule(tr, rule);

                    if (result.Aborted)
                    {
                        // 不 Commit，Dispose 时自动回滚本条规则的所有修改
                        _log.Warn(string.Format("规则「{0}」已中止，本条规则的修改已回滚。", rule.Name));
                    }
                    else
                    {
                        tr.Commit();
                    }
                }

                summary.Results.Add(result);

                bool ruleContinue = rule.ContinueOnError.HasValue ? rule.ContinueOnError.Value : continueOnError;
                if (result.Aborted && !ruleContinue)
                {
                    _log.Warn("ContinueOnError=false，后续规则不再执行。");
                    break;
                }
            }
        }

        /// <summary>执行单条规则并记录日志。任何异常都不会向外抛出。</summary>
        private RuleExecutionResult ExecuteRule(Transaction tr, RuleConfig rule)
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
                _log.Error(result.AbortReason + " 已注册类型：" + string.Join(", ", RuleFactory.GetRegisteredTypes()));
                return result;
            }

            try
            {
                var ctx = new RuleExecutionContext(_doc, tr, _config, rule, _log);
                handler.Execute(ctx, result);
            }
            catch (RuleValidationException ex)
            {
                result.Aborted = true;
                result.AbortReason = ex.Message;
                _log.Error(string.Format("规则「{0}」中止：{1}", rule.Name, ex.Message));
            }
            catch (HostException ex)
            {
                result.Aborted = true;
                result.AbortReason = ex.Message;
                _log.Error(string.Format("规则「{0}」遇到 AutoCAD 异常（{1}）：{2}", rule.Name, ex.ErrorStatus, ex.Message));
            }
            catch (Exception ex)
            {
                result.Aborted = true;
                result.AbortReason = ex.Message;
                _log.Error(string.Format("规则「{0}」异常：{1}", rule.Name, ex));
            }

            watch.Stop();
            result.Elapsed = watch.Elapsed;

            // 失败明细逐条输出（含句柄），便于用户按句柄定位
            foreach (string failure in result.Failures)
            {
                _log.Error("  失败明细：" + failure);
            }

            _log.Info("  " + result.ToSummaryLine());
            return result;
        }

        /// <summary>输出整批处理的汇总表。</summary>
        public void WriteSummary(BatchRunSummary summary)
        {
            _log.Section("批处理汇总");
            _log.Info(string.Format("{0,-28}{1,8}{2,8}{3,8}{4,8}{5,10}", "规则", "匹配", "成功", "跳过", "失败", "耗时(ms)"));

            foreach (RuleExecutionResult r in summary.Results)
            {
                _log.Info(string.Format("{0,-28}{1,8}{2,8}{3,8}{4,8}{5,10}",
                    Trim(r.RuleName, 26), r.Matched, r.Changed, r.Skipped, r.Failed, (int)r.Elapsed.TotalMilliseconds));
            }

            _log.Info(new string('-', 70));
            _log.Info(string.Format("{0,-28}{1,8}{2,8}{3,8}{4,8}{5,10}",
                "合计", summary.TotalMatched, summary.TotalChanged, summary.TotalSkipped, summary.TotalFailed,
                (int)summary.Elapsed.TotalMilliseconds));

            if (!string.IsNullOrEmpty(_log.LogFilePath))
            {
                _log.Info("日志文件：" + _log.LogFilePath);
            }

            // ---- 中止原因集中回放：规则为什么没干活，必须说清楚 ----
            var aborted = new List<RuleExecutionResult>();
            foreach (RuleExecutionResult r in summary.Results)
            {
                if (r.Aborted)
                {
                    aborted.Add(r);
                }
            }

            if (aborted.Count > 0)
            {
                _log.Warn("以下规则已中止（未执行完成）：");
                foreach (RuleExecutionResult r in aborted)
                {
                    _log.Warn(string.Format("  · {0}：{1}", r.RuleName, r.AbortReason));
                }
            }

            // ---- 零改动时给出可执行的排查指引，避免"跑了但什么都没发生" ----
            if (summary.TotalChanged == 0)
            {
                _log.Section("本次没有任何对象被修改 —— 按下面几条排查");
                _log.Info("1) Filter 里的名字必须与图纸一致：块名 / 图层名 / 文字样式名 / 填充图案名。");
                _log.Info("   示例配置用的是 A1_TITLE、EQUIPMENT 这类演示名字，图纸里没有就会 0 命中。");
                _log.Info("2) 颜色索引：随层实体是 256；写 1 只会命中\"显式设为红色\"的实体，随层的红线不会命中。");
                _log.Info("3) 规则是否被 Enabled=false 关闭（示例配置里大部分扩展规则默认关闭）。");
                _log.Info("4) 对象是否在扫描范围内：Scope=ModelAndLayouts 会覆盖布局与块定义内部；");
                _log.Info("   Scope=Selection 只处理当前选择集，需要先选中对象。");
                _log.Info("5) 目标不存在时规则会主动中止（如目标块/线型/图案未定义），原因见上面的\"已中止\"列表。");
                _log.Info("6) 逐条明细在日志文件里；环境问题可执行 BATCHDIAG 自检。");
            }
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
