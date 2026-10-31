using System;
using System.Collections.Generic;
using System.Linq;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 规则工厂：Type 字符串 → IRuleHandler 实例。
    ///
    /// 这是"可扩展性"的落点：新增规则类型只需调用 Register，主流程（BatchRunner）完全不用改。
    /// 注册表使用大小写不敏感的字典，配置里写 "blockreplace" 也能命中。
    /// </summary>
    public static class RuleFactory
    {
        private static readonly Dictionary<string, Func<IRuleHandler>> Registry =
            new Dictionary<string, Func<IRuleHandler>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object SyncRoot = new object();
        private static bool _builtInRegistered;

        /// <summary>
        /// 内置规则类型名。
        ///
        /// 刻意与"实例化"分开：测试进程只读这个数组时不会碰到任何 handler 类型，
        /// 而 handler 类型会引到 acdbmgd——它在宿主进程外加载不了。
        /// 字段标注里的 <c>AppliesTo</c>、schema 自检都以这份清单为准。
        /// </summary>
        public static readonly string[] BuiltInTypeNames =
        {
            "BlockReplace",
            "BlockLayerChange",
            "EntityLayerChange",
            "TextFontChange",
            "PurgeUnused",
            "EntityLinetypeChange",
            "EntityLineweightChange",
            "DimensionStyleChange",
            "SetByLayer",
            "TextHeightChange",
            "HatchPatternChange"
        };

        /// <summary>注册内置规则（幂等，多次调用安全）。</summary>
        public static void RegisterBuiltIns()
        {
            lock (SyncRoot)
            {
                if (_builtInRegistered)
                {
                    return;
                }

                foreach (string name in BuiltInTypeNames)
                {
                    string captured = name;
                    Register(captured, () => CreateBuiltIn(captured));
                }

                _builtInRegistered = true;
            }
        }

        /// <summary>按名字造一个内置规则处理器；未知名字返回 null。</summary>
        private static IRuleHandler CreateBuiltIn(string ruleType)
        {
            switch ((ruleType ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "BLOCKREPLACE":
                    return new Rules.BlockReplaceRule();
                case "BLOCKLAYERCHANGE":
                    return new Rules.BlockLayerChangeRule();
                case "ENTITYLAYERCHANGE":
                    return new Rules.EntityLayerChangeRule();
                case "TEXTFONTCHANGE":
                    return new Rules.TextFontChangeRule();
                case "PURGEUNUSED":
                    return new Rules.PurgeUnusedRule();
                case "ENTITYLINETYPECHANGE":
                    return new Rules.EntityLinetypeChangeRule();
                case "ENTITYLINEWEIGHTCHANGE":
                    return new Rules.EntityLineweightChangeRule();
                case "DIMENSIONSTYLECHANGE":
                    return new Rules.DimensionStyleChangeRule();
                case "SETBYLAYER":
                    return new Rules.SetByLayerRule();
                case "TEXTHEIGHTCHANGE":
                    return new Rules.TextHeightChangeRule();
                case "HATCHPATTERNCHANGE":
                    return new Rules.HatchPatternChangeRule();
                default:
                    return null;
            }
        }

        /// <summary>
        /// 注册（或覆盖）一种规则类型。
        /// 第三方扩展在 IExtensionApplication.Initialize 里调用即可接入配置框架。
        /// </summary>
        public static void Register(string ruleType, Func<IRuleHandler> factory)
        {
            if (string.IsNullOrWhiteSpace(ruleType) || factory == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                Registry[ruleType.Trim()] = factory;
            }
        }

        public static bool IsRegistered(string ruleType)
        {
            if (string.IsNullOrWhiteSpace(ruleType))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return Registry.ContainsKey(ruleType.Trim());
            }
        }

        /// <summary>创建处理器实例；未注册返回 null（由调用方记为跳过）。</summary>
        public static IRuleHandler Create(string ruleType)
        {
            if (string.IsNullOrWhiteSpace(ruleType))
            {
                return null;
            }

            Func<IRuleHandler> factory;
            lock (SyncRoot)
            {
                if (!Registry.TryGetValue(ruleType.Trim(), out factory))
                {
                    return null;
                }
            }

            return factory();
        }

        public static IEnumerable<string> GetRegisteredTypes()
        {
            lock (SyncRoot)
            {
                return Registry.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>
        /// 字段标注自检：每个 <c>AppliesTo</c> 里出现的类型名都必须是已注册的内置类型。
        ///
        /// 能抓住的是"拼错类型名"（例如写成 `BlockLayerChang`，字段会在所有类型下都不显示）。
        /// 抓不住的是"字段声明了但 handler 根本不读"——那需要跑规则，见设计文档"下一轮遗留项"。
        /// 返回问题列表，空表示通过。
        /// </summary>
        public static List<string> ValidateSchema()
        {
            var problems = new List<string>();
            var known = new HashSet<string>(BuiltInTypeNames, StringComparer.OrdinalIgnoreCase);

            foreach (FieldDescriptor field in FieldCatalog.All())
            {
                string appliesTo = field.Meta == null ? null : field.Meta.AppliesTo;
                if (string.IsNullOrWhiteSpace(appliesTo) || appliesTo.Trim() == "*")
                {
                    continue;
                }

                foreach (string raw in appliesTo.Split(','))
                {
                    string token = raw.Trim();
                    if (token.Length == 0)
                    {
                        continue;
                    }

                    if (!known.Contains(token))
                    {
                        problems.Add(string.Format(
                            "字段 {0} 的 AppliesTo 含未知类型「{1}」", field.Path, token));
                    }
                }
            }

            return problems;
        }
    }
}
