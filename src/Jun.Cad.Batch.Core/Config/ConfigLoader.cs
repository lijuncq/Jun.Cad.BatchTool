using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Jun.Cad.Batch.Editing;
using Jun.Cad.Batch.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jun.Cad.Batch.Config
{
    /// <summary>
    /// 配置文件加载器：路径解析、反序列化、结构校验、示例生成。
    ///
    /// 与 AutoCAD 版的差别：
    ///   * 默认路径不再"猜图形所在目录"（离线工具没有当前图形），
    ///     而是"命令行指定 → 程序目录 → 当前工作目录"；
    ///   * 类型级校验内置在本类（不再依赖界面层的 RuleValidator），
    ///     保证命令行跑的时候也能在**执行前**把"缺字段"指出来。
    /// </summary>
    public static class ConfigLoader
    {
        /// <summary>默认配置文件名（放在 exe 同目录）。</summary>
        public const string DefaultFileName = "Jun.Cad.Batch.json";

        /// <summary>嵌入的默认配置模板（20 种规则各一条示例，全部 Enabled=false）。</summary>
        private const string TemplateResource = "Jun.Cad.Batch.DefaultConfig.json";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // 允许缺少字段、允许大小写不一致（SourceBlockName / sourceBlockName 都能识别）
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Error = (sender, args) =>
            {
                // 类型不匹配（例如把字符串写进 int 字段）时不抛，由上层按"警告"处理
                if (args.ErrorContext.Error is JsonReaderException || args.ErrorContext.Error is JsonSerializationException)
                {
                    args.ErrorContext.Handled = true;
                }
            }
        };

        /// <summary>
        /// exe 所在目录。
        ///
        /// 为什么不用 <see cref="AppContext.BaseDirectory"/>：单文件发布
        /// （`IncludeAllContentForSelfExtract`）时它指向 %TEMP% 下的解压目录，
        /// 配置文件会被写到一个用户永远找不到、清 TEMP 就丢的地方。
        /// <see cref="Environment.ProcessPath"/> 拿到的是用户真正双击的那个 exe。
        /// </summary>
        public static string GetExecutableDirectory()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return dir;
                    }
                }
            }
            catch
            {
                // 老运行时上取不到就走兜底
            }

            try
            {
                return AppContext.BaseDirectory;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>默认配置的完整路径（只算路径，不管文件在不在）。</summary>
        public static string GetDefaultConfigPath()
        {
            string dir = GetExecutableDirectory();
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, DefaultFileName);
        }

        /// <summary>默认配置文件路径；文件不存在时返回 null。</summary>
        public static string ResolveDefaultConfigPath()
        {
            string path = GetDefaultConfigPath();
            return path != null && File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 保证 exe 旁边有一份配置文件：不存在就从嵌入模板生成一份。
        ///
        /// 返回值是"应该用的路径"（即使写盘失败也返回路径，让上层报错时能指出位置）。
        /// <paramref name="created"/> 表示这次是否新建；<paramref name="error"/> 为写盘失败原因。
        ///
        /// 为什么模板要嵌进程序而不是随包发一个文件：用户把配置文件删了/改坏了，
        /// 程序要能自己长回来；随包发的话删了就没了，而且单文件发布还得额外处理。
        /// </summary>
        public static string EnsureDefaultConfig(out bool created, out string error)
        {
            created = false;
            error = null;

            string path = GetDefaultConfigPath();
            if (path == null)
            {
                error = "取不到程序所在目录，无法确定默认配置路径。";
                return null;
            }

            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, LoadTemplate(), new UTF8Encoding(true));
                created = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return path;
        }

        /// <summary>读出嵌入的默认配置模板（取不到时返回一个最小可用的 JSON）。</summary>
        public static string LoadTemplate()
        {
            try
            {
                using (Stream stream = typeof(ConfigLoader).Assembly.GetManifestResourceStream(TemplateResource))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }
            catch
            {
                // 嵌入资源取不到时退回最小模板，至少别让程序起不来
            }

            return "{\r\n  \"Version\": 1,\r\n  \"Rules\": []\r\n}\r\n";
        }

        /// <summary>读取并反序列化配置文件。</summary>
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
            RuleFactory.RegisterBuiltIns();

            DetectUnknownFields(json, config, warnings);
            Validate(config, warnings);
            return config;
        }

        /// <summary>结构校验：必填项、取值范围、规则类型是否已注册。</summary>
        public static void Validate(BatchConfig config, List<string> warnings)
        {
            if (config == null)
            {
                warnings.Add("配置对象为空。");
                return;
            }

            if (config.Rules == null || config.Rules.Count == 0)
            {
                warnings.Add("配置中没有任何规则（Rules 为空）。");
                return;
            }

            for (int i = 0; i < config.Rules.Count; i++)
            {
                RuleConfig rule = config.Rules[i];
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

                if (!RuleFactory.IsRegistered(rule.Type))
                {
                    warnings.Add(string.Format(
                        "规则「{0}」的类型 \"{1}\" 未注册，执行时将被跳过。已注册类型：{2}",
                        rule.Name, rule.Type, string.Join(", ", RuleFactory.GetRegisteredTypes().ToArray())));
                }

                ValidateByType(rule, warnings);
            }
        }

        /// <summary>
        /// 类型级校验：把"这条规则缺什么"在**执行前**说清楚。
        ///
        /// **校验逻辑只有一份**：<see cref="RuleValidator.Validate"/>。这里只做翻译——
        /// 把 `FieldIssue` 拼成命令行一直以来的那一行（`规则「名」原因`），
        /// 所以命令行的输出形态不变，但覆盖面从原来的 9 种类型扩到全部 20 种。
        ///
        /// Error 与 Warning 在命令行都只作为提示输出（命令行没有"阻断保存"这个动作），
        /// 真正的阻断在界面侧（`RuleEditorDrawer` 用 `IsError` 卡保存）。
        /// </summary>
        private static void ValidateByType(RuleConfig rule, List<string> warnings)
        {
            try
            {
                foreach (FieldIssue issue in RuleValidator.Validate(rule))
                {
                    warnings.Add(string.Format("规则「{0}」{1}", rule.Name, issue.Message));
                }
            }
            catch (Exception ex)
            {
                // 校验本身出问题不能连累配置加载
                warnings.Add(string.Format("规则「{0}」校验时出错：{1}", rule.Name, ex.Message));
            }
        }

        // ---------------------------------------------------------------- 未知字段检测

        /// <summary>轻量检测 Rules 数组元素里的未知字段（多半是拼写错误），写入 warnings。</summary>
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
                foreach (JProperty prop in root.Properties())
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
                        foreach (JProperty prop in item.Properties())
                        {
                            if (!knownRule.Contains(prop.Name))
                            {
                                warnings.Add(string.Format("规则「{0}」存在未知字段：{1}", name, prop.Name));
                            }
                        }
                    }

                    CheckSection(item["Filter"] as JObject, knownFilter, name, "Filter", warnings,
                        rule != null && rule.Filter != null && rule.Filter.Extra != null && rule.Filter.Extra.Count > 0);

                    CheckSection(item["Action"] as JObject, knownAction, name, "Action", warnings,
                        rule != null && rule.Action != null && rule.Action.Extra != null && rule.Action.Extra.Count > 0);
                }
            }
            catch
            {
                // 检测失败不影响主流程
            }
        }

        private static void CheckSection(
            JObject section, HashSet<string> known, string ruleName, string sectionName,
            List<string> warnings, bool hasExtra)
        {
            if (section == null || hasExtra)
            {
                return;
            }

            foreach (JProperty prop in section.Properties())
            {
                if (!known.Contains(prop.Name))
                {
                    warnings.Add(string.Format("规则「{0}」的 {1} 存在未知字段：{2}", ruleName, sectionName, prop.Name));
                }
            }
        }

        private static HashSet<string> KnownNames(Type type)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = p.GetCustomAttributes(typeof(JsonPropertyAttribute), true)
                    .OfType<JsonPropertyAttribute>()
                    .FirstOrDefault();

                string name = attr != null && !string.IsNullOrEmpty(attr.PropertyName) ? attr.PropertyName : p.Name;
                set.Add(name);
            }

            return set;
        }

        // ---------------------------------------------------------------- 保存

        /// <summary>把配置对象写回 JSON。</summary>
        public static void Save(BatchConfig config, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Settings));
        }

        /// <summary>把原始 JSON 文本落盘（生成示例配置时保留排版）。</summary>
        public static void SaveRaw(string json, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, json);
        }

        private static void EnsureDirectory(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
