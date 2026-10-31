using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>单条规则的执行结果。</summary>
    public sealed class RuleExecutionResult
    {
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _notes = new List<string>();

        /// <summary>跳过原因分桶（粗分类，不是逐对象明细）。</summary>
        private readonly Dictionary<string, int> _skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);

        public RuleExecutionResult(string ruleName, string ruleType)
        {
            RuleName = ruleName;
            RuleType = ruleType;
        }

        public string RuleName { get; private set; }

        public string RuleType { get; private set; }

        /// <summary>命中数（匹配 = 成功 + 跳过 + 失败）。</summary>
        public int Matched { get; private set; }

        /// <summary>实际改动数。</summary>
        public int Changed { get; private set; }

        /// <summary>命中但无需改动（已是目标状态 / 锁定图层）。</summary>
        public int Skipped { get; private set; }

        /// <summary>处理失败数。</summary>
        public int Failed { get; private set; }

        /// <summary>规则是否中止（配置错误、目标不存在等可预期失败）。</summary>
        public bool Aborted { get; set; }

        public string AbortReason { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>
        /// 计数不自洽：`匹配 ≠ 改动 + 跳过 + 失败`。
        ///
        /// 这不是"统计不好看"，而是**规则漏报**的信号——某条分支 `Hit()` 了却没有
        /// `MarkChanged/MarkSkipped/MarkFailed`，用户看到的"匹配 N"里就有 N 个对象去向不明。
        /// 契约上要求三者相等，但接口没有强制手段，所以引擎在收尾时检查并告警。
        /// </summary>
        public bool CountMismatch { get; set; }

        /// <summary>不自洽的差额（匹配 − 改动 − 跳过 − 失败）。</summary>
        public int CountGap
        {
            get { return Matched - Changed - Skipped - Failed; }
        }

        public IReadOnlyList<string> Failures
        {
            get { return _failures; }
        }

        /// <summary>
        /// 不计入统计的说明（"属性 Tag 在源块里不存在"这类"做了但要告诉你"的事）。
        /// 与 Failures 分开：笔记不是错误，但也不能悄悄丢掉。
        /// </summary>
        public IReadOnlyList<string> Notes
        {
            get { return _notes; }
        }

        public void AddNote(string note)
        {
            if (!string.IsNullOrEmpty(note))
            {
                _notes.Add(note);
            }
        }

        public void Hit()
        {
            Matched++;
        }

        public void MarkChanged(string detail)
        {
            Changed++;
        }

        public void MarkSkipped(string detail)
        {
            Skipped++;

            string bucket = ClassifySkipReason(detail);
            int n;
            _skipReasons.TryGetValue(bucket, out n);
            _skipReasons[bucket] = n + 1;
        }

        /// <summary>
        /// 把规则传来的跳过说明归到少数几类。
        ///
        /// 为什么要归类：规则里传的多是"#4A MTEXT：图层被锁定"这种逐对象文案，
        /// 直接按原文分桶会得到几百个只出现一次的桶，汇总行反而更长。这里只按关键词粗分，
        /// 目的是让"跳过 228"变成"跳过 228（块内实体 204 / 锁定图层 20 / 无遮挡 4）"。
        /// </summary>
        public static string ClassifySkipReason(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return "未说明";
            }

            if (detail.IndexOf("预演", StringComparison.Ordinal) >= 0) return "仅预演";
            if (detail.IndexOf("锁定", StringComparison.Ordinal) >= 0) return "锁定图层";
            if (detail.IndexOf("块内", StringComparison.Ordinal) >= 0) return "块内实体";
            if (detail.IndexOf("Defpoints", StringComparison.OrdinalIgnoreCase) >= 0) return "Defpoints 层";
            if (detail.IndexOf("无遮挡", StringComparison.Ordinal) >= 0) return "无遮挡";
            if (detail.IndexOf("已是", StringComparison.Ordinal) >= 0) return "已是目标状态";
            if (detail.IndexOf("为空", StringComparison.Ordinal) >= 0) return "结果为空";
            if (detail.IndexOf("未匹配", StringComparison.Ordinal) >= 0
                || detail.IndexOf("不匹配", StringComparison.Ordinal) >= 0) return "未匹配";
            if (detail.IndexOf("上限", StringComparison.Ordinal) >= 0) return "超出上限";
            if (detail.IndexOf("无法", StringComparison.Ordinal) >= 0) return "无法判定";
            if (detail.IndexOf("跟随", StringComparison.Ordinal) >= 0) return "跟随测量值";

            return "其它";
        }

        /// <summary>跳过原因摘要（按次数降序，最多 4 类）。</summary>
        public string DescribeSkipReasons()
        {
            if (_skipReasons.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in _skipReasons
                .OrderByDescending(p => p.Value)
                .Take(4))
            {
                parts.Add(pair.Key + " " + pair.Value);
            }

            return string.Join(" / ", parts.ToArray());
        }

        public void MarkFailed(string detail)
        {
            Failed++;
            if (!string.IsNullOrEmpty(detail))
            {
                _failures.Add(detail);
            }
        }

        public string ToSummaryLine()
        {
            var sb = new StringBuilder();
            // "成功"改成"改动"：审计类规则的成功数永远是 0（它们只报告），
            // 叫"成功"会让用户以为一条都没生效。
            sb.AppendFormat("匹配 {0}，改动 {1}，跳过 {2}，失败 {3}", Matched, Changed, Skipped, Failed);

            string reasons = DescribeSkipReasons();
            if (!string.IsNullOrEmpty(reasons))
            {
                sb.Append("（跳过原因：").Append(reasons).Append('）');
            }
            if (Aborted)
            {
                sb.Append("，已中止");
            }

            return sb.ToString();
        }
    }

    /// <summary>整批处理的汇总。</summary>
    public sealed class BatchRunSummary
    {
        public BatchRunSummary()
        {
            Results = new List<RuleExecutionResult>();
            Warnings = new List<string>();
            Notes = new List<string>();
        }

        public List<RuleExecutionResult> Results { get; private set; }

        public List<string> Warnings { get; private set; }

        /// <summary>
        /// 规则之外、作用在整份文档上的改动（目前只有 <c>$MEASUREMENT</c> 修正）。
        /// 单独放一列而不是塞进 Warnings：它是"确实改了"，不是"要注意"。
        /// </summary>
        public List<string> Notes { get; private set; }

        public string ExecutionMode { get; set; }

        public TimeSpan Elapsed { get; set; }

        public int TotalMatched
        {
            get { return Sum(r => r.Matched); }
        }

        public int TotalChanged
        {
            get { return Sum(r => r.Changed); }
        }

        public int TotalSkipped
        {
            get { return Sum(r => r.Skipped); }
        }

        public int TotalFailed
        {
            get { return Sum(r => r.Failed); }
        }

        private int Sum(Func<RuleExecutionResult, int> selector)
        {
            int total = 0;
            foreach (RuleExecutionResult r in Results)
            {
                total += selector(r);
            }

            return total;
        }
    }

    /// <summary>
    /// 单个文件的处理结果（批处理会遍历多个文件）。
    /// </summary>
    public sealed class FileRunResult
    {
        public string FilePath { get; set; }

        public bool Succeeded { get; set; }

        public string Error { get; set; }

        public BatchRunSummary Summary { get; set; }

        public string OutputPath { get; set; }

        public bool Skipped { get; set; }
    }
}
