using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 材料表导出与核对（**只读，不改图**）。
    ///
    /// 只做"属性块形式的材料表"：按 Tag 清单把每个材料表行的属性导成 CSV，
    /// 再和外部清单（Excel 另存的 CSV）比对，报"多了/少了/改了"。
    ///
    /// 为什么不做 ACAD_TABLE：单元格写回在本机没有任何样图可回归（前置检查里一直显示"未测"），
    /// 拿未验证的能力去改材料表风险太高。表格的导出可以等有样图后再加。
    ///
    /// 导出是**追加**模式：一批图跑下来所有行落到同一个 CSV，表头只在文件不存在时写一次。
    /// 预览模式（`--preview`）不写文件——预览只应该"看"，不应该产生副作用。
    /// </summary>
    public sealed class BomSyncRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "BomSync"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            List<string> columns = CleanColumns(action.BomColumnTags);
            if (columns.Count == 0)
            {
                throw new RuleValidationException("Action.BomColumnTags 至少要填一个属性 Tag（第一项作为主键）。");
            }

            var insertFilter = new RuleFilter { EntityTypes = new List<string> { "INSERT" } };
            var rows = new List<List<string>>();

            foreach (ScanHit hit in EntityScanner.Scan(context, EntityFilter.Build(insertFilter)))
            {
                var insert = hit.Entity as Insert;
                if (insert == null || insert is TableEntity || insert.Attributes == null)
                {
                    continue;
                }

                string blockName = EntityFilter.EffectiveBlockName(insert.Block);
                if (!Wildcard.IsMatch(blockName, context.Filter.SourceBlockName))
                {
                    continue;
                }

                result.Hit();

                var row = new List<string>(columns.Count + 1);
                foreach (string tag in columns)
                {
                    AttributeEntity attribute = FindAttribute(insert, tag);
                    row.Add(attribute == null ? string.Empty : (attribute.Value ?? string.Empty));
                }

                if (row.All(string.IsNullOrWhiteSpace))
                {
                    result.MarkSkipped("整行为空");
                    continue;
                }

                rows.Add(row);
                result.MarkSkipped("已读取 " + blockName);
                context.RecordSkip(insert, "材料表读取",
                    string.Join(" / ", row.ToArray()), false, EntityScanner.SpaceNameOf(hit));
            }

            if (rows.Count == 0)
            {
                result.AddNote("没有找到材料表块（检查 Filter.SourceBlockName 与 Action.BomColumnTags）。");
                return;
            }

            bool preview = context.Preview != null;

            // ---- 导出 ----
            string exportPath = action.BomExportPath;
            if (!string.IsNullOrWhiteSpace(exportPath))
            {
                if (preview)
                {
                    result.AddNote(string.Format("预览模式：跳过写 CSV（本应追加 {0} 行到 {1}）。", rows.Count, exportPath));
                }
                else
                {
                    try
                    {
                        AppendCsv(exportPath, columns, rows);
                        result.AddNote(string.Format("已追加 {0} 行到 {1}。", rows.Count, Path.GetFullPath(exportPath)));
                    }
                    catch (Exception ex)
                    {
                        result.MarkFailed("写 CSV 失败：" + ex.Message);
                    }
                }
            }

            // ---- 比对 ----
            string comparePath = action.BomComparePath;
            if (!string.IsNullOrWhiteSpace(comparePath))
            {
                CompareWith(context, result, comparePath, columns, rows);
            }

            result.AddNote(string.Format("材料表：读取 {0} 行 × {1} 列。", rows.Count, columns.Count));
        }

        private static void CompareWith(
            RuleContext context,
            RuleExecutionResult result,
            string comparePath,
            List<string> columns,
            List<List<string>> rows)
        {
            if (!File.Exists(comparePath))
            {
                result.AddNote("比对基准 CSV 不存在，跳过比对：" + comparePath);
                return;
            }

            Dictionary<string, string[]> baseline;
            try
            {
                baseline = ReadCsv(comparePath, columns[0]);
            }
            catch (Exception ex)
            {
                result.MarkFailed("读基准 CSV 失败：" + ex.Message);
                return;
            }

            var current = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (List<string> row in rows)
            {
                string key = row.Count > 0 ? row[0] : string.Empty;
                if (!string.IsNullOrEmpty(key))
                {
                    current[key] = row.ToArray();
                }
            }

            int added = 0;
            int missing = 0;
            int changed = 0;

            foreach (KeyValuePair<string, string[]> pair in current)
            {
                string[] other;
                if (!baseline.TryGetValue(pair.Key, out other))
                {
                    added++;
                    result.AddNote("基准里没有：" + pair.Key);
                    continue;
                }

                string a = string.Join("|", pair.Value);
                string b = string.Join("|", other);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    changed++;
                    result.AddNote(string.Format("内容不同：{0}\n    图内：{1}\n    基准：{2}",
                        pair.Key, a, b));
                }
            }

            foreach (KeyValuePair<string, string[]> pair in baseline)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    missing++;
                    result.AddNote("图里没有：" + pair.Key);
                }
            }

            result.AddNote(string.Format(
                "与基准比对：图内 {0} 行，新增 {1}，缺失 {2}，内容不同 {3}。",
                current.Count, added, missing, changed));
        }

        private static void AppendCsv(string path, List<string> columns, List<List<string>> rows)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

            // UTF-8 带 BOM：Excel 直接双击打开中文不乱码
            using (var writer = new StreamWriter(path, true, new UTF8Encoding(true)))
            {
                if (writeHeader)
                {
                    writer.WriteLine(string.Join(",", columns.Select(Escape).ToArray()));
                }

                foreach (List<string> row in rows)
                {
                    writer.WriteLine(string.Join(",", row.Select(Escape).ToArray()));
                }
            }
        }

        /// <summary>
        /// 读基准 CSV。
        ///
        /// 表头**按内容判断**，不按"第一行就是表头"：第一行第一个单元格等于主键列名才算表头。
        /// 否则手工整理的无表头 CSV 会丢掉第一行（实测踩过：A-101 整行被当成表头吃掉）。
        /// </summary>
        private static Dictionary<string, string[]> ReadCsv(string path, string keyColumn)
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            int start = 0;
            if (lines.Length > 0)
            {
                string[] first = SplitCsv(lines[0].TrimStart('\uFEFF'));
                if (first.Length > 0 && string.Equals(first[0].Trim(), keyColumn, StringComparison.OrdinalIgnoreCase))
                {
                    start = 1;
                }
            }

            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] cells = SplitCsv(lines[i].TrimStart('\uFEFF'));
                if (cells.Length == 0)
                {
                    continue;
                }

                map[cells[0]] = cells;
            }

            return map;
        }

        /// <summary>够用的 CSV 解析：支持双引号包裹与转义的双引号。</summary>
        private static string[] SplitCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    cells.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(c);
                }
            }

            cells.Add(sb.ToString());
            return cells.ToArray();
        }

        private static string Escape(string value)
        {
            value = value ?? string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static List<string> CleanColumns(List<string> tags)
        {
            var list = new List<string>();
            if (tags == null)
            {
                return list;
            }

            foreach (string tag in tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    list.Add(tag.Trim());
                }
            }

            return list;
        }

        private static AttributeEntity FindAttribute(Insert insert, string tag)
        {
            foreach (AttributeEntity attribute in insert.Attributes)
            {
                if (attribute != null && string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute;
                }
            }

            return null;
        }
    }
}
