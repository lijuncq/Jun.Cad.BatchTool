using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 单条规则的执行结果统计。
    /// 约定：Matched = Changed + Skipped + Failed（部分属性迁移失败时，实体仍计入 Changed，
    /// 失败明细单独记录在 Failures 中）。
    /// </summary>
    public sealed class RuleExecutionResult
    {
        private readonly List<string> _details = new List<string>();
        private readonly List<string> _failures = new List<string>();

        public RuleExecutionResult(string ruleName, string ruleType)
        {
            RuleName = ruleName;
            RuleType = ruleType;
        }

        public string RuleName { get; private set; }

        public string RuleType { get; private set; }

        /// <summary>命中过滤条件的对象数量。</summary>
        public int Matched { get; set; }

        /// <summary>成功修改的对象数量。</summary>
        public int Changed { get; set; }

        /// <summary>命中但按规则主动跳过的数量（如动态块被禁止替换、属性 Tag 不匹配等）。</summary>
        public int Skipped { get; set; }

        /// <summary>处理异常的对象数量。</summary>
        public int Failed { get; set; }

        /// <summary>规则是否整体失败（例如目标块不存在）。</summary>
        public bool Aborted { get; set; }

        /// <summary>整体失败原因。</summary>
        public string AbortReason { get; set; }

        public TimeSpan Elapsed { get; set; }

        public IReadOnlyList<string> Details => _details;

        public IReadOnlyList<string> Failures => _failures;

        /// <summary>命中一个对象。</summary>
        public void Hit()
        {
            Matched++;
        }

        /// <summary>记录一次成功修改。</summary>
        public void MarkChanged(string detail)
        {
            Changed++;
            if (!string.IsNullOrEmpty(detail))
            {
                _details.Add(detail);
            }
        }

        /// <summary>记录一次主动跳过。</summary>
        public void MarkSkipped(string detail)
        {
            Skipped++;
            if (!string.IsNullOrEmpty(detail))
            {
                _details.Add("[跳过] " + detail);
            }
        }

        /// <summary>记录一次失败。</summary>
        public void MarkFailed(string detail)
        {
            Failed++;
            if (!string.IsNullOrEmpty(detail))
            {
                _failures.Add(detail);
            }
        }

        public void AddNote(string note)
        {
            if (!string.IsNullOrEmpty(note))
            {
                _details.Add("[说明] " + note);
            }
        }

        /// <summary>统计行文本。</summary>
        public string ToSummaryLine()
        {
            string state = Aborted ? "规则中止" : (Failed > 0 ? "部分失败" : "完成");
            return string.Format(
                "{0} | 匹配 {1} | 成功 {2} | 跳过 {3} | 失败 {4} | 耗时 {5} ms | {6}",
                RuleName, Matched, Changed, Skipped, Failed, (int)Elapsed.TotalMilliseconds, state);
        }
    }

    /// <summary>
    /// 一次批处理的汇总结果。
    /// </summary>
    public sealed class BatchRunSummary
    {
        public BatchRunSummary()
        {
            Results = new List<RuleExecutionResult>();
            Warnings = new List<string>();
        }

        public List<RuleExecutionResult> Results { get; private set; }

        public List<string> Warnings { get; private set; }

        public TimeSpan Elapsed { get; set; }

        public string ExecutionMode { get; set; }

        public int TotalMatched
        {
            get
            {
                int n = 0;
                foreach (var r in Results)
                {
                    n += r.Matched;
                }

                return n;
            }
        }

        public int TotalChanged
        {
            get
            {
                int n = 0;
                foreach (var r in Results)
                {
                    n += r.Changed;
                }

                return n;
            }
        }

        public int TotalSkipped
        {
            get
            {
                int n = 0;
                foreach (var r in Results)
                {
                    n += r.Skipped;
                }

                return n;
            }
        }

        public int TotalFailed
        {
            get
            {
                int n = 0;
                foreach (var r in Results)
                {
                    n += r.Failed;
                }

                return n;
            }
        }

        public Stopwatch StartTimer()
        {
            return Stopwatch.StartNew();
        }
    }
}
