using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace Jun.Cad.Batch.Engine.Preview
{
    /// <summary>一个实体在某一时刻的"可比较状态"。</summary>
    public sealed class EntityState
    {
        public string Handle;
        public string Space;
        public string DxfType;
        public string Layer;
        public string Color;
        public string Linetype;
        public string LineWeight;
        public string Style;
        public string Height;
        public string Text;
        public string Hatch;
    }

    /// <summary>
    /// 实体快照 + 逐规则 diff。
    ///
    /// 为什么用"快照对比"而不是让每条规则自己上报：
    ///   * 现有 11 条规则都已经写好并验证过，为了预览去改它们的内部逻辑风险太大；
    ///   * 快照只读实体属性，规则改了什么就 diff 出什么，**不会漏也不会编**；
    ///   * 代价是每条规则前后各遍历一次文档，对大图（1 万实体量级）是毫秒级开销，可接受。
    ///
    /// 局限（写进预览的说明里，不能假装没有）：
    ///   * 只能看到**实体属性**的变化，看不到"新增/删除的实体属于哪条规则"之外的结构变化；
    ///   * 表格单元格文字不在快照字段里（逐格抓太贵），表格规则需自己上报；
    ///   * 同名句柄在块定义与布局里不会重复，但块定义内的实体没有"空间"概念，
    ///     统一记成 <c>块:&lt;块名&gt;</c>。
    /// </summary>
    public static class EntitySnapshot
    {
        /// <summary>抓一份快照（模型 + 布局 + 块定义内部）。</summary>
        public static Dictionary<string, EntityState> Take(CadDocument doc, int maxDepth, bool includeNested)
        {
            var map = new Dictionary<string, EntityState>(StringComparer.OrdinalIgnoreCase);
            if (doc == null)
            {
                return map;
            }

            List<ScanHit> hits = DatabaseWalker.Collect(doc, ScanScope.ModelAndLayouts, maxDepth, includeNested, null);
            foreach (ScanHit hit in hits)
            {
                EntityState state = Describe(hit);
                if (state != null)
                {
                    map[state.Handle] = state;
                }
            }

            return map;
        }

        private static EntityState Describe(ScanHit hit)
        {
            Entity entity = hit.Entity;
            if (entity == null)
            {
                return null;
            }

            var state = new EntityState
            {
                Handle = entity.Handle.ToString("X"),
                Space = SpaceNameOf(hit),
                DxfType = EntityFilter.DxfNameOf(entity),
                Layer = entity.Layer == null ? string.Empty : entity.Layer.Name,
                Color = entity.Color.Index.ToString(CultureInfo.InvariantCulture),
                Linetype = entity.LineType == null ? string.Empty : entity.LineType.Name,
                LineWeight = ((int)entity.LineWeight).ToString(CultureInfo.InvariantCulture)
            };

            var text = entity as TextEntity;
            if (text != null)
            {
                state.Text = Trim(text.Value);
                state.Height = text.Height.ToString("0.####", CultureInfo.InvariantCulture);
                state.Style = text.Style == null ? string.Empty : text.Style.Name;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                state.Text = Trim(mtext.Value);
                state.Height = mtext.Height.ToString("0.####", CultureInfo.InvariantCulture);
                state.Style = mtext.Style == null ? string.Empty : mtext.Style.Name;
            }

            var dim = entity as Dimension;
            if (dim != null)
            {
                state.Text = Trim(dim.Text);
                state.Style = dim.Style == null ? string.Empty : dim.Style.Name;
            }

            var hatch = entity as Hatch;
            if (hatch != null)
            {
                state.Hatch = string.Format("{0}|{1:0.####}|{2:0.####}",
                    hatch.Pattern == null ? "(solid)" : hatch.Pattern.Name,
                    hatch.PatternScale,
                    hatch.PatternAngle);
            }

            var table = entity as TableEntity;
            if (table != null)
            {
                state.Text = string.Format("<表格 {0}×{1}>", table.Rows.Count, table.Columns.Count);
            }

            return state;
        }

        private static string SpaceNameOf(ScanHit hit)
        {
            if (hit.Owner == null)
            {
                return string.Empty;
            }

            if (hit.Depth == 0)
            {
                return hit.Owner.Layout != null ? hit.Owner.Layout.Name : hit.Owner.Name;
            }

            return "块:" + hit.Owner.Name;
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length > 160 ? value.Substring(0, 160) + "…" : value;
        }

        // ------------------------------------------------------------------ diff

        /// <summary>
        /// 对比两份快照，产出差异行。只输出**真的变了**的实体。
        /// </summary>
        public static List<ChangeRecord> Diff(
            Dictionary<string, EntityState> before,
            Dictionary<string, EntityState> after)
        {
            var records = new List<ChangeRecord>();
            if (before == null || after == null)
            {
                return records;
            }

            foreach (KeyValuePair<string, EntityState> pair in after)
            {
                EntityState now = pair.Value;

                EntityState old;
                if (!before.TryGetValue(pair.Key, out old))
                {
                    records.Add(new ChangeRecord
                    {
                        Handle = now.Handle,
                        Space = now.Space,
                        DxfType = now.DxfType,
                        Operation = "新增实体",
                        NewValue = now.DxfType,
                        Status = ChangeStatus.WillChange
                    });
                    continue;
                }

                var oldParts = new List<string>();
                var newParts = new List<string>();

                Compare(oldParts, newParts, "图层", old.Layer, now.Layer);
                Compare(oldParts, newParts, "颜色", old.Color, now.Color);
                Compare(oldParts, newParts, "线型", old.Linetype, now.Linetype);
                Compare(oldParts, newParts, "线宽", old.LineWeight, now.LineWeight);
                Compare(oldParts, newParts, "文字样式", old.Style, now.Style);
                Compare(oldParts, newParts, "字高", old.Height, now.Height);
                Compare(oldParts, newParts, "文字", old.Text, now.Text);
                Compare(oldParts, newParts, "填充", old.Hatch, now.Hatch);

                if (newParts.Count == 0)
                {
                    continue;
                }

                records.Add(new ChangeRecord
                {
                    Handle = now.Handle,
                    Space = now.Space,
                    DxfType = now.DxfType,
                    Operation = "修改",
                    OldValue = string.Join("；", oldParts.ToArray()),
                    NewValue = string.Join("；", newParts.ToArray()),
                    Status = ChangeStatus.WillChange
                });
            }

            foreach (KeyValuePair<string, EntityState> pair in before)
            {
                if (after.ContainsKey(pair.Key))
                {
                    continue;
                }

                records.Add(new ChangeRecord
                {
                    Handle = pair.Value.Handle,
                    Space = pair.Value.Space,
                    DxfType = pair.Value.DxfType,
                    Operation = "删除实体",
                    OldValue = pair.Value.DxfType,
                    Status = ChangeStatus.WillChange
                });
            }

            return records;
        }

        private static void Compare(List<string> oldParts, List<string> newParts, string label, string oldValue, string newValue)
        {
            oldValue = oldValue ?? string.Empty;
            newValue = newValue ?? string.Empty;
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return;
            }

            oldParts.Add(label + "=" + Shorten(oldValue));
            newParts.Add(label + "=" + Shorten(newValue));
        }

        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(空)";
            }

            return value.Length > 60 ? value.Substring(0, 60) + "…" : value;
        }

        /// <summary>把差异行渲染成等宽文本表（Markdown 用）。</summary>
        public static string ToMarkdownTable(IEnumerable<ChangeRecord> records, int maxRows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 规则 | 文件 | 空间 | 句柄 | 类型 | 操作 | 旧值 → 新值 | 状态 | 备注 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

            int n = 0;
            foreach (ChangeRecord r in records)
            {
                if (maxRows > 0 && n >= maxRows)
                {
                    sb.AppendLine(string.Format("| … | | | | | | 另有 {0} 行未列出 | | |", "…"));
                    break;
                }

                string change = string.IsNullOrEmpty(r.OldValue)
                    ? (r.NewValue ?? string.Empty)
                    : (r.OldValue + " → " + (r.NewValue ?? string.Empty));

                sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |",
                    Escape(r.RuleName),
                    Escape(System.IO.Path.GetFileName(r.File)),
                    Escape(r.Space),
                    Escape(r.Handle),
                    Escape(r.DxfType),
                    Escape(r.Operation),
                    Escape(change),
                    r.StatusText,
                    Escape(r.Degraded ? "降级：" + r.Note : r.Note));
                sb.AppendLine();
                n++;
            }

            return sb.ToString();
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
