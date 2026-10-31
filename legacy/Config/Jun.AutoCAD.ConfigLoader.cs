using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jun.AutoCAD.Config
{
    /// <summary>
    /// 配置文件加载器：负责默认路径解析、反序列化、结构校验与示例生成。
    /// </summary>
    public static class ConfigLoader
    {
        public const string DefaultFileName = "config.json";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // 允许缺少字段、允许大小写不一致（JSON 中 SourceBlockName / sourceBlockName 都能识别）
            MissingMemberHandling = MissingMemberHandling.Ignore,
            // 允许把 "1" 转成 int、把 1 转成 double 之类
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            // 未知字段不报错，但收集起来给用户提示（通常是拼写错误）
            Error = (sender, args) =>
            {
                // 类型不匹配（例如把字符串写进 int 字段）时记录错误，交由上层决定是否终止
                if (args.ErrorContext.Error is JsonReaderException || args.ErrorContext.Error is JsonSerializationException)
                {
                    args.ErrorContext.Handled = true;
                }
            }
        };

        /// <summary>
        /// 默认配置文件路径查找顺序：
        ///   1. 插件 DLL 同目录 config.json
        ///   2. 当前图形所在目录 config.json
        ///   3. 我的文档\Jun.AutoCAD.BatchTool\config.json
        /// 返回第一个存在的文件；都不存在时返回 (1) 的路径（便于提示用户去那里放文件）。
        /// </summary>
        public static string ResolveDefaultConfigPath()
        {
            var candidates = GetDefaultConfigCandidates();
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return candidates[0];
        }

        /// <summary>返回所有候选默认路径（按优先级），供提示信息展示。</summary>
        public static IList<string> GetDefaultConfigCandidates()
        {
            var list = new List<string>();

            string asmDir = null;
            try
            {
                asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                // 某些沙箱环境下 Location 可能不可用，忽略
            }

            if (!string.IsNullOrEmpty(asmDir))
            {
                list.Add(Path.Combine(asmDir, DefaultFileName));
            }

            try
            {
                var doc = Core.HostCompat.DocumentManager == null
                    ? null
                    : Core.HostCompat.DocumentManager.MdiActiveDocument;
                if (doc != null && !string.IsNullOrEmpty(doc.Name))
                {
                    string dwgDir = Path.GetDirectoryName(doc.Name);
                    if (!string.IsNullOrEmpty(dwgDir))
                    {
                        list.Add(Path.Combine(dwgDir, DefaultFileName));
                    }
                }
            }
            catch
            {
                // 无文档上下文时忽略
            }

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
            {
                list.Add(Path.Combine(docs, "Jun.AutoCAD.BatchTool", DefaultFileName));
            }

            return list;
        }

        /// <summary>
        /// 读取并反序列化配置文件。
        /// </summary>
        /// <param name="path">配置文件绝对或相对路径。</param>
        /// <param name="warnings">加载过程中的非致命问题（未知字段、未知规则类型等）。</param>
        public static BatchConfig Load(string path, out List<string> warnings)
        {
            warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FileNotFoundException("未指定配置文件路径。");
            }

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("配置文件不存在：" + path, path);
            }

            string json = File.ReadAllText(path);

            BatchConfig config;
            try
            {
                config = JsonConvert.DeserializeObject<BatchConfig>(json, Settings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("配置文件 JSON 解析失败：" + ex.Message, ex);
            }

            if (config == null)
            {
                throw new InvalidOperationException("配置文件内容为空或不是合法的 JSON 对象。");
            }

            config.Rules = config.Rules ?? new List<RuleConfig>();

            // 保证内置规则已注册（幂等），这样"规则类型是否已注册"的校验在任何调用路径下都准确
            Core.RuleFactory.RegisterBuiltIns();

            // 未知字段检查：只针对 Rules[] 的元素做一次轻量比对，帮助用户发现拼写错误
            DetectUnknownFields(json, config, warnings);

            Validate(config, warnings);
            return config;
        }

        /// <summary>结构校验：必填项、取值范围、规则类型是否已注册。</summary>
        public static void Validate(BatchConfig config, List<string> warnings)
        {
            if (config.Rules.Count == 0)
            {
                warnings.Add("配置中没有任何规则（Rules 为空）。");
            }

            for (int i = 0; i < config.Rules.Count; i++)
            {
                var rule = config.Rules[i];
                if (rule == null)
                {
                    warnings.Add(string.Format("第 {0} 条规则为 null，已忽略。", i + 1));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.Name))
                {
                    rule.Name = "规则" + (i + 1);
                }

                if (string.IsNullOrWhiteSpace(rule.Type))
                {
                    warnings.Add(string.Format("规则「{0}」缺少 Type 字段，已忽略。", rule.Name));
                    rule.Enabled = false;
                    continue;
                }

                rule.Filter = rule.Filter ?? new RuleFilter();
                rule.Action = rule.Action ?? new RuleAction();

                if (!Core.RuleFactory.IsRegistered(rule.Type))
                {
                    warnings.Add(string.Format(
                        "规则「{0}」的类型 \"{1}\" 未注册，执行时将被跳过。已注册类型：{2}",
                        rule.Name, rule.Type, string.Join(", ", Core.RuleFactory.GetRegisteredTypes())));
                }

                ValidateByType(rule, warnings);
            }
        }

        /// <summary>
        /// 类型级校验。
        ///
        /// 具体规则已全部搬到 <see cref="Core.RuleValidator"/>（那里带字段路径与级别，界面能直接标注到控件上）。
        /// 这里只做一件事：把它的结果拼成字符串塞进 <c>warnings</c>，保持命令行与 BATCHDIAG 的输出格式不变。
        /// </summary>
        private static void ValidateByType(RuleConfig rule, List<string> warnings)
        {
            foreach (Core.FieldIssue issue in Core.RuleValidator.Validate(rule))
            {
                warnings.Add(string.Format("规则「{0}」{1}", rule.Name, issue.Message));
            }
        }

        /// <summary>
        /// 轻量检测 Rules 数组元素里的未知字段（多半是拼写错误），写入 warnings。
        /// </summary>
        private static void DetectUnknownFields(string json, BatchConfig config, List<string> warnings)
        {
            try
            {
                var root = JToken.Parse(json) as JObject;
                if (root == null)
                {
                    return;
                }

                var knownRoot = KnownNames(typeof(BatchConfig));
                foreach (var prop in root.Properties())
                {
                    if (!knownRoot.Contains(prop.Name))
                    {
                        warnings.Add("配置根节点存在未知字段：" + prop.Name);
                    }
                }

                var rulesToken = root["Rules"] as JArray;
                if (rulesToken == null)
                {
                    return;
                }

                var knownRule = KnownNames(typeof(RuleConfig));
                var knownFilter = KnownNames(typeof(RuleFilter));
                var knownAction = KnownNames(typeof(RuleAction));

                for (int i = 0; i < rulesToken.Count; i++)
                {
                    var item = rulesToken[i] as JObject;
                    if (item == null)
                    {
                        continue;
                    }

                    string name = (string)item["Name"] ?? "(未命名)";

                    // Extra 非空说明这些"未知字段"已经被接住并会原样写回，再报一次只是噪声
                    RuleConfig rule = i < config.Rules.Count ? config.Rules[i] : null;
                    bool ruleHasExtra = rule != null && rule.Extra != null && rule.Extra.Count > 0;

                    if (!ruleHasExtra)
                    {
                        foreach (var prop in item.Properties())
                        {
                            if (!knownRule.Contains(prop.Name))
                            {
                                warnings.Add(string.Format("规则「{0}」存在未知字段：{1}", name, prop.Name));
                            }
                        }
                    }
                    CheckSection(item["Filter"] as JObject, knownFilter, name, "Filter", warnings,
                        HasExtra(rule == null ? null : rule.Filter));
                    CheckSection(item["Action"] as JObject, knownAction, name, "Action", warnings,
                        HasExtra(rule == null ? null : rule.Action));
                }
            }
            catch
            {
                // 检测失败不影响主流程
            }
        }

        private static void CheckSection(JObject section, HashSet<string> known, string ruleName, string sectionName, List<string> warnings, bool hasExtra)
        {
            if (section == null || hasExtra)
            {
                return;
            }

            foreach (var prop in section.Properties())
            {
                if (!known.Contains(prop.Name))
                {
                    warnings.Add(string.Format("规则「{0}」的 {1} 存在未知字段：{2}", ruleName, sectionName, prop.Name));
                }
            }
        }

        /// <summary>该节点上是否有"模型不认识但已被接住"的字段。</summary>
        private static bool HasExtra(RuleFilter filter)
        {
            return filter != null && filter.Extra != null && filter.Extra.Count > 0;
        }

        private static bool HasExtra(RuleAction action)
        {
            return action != null && action.Extra != null && action.Extra.Count > 0;
        }

        private static HashSet<string> KnownNames(Type type)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = p.GetCustomAttributes(typeof(JsonPropertyAttribute), true)
                    .OfType<JsonPropertyAttribute>()
                    .FirstOrDefault();
                string name = attr != null && !string.IsNullOrEmpty(attr.PropertyName) ? attr.PropertyName : p.Name;
                set.Add(name);
            }

            return set;
        }

        /// <summary>把配置对象写回 JSON（用于生成示例文件）。</summary>
        public static void Save(BatchConfig config, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(config, Settings));
        }

        /// <summary>把原始 JSON 文本落盘（生成示例配置时保留注释与排版）。</summary>
        public static void SaveRaw(string json, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, json);
        }
    }
}
