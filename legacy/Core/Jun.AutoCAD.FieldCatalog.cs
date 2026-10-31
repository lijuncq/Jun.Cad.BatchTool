using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 面板上的一个字段。由 <see cref="FieldCatalog"/> 从 <see cref="RuleFieldAttribute"/> 反射得到。
    /// </summary>
    public sealed class FieldDescriptor
    {
        public FieldDescriptor(string path, PropertyInfo property, bool isFilter, RuleFieldAttribute meta)
        {
            Path = path;
            Property = property;
            IsFilter = isFilter;
            Meta = meta;
        }

        /// <summary>相对 RuleConfig 的点路径，例如 <c>Filter.LayerName</c>。</summary>
        public string Path { get; private set; }

        public PropertyInfo Property { get; private set; }

        /// <summary>true 表示挂在 <see cref="RuleFilter"/> 上，false 表示挂在 <see cref="RuleAction"/> 上。</summary>
        public bool IsFilter { get; private set; }

        public RuleFieldAttribute Meta { get; private set; }

        public string Label
        {
            get { return Meta == null || string.IsNullOrEmpty(Meta.Label) ? Property.Name : Meta.Label; }
        }

        public RuleFieldEditor Editor
        {
            get { return Meta == null ? RuleFieldEditor.Text : Meta.Editor; }
        }

        public RuleFieldGroup Group
        {
            get { return Meta == null ? RuleFieldGroup.Action : Meta.Group; }
        }

        public int Order
        {
            get { return Meta == null ? 0 : Meta.Order; }
        }

        /// <summary>
        /// 当前规则类型用不上这个字段，但它已经有值。
        /// 这种字段仍然显示（禁用状态），否则会变成"界面上看不见、执行时仍生效"的隐藏配置。
        /// </summary>
        public bool NotApplicable { get; set; }

        /// <summary>被联动规则禁用（例如开了 ForceByLayer 就不能再填 LinetypeName）。</summary>
        public bool Disabled { get; set; }

        /// <summary>禁用的原因，显示在字段右侧。</summary>
        public string DisabledReason { get; set; }

        /// <summary>读取当前值。</summary>
        public object GetValue(RuleConfig rule)
        {
            object owner = IsFilter ? (object)rule.Filter : rule.Action;
            if (owner == null)
            {
                return null;
            }

            try
            {
                return Property.GetValue(owner, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入值。</summary>
        public void SetValue(RuleConfig rule, object value)
        {
            object owner = IsFilter ? (object)rule.Filter : rule.Action;
            if (owner == null)
            {
                return;
            }

            try
            {
                Property.SetValue(owner, Coerce(value), null);
            }
            catch
            {
                // 类型不匹配时静默忽略：表单只负责搬运，判定交给 RuleValidator
            }
        }

        /// <summary>
        /// 下拉框（<see cref="RuleFieldEditor.Enum"/>）给的是字符串，数值字段（例如线宽）要转回数值再写。
        /// 只在"字符串 → 数值"这一个方向上做转换，其余类型原样返回。
        /// </summary>
        private object Coerce(object value)
        {
            string text = value as string;
            if (text == null)
            {
                return value;
            }

            Type target = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            if (target == typeof(int))
            {
                int parsed;
                return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed) ? (object)parsed : null;
            }

            if (target == typeof(double))
            {
                double parsed;
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed) ? (object)parsed : null;
            }

            return value;
        }
    }

    /// <summary>
    /// 字段目录：把 <see cref="RuleFilter"/> / <see cref="RuleAction"/> 上的标注反射成"这条规则该显示哪些字段"。
    ///
    /// 三条规则：
    ///   1. **按类型过滤**：<c>AppliesTo</c> 为 <c>"*"</c> 或包含当前类型才显示；
    ///   2. **已设置就不隐藏**：类型用不上但已经有值的字段仍然列出，标 <see cref="FieldDescriptor.NotApplicable"/>，
    ///      界面用禁用态显示。否则用户切了类型之后，旧字段会变成看不见但仍在生效的配置；
    ///   3. **联动禁用而不是隐藏**：字段之间的覆盖关系（`ForceByLayer` 压过线型/线宽等）硬编码在
    ///      <see cref="FieldRules"/> 里，表现为禁用 + 说明，不隐藏。
    ///
    /// 本类不引用任何宿主类型。
    /// </summary>
    public static class FieldCatalog
    {
        private static readonly PropertyInfo[] FilterProperties = AnnotatedProperties(typeof(RuleFilter));
        private static readonly PropertyInfo[] ActionProperties = AnnotatedProperties(typeof(RuleAction));

        /// <summary>只返回该类型用得上的字段，按分组与顺序排好。</summary>
        public static List<FieldDescriptor> ForType(string ruleType)
        {
            return For(ruleType, null);
        }

        /// <summary>所有带标注的字段，不做类型过滤。供 schema 自检与文档生成使用。</summary>
        public static List<FieldDescriptor> All()
        {
            var list = new List<FieldDescriptor>();

            foreach (PropertyInfo p in FilterProperties)
            {
                list.Add(new FieldDescriptor("Filter." + p.Name, p, true,
                    (RuleFieldAttribute)Attribute.GetCustomAttribute(p, typeof(RuleFieldAttribute))));
            }

            foreach (PropertyInfo p in ActionProperties)
            {
                list.Add(new FieldDescriptor("Action." + p.Name, p, false,
                    (RuleFieldAttribute)Attribute.GetCustomAttribute(p, typeof(RuleFieldAttribute))));
            }

            list.Sort(Compare);
            return list;
        }

        /// <summary>
        /// 返回该规则应当显示的字段。
        /// 传入 <paramref name="rule"/> 时会额外带上"类型用不上但已有值"的字段，并计算禁用状态。
        /// </summary>
        public static List<FieldDescriptor> For(string ruleType, RuleConfig rule)
        {
            var list = new List<FieldDescriptor>();
            string type = ruleType == null ? string.Empty : ruleType.Trim();

            Append(list, FilterProperties, true, type, rule);
            Append(list, ActionProperties, false, type, rule);

            if (rule != null)
            {
                FieldRules.Apply(rule, list);
            }

            list.Sort(Compare);
            return list;
        }

        /// <summary>某个类型是否真的用得上某个字段（不看当前值）。</summary>
        public static bool IsApplicable(string ruleType, string path)
        {
            foreach (FieldDescriptor d in ForType(ruleType))
            {
                if (string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>该类型用不上的字段名集合，供"切换类型清理残留"使用。</summary>
        public static HashSet<string> FieldPathsNotUsedBy(string ruleType)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldDescriptor d in ForType(ruleType))
            {
                used.Add(d.Path);
            }

            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in FilterProperties)
            {
                all.Add("Filter." + p.Name);
            }

            foreach (PropertyInfo p in ActionProperties)
            {
                all.Add("Action." + p.Name);
            }

            all.ExceptWith(used);
            return all;
        }

        // ------------------------------------------------------------------ 内部

        private static void Append(List<FieldDescriptor> list, PropertyInfo[] properties, bool isFilter, string ruleType, RuleConfig rule)
        {
            foreach (PropertyInfo p in properties)
            {
                var meta = (RuleFieldAttribute)Attribute.GetCustomAttribute(p, typeof(RuleFieldAttribute));
                string path = (isFilter ? "Filter." : "Action.") + p.Name;
                bool applicable = Matches(meta, ruleType);

                if (applicable)
                {
                    list.Add(new FieldDescriptor(path, p, isFilter, meta));
                    continue;
                }

                if (rule == null)
                {
                    continue;
                }

                // 类型用不上但已经有值：仍然显示（禁用），避免隐藏配置
                var probe = new FieldDescriptor(path, p, isFilter, meta);
                if (IsSet(probe.GetValue(rule)))
                {
                    probe.NotApplicable = true;
                    probe.Disabled = true;
                    probe.DisabledReason = string.Format("当前类型（{0}）不读取该字段，但它有值", ruleType);
                    list.Add(probe);
                }
            }
        }

        private static bool Matches(RuleFieldAttribute meta, string ruleType)
        {
            if (meta == null || string.IsNullOrWhiteSpace(meta.AppliesTo) || meta.AppliesTo.Trim() == "*")
            {
                return true;
            }

            foreach (string raw in meta.AppliesTo.Split(','))
            {
                if (string.Equals(raw.Trim(), ruleType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSet(object value)
        {
            if (value == null)
            {
                return false;
            }

            var text = value as string;
            if (text != null)
            {
                return !string.IsNullOrWhiteSpace(text);
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                IEnumerator e = enumerable.GetEnumerator();
                try
                {
                    return e.MoveNext();
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

            return true;
        }

        private static PropertyInfo[] AnnotatedProperties(Type type)
        {
            var list = new List<PropertyInfo>();
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (Attribute.GetCustomAttribute(p, typeof(RuleFieldAttribute)) == null)
                {
                    continue;
                }

                list.Add(p);
            }

            return list.ToArray();
        }

        private static int Compare(FieldDescriptor a, FieldDescriptor b)
        {
            int g = ((int)a.Group).CompareTo((int)b.Group);
            if (g != 0)
            {
                return g;
            }

            int o = a.Order.CompareTo(b.Order);
            if (o != 0)
            {
                return o;
            }

            return string.CompareOrdinal(a.Path, b.Path);
        }
    }

    /// <summary>
    /// 字段之间的联动关系（谁覆盖谁）。
    ///
    /// 这些是**类型语义**，不是字段元数据：单字段标注表达不了"打开 A 之后 B 就没意义了"。
    /// 全项目只有下面几处，所以硬编码在这里，而不是养一个 `VisibleWhen="..."` 字符串表达式——
    /// 表达式写错了没人会发现，硬编码写错了编译器和测试会。
    ///
    /// 一律用**禁用**而不是隐藏：隐藏会留下"界面上看不见、执行时仍生效"的字段。
    /// </summary>
    public static class FieldRules
    {
        public static void Apply(RuleConfig rule, List<FieldDescriptor> fields)
        {
            if (rule == null || fields == null)
            {
                return;
            }

            RuleAction action = rule.Action;
            if (action == null)
            {
                return;
            }

            string type = rule.Type == null ? string.Empty : rule.Type.Trim();

            // 强制随层 → 线型/线宽没有意义
            if (Eq(type, "EntityLinetypeChange") || Eq(type, "EntityLineweightChange"))
            {
                if (ConfigValues.Bool(action.ForceByLayer, false))
                {
                    Disable(fields, "Action.LinetypeName", "已开启「强制随层」，线型由图层决定");
                    Disable(fields, "Action.LineWeight", "已开启「强制随层」，线宽由图层决定");
                }
            }

            // 填了目标渐变 → 图案相关设置全部失效
            if (Eq(type, "HatchPatternChange") && !string.IsNullOrWhiteSpace(action.TargetGradientName))
            {
                Disable(fields, "Action.TargetPatternName", "已指定目标渐变，渐变优先于图案");
                Disable(fields, "Action.PatternType", "已指定目标渐变");
                Disable(fields, "Action.PatternScale", "已指定目标渐变");
                Disable(fields, "Action.PatternAngle", "已指定目标渐变");
                Disable(fields, "Action.PatternSpace", "已指定目标渐变");
                Disable(fields, "Action.PatternDouble", "已指定目标渐变");
            }

            // 线间距/双线只对用户自定义图案有效
            if (Eq(type, "HatchPatternChange") && !string.IsNullOrWhiteSpace(action.TargetPatternName))
            {
                string patternType = action.PatternType == null ? "PreDefined" : action.PatternType.Trim();
                if (!Eq(patternType, "UserDefined"))
                {
                    Disable(fields, "Action.PatternSpace", "仅「用户自定义」图案使用线间距");
                    Disable(fields, "Action.PatternDouble", "仅「用户自定义」图案使用双线");
                }
            }

            // 不自动创建图层 → 新建图层的特性没有意义
            if ((Eq(type, "EntityLayerChange") || Eq(type, "BlockLayerChange"))
                && action.CreateIfMissing.HasValue && !action.CreateIfMissing.Value)
            {
                Disable(fields, "Action.LayerColorIndex", "未开启自动创建图层");
                Disable(fields, "Action.LayerLinetype", "未开启自动创建图层");
                Disable(fields, "Action.LayerLineWeight", "未开启自动创建图层");
                Disable(fields, "Action.LayerPlottable", "未开启自动创建图层");
            }
        }

        private static void Disable(List<FieldDescriptor> fields, string path, string reason)
        {
            foreach (FieldDescriptor f in fields)
            {
                if (string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    f.Disabled = true;
                    f.DisabledReason = reason;
                    return;
                }
            }
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
