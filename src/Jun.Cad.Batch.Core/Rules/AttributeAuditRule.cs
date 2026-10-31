using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 必填属性审计（默认只报告，可选按默认值填充）。
    ///
    /// 场景：图框/标题栏的图号、版次、项目号、设计人这些属性，漏填在出图前很难发现，
    /// 但审图一定会被打回来。这条规则按 Tag 清单逐个块参照检查。
    ///
    /// 注意：属性挂在 `Insert.Attributes` 上，不在块定义里——"递归块定义"拿不到它，
    /// 必须单独遍历块参照（实测本机样本图 ATTDEF=10 但块定义内 ATTRIB=0，
    /// 真实属性全在实例上）。
    /// </summary>
    public sealed class AttributeAuditRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "AttributeAudit"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;

            List<string> required = CleanTags(action.RequiredAttributeTags);
            if (required.Count == 0)
            {
                throw new RuleValidationException("Action.RequiredAttributeTags 至少要填一个属性 Tag。");
            }

            bool fill = RuleContext.Bool(action.AttributeAuditFillDefaults, false);
            Dictionary<string, string> defaults = ParseDefaults(action.AttributeDefaults);

            var insertFilter = new RuleFilter { EntityTypes = new List<string> { "INSERT" } };
            if (context.Filter != null && context.Filter.ContainerBlockPattern != null)
            {
                insertFilter.ContainerBlockPattern = context.Filter.ContainerBlockPattern;
                insertFilter.ContainerAttributeTag = context.Filter.ContainerAttributeTag;
                insertFilter.ContainerAttributePattern = context.Filter.ContainerAttributePattern;
            }

            int missing = 0;
            int empty = 0;
            int filled = 0;
            var notes = new List<string>();

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

                foreach (string tag in required)
                {
                    AttributeEntity attribute = FindAttribute(insert, tag);

                    if (attribute == null)
                    {
                        missing++;
                        result.Hit();
                        result.MarkSkipped("缺少属性 " + tag);
                        context.RecordSkip(insert, "属性审计",
                            string.Format("块 {0} 缺少属性 {1}", blockName, tag), false,
                            EntityScanner.SpaceNameOf(hit));

                        if (notes.Count < 30)
                        {
                            notes.Add(string.Format("#{0} {1}：缺少 {2}", insert.Handle.ToString("X"), blockName, tag));
                        }

                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        result.Hit();
                        result.MarkSkipped("已填 " + tag);
                        continue;
                    }

                    empty++;
                    result.Hit();

                    string fallback;
                    if (fill && defaults.TryGetValue(tag, out fallback))
                    {
                        attribute.Value = fallback;
                        filled++;
                        result.MarkChanged("填充属性 " + tag);
                        context.RecordChange(attribute, "填充属性",
                            tag + "=(空)", tag + "=" + fallback, false,
                            "按 Action.AttributeDefaults 填充", EntityScanner.SpaceNameOf(hit));
                        continue;
                    }

                    result.MarkSkipped("空属性 " + tag);
                    context.RecordSkip(insert, "属性审计",
                        string.Format("块 {0} 的属性 {1} 为空", blockName, tag), false,
                        EntityScanner.SpaceNameOf(hit));

                    if (notes.Count < 30)
                    {
                        notes.Add(string.Format("#{0} {1}：{2} 为空", insert.Handle.ToString("X"), blockName, tag));
                    }
                }
            }

            foreach (string note in notes)
            {
                result.AddNote(note);
            }

            result.AddNote(string.Format(
                "必填属性审计：缺失 {0}，空值 {1}，已填充 {2}（共检查 {3} 个 Tag）。",
                missing, empty, filled, required.Count));
        }

        private static AttributeEntity FindAttribute(Insert insert, string tag)
        {
            if (insert.Attributes == null)
            {
                return null;
            }

            foreach (AttributeEntity attribute in insert.Attributes)
            {
                if (attribute != null
                    && string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute;
                }
            }

            return null;
        }

        private static List<string> CleanTags(List<string> tags)
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

        /// <summary>解析 <c>"TAG=值;TAG2=值2"</c>（分号或换行分隔）。</summary>
        public static Dictionary<string, string> ParseDefaults(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                return map;
            }

            string[] parts = text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
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
                    map[key] = value;
                }
            }

            return map;
        }
    }
}
