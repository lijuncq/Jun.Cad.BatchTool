using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 标注文字与测量值一致性审计（**只读，不改图**）。
    ///
    /// 为什么值得单独一条规则：海工详图里手工覆盖标注文字极其常见（实测一张管道布置图
    /// 84 个标注**全部**是显式文字覆盖），图纸改过尺寸但文字没跟着改，就是错图。
    /// 这条规则把"文字和测量值对不上"的标注挑出来，出报告给人核对。
    ///
    /// 解析策略：从文字里抠出**第一个数值**（允许 `%%c50`、`R100`、`±12.5`、`2×45°` 这类前后缀），
    /// 抠不出来就记"无法解析"，不当成 0 偏差。
    ///
    /// 只读：`Changed` 永远为 0，命中的都记 `Skipped`，不一致的额外写一条说明。
    /// </summary>
    public sealed class DimTextAuditRule : IRuleHandler
    {
        private static readonly Regex NumberPattern = new Regex(
            @"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

        public string RuleType
        {
            get { return "DimTextAudit"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            double absolute = RuleContext.Double(action.AuditTolerance, 0.5);
            double percent = RuleContext.Double(action.AuditTolerancePercent, 0.0) / 100.0;
            bool includeEmpty = RuleContext.Bool(action.AuditIncludeEmptyText, false);

            RuleFilter filter = context.Filter;
            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                filter.EntityTypes = new List<string> { "DIMENSION" };
            }

            int checkedCount = 0;
            int mismatch = 0;
            int unparsable = 0;
            int emptyText = 0;
            var notes = new List<string>();

            foreach (ScanHit hit in EntityScanner.Scan(context, EntityFilter.Build(filter)))
            {
                var dim = hit.Entity as Dimension;
                if (dim == null)
                {
                    continue;
                }

                result.Hit();
                checkedCount++;

                string text = dim.Text;
                if (string.IsNullOrEmpty(text) || text == "<>")
                {
                    emptyText++;

                    // 无论报不报告，都要计入"跳过"——否则 匹配 ≠ 成功+跳过+失败，汇总表会看着像漏了
                    result.MarkSkipped("跟随测量值");

                    if (includeEmpty)
                    {
                        context.RecordSkip(dim, "标注文字审计", "该标注没有显式文字（跟随测量值）", false,
                            EntityScanner.SpaceNameOf(hit));
                    }

                    continue;
                }

                double? value = ParseFirstNumber(text);
                if (!value.HasValue)
                {
                    unparsable++;
                    result.MarkSkipped("文字无法解析为数值");
                    context.RecordSkip(dim, "标注文字审计",
                        "文字「" + Trim(text) + "」里找不到数值", false, EntityScanner.SpaceNameOf(hit));
                    continue;
                }

                double measurement = dim.Measurement;
                double tolerance = Math.Max(absolute, Math.Abs(measurement) * percent);
                double diff = Math.Abs(value.Value - measurement);

                if (diff > tolerance)
                {
                    mismatch++;
                    string reason = string.Format("文字 {0:0.###} ≠ 测量 {1:0.###}（差 {2:0.###}，容差 {3:0.###}）",
                        value.Value, measurement, diff, tolerance);

                    result.MarkSkipped("文字与测量值不一致");
                    context.RecordSkip(dim, "标注文字审计", reason, false, EntityScanner.SpaceNameOf(hit));

                    if (notes.Count < 30)
                    {
                        notes.Add(string.Format("#{0} {1}", dim.Handle.ToString("X"), reason));
                    }
                }
                else
                {
                    result.MarkSkipped("一致");
                }
            }

            foreach (string note in notes)
            {
                result.AddNote(note);
            }

            result.AddNote(string.Format(
                "审计 {0} 个标注：不一致 {1}，无法解析 {2}，未覆盖文字 {3}。本规则只报告、不改图。",
                checkedCount, mismatch, unparsable, emptyText));

            if (mismatch > 0)
            {
                context.Log.Warn(string.Format("有 {0} 个标注的文字与测量值不一致，见明细。", mismatch));
            }
        }

        /// <summary>取第一个数值（去掉 %%c、R、Ø、±、×、° 之类前后缀）。</summary>
        public static double? ParseFirstNumber(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // %%c 是直径符号的转义，先清掉，避免 %%c50 被解析成 50 之前的干扰
            string cleaned = text.Replace("%%c", string.Empty).Replace("%%C", string.Empty);

            Match match = NumberPattern.Match(cleaned);
            if (!match.Success)
            {
                return null;
            }

            double value;
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return null;
            }

            return value;
        }

        private static string Trim(string text)
        {
            return text.Length > 40 ? text.Substring(0, 40) + "…" : text;
        }
    }
}
