using System;

namespace Jun.AutoCAD.Config
{
    /// <summary>字段用什么控件编辑。</summary>
    public enum RuleFieldEditor
    {
        /// <summary>单行文本框。</summary>
        Text,

        /// <summary>多行文本框。</summary>
        Multiline,

        /// <summary>整数输入（只接受整数，越界在离开字段时提示）。</summary>
        Integer,

        /// <summary>小数输入。</summary>
        Double,

        /// <summary>三态开关：不设置 / 是 / 否。对应 <c>bool?</c>。</summary>
        Toggle,

        /// <summary>单选下拉，候选取自 <see cref="RuleFieldAttribute.Values"/>。</summary>
        Enum,

        /// <summary>多选（复选框列表），对应 <c>List&lt;string&gt;</c>。</summary>
        MultiEnum,

        /// <summary>标签列表：可增删的字符串集合，对应 <c>List&lt;string&gt;</c>。</summary>
        TagList,

        /// <summary>只读展示。</summary>
        ReadOnly
    }

    /// <summary>字段在面板里的分组。</summary>
    public enum RuleFieldGroup
    {
        /// <summary>过滤条件（选中什么）。</summary>
        Filter,

        /// <summary>操作（改什么）。</summary>
        Action,

        /// <summary>高级：该类型最小有效配置之外的字段。</summary>
        Advanced
    }

    /// <summary>
    /// 字段的值是不是一个"图纸里的名字"。
    /// 驱动两件事：保存时对照符号表校验、是否显示「拾取」按钮（拾取在下一轮实现）。
    /// 它同时让控件枚举保持与宿主无关——UI 不需要知道"图层"是什么类型。
    /// </summary>
    public enum RuleFieldValidateAs
    {
        None,
        Layer,
        Block,
        TextStyle,
        DimStyle,
        Linetype,
        Pattern
    }

    /// <summary>
    /// 字段元数据。
    ///
    /// 放在模型属性上，是"哪个字段该怎么编辑"的**唯一声明处**：面板按它生成控件，
    /// 校验按 <see cref="ValidateAs"/> 决定要不要查符号表，摘要（`RuleSummary`）与它无关。
    ///
    /// 三条刻意的取舍（2026-10-29 评审后定）：
    ///   1. **不声明"默认值"**。默认值住在 handler 里（`ConfigValues.Bool(x, true)` 这类调用），
    ///      属性上再写一份必然漂移，而漂移只能靠测试发现。三态开关因此只显示"不设置"，
    ///      由 `Help` 文字说明留空的含义。
    ///   2. **不引入 `VisibleWhen` 表达式**。字段之间只有两处联动
    ///      （`ForceByLayer` 压过 `LinetypeName`/`LineWeight`、`TargetGradientName` 压过 `TargetPatternName`），
    ///      硬编码在 `FieldRules` 里比养一个字符串 DSL 便宜，也不会出现"表达式写错没人发现"。
    ///      联动表现为**禁用而非隐藏**：隐藏会留下"界面上看不见、执行时仍生效"的字段。
    ///   3. **`AppliesTo` 只表达"哪个类型用得上"**，不表达"引擎级字段"。
    ///      引擎级字段（`DxfFilter` 无条件读取的那些）对所有类型都生效，标记为 `"*"`。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class RuleFieldAttribute : Attribute
    {
        /// <summary>中文标签，面板上显示。</summary>
        public string Label { get; set; }

        /// <summary>控件种类。</summary>
        public RuleFieldEditor Editor { get; set; }

        /// <summary>
        /// 哪些规则类型用得上：<c>"*"</c> 表示全部，或逗号分隔的类型名（大小写不敏感）。
        /// 例：<c>"BlockReplace,BlockLayerChange"</c>。
        /// </summary>
        public string AppliesTo { get; set; }

        /// <summary>分组。</summary>
        public RuleFieldGroup Group { get; set; }

        /// <summary>组内排序，小的在前；相同则按属性声明顺序。</summary>
        public int Order { get; set; }

        /// <summary>单位后缀，例如 <c>"0.01mm"</c>、<c>"度"</c>、<c>"0~1"</c>。仅显示用。</summary>
        public string Unit { get; set; }

        /// <summary>
        /// <see cref="RuleFieldEditor.Enum"/> / <see cref="RuleFieldEditor.MultiEnum"/> 的候选项，
        /// 格式 <c>"值=显示名;值=显示名"</c>。显示名里不能含 <c>;</c> 或 <c>=</c>。
        /// </summary>
        public string Values { get; set; }

        /// <summary>
        /// 单/复数配对的另一侧属性名。例如 `LayerName` 的 `PairPath` 是 `LayerNames`。
        /// 列表编辑器据此在"只有 1 项"时写单数字段、多项时写复数字段，避免两边都填。
        /// </summary>
        public string PairPath { get; set; }

        /// <summary>是否必填（缺失时红色阻断保存）。</summary>
        public bool Required { get; set; }

        /// <summary>值的种类，决定是否查符号表 / 是否给拾取按钮。</summary>
        public RuleFieldValidateAs ValidateAs { get; set; }

        /// <summary>帮助文字，显示在字段右侧的问号气泡里。</summary>
        public string Help { get; set; }

        public RuleFieldAttribute()
        {
            Editor = RuleFieldEditor.Text;
            AppliesTo = "*";
            Group = RuleFieldGroup.Action;
            ValidateAs = RuleFieldValidateAs.None;
        }
    }
}
