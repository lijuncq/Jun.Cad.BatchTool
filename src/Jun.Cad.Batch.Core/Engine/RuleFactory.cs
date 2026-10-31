using System;
using System.Collections.Generic;
using System.Linq;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 规则工厂：Type 字符串 → <see cref="IRuleHandler"/> 实例。
    ///
    /// 这是"可扩展性"的落点：新增规则类型只需调用 <see cref="Register"/>，
    /// 主流程（<see cref="BatchRunner"/>）完全不用改。
    /// 注册表使用大小写不敏感的字典，配置里写 "blockreplace" 也能命中。
    /// </summary>
    public static class RuleFactory
    {
        private static readonly Dictionary<string, Func<IRuleHandler>> Registry =
            new Dictionary<string, Func<IRuleHandler>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object SyncRoot = new object();
        private static bool _builtInRegistered;

        /// <summary>内置规则类型名。字段标注里的 <c>AppliesTo</c>、schema 自检都以这份清单为准。</summary>
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
            "HatchPatternChange",
            "TextReplace",
            "TextMask",
            "DimTextAudit",
            "AttributeAudit",
            "LayerMap",
            "LayoutRename",
            "TitleBlockNormalize",
            "GeometryCleanup",
            "BomSync"
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
                case "TEXTREPLACE":
                    return new Rules.TextReplaceRule();
                case "TEXTMASK":
                    return new Rules.TextMaskRule();
                case "DIMTEXTAUDIT":
                    return new Rules.DimTextAuditRule();
                case "ATTRIBUTEAUDIT":
                    return new Rules.AttributeAuditRule();
                case "LAYERMAP":
                    return new Rules.LayerMapRule();
                case "LAYOUTRENAME":
                    return new Rules.LayoutRenameRule();
                case "TITLEBLOCKNORMALIZE":
                    return new Rules.TitleBlockNormalizeRule();
                case "GEOMETRYCLEANUP":
                    return new Rules.GeometryCleanupRule();
                case "BOMSYNC":
                    return new Rules.BomSyncRule();
                default:
                    return null;
            }
        }

        /// <summary>注册（或覆盖）一种规则类型。</summary>
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

        /// <summary>创建处理器实例；未注册返回 null（由调用方记为中止）。</summary>
        public static IRuleHandler Create(string ruleType)
        {
            if (string.IsNullOrWhiteSpace(ruleType))
            {
                return null;
            }

            RegisterBuiltIns();

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
            RegisterBuiltIns();

            lock (SyncRoot)
            {
                return Registry.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }
}
