using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jun.AutoCAD.Config
{
    /// <summary>
    /// 配置根对象。对应 config.json 的顶层结构。
    /// </summary>
    public sealed class BatchConfig
    {
        /// <summary>配置版本，便于将来做兼容迁移（当前固定 1）。</summary>
        [JsonProperty("Version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// 执行模式：
        ///   Sequential（默认）：所有规则共用同一个事务，规则按顺序执行，
        ///                       后一条规则看到的是前一条规则修改后的数据库状态。
        ///   Isolated          ：每条规则使用独立事务并立即提交，单条规则失败可整体回滚，
        ///                       规则之间仍然是"后看到前"的顺序可见性。
        ///   Parallel          ：AutoCAD 数据库不支持多线程并发写入，此值会被降级为 Isolated 并给出警告。
        /// </summary>
        [JsonProperty("ExecutionMode")]
        public string ExecutionMode { get; set; } = "Sequential";

        /// <summary>单条规则内部抛出异常时是否继续执行后续规则（默认 true，符合"不得整体中断"的要求）。</summary>
        [JsonProperty("ContinueOnError")]
        public bool ContinueOnError { get; set; } = true;

        /// <summary>日志文件路径。为空时默认写到"配置文件同目录\Logs\batch-yyyyMMdd-HHmmss.log"；填 "none" 关闭文件日志。</summary>
        [JsonProperty("LogFile")]
        public string LogFile { get; set; }

        /// <summary>是否把每条明细都打印到命令行（false 时命令行只输出统计，明细仅写入日志文件）。</summary>
        [JsonProperty("Verbose")]
        public bool Verbose { get; set; } = true;

        /// <summary>单条规则最多向命令行打印多少条明细，避免刷屏。</summary>
        [JsonProperty("MaxEditorDetailLines")]
        public int MaxEditorDetailLines { get; set; } = 100;

        /// <summary>规则列表，按数组顺序执行。</summary>
        [JsonProperty("Rules")]
        public List<RuleConfig> Rules { get; set; } = new List<RuleConfig>();
    }

    /// <summary>
    /// 一条规则的完整定义：Name/Type 是元信息，Filter 描述"选中什么"，Action 描述"改什么"。
    /// 过滤条件与操作彻底解耦，新增规则类型时无需改动过滤逻辑。
    /// </summary>
    public sealed class RuleConfig
    {
        /// <summary>规则名称，用于日志与统计输出。</summary>
        [JsonProperty("Name")]
        public string Name { get; set; }

        /// <summary>
        /// 规则类型（大小写不敏感）：BlockReplace / EntityLayerChange / TextFontChange /
        /// PurgeUnused / EntityLinetypeChange / EntityLineweightChange /
        /// DimensionStyleChange / SetByLayer / TextHeightChange
        /// </summary>
        [JsonProperty("Type")]
        public string Type { get; set; }

        /// <summary>是否启用该规则，默认 true。</summary>
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>覆盖全局 ContinueOnError（可选）。</summary>
        [JsonProperty("ContinueOnError")]
        public bool? ContinueOnError { get; set; }

        [JsonProperty("Filter")]
        public RuleFilter Filter { get; set; } = new RuleFilter();

        [JsonProperty("Action")]
        public RuleAction Action { get; set; } = new RuleAction();

        /// <summary>
        /// 规则这一层（`Filter`/`Action` 之外）的未知字段。与两个子对象上的同名成员一起，
        /// 保证"读入 → 编辑 → 保存"三步不会静默丢掉手写内容。
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Name) ? Type : Name;
        }
    }

    /// <summary>
    /// 过滤条件（"选中什么"）。所有字段都是可选的，按"与"关系叠加。
    /// 会被翻译成 DXF 组码组合（DxfCode.Start / DxfCode.Color / DxfCode.LayerName / DxfCode.BlockName），
    /// 见 Core/DxfFilter.cs。
    /// </summary>
    public sealed class RuleFilter
    {
        // ---------------- 块替换相关 ----------------

        /// <summary>源块名（BlockReplace 必填）。对动态块按"有效块名"（动态块定义名）匹配。</summary>
        [RuleField(Label = "源块名", Editor = RuleFieldEditor.Text, AppliesTo = "BlockReplace,BlockLayerChange", Group = RuleFieldGroup.Filter, Order = 10, ValidateAs = RuleFieldValidateAs.Block, Help = "动态块按有效块名匹配")]
        [JsonProperty("SourceBlockName")]
        public string SourceBlockName { get; set; }

        /// <summary>
        /// 多个源块名（与 SourceBlockName 取并集），用于"一次把好几种块归到同一图层"这类场景。
        /// BlockLayerChange 用它批量指定块；BlockReplace 只认单个源块（替换目标只能有一个）。
        /// </summary>
        [RuleField(Label = "源块名（多个）", Editor = RuleFieldEditor.TagList, AppliesTo = "BlockLayerChange", Group = RuleFieldGroup.Filter, Order = 20, ValidateAs = RuleFieldValidateAs.Block, PairPath = "SourceBlockName", Help = "一次归并好几种块；只写 1 项时会写回 SourceBlockName")]
        [JsonProperty("SourceBlockNames")]
        public List<string> SourceBlockNames { get; set; }

        /// <summary>
        /// 扫描范围：ModelAndLayouts（默认，模型空间 + 全部布局）/ ModelOnly / LayoutsOnly / Selection（当前选择集）。
        /// 留空等价于 ModelAndLayouts——保持 null 而不是写死默认值，规则 JSON 才不会每行都多出一句
        /// "Scope": "ModelAndLayouts"。
        /// </summary>
        [RuleField(Label = "扫描范围", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 10, Values = "ModelAndLayouts=模型+全部布局;ModelOnly=仅模型;LayoutsOnly=仅布局;Selection=当前选择集", Help = "留空等价于模型+全部布局")]
        [JsonProperty("Scope")]
        public string Scope { get; set; }

        // ---------------- 实体类型 / 颜色 / 图层 ----------------

        /// <summary>
        /// DXF 实体类型名列表，例如 ["LINE","LWPOLYLINE","CIRCLE"]、["TEXT","MTEXT"]。
        /// 使用 DXF 名称而非 .NET 类名（DIMENSION 覆盖全部标注子类，INSERT 即 BlockReference）。
        /// </summary>
        [RuleField(Label = "实体类型", Editor = RuleFieldEditor.TagList, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 30, Help = "DXF 类型名，如 LINE / LWPOLYLINE / TEXT；留空表示不限类型")]
        [JsonProperty("EntityTypes")]
        public List<string> EntityTypes { get; set; }

        /// <summary>单个颜色索引号：1=红、2=黄、3=绿、4=青、5=蓝、6=洋红、7=白/黑、256=随层、0=随块。</summary>
        [RuleField(Label = "颜色索引", Editor = RuleFieldEditor.Integer, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 40, Unit = "ACI 索引", Help = "1=红 2=黄 3=绿 4=青 5=蓝 6=洋红 7=白/黑 256=随层 0=随块")]
        [JsonProperty("ColorIndex")]
        public int? ColorIndex { get; set; }

        /// <summary>多个颜色索引号（与 ColorIndex 取并集），用于"红或黄"这类组合过滤。</summary>
        [RuleField(Label = "颜色索引（多个）", Editor = RuleFieldEditor.TagList, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 50, Help = "与单个颜色取并集，可写 1,3,5 这样组合")]
        [JsonProperty("ColorIndexes")]
        public List<int> ColorIndexes { get; set; }

        /// <summary>
        /// 真彩色过滤，格式 "#RRGGBB" 或 "RRGGBB"。
        /// 注意：真彩色无法用 DxfCode.Color（组码 62 只存 ACI 索引）过滤，
        /// 插件在遍历后会额外比较 Entity.Color.ColorValue，见 DxfFilter.Matches。
        /// </summary>
        [RuleField(Label = "真彩色", Editor = RuleFieldEditor.Text, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 60, Help = "#RRGGBB；只能用遍历判定，Scope=Selection 时不生效")]
        [JsonProperty("TrueColor")]
        public string TrueColor { get; set; }

        /// <summary>单个图层名过滤（匹配实体自身图层，不做随层解析）。</summary>
        [RuleField(Label = "图层", Editor = RuleFieldEditor.Text, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 70, ValidateAs = RuleFieldValidateAs.Layer, PairPath = "LayerNames", Help = "匹配实体自身图层，不做随层解析")]
        [JsonProperty("LayerName")]
        public string LayerName { get; set; }

        /// <summary>多个图层名过滤（与 LayerName 取并集）。</summary>
        [RuleField(Label = "图层（多个）", Editor = RuleFieldEditor.TagList, AppliesTo = "*", Group = RuleFieldGroup.Filter, Order = 80, ValidateAs = RuleFieldValidateAs.Layer, PairPath = "LayerName")]
        [JsonProperty("LayerNames")]
        public List<string> LayerNames { get; set; }

        // ---------------- 样式相关 ----------------

        /// <summary>当前文字样式名过滤（TextFontChange / TextHeightChange 可用）。</summary>
        [RuleField(Label = "当前文字样式", Editor = RuleFieldEditor.Text, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Filter, Order = 110, ValidateAs = RuleFieldValidateAs.TextStyle, Help = "只处理使用该样式的文字")]
        [JsonProperty("TextStyleName")]
        public string TextStyleName { get; set; }

        /// <summary>当前标注样式名过滤（DimensionStyleChange 可用）。</summary>
        [RuleField(Label = "当前标注样式", Editor = RuleFieldEditor.Text, AppliesTo = "DimensionStyleChange", Group = RuleFieldGroup.Filter, Order = 120, ValidateAs = RuleFieldValidateAs.DimStyle)]
        [JsonProperty("DimensionStyleName")]
        public string DimensionStyleName { get; set; }

        // ---------------- 填充图案相关 ----------------

        /// <summary>
        /// 源填充图案名过滤（HatchPatternChange 可用），例如 "AR-CONC"。
        /// 渐变填充会按渐变名（GradientName）参与匹配。
        /// </summary>
        [RuleField(Label = "源图案", Editor = RuleFieldEditor.Text, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Filter, Order = 90, ValidateAs = RuleFieldValidateAs.Pattern, PairPath = "SourcePatternNames", Help = "只改这种图案的填充；留空则改所有 HATCH")]
        [JsonProperty("SourcePatternName")]
        public string SourcePatternName { get; set; }

        /// <summary>多个源图案名（与 SourcePatternName 取并集）。</summary>
        [RuleField(Label = "源图案（多个）", Editor = RuleFieldEditor.TagList, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Filter, Order = 100, ValidateAs = RuleFieldValidateAs.Pattern, PairPath = "SourcePatternName")]
        [JsonProperty("SourcePatternNames")]
        public List<string> SourcePatternNames { get; set; }

        // ---------------- 遍历行为 ----------------

        /// <summary>是否递归进入块定义内部（默认 true）。关闭后只扫描顶层空间。</summary>
        [RuleField(Label = "递归块定义内部", Editor = RuleFieldEditor.Toggle, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 20, Help = "默认是；关掉后不进入任何块定义，只处理顶层实体")]
        [JsonProperty("IncludeNestedBlocks")]
        public bool? IncludeNestedBlocks { get; set; }

        /// <summary>最大嵌套递归深度，默认 8，防止异常图纸导致栈过深。</summary>
        [RuleField(Label = "最大嵌套深度", Editor = RuleFieldEditor.Integer, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 30, Help = "默认 8，防止异常图纸导致递归过深")]
        [JsonProperty("MaxNestingDepth")]
        public int? MaxNestingDepth { get; set; }

        /// <summary>是否跳过锁定图层上的实体（默认 true，避免修改锁定图层内容）。</summary>
        [RuleField(Label = "跳过锁定图层", Editor = RuleFieldEditor.Toggle, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 40, Help = "默认是")]
        [JsonProperty("SkipLockedLayers")]
        public bool? SkipLockedLayers { get; set; }

        /// <summary>
        /// 模型不认识的字段（手写在 JSON 里的自定义字段、自研规则类型自己的参数）。
        ///
        /// 之前 <c>ConfigLoader</c> 用 <c>MissingMemberHandling.Ignore</c> 读配置，未知字段在进内存时就被丢掉，
        /// 写回时自然也没了。加这个袋子之后它们被原样接住并写回，编辑一次配置不会再静默毁掉手写内容。
        ///
        /// 注意：袋子里的内容不参与任何业务逻辑，只做往返搬运。
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>
    /// 操作定义（"改什么"）。按规则类型只使用其中相关字段，其余留空即可。
    /// </summary>
    public sealed class RuleAction
    {
        // ---------------- BlockReplace ----------------

        /// <summary>目标块名（BlockReplace 必填），必须在当前图纸的块表中已存在。</summary>
        [RuleField(Label = "目标块名", Editor = RuleFieldEditor.Text, AppliesTo = "BlockReplace", Group = RuleFieldGroup.Action, Order = 10, Required = true, ValidateAs = RuleFieldValidateAs.Block, Help = "目标块必须已存在于当前图纸块表，插件不会跨图导入")]
        [JsonProperty("TargetBlockName")]
        public string TargetBlockName { get; set; }

        /// <summary>
        /// 是否允许替换动态块（默认 false）。false 时遇到动态块直接跳过并记录警告，
        /// 这是"动态块边界"的显式开关。
        /// </summary>
        [RuleField(Label = "允许替换动态块", Editor = RuleFieldEditor.Toggle, AppliesTo = "BlockReplace", Group = RuleFieldGroup.Action, Order = 20, Help = "默认否；关着时遇到动态块直接跳过并记警告")]
        [JsonProperty("AllowDynamicBlock")]
        public bool? AllowDynamicBlock { get; set; }

        /// <summary>是否迁移属性值（默认 true）。</summary>
        [RuleField(Label = "迁移属性值", Editor = RuleFieldEditor.Toggle, AppliesTo = "BlockReplace", Group = RuleFieldGroup.Action, Order = 30, Help = "按 Tag 名称把源块属性值搬到目标块")]
        [JsonProperty("MigrateAttributes")]
        public bool? MigrateAttributes { get; set; }

        // ---------------- BlockLayerChange ----------------

        /// <summary>
        /// 块归层时是否连同块参照上的属性（ATTRIB）一起移到目标图层。
        /// 属性是独立实体、各自带图层，只改块参照的图层不会带走属性。
        ///
        /// 未写时**跟随 <see cref="AlsoMoveBlockContent"/>**（后者默认 true）：
        /// 块内属性定义（ATTDEF）都改到目标图层了，已有 ATTRIB 却留在原图层，等于只修了一半。
        /// 显式写 false 才保留属性的原图层。
        /// </summary>
        [RuleField(Label = "属性一并归层", Editor = RuleFieldEditor.Toggle, AppliesTo = "BlockLayerChange", Group = RuleFieldGroup.Action, Order = 40, Help = "不写时跟随「块内对象一并归层」；显式关闭则属性留在原图层")]
        [JsonProperty("AlsoMoveAttributes")]
        public bool? AlsoMoveAttributes { get; set; }

        /// <summary>
        /// 块归层时是否连同**块定义内部的对象**一起移到目标图层（默认 true）。
        ///
        /// 只改块参照的图层，块里画的东西仍然留在原图层上——图上看起来"没变"，
        /// 因为块内对象自带图层，不随参照的图层走（只有画在 0 层上的块内对象才会跟随参照图层显示）。
        /// 置为 true 时会把块定义里的实体（含嵌套块、属性定义 ATTDEF）逐个改到目标图层。
        ///
        /// 注意：
        ///   * 改的是**块定义**，所以引用同一定义的所有插入会一起变——这正是批量治理要的效果；
        ///   * `Filter.IncludeNestedBlocks=false` 时本开关不生效（那个开关的语义就是"不进入块定义"）；
        ///   * 置为 false 则恢复"只挪块参照本身"的旧行为（<see cref="AlsoMoveAttributes"/> 未显式设置时也会跟着关闭）。
        /// </summary>
        [RuleField(Label = "块内对象一并归层", Editor = RuleFieldEditor.Toggle, AppliesTo = "BlockLayerChange", Group = RuleFieldGroup.Action, Order = 45, Help = "默认是：块定义里的实体（含嵌套块、属性定义）一起改到目标图层；关掉只挪块参照本身")]
        [JsonProperty("AlsoMoveBlockContent")]
        public bool? AlsoMoveBlockContent { get; set; }

        // ---------------- 图层 ----------------

        /// <summary>目标图层名（EntityLayerChange 必填）。</summary>
        [RuleField(Label = "目标图层", Editor = RuleFieldEditor.Text, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 50, Required = true, ValidateAs = RuleFieldValidateAs.Layer, Help = "不存在时按下面的颜色/线型/线宽自动创建")]
        [JsonProperty("TargetLayer")]
        public string TargetLayer { get; set; }

        /// <summary>目标图层不存在时是否自动创建（默认 true）。</summary>
        [RuleField(Label = "图层不存在时自动创建", Editor = RuleFieldEditor.Toggle, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 60, Help = "默认是")]
        [JsonProperty("CreateIfMissing")]
        public bool? CreateIfMissing { get; set; }

        /// <summary>新建图层使用的颜色索引（默认 7）。</summary>
        [RuleField(Label = "新建图层颜色", Editor = RuleFieldEditor.Integer, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 70, Help = "默认 7")]
        [JsonProperty("LayerColorIndex")]
        public int? LayerColorIndex { get; set; }

        /// <summary>新建图层使用的线型名（默认 Continuous）。</summary>
        [RuleField(Label = "新建图层线型", Editor = RuleFieldEditor.Text, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 80, ValidateAs = RuleFieldValidateAs.Linetype, Help = "缺失时会尝试从 acad.lin / acadiso.lin 加载")]
        [JsonProperty("LayerLinetype")]
        public string LayerLinetype { get; set; }

        /// <summary>新建图层使用的线宽（单位：0.01mm，例如 25 表示 0.25mm）。</summary>
        [RuleField(Label = "新建图层线宽", Editor = RuleFieldEditor.Enum, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 90,
            Values = "0=0.00mm;5=0.05mm;9=0.09mm;13=0.13mm;15=0.15mm;18=0.18mm;20=0.20mm;25=0.25mm;30=0.30mm;35=0.35mm;40=0.40mm;50=0.50mm;53=0.53mm;60=0.60mm;70=0.70mm;80=0.80mm;90=0.90mm;100=1.00mm;106=1.06mm;120=1.20mm;140=1.40mm;158=1.58mm;200=2.00mm;211=2.11mm",
            Help = "只能选 AutoCAD 支持的线宽（图层不能用随层/随块）；不设置则沿用 AutoCAD 默认")]
        [JsonProperty("LayerLineWeight")]
        public int? LayerLineWeight { get; set; }

        /// <summary>
        /// 新建图层是否可打印（默认 true，与 AutoCAD 新建图层的默认值一致）。
        /// 设为 false 时该图层上的对象不会被打印——适合"辅助线/标注定位点"这类只给看图用的图层。
        /// 只影响**自动创建**出来的图层；图层已存在时保持它原来的打印设置，规则不越权改它。
        /// </summary>
        [RuleField(Label = "新建图层可打印", Editor = RuleFieldEditor.Toggle, AppliesTo = "EntityLayerChange,BlockLayerChange", Group = RuleFieldGroup.Action, Order = 95, Help = "默认是；设为否后该图层上的对象不打印。只影响自动创建的新图层")]
        [JsonProperty("LayerPlottable")]
        public bool? LayerPlottable { get; set; }

        // ---------------- 文字样式 / 字体 ----------------

        /// <summary>目标文字样式名（TextFontChange 必填），不存在时自动创建。</summary>
        [RuleField(Label = "目标文字样式", Editor = RuleFieldEditor.Text, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 100, Required = true, ValidateAs = RuleFieldValidateAs.TextStyle, Help = "不存在时自动创建")]
        [JsonProperty("TargetTextStyle")]
        public string TargetTextStyle { get; set; }

        /// <summary>主字体文件名，例如 "gbenor.shx"、"simplex.shx"、"Arial"。</summary>
        [RuleField(Label = "字体文件", Editor = RuleFieldEditor.Text, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 110, Help = "gbenor.shx 这类 SHX，或 Arial 这类 TrueType")]
        [JsonProperty("FontFileName")]
        public string FontFileName { get; set; }

        /// <summary>大字体文件名（仅 SHX 有效），例如 "gbcbig.shx"。</summary>
        [RuleField(Label = "大字体", Editor = RuleFieldEditor.Text, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 120, Help = "仅 SHX 有效，例如 gbcbig.shx")]
        [JsonProperty("BigFont")]
        public string BigFont { get; set; }

        /// <summary>宽度因子（写入 TextStyleTableRecord.XScale）。</summary>
        [RuleField(Label = "宽度因子", Editor = RuleFieldEditor.Double, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 130)]
        [JsonProperty("WidthFactor")]
        public double? WidthFactor { get; set; }

        /// <summary>倾斜角，单位【度】（内部会换算成弧度写入 TextStyleTableRecord.ObliqueAngle）。</summary>
        [RuleField(Label = "倾斜角", Editor = RuleFieldEditor.Double, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 140, Unit = "度")]
        [JsonProperty("ObliqueAngle")]
        public double? ObliqueAngle { get; set; }

        /// <summary>样式固定字高（0 表示可变）。</summary>
        [RuleField(Label = "样式固定字高", Editor = RuleFieldEditor.Double, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 150, Unit = "0 = 可变")]
        [JsonProperty("TextStyleHeight")]
        public double? TextStyleHeight { get; set; }

        /// <summary>是否解析并替换 MText 内容里的 \F 内联字体格式码（默认 true）。</summary>
        [RuleField(Label = "解析 MText 内联字体", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextFontChange", Group = RuleFieldGroup.Action, Order = 160, Help = "处理内容里的内联字体码（反斜杠 F）")]
        [JsonProperty("HandleMTextInlineFont")]
        public bool? HandleMTextInlineFont { get; set; }

        /// <summary>
        /// 是否同步更新"标注样式"的文字样式（默认 true）。
        /// 标注文字的文字样式由标注样式控制，无法直接挂在 Dimension 实体上。
        /// </summary>
        [RuleField(Label = "同步标注样式", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextFontChange,TextHeightChange", Group = RuleFieldGroup.Action, Order = 170, Help = "标注文字由标注样式控制，这里一并改过去")]
        [JsonProperty("AlsoUpdateDimensionStyle")]
        public bool? AlsoUpdateDimensionStyle { get; set; }

        // ---------------- 线型 / 线宽 ----------------

        /// <summary>目标线型名，"ByLayer"/"ByBlock" 亦可（EntityLinetypeChange 必填）。</summary>
        [RuleField(Label = "目标线型", Editor = RuleFieldEditor.Text, AppliesTo = "EntityLinetypeChange", Group = RuleFieldGroup.Action, Order = 180, ValidateAs = RuleFieldValidateAs.Linetype, Help = "也可以写 ByLayer / ByBlock")]
        [JsonProperty("LinetypeName")]
        public string LinetypeName { get; set; }

        /// <summary>目标线宽（单位：0.01mm，例如 25 表示 0.25mm；-1=ByLayer，-2=ByBlock，-3=Default）。</summary>
        [RuleField(Label = "目标线宽", Editor = RuleFieldEditor.Enum, AppliesTo = "EntityLineweightChange", Group = RuleFieldGroup.Action, Order = 200,
            Values = "-1=随层 (ByLayer);-2=随块 (ByBlock);-3=默认 (Default);0=0.00mm;5=0.05mm;9=0.09mm;13=0.13mm;15=0.15mm;18=0.18mm;20=0.20mm;25=0.25mm;30=0.30mm;35=0.35mm;40=0.40mm;50=0.50mm;53=0.53mm;60=0.60mm;70=0.70mm;80=0.80mm;90=0.90mm;100=1.00mm;106=1.06mm;120=1.20mm;140=1.40mm;158=1.58mm;200=2.00mm;211=2.11mm",
            Help = "只能选 AutoCAD 支持的线宽；随层/随块/默认也可选")]
        [JsonProperty("LineWeight")]
        public int? LineWeight { get; set; }

        /// <summary>强制置为随层（覆盖 LinetypeName / LineWeight）。</summary>
        [RuleField(Label = "强制随层", Editor = RuleFieldEditor.Toggle, AppliesTo = "EntityLinetypeChange,EntityLineweightChange", Group = RuleFieldGroup.Action, Order = 190, Help = "打开后忽略上面的线型/线宽设置")]
        [JsonProperty("ForceByLayer")]
        public bool? ForceByLayer { get; set; }

        // ---------------- 标注样式 ----------------

        /// <summary>目标标注样式名（DimensionStyleChange 必填），不存在时可自动创建。</summary>
        [RuleField(Label = "目标标注样式", Editor = RuleFieldEditor.Text, AppliesTo = "DimensionStyleChange", Group = RuleFieldGroup.Action, Order = 210, Required = true, ValidateAs = RuleFieldValidateAs.DimStyle, Help = "不存在时以 Standard 为模板创建")]
        [JsonProperty("TargetDimensionStyle")]
        public string TargetDimensionStyle { get; set; }

        /// <summary>箭头大小（写入 DimStyleTableRecord.Dimasz）。</summary>
        [RuleField(Label = "箭头大小", Editor = RuleFieldEditor.Double, AppliesTo = "DimensionStyleChange", Group = RuleFieldGroup.Action, Order = 220, Help = "写入 Dimasz")]
        [JsonProperty("ArrowSize")]
        public double? ArrowSize { get; set; }

        /// <summary>标注文字高度（写入 DimStyleTableRecord.Dimtxt）。</summary>
        [RuleField(Label = "标注文字高度", Editor = RuleFieldEditor.Double, AppliesTo = "DimensionStyleChange", Group = RuleFieldGroup.Action, Order = 230, Help = "写入 Dimtxt")]
        [JsonProperty("TextHeight")]
        public double? TextHeight { get; set; }

        // ---------------- 文字高度 ----------------

        /// <summary>统一文字高度（TextHeightChange 必填）。</summary>
        [RuleField(Label = "统一字高", Editor = RuleFieldEditor.Double, AppliesTo = "TextHeightChange", Group = RuleFieldGroup.Action, Order = 240, Required = true)]
        [JsonProperty("Height")]
        public double? Height { get; set; }

        /// <summary>MText 行距系数（0.25~4.0，可选；修改字高时按比例联动行距时可显式指定）。</summary>
        [RuleField(Label = "MText 行距系数", Editor = RuleFieldEditor.Double, AppliesTo = "TextHeightChange", Group = RuleFieldGroup.Action, Order = 250, Unit = "0.25~4.0")]
        [JsonProperty("LineSpacingFactor")]
        public double? LineSpacingFactor { get; set; }

        // ---------------- SetByLayer ----------------

        /// <summary>是否把颜色置为随层（默认 true）。</summary>
        [RuleField(Label = "颜色随层", Editor = RuleFieldEditor.Toggle, AppliesTo = "SetByLayer", Group = RuleFieldGroup.Action, Order = 260, Help = "默认是")]
        [JsonProperty("ForceColorByLayer")]
        public bool? ForceColorByLayer { get; set; }

        /// <summary>是否把线型置为随层（默认 true）。</summary>
        [RuleField(Label = "线型随层", Editor = RuleFieldEditor.Toggle, AppliesTo = "SetByLayer", Group = RuleFieldGroup.Action, Order = 270, Help = "默认是")]
        [JsonProperty("ForceLinetypeByLayer")]
        public bool? ForceLinetypeByLayer { get; set; }

        /// <summary>是否把线宽置为随层（默认 true）。</summary>
        [RuleField(Label = "线宽随层", Editor = RuleFieldEditor.Toggle, AppliesTo = "SetByLayer", Group = RuleFieldGroup.Action, Order = 280, Help = "默认是")]
        [JsonProperty("ForceLineWeightByLayer")]
        public bool? ForceLineWeightByLayer { get; set; }

        /// <summary>是否把打印样式置为随层（默认 true）。</summary>
        [RuleField(Label = "打印样式随层", Editor = RuleFieldEditor.Toggle, AppliesTo = "SetByLayer", Group = RuleFieldGroup.Action, Order = 290, Help = "默认是")]
        [JsonProperty("ForcePlotStyleByLayer")]
        public bool? ForcePlotStyleByLayer { get; set; }

        // ---------------- 填充图案 ----------------

        /// <summary>
        /// 目标填充图案名（HatchPatternChange 必填，与 TargetGradientName 二选一），
        /// 例如 "ANSI31"、"AR-CONC"、"SOLID"（纯色填充）。
        /// 图案必须已在 acad.pat / acadiso.pat 中定义。
        /// </summary>
        [RuleField(Label = "目标图案", Editor = RuleFieldEditor.Text, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 300, ValidateAs = RuleFieldValidateAs.Pattern, Help = "必须已在 acad.pat / acadiso.pat 中定义；SOLID 表示纯色填充")]
        [JsonProperty("TargetPatternName")]
        public string TargetPatternName { get; set; }

        /// <summary>
        /// 目标渐变填充名（可选），填了就转成渐变填充，例如 "LINEAR"、"CYLINDER"、"SPHERICAL"。
        /// 与 TargetPatternName 同时存在时以渐变优先。
        /// </summary>
        [RuleField(Label = "目标渐变", Editor = RuleFieldEditor.Text, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 310, Help = "填了就转成渐变填充，优先于目标图案")]
        [JsonProperty("TargetGradientName")]
        public string TargetGradientName { get; set; }

        /// <summary>图案类型：PreDefined（默认）/ UserDefined / CustomDefined。</summary>
        [RuleField(Label = "图案类型", Editor = RuleFieldEditor.Enum, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 320, Values = "PreDefined=预定义;UserDefined=用户自定义;CustomDefined=自定义", Help = "默认 PreDefined")]
        [JsonProperty("PatternType")]
        public string PatternType { get; set; }

        /// <summary>渐变类型：PreDefinedGradient（默认）/ UserDefinedGradient。</summary>
        [RuleField(Label = "渐变类型", Editor = RuleFieldEditor.Enum, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 330, Values = "PreDefinedGradient=预定义渐变;UserDefinedGradient=用户自定义渐变")]
        [JsonProperty("GradientType")]
        public string GradientType { get; set; }

        /// <summary>填充图案比例（PatternScale），不写则保持原值。</summary>
        [RuleField(Label = "图案比例", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 340, Help = "绝对值，不做相对缩放")]
        [JsonProperty("PatternScale")]
        public double? PatternScale { get; set; }

        /// <summary>填充图案角度，单位【度】（内部换算成弧度），不写则保持原值。</summary>
        [RuleField(Label = "图案角度", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 350, Unit = "度")]
        [JsonProperty("PatternAngle")]
        public double? PatternAngle { get; set; }

        /// <summary>用户自定义图案的线间距（PatternSpace），不写则保持原值。</summary>
        [RuleField(Label = "线间距", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 360, Help = "仅用户自定义图案有效")]
        [JsonProperty("PatternSpace")]
        public double? PatternSpace { get; set; }

        /// <summary>用户自定义图案是否双线（PatternDouble）。</summary>
        [RuleField(Label = "双线", Editor = RuleFieldEditor.Toggle, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 370, Help = "仅用户自定义图案有效")]
        [JsonProperty("PatternDouble")]
        public bool? PatternDouble { get; set; }

        /// <summary>填充样式：Normal（普通，默认）/ Outer（外部）/ Ignore（忽略）。</summary>
        [RuleField(Label = "填充样式", Editor = RuleFieldEditor.Enum, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 380, Values = "Normal=普通;Outer=外部;Ignore=忽略")]
        [JsonProperty("HatchStyle")]
        public string HatchStyle { get; set; }

        /// <summary>渐变填充角度，单位【度】。</summary>
        [RuleField(Label = "渐变角度", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 390, Unit = "度")]
        [JsonProperty("GradientAngle")]
        public double? GradientAngle { get; set; }

        /// <summary>渐变填充偏移（0~1）。</summary>
        [RuleField(Label = "渐变偏移", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 400, Unit = "0~1")]
        [JsonProperty("GradientShift")]
        public double? GradientShift { get; set; }

        /// <summary>渐变填充是否单色模式。</summary>
        [RuleField(Label = "单色渐变", Editor = RuleFieldEditor.Toggle, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 410)]
        [JsonProperty("GradientOneColorMode")]
        public bool? GradientOneColorMode { get; set; }

        // ---------------- 清理 ----------------

        /// <summary>
        /// 要清理的类别（默认全部）：Block / Layer / Linetype / TextStyle / DimStyle / RegApp / Group / MLeaderStyle。
        /// </summary>
        [RuleField(Label = "清理类别", Editor = RuleFieldEditor.MultiEnum, AppliesTo = "PurgeUnused", Group = RuleFieldGroup.Action, Order = 420, Values = "Block=块;Layer=图层;Linetype=线型;TextStyle=文字样式;DimStyle=标注样式;RegApp=注册应用;Group=组;MLeaderStyle=多重引线样式", Help = "留空表示全部类别")]
        [JsonProperty("PurgeCategories")]
        public List<string> PurgeCategories { get; set; }

        /// <summary>清理的最大迭代轮次（默认 3），用于处理"块定义删掉后图层才空出来"这类链式依赖。</summary>
        [RuleField(Label = "迭代轮次", Editor = RuleFieldEditor.Integer, AppliesTo = "PurgeUnused", Group = RuleFieldGroup.Action, Order = 430, Help = "处理块删掉后图层才空出来这类链式依赖，默认 3")]
        [JsonProperty("PurgePasses")]
        public int? PurgePasses { get; set; }

        /// <summary>
        /// 模型不认识的字段（见 <see cref="RuleFilter.Extra"/>）。只做往返搬运，不参与业务逻辑。
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }
}
