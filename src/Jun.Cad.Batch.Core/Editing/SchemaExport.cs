using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>
    /// 把"配置契约"导出成机器可读的 schema（JSON）和人类可读的字段表（Markdown）。
    ///
    /// 为什么需要它：配置的消费者有五个——文档、界面、校验、摘要、legacy 兼容。
    /// 之前它们各读一份反射面，于是同一件事有五份说法（默认值写三遍、字段名对不上、
    /// 状态词表八种写法）。schema 是这五份的**单一真源**：文档的字段表由它生成，
    /// 界面的控件由同一份 <see cref="RuleFieldAttribute"/> 生成，校验读同一份标注。
    ///
    /// 它不替代 `BatchConfig.cs`：schema 是从属性标注**导出**的，不是另一份手写清单。
    /// 新增字段只改属性标注，schema 与文档自动跟上。
    /// </summary>
    public static class SchemaExport
    {
        /// <summary>schema 版本：字段语义变了就加一，便于下游判断兼容性。</summary>
        public const int SchemaVersion = 1;

        /// <summary>一条字段的导出形态。</summary>
        public sealed class FieldSchema
        {
            public string Name { get; set; }
            public string Label { get; set; }
            public string Editor { get; set; }
            public string Unit { get; set; }
            public string Values { get; set; }
            public bool Required { get; set; }
            public bool EngineLevel { get; set; }
            public string Help { get; set; }
        }

        /// <summary>一种规则类型的导出形态。</summary>
        public sealed class RuleSchema
        {
            public string Type { get; set; }
            public List<FieldSchema> Filter { get; set; }
            public List<FieldSchema> Action { get; set; }
        }

        /// <summary>顶层配置字段（从 <see cref="BatchConfig"/> 的属性导出）。</summary>
        public sealed class TopLevelSchema
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Default { get; set; }
        }

        public static List<TopLevelSchema> TopLevel()
        {
            var list = new List<TopLevelSchema>();
            var defaults = new BatchConfig();

            foreach (PropertyInfo property in typeof(BatchConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var json = (Newtonsoft.Json.JsonPropertyAttribute)Attribute.GetCustomAttribute(
                    property, typeof(Newtonsoft.Json.JsonPropertyAttribute));

                if (json == null || string.IsNullOrEmpty(json.PropertyName))
                {
                    continue;
                }

                string text = string.Empty;
                try
                {
                    object value = property.GetValue(defaults, null);
                    text = Describe(value);
                }
                catch
                {
                    text = string.Empty;
                }

                list.Add(new TopLevelSchema
                {
                    Name = json.PropertyName,
                    Type = FriendlyType(property.PropertyType),
                    Default = text
                });
            }

            return list;
        }

        /// <summary>所有已注册规则类型的字段契约。</summary>
        public static List<RuleSchema> Rules()
        {
            RuleFactory.RegisterBuiltIns();

            var list = new List<RuleSchema>();

            foreach (string type in RuleFactory.GetRegisteredTypes())
            {
                var schema = new RuleSchema
                {
                    Type = type,
                    Filter = new List<FieldSchema>(),
                    Action = new List<FieldSchema>()
                };

                foreach (FieldDescriptor field in FieldCatalog.ForType(type))
                {
                    RuleFieldAttribute meta = field.Meta;
                    if (meta == null)
                    {
                        continue;
                    }

                    var item = new FieldSchema
                    {
                        Name = field.Property.Name,
                        Label = field.Label,
                        Editor = meta.Editor.ToString(),
                        Unit = meta.Unit ?? string.Empty,
                        Values = meta.Values ?? string.Empty,
                        Required = meta.Required,
                        EngineLevel = IsEngineLevel(meta),
                        Help = meta.Help ?? string.Empty
                    };

                    if (field.IsFilter)
                    {
                        schema.Filter.Add(item);
                    }
                    else
                    {
                        schema.Action.Add(item);
                    }
                }

                list.Add(schema);
            }

            return list;
        }

        /// <summary>
        /// schema 自检：字段标注与规则注册表是否对得上。
        ///
        /// 检查两件事：
        ///   1. `AppliesTo` 里写的类型名必须已注册——写错的话那个字段在界面上永远不出现，
        ///      而且不会报错（`FieldCatalog` 只是按名字过滤，匹配不到就当没有）；
        ///   2. 每个已注册类型至少要有一个字段——否则界面上是一条点开只有空白的规则。
        ///
        /// 返回问题清单，空表示通过。供测试与 `schema` 自检使用。
        /// </summary>
        public static List<string> Validate()
        {
            var problems = new List<string>();

            RuleFactory.RegisterBuiltIns();
            var registered = new HashSet<string>(RuleFactory.GetRegisteredTypes(), StringComparer.OrdinalIgnoreCase);

            foreach (FieldDescriptor field in FieldCatalog.All())
            {
                RuleFieldAttribute meta = field.Meta;
                if (meta == null || string.IsNullOrWhiteSpace(meta.AppliesTo))
                {
                    problems.Add(string.Format("{0} 没有写 AppliesTo", field.Path));
                    continue;
                }

                if (IsEngineLevel(meta))
                {
                    continue;
                }

                foreach (string token in meta.AppliesTo.Split(','))
                {
                    string type = token.Trim();
                    if (type.Length == 0)
                    {
                        continue;
                    }

                    if (!registered.Contains(type))
                    {
                        problems.Add(string.Format("{0} 的 AppliesTo 写了未注册的类型「{1}」", field.Path, type));
                    }
                }
            }

            foreach (string type in registered)
            {
                if (FieldCatalog.ForType(type).Count == 0)
                {
                    problems.Add(string.Format("规则类型「{0}」没有任何字段", type));
                }
            }

            return problems;
        }

        public static string ToJson()
        {
            var model = new
            {
                schemaVersion = SchemaVersion,
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                topLevel = TopLevel(),
                ruleTypes = Rules()
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(model, Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// 生成 README 用的 Markdown 字段表。
        /// 只输出"该类型用得上"的字段——这正是它比手抄可靠的地方：手抄会漏，反射不会。
        /// </summary>
        public static string ToMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine("### 顶层字段");
            sb.AppendLine();
            sb.AppendLine("| 字段 | 类型 | 默认值 |");
            sb.AppendLine("|---|---|---|");
            foreach (TopLevelSchema item in TopLevel())
            {
                if (string.Equals(item.Name, "Rules", StringComparison.Ordinal))
                {
                    continue;
                }

                sb.AppendFormat("| `{0}` | {1} | {2} |", item.Name, item.Type,
                    string.IsNullOrEmpty(item.Default) ? "—" : "`" + item.Default + "`");
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("### 通用过滤字段（对所有规则生效）");
            sb.AppendLine();
            sb.AppendLine("| 字段 | 标签 | 类型 | 可选值 / 单位 | 说明 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (FieldSchema field in CommonFilterFields())
            {
                sb.AppendFormat("| `{0}` | {1} | {2} | {3} | {4} |",
                    field.Name, field.Label, field.Editor, ValuesOf(field), Escape(field.Help));
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("### 各规则专属字段");
            sb.AppendLine();
            sb.AppendLine("通用过滤字段不再重复列出；表里只有该类型**专属**的过滤条件与参数。");

            foreach (RuleSchema rule in Rules())
            {
                sb.AppendLine();
                sb.AppendFormat("#### `{0}`", rule.Type);
                sb.AppendLine();
                sb.AppendLine();

                var ownFilter = rule.Filter.Where(f => !f.EngineLevel).ToList();

                if (ownFilter.Count == 0)
                {
                    sb.AppendLine("过滤条件：无专属字段（用上面的通用过滤字段）。");
                }
                else
                {
                    sb.AppendLine("| 专属过滤字段 | 标签 | 类型 | 可选值 / 单位 | 说明 |");
                    sb.AppendLine("|---|---|---|---|---|");
                    foreach (FieldSchema field in ownFilter)
                    {
                        sb.AppendFormat("| `{0}` | {1} | {2} | {3} | {4} |",
                            field.Name, field.Label, field.Editor, ValuesOf(field), Escape(field.Help));
                        sb.AppendLine();
                    }
                }

                if (rule.Action.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| 参数 | 标签 | 类型 | 可选值 / 单位 | 说明 |");
                    sb.AppendLine("|---|---|---|---|---|");
                    foreach (FieldSchema field in rule.Action)
                    {
                        sb.AppendFormat("| `{0}` | {1} | {2} | {3} | {4} |",
                            field.Name, field.Label, field.Editor, ValuesOf(field), Escape(field.Help));
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>所有 `AppliesTo="*"` 的过滤字段（每个规则都能用，只列一次）。</summary>
        public static List<FieldSchema> CommonFilterFields()
        {
            var list = new List<FieldSchema>();

            foreach (FieldDescriptor field in FieldCatalog.All())
            {
                RuleFieldAttribute meta = field.Meta;
                if (meta == null || !field.IsFilter || !IsEngineLevel(meta))
                {
                    continue;
                }

                list.Add(new FieldSchema
                {
                    Name = field.Property.Name,
                    Label = field.Label,
                    Editor = meta.Editor.ToString(),
                    Unit = meta.Unit ?? string.Empty,
                    Values = meta.Values ?? string.Empty,
                    Required = meta.Required,
                    EngineLevel = true,
                    Help = meta.Help ?? string.Empty
                });
            }

            return list;
        }

        private static string ValuesOf(FieldSchema field)
        {
            if (!string.IsNullOrEmpty(field.Values))
            {
                return field.Values.Replace(";", "<br>");
            }

            if (!string.IsNullOrEmpty(field.Unit))
            {
                return field.Unit;
            }

            return "—";
        }

        /// <summary>`AppliesTo == "*"` 的字段对所有类型生效，属于引擎级通用字段。</summary>
        private static bool IsEngineLevel(RuleFieldAttribute meta)
        {
            return !string.IsNullOrEmpty(meta.AppliesTo)
                   && string.Equals(meta.AppliesTo.Trim(), "*", StringComparison.Ordinal);
        }

        private static string FriendlyType(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string)) return "string";
            if (underlying == typeof(bool)) return "bool";
            if (underlying == typeof(int)) return "int";
            if (underlying == typeof(double)) return "number";

            if (underlying.IsGenericType
                && (underlying.GetGenericTypeDefinition() == typeof(List<>)
                    || underlying.GetGenericTypeDefinition() == typeof(IList<>)))
            {
                return FriendlyType(underlying.GetGenericArguments()[0]) + "[]";
            }

            return underlying.Name;
        }

        private static string Describe(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var list = value as System.Collections.IEnumerable;
            if (list != null && !(value is string))
            {
                return "[]";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
