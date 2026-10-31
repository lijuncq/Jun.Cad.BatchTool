using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 标题栏属性写入：把图号、图名、版次这些属性按表批量写进图框块。
    ///
    /// 与 `AttributeAudit` 的分工：审计只查不写；这条规则负责**写**。
    /// 默认 `TitleBlockOnlyIfEmpty=true`——已有值不覆盖，避免把人工填好的图号刷掉。
    ///
    /// 值支持四个占位符（都是文件/布局级信息，不需要人工逐张填）：
    ///   `{filename}` 源文件名（不含扩展名）、`{layout}` 所在布局名、
    ///   `{date}` 当天日期、`{index}` 本次命中的第几个图框（从 1 开始）。
    /// </summary>
    public sealed class TitleBlockNormalizeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TitleBlockNormalize"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            List<KeyValuePair<string, string>> map = ParseMap(action.TitleBlockAttributeMap);
            if (map.Count == 0)
            {
                throw new RuleValidationException(
                    "Action.TitleBlockAttributeMap 为空。写法：\"TAG=值\"，值可用 {filename} {layout} {date} {index}。");
            }

            bool onlyIfEmpty = RuleContext.Bool(action.TitleBlockOnlyIfEmpty, true);

            // 没有块名模式时 Wildcard.IsMatch(x, null) 恒真 —— 会把属性写进图上**每一个**带该 Tag 的块。
            // 其它写操作规则在缺少选择条件时都会主动中止，这条也必须一致。
            if (string.IsNullOrWhiteSpace(context.Filter.SourceBlockName)
                && string.IsNullOrWhiteSpace(context.Filter.ContainerBlockPattern))
            {
                throw new RuleValidationException(
                    "TitleBlockNormalize 必须指定 Filter.SourceBlockName（图框块名模式，可用通配符）"
                    + "或 Filter.ContainerBlockPattern，否则会把属性写进所有带同名 Tag 的块。");
            }

            string fileName = string.IsNullOrEmpty(context.FileLabel)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(context.FileLabel);

            var insertFilter = new RuleFilter { EntityTypes = new List<string> { "INSERT" } };
            if (context.Filter != null)
            {
                insertFilter.ContainerBlockPattern = context.Filter.ContainerBlockPattern;
                insertFilter.ContainerAttributeTag = context.Filter.ContainerAttributeTag;
                insertFilter.ContainerAttributePattern = context.Filter.ContainerAttributePattern;
            }

            int index = 0;
            int written = 0;
            int skippedFilled = 0;
            int missingTags = 0;

            foreach (ScanHit hit in EntityScanner.Scan(context, EntityFilter.Build(insertFilter)))
            {
                var insert = hit.Entity as Insert;
                if (insert == null || insert is TableEntity)
                {
                    continue;
                }

                string blockName = EntityFilter.EffectiveBlockName(insert.Block);
                if (!Wildcard.IsMatch(blockName, context.Filter.SourceBlockName))
                {
                    continue;
                }

                if (insert.Attributes == null || insert.Attributes.Count == 0)
                {
                    continue;
                }

                index++;
                string spaceName = EntityScanner.SpaceNameOf(hit);

                foreach (KeyValuePair<string, string> pair in map)
                {
                    AttributeEntity attribute = FindAttribute(insert, pair.Key);
                    if (attribute == null)
                    {
                        missingTags++;
                        continue;
                    }

                    if (onlyIfEmpty && !string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        skippedFilled++;
                        result.Hit();
                        result.MarkSkipped("已有值 " + pair.Key);
                        continue;
                    }

                    string value = Expand(pair.Value, fileName, spaceName, index);
                    if (string.Equals(attribute.Value, value, StringComparison.Ordinal))
                    {
                        result.Hit();
                        result.MarkSkipped("已是目标值 " + pair.Key);
                        continue;
                    }

                    result.Hit();
                    string before = attribute.Value;
                    attribute.Value = value;
                    written++;

                    result.MarkChanged("属性 " + pair.Key);
                    context.RecordChange(attribute, "写标题栏",
                        pair.Key + "=" + (string.IsNullOrEmpty(before) ? "(空)" : before),
                        pair.Key + "=" + value, false, null, spaceName);
                }
            }

            result.AddNote(string.Format(
                "标题栏写入：处理 {0} 个图框，写入 {1} 处，已有值跳过 {2} 处，缺少属性 {3} 处。",
                index, written, skippedFilled, missingTags));

            if (missingTags > 0)
            {
                result.AddNote("缺少属性的图框不会被创建新属性——属性定义在块里，新增实例级属性需要改块定义，本规则不做。");
            }
        }

        /// <summary>展开占位符。</summary>
        public static string Expand(string template, string fileName, string layout, int index)
        {
            if (template == null)
            {
                return string.Empty;
            }

            return template
                .Replace("{filename}", fileName ?? string.Empty)
                .Replace("{layout}", layout ?? string.Empty)
                .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Replace("{index}", index.ToString(CultureInfo.InvariantCulture));
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

        public static List<KeyValuePair<string, string>> ParseMap(List<string> raw)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (raw == null)
            {
                return list;
            }

            foreach (string item in raw)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                foreach (string part in item.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int index = part.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    string key = part.Substring(0, index).Trim();
                    string value = part.Substring(index + 1).Trim();
                    if (key.Length > 0)
                    {
                        list.Add(new KeyValuePair<string, string>(key, value));
                    }
                }
            }

            return list;
        }
    }
}
