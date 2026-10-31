using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 批量替换文字内容。覆盖 TEXT / MTEXT / ATTRIB（含嵌套块递归）/ ACAD_TABLE 单元格；
    /// DIMENSION 只在显式打开时处理，且只改**显式文字覆盖**，测量值永远不动。
    ///
    /// 几个刻意的取舍：
    ///   * **MTEXT 必须走 tokenizer**。实测 <c>MText.PlainText</c> 会把 <c>\l</c>、<c>\T1;</c>
    ///     的碎片留在结果里（<c>{\l\f...;\T1;CONNECT TO}</c> → <c>lT1;CONNECT TO</c>），
    ///     拿它当匹配源会替换到错误位置，写回也无处安放；
    ///   * **ATTRIB 不在块定义里**，它挂在 <c>Insert.Attributes</c> 上。所以"递归块定义"
    ///     并不等于"能拿到属性"，必须单独遍历块参照；
    ///   * **表格单元格**只处理 <c>CadValue.Value is string</c> 的格：字段（Field）、块内容的格
    ///     不是字符串，跳过——改它们等于破坏表格公式；
    ///   * 替换后为空默认拒绝（<c>AllowEmptyResult</c>），避免一条写错的规则把整批文字清空。
    /// </summary>
    public sealed class TextReplaceRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextReplace"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            string error;
            TextMatcher matcher = TextMatcher.Create(
                action.MatchMode,
                action.SearchText,
                action.Replacement,
                RuleContext.Bool(action.CaseSensitive, false),
                out error);

            if (matcher == null)
            {
                throw new RuleValidationException(error);
            }

            if (matcher.WholeWordDegraded)
            {
                context.Log.Warn("MatchMode=WholeWord 但查找内容含中文：中文没有空格分词，已降级为 Contains。");
            }

            bool allowEmpty = RuleContext.Bool(action.AllowEmptyResult, false);
            bool includeInvisible = RuleContext.Bool(action.IncludeInvisibleAttributes, false);
            bool tables = RuleContext.Bool(action.ReplaceInTables, true);
            bool dimensionText = RuleContext.Bool(action.ReplaceDimensionText, false);

            ReplaceTextEntities(context, result, matcher, allowEmpty, tables, dimensionText);
            ReplaceAttributes(context, result, matcher, allowEmpty, includeInvisible);

            if (result.Changed == 0 && result.Failed == 0)
            {
                result.AddNote("没有任何文字被替换。先确认查找内容、大小写、以及实体是否在扫描范围内（可用 inspect 看类型分布）。");
            }
        }

        // ------------------------------------------------------------------ TEXT / MTEXT / 表格 / 标注

        private static void ReplaceTextEntities(
            RuleContext context,
            RuleExecutionResult result,
            TextMatcher matcher,
            bool allowEmpty,
            bool tables,
            bool dimensionText)
        {
            RuleFilter filter = context.Filter;

            // 没写实体类型时，默认只碰文字类，避免"一条替换规则把整张图扫一遍"
            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                var types = new List<string> { "TEXT", "MTEXT" };
                if (tables)
                {
                    types.Add("ACAD_TABLE");
                }

                if (dimensionText)
                {
                    types.Add("DIMENSION");
                }

                filter.EntityTypes = types;
            }

            EntityFilter entityFilter = EntityFilter.Build(filter);

            foreach (ScanHit hit in EntityScanner.Scan(context, entityFilter))
            {
                Entity entity = hit.Entity;
                result.Hit();

                if (EntityScanner.ShouldSkipLocked(context, entity))
                {
                    result.MarkSkipped("锁定图层");
                    context.RecordSkip(entity, "替换文字", "图层被锁定");
                    continue;
                }

                string spaceName = EntityScanner.SpaceNameOf(hit);

                var table = entity as TableEntity;
                if (table != null)
                {
                    if (!tables)
                    {
                        result.MarkSkipped("表格未启用");
                        continue;
                    }

                    ReplaceInTable(context, result, table, matcher, allowEmpty, spaceName);
                    continue;
                }

                var dimension = entity as Dimension;
                if (dimension != null)
                {
                    ReplaceInDimension(context, result, dimension, matcher, allowEmpty, spaceName);
                    continue;
                }

                var mtext = entity as MText;
                if (mtext != null)
                {
                    string replaced;
                    string reason;
                    if (MTextReplacer.TryReplace(mtext.Value, matcher, allowEmpty, out replaced, out reason))
                    {
                        string before = mtext.Value;
                        mtext.Value = replaced;
                        result.MarkChanged("MTEXT");
                        context.RecordChange(mtext, "替换文字", Trim(before), Trim(replaced), false, null, spaceName);
                    }
                    else if (!string.IsNullOrEmpty(reason))
                    {
                        result.MarkSkipped(reason);
                        context.RecordSkip(mtext, "替换文字", reason, false, spaceName);
                    }
                    else
                    {
                        result.MarkSkipped("未匹配");
                    }

                    continue;
                }

                var text = entity as TextEntity;
                if (text != null)
                {
                    string before = text.Value ?? string.Empty;
                    string replaced;
                    if (!matcher.TryReplaceAll(before, out replaced))
                    {
                        result.MarkSkipped("未匹配");
                        continue;
                    }

                    if (!allowEmpty && string.IsNullOrWhiteSpace(replaced))
                    {
                        result.MarkSkipped("替换后为空");
                        context.RecordSkip(text, "替换文字", "替换后为空，已跳过（AllowEmptyResult=false）", false, spaceName);
                        continue;
                    }

                    text.Value = replaced;
                    result.MarkChanged("TEXT");
                    context.RecordChange(text, "替换文字", Trim(before), Trim(replaced), false, null, spaceName);
                }
            }
        }

        private static void ReplaceInTable(
            RuleContext context,
            RuleExecutionResult result,
            TableEntity table,
            TextMatcher matcher,
            bool allowEmpty,
            string spaceName)
        {
            int changedCells = 0;
            int lockedCells = 0;

            foreach (TableEntity.Row row in table.Rows)
            {
                foreach (TableEntity.Cell cell in row.Cells)
                {
                    if ((cell.StateFlags & TableEntity.TableCellStateFlags.ContentLocked) != 0)
                    {
                        lockedCells++;
                        continue;
                    }

                    foreach (TableEntity.CellContent content in cell.Contents)
                    {
                        if (content == null || content.CadValue == null)
                        {
                            continue;
                        }

                        // 字段 / 块内容不是字符串：改了会破坏表格公式，直接跳过
                        string value = content.CadValue.Value as string;
                        if (value == null)
                        {
                            continue;
                        }

                        string replaced;
                        if (!matcher.TryReplaceAll(value, out replaced))
                        {
                            continue;
                        }

                        if (!allowEmpty && string.IsNullOrWhiteSpace(replaced))
                        {
                            continue;
                        }

                        content.CadValue.SetValue(replaced);
                        changedCells++;
                    }
                }
            }

            if (changedCells > 0)
            {
                result.MarkChanged("ACAD_TABLE");
                context.RecordChange(table, "替换表格单元格",
                    string.Format("{0}×{1}", table.Rows.Count, table.Columns.Count),
                    string.Format("{0} 格已改", changedCells),
                    true,
                    "表格缓存块不随单元格数据更新：查看端可能仍显示旧值（纯文件模式的限制）",
                    spaceName);
            }
            else
            {
                result.MarkSkipped(lockedCells > 0 ? "表格无可改单元格（部分被锁定）" : "表格未匹配");
            }
        }

        private static void ReplaceInDimension(
            RuleContext context,
            RuleExecutionResult result,
            Dimension dimension,
            TextMatcher matcher,
            bool allowEmpty,
            string spaceName)
        {
            string before = dimension.Text;

            // 测量值（Text 为空或 "<>"）永远不改：那是几何算出来的，改了也不显示
            if (string.IsNullOrEmpty(before) || before == "<>")
            {
                result.MarkSkipped("测量值文字（不改）");
                context.RecordSkip(dimension, "替换标注文字", "该标注没有显式文字覆盖，测量值不替换", false, spaceName);
                return;
            }

            string replaced;
            if (!matcher.TryReplaceAll(before, out replaced))
            {
                result.MarkSkipped("未匹配");
                return;
            }

            if (!allowEmpty && string.IsNullOrWhiteSpace(replaced))
            {
                result.MarkSkipped("替换后为空");
                return;
            }

            dimension.Text = replaced;
            result.MarkChanged("DIMENSION");
            context.RecordChange(dimension, "替换标注文字", Trim(before), Trim(replaced), true,
                "只改标注的显式文字；显示缓存块不同步，需重新生成标注后才会刷新", spaceName);
        }

        // ------------------------------------------------------------------ ATTRIB

        private static void ReplaceAttributes(
            RuleContext context,
            RuleExecutionResult result,
            TextMatcher matcher,
            bool allowEmpty,
            bool includeInvisible)
        {
            // ATTRIB 挂在块参照上，不在块定义里——必须单独扫一遍 INSERT
            var insertFilter = new RuleFilter { EntityTypes = new List<string> { "INSERT" } };
            EntityFilter filter = EntityFilter.Build(insertFilter);

            int invisibleSkipped = 0;

            foreach (ScanHit hit in EntityScanner.Scan(context, filter))
            {
                var insert = hit.Entity as Insert;
                if (insert == null || insert is TableEntity || insert.Attributes == null)
                {
                    continue;
                }

                foreach (AttributeEntity attribute in insert.Attributes)
                {
                    if (attribute == null)
                    {
                        continue;
                    }

                    if (attribute.IsInvisible && !includeInvisible)
                    {
                        invisibleSkipped++;
                        continue;
                    }

                    result.Hit();

                    string before = attribute.Value ?? string.Empty;
                    string replaced;
                    if (!matcher.TryReplaceAll(before, out replaced))
                    {
                        result.MarkSkipped("属性未匹配");
                        continue;
                    }

                    if (!allowEmpty && string.IsNullOrWhiteSpace(replaced))
                    {
                        result.MarkSkipped("替换后为空");
                        continue;
                    }

                    attribute.Value = replaced;
                    result.MarkChanged("ATTRIB");
                    context.RecordChange(attribute, "替换属性",
                        attribute.Tag + "=" + Trim(before),
                        attribute.Tag + "=" + Trim(replaced), false, null,
                        EntityScanner.SpaceNameOf(hit));
                }
            }

            if (invisibleSkipped > 0)
            {
                result.AddNote(string.Format("另有 {0} 个不可见属性未处理（IncludeInvisibleAttributes=false）。", invisibleSkipped));
            }
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(空)";
            }

            return value.Length > 80 ? value.Substring(0, 80) + "…" : value;
        }
    }
}
