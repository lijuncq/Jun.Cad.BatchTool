using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 规则 JSON 的统一序列化入口。
    ///
    /// 为什么需要它：
    ///   RuleConfig 里大量字段是可空类型（<c>int?</c> / <c>bool?</c> / <c>string</c> / <c>List&lt;string&gt;</c>），
    ///   直接用默认设置序列化会把所有"当前没用到的字段"都写成 <c>null</c>，规则 JSON 动辄上百行，
    ///   真正需要改的那三五个字段被淹没在噪声里。规则编辑页正是靠这段 JSON 让用户改参数，
    ///   所以这里做了三件事：
    ///     1. <see cref="NullValueHandling.Ignore"/> —— 值为 null 的条目整条去掉；
    ///     2. 空数组 / 空集合也去掉（它们和 null 等价，都是"没配置"）；
    ///     3. 空的 <c>Filter</c> / <c>Action</c> 对象整体去掉（反序列化时会自动补回默认实例）。
    ///
    /// 语义等价性：被去掉的都是"缺省即等于当前值"的字段——RuleConfig 上的可空字段默认就是 null，
    /// 集合默认就是 null，Filter/Action 反序列化时由属性初始化器补 new，因此
    /// <c>Deserialize(Serialize(rule))</c> 与原对象行为一致（这点由界面来回切换页签验证）。
    ///
    /// 注意：这里只影响"给人看的 JSON"和"写回 config.json 的 Rules 数组"，
    /// 不影响 ConfigLoader 的读取逻辑——读取依然允许缺字段。
    /// </summary>
    public static class RuleJson
    {
        /// <summary>供界面与 ConfigSaver 共用的序列化设置。</summary>
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            ContractResolver = new CompactContractResolver()
        };

        /// <summary>把一条规则（或规则数组）序列化成"精简版"JSON 文本。</summary>
        public static string Serialize(object value)
        {
            DropConflictingExtra(value);
            return JsonConvert.SerializeObject(value, Settings);
        }

        /// <summary>
        /// 丢弃 <c>Extra</c> 里与真实属性同名的键。
        ///
        /// `[JsonExtensionData]` 序列化时会把字典里的键**平铺**进同一个对象，如果某个键后来变成了模型的
        /// 真实属性，输出里就会出现重复键；而 `JToken.Parse` 在默认设置下不会报错，重复键会静默落盘，
        /// 下次读取时只认其中一个。这里在序列化前把它们剔掉（真实属性优先）。
        ///
        /// 实际很难触发：能进 <c>Extra</c> 的键在读取时一定不是已知属性。这是防御性检查。
        /// </summary>
        private static void DropConflictingExtra(object value)
        {
            var rule = value as RuleConfig;
            if (rule != null)
            {
                SanitizeSection(rule.Filter, FilterJsonNames);
                SanitizeSection(rule.Action, ActionJsonNames);
                return;
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                return;
            }

            foreach (object item in enumerable)
            {
                var each = item as RuleConfig;
                if (each != null)
                {
                    SanitizeSection(each.Filter, FilterJsonNames);
                    SanitizeSection(each.Action, ActionJsonNames);
                }
            }
        }

        private static void SanitizeSection(object section, HashSet<string> knownNames)
        {
            if (section == null)
            {
                return;
            }

            IDictionary<string, JToken> extra = GetExtra(section);
            if (extra == null || extra.Count == 0)
            {
                return;
            }

            List<string> conflicts = null;
            foreach (string key in extra.Keys)
            {
                if (knownNames.Contains(key))
                {
                    if (conflicts == null)
                    {
                        conflicts = new List<string>();
                    }

                    conflicts.Add(key);
                }
            }

            if (conflicts == null)
            {
                return;
            }

            foreach (string key in conflicts)
            {
                extra.Remove(key);
                DiagnosticLog.Write(string.Format(
                    "RuleJson：字段「{0}」与模型属性同名，已丢弃 Extra 里的重复值（以模型属性为准）。", key));
            }
        }

        private static IDictionary<string, JToken> GetExtra(object section)
        {
            if (section == null)
            {
                return null;
            }

            PropertyInfo prop = section.GetType().GetProperty("Extra", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                return null;
            }

            try
            {
                return prop.GetValue(section, null) as IDictionary<string, JToken>;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<string> JsonNames(Type type)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = (JsonPropertyAttribute)Attribute.GetCustomAttribute(p, typeof(JsonPropertyAttribute));
                string name = attr != null && !string.IsNullOrEmpty(attr.PropertyName) ? attr.PropertyName : p.Name;
                set.Add(name);
            }

            return set;
        }

        private static readonly HashSet<string> FilterJsonNames = JsonNames(typeof(RuleFilter));
        private static readonly HashSet<string> ActionJsonNames = JsonNames(typeof(RuleAction));

        /// <summary>
        /// 判定一个值是否算"没配置"：null、空集合、所有成员都没配置的对象。
        /// </summary>
        private static bool IsEmptyValue(object value)
        {
            if (value == null)
            {
                return true;
            }

            var text = value as string;
            if (text != null)
            {
                // 空字符串是"有意义的空值"（例如 LogFile=""），保留
                return false;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                IEnumerator e = enumerable.GetEnumerator();
                try
                {
                    return !e.MoveNext();
                }
                finally
                {
                    var disposable = e as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
            }

            return IsEmptyObject(value);
        }

        /// <summary>
        /// 对象的所有可序列化成员是否都"没配置"（典型场景：只有空壳的 Filter / Action）。
        /// 只看一层，够用且不会误伤嵌套结构。
        /// </summary>
        private static bool IsEmptyObject(object value)
        {
            PropertyInfo[] props = GetSerializableProperties(value.GetType());
            if (props.Length == 0)
            {
                // 没有可序列化成员的类型不参与"空对象"判定，避免误删
                return false;
            }

            foreach (PropertyInfo p in props)
            {
                object child;
                try
                {
                    child = p.GetValue(value, null);
                }
                catch
                {
                    return false;
                }

                if (child == null)
                {
                    continue;
                }

                if (child is string)
                {
                    return false;
                }

                var enumerable = child as IEnumerable;
                if (enumerable != null)
                {
                    IEnumerator e = enumerable.GetEnumerator();
                    bool hasAny;
                    try
                    {
                        hasAny = e.MoveNext();
                    }
                    finally
                    {
                        var disposable = e as IDisposable;
                        if (disposable != null)
                        {
                            disposable.Dispose();
                        }
                    }

                    if (hasAny)
                    {
                        return false;
                    }

                    continue;
                }

                return false;
            }

            return true;
        }

        private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo[] GetSerializableProperties(Type type)
        {
            lock (PropertyCache)
            {
                PropertyInfo[] cached;
                if (PropertyCache.TryGetValue(type, out cached))
                {
                    return cached;
                }

                var list = new List<PropertyInfo>();
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0)
                    {
                        continue;
                    }

                    list.Add(p);
                }

                cached = list.ToArray();
                PropertyCache[type] = cached;
                return cached;
            }
        }

        /// <summary>
        /// 契约解析器：在默认契约之上追加"空集合 / 空对象不输出"的判定。
        /// 只处理可空与集合类型，不改变任何字段名或顺序。
        /// </summary>
        private sealed class CompactContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);

                if (property.Ignored || property.ValueProvider == null)
                {
                    return property;
                }

                Predicate<object> existing = property.ShouldSerialize;
                property.ShouldSerialize = instance =>
                {
                    if (existing != null && !existing(instance))
                    {
                        return false;
                    }

                    object value;
                    try
                    {
                        value = property.ValueProvider.GetValue(instance);
                    }
                    catch
                    {
                        return true;
                    }

                    if (value == null)
                    {
                        return false;
                    }

                    if (value is string)
                    {
                        return true;
                    }

                    // 集合：空则省略
                    if (value is IEnumerable)
                    {
                        return !IsEmptyValue(value);
                    }

                    // 嵌套配置对象（Filter / Action）：所有成员都没配置时整体省略，
                    // 反序列化时由属性初始化器补回空实例，语义不变。
                    Type type = value.GetType();
                    if (type.Namespace != null && type.Namespace.StartsWith("Jun.AutoCAD.Config", StringComparison.Ordinal))
                    {
                        return !IsEmptyValue(value);
                    }

                    return true;
                };

                return property;
            }
        }
    }
}
