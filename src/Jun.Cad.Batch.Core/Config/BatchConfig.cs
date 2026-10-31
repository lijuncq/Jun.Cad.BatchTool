using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jun.Cad.Batch.Config
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

        /// <summary>
        /// 规则中止（配置错、目标不存在、内部异常）时怎么处理这个文件。
        ///
        /// DiscardFile（默认）：整个文件不写盘，源文件保持原样——与 README "失败不写盘" 一致；
        /// WritePartial：把已成功规则的改动写出去（旧行为）。
        ///
        /// 为什么默认 DiscardFile：一条规则崩在中途时，内存里的文档已经处于"改了一半"的状态，
        /// 写出去就是一张半成品图；而用户看到的汇总只会说"某条规则中止"，不会意识到图上多了改动。
        /// </summary>
        [RuleField(Label = "规则中止时", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 5,
            Values = "DiscardFile=丢弃该文件（不写盘）;WritePartial=写出已完成的部分",
            Help = "默认 DiscardFile：有规则中止就整个文件不写盘")]
        [JsonProperty("OnRuleFailure")]
        public string OnRuleFailure { get; set; }

        /// <summary>
        /// 图形测量单位（AutoCAD 的 <c>$MEASUREMENT</c>）：决定 AutoCAD 按哪份图案库解析填充图案名。
        ///
        /// Auto（默认）：只有在"写进图的图案名在英制库 acad.pat 里不存在"时才改成公制。
        ///   典型例子是 JIS 系列（JIS_LC_8A 等），它们只存在于 acadiso.pat。
        ///   不改的后果：填充画得出来，但属性面板的预定义图案列表里找不到这个名字。
        /// Keep：不动（会在日志里告警）。
        /// Metric / English：强制。
        ///
        /// 只影响图案/线型库的选择，不动 INSUNITS，也不动任何已有实体的几何。
        /// </summary>
        [RuleField(Label = "图形测量单位", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 6,
            Values = "Auto=自动（图案名在英制库里没有时才改公制）;Keep=保持;Metric=强制公制（acadiso.pat）;English=强制英制（acad.pat）",
            Help = "$MEASUREMENT 决定按 acad.pat 还是 acadiso.pat 解析图案名；JIS 系列只在 acadiso.pat 里")]
        [JsonProperty("MeasurementUnits")]
        public string MeasurementUnits { get; set; }

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
    /// 见 Engine/EntityFilter.cs。
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
        /// 扫描范围：ModelAndLayouts（默认，模型空间 + 全部布局）/ ModelOnly / LayoutsOnly。
        /// 留空等价于 ModelAndLayouts——保持 null 而不是写死默认值，规则 JSON 才不会每行都多出一句
        /// "Scope": "ModelAndLayouts"。
        ///
        /// 离线版**没有** Selection（当前选择集）——没有交互会话可选。旧配置里写了这个值会降级为整张图。
        /// </summary>
        [RuleField(Label = "扫描范围", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 10, Values = "ModelAndLayouts=模型+全部布局;ModelOnly=仅模型;LayoutsOnly=仅布局", Help = "留空等价于模型+全部布局")]
        [JsonProperty("Scope")]
        public string Scope { get; set; }

        // ---------------- 空间 / 容器 / 区域（离线版的空间过滤） ----------------

        /// <summary>
        /// 布局名模式（通配符 <c>*</c> <c>?</c>，大小写不敏感），与 <see cref="Scope"/> 叠加。
        ///
        /// 语义：**列了模式就只处理匹配的布局**；模型空间只有在模式能匹配 "Model" 时才保留
        /// （`*` 会匹配它）。不列则按 Scope 处理全部。
        ///
        /// 为什么要这个：一张海工图里常有 Layout1/Layout2 这类空布局和十几个出图布局，
        /// 按 Scope=LayoutsOnly 会把空的也扫一遍，日志里全是噪声。
        /// </summary>
        [RuleField(Label = "布局名模式", Editor = RuleFieldEditor.TagList, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 11, Help = "通配符 * ?；留空=全部布局。列了模式就只处理匹配的布局（模型空间要写 Model 或 * 才保留）")]
        [JsonProperty("LayoutPatterns")]
        public List<string> LayoutPatterns { get; set; }

        /// <summary>
        /// 图框块名模式（通配符）。填了就只在**匹配的图框块参照包围盒内**取实体，
        /// 相当于"按图框裁一张纸"。
        ///
        /// 实现上不解析图框内部结构，直接取块参照的包围盒（实测 <c>Insert.GetBoundingBox()</c>
        /// 含块内实体、含缩放，可用）；多个图框取并集。
        /// </summary>
        [RuleField(Label = "图框块名模式", Editor = RuleFieldEditor.Text, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 12, ValidateAs = RuleFieldValidateAs.Block, Help = "通配符；填了只处理落在这些图框块参照包围盒内的对象。留空=不限")]
        [JsonProperty("ContainerBlockPattern")]
        public string ContainerBlockPattern { get; set; }

        /// <summary>图框属性条件：属性 Tag（配合 <see cref="ContainerAttributePattern"/> 进一步筛图框）。</summary>
        [RuleField(Label = "图框属性 Tag", Editor = RuleFieldEditor.Text, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 13, Help = "只处理该属性值匹配的图框；与下一项配对使用")]
        [JsonProperty("ContainerAttributeTag")]
        public string ContainerAttributeTag { get; set; }

        /// <summary>图框属性值模式（通配符）。</summary>
        [RuleField(Label = "图框属性值模式", Editor = RuleFieldEditor.Text, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 14, Help = "通配符；留空表示只要属性存在即可")]
        [JsonProperty("ContainerAttributePattern")]
        public string ContainerAttributePattern { get; set; }

        /// <summary>
        /// 几何区域形状：None（默认）/ Rect（矩形，取 <see cref="RegionPoints"/> 前两点）/ Polygon（多边形）。
        /// </summary>
        [RuleField(Label = "区域形状", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 15, Values = "None=不限;Rect=矩形;Polygon=多边形", Help = "矩形取前两个点；多边形至少三个点。与图框条件同时存在时取交集")]
        [JsonProperty("RegionMode")]
        public string RegionMode { get; set; }

        /// <summary>区域顶点，每项 <c>"x,y"</c>（图纸单位）。矩形写对角两点即可。</summary>
        [RuleField(Label = "区域顶点", Editor = RuleFieldEditor.TagList, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 16, Help = "每项写 x,y（如 0,0 与 10000,5000）。矩形写两点，多边形至少三点")]
        [JsonProperty("RegionPoints")]
        public List<string> RegionPoints { get; set; }

        /// <summary>
        /// 区域判定谓词：CenterInside（默认，包围盒中心在区域内）/ FullyInside（完全包含）/
        /// Intersects（部分相交）。
        ///
        /// 为什么默认中心点：文字、块参照、曲线都只有**轴对齐包络**，用"完全包含"会漏掉
        /// 一半跨界的对象，用"部分相交"会把擦边的全捞进来。中心点是唯一"错得最少"的选择。
        /// </summary>
        [RuleField(Label = "区域判定", Editor = RuleFieldEditor.Enum, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 17, Values = "CenterInside=中心点在区域内;FullyInside=完全包含;Intersects=部分相交", Help = "默认中心点；包络盒都是近似的，边界上会有误差")]
        [JsonProperty("RegionPredicate")]
        public string RegionPredicate { get; set; }

        /// <summary>
        /// 区域/图框过滤是否也作用于**块定义内部**的实体（默认 false）。
        ///
        /// 默认关闭的原因：块定义内实体的坐标是**块坐标**，不是世界坐标，
        /// 现在没有累计变换矩阵（<see cref="Engine.ScanHit"/> 只记了 Owner 和深度），
        /// 直接拿去和世界坐标区域比是错的。要做对得先给扫描结果加变换链。
        /// </summary>
        [RuleField(Label = "区域过滤含块内实体", Editor = RuleFieldEditor.Toggle, AppliesTo = "*", Group = RuleFieldGroup.Advanced, Order = 18, Help = "默认否：块内实体是块坐标，没有累计变换，按世界坐标判定会错位")]
        [JsonProperty("RegionIncludesNested")]
        public bool? RegionIncludesNested { get; set; }


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
        [RuleField(Label = "图案比例", Editor = RuleFieldEditor.Double, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 340, Help = "绝对值，不做相对缩放；填了就以它为准，不再保持线距")]
        [JsonProperty("PatternScale")]
        public double? PatternScale { get; set; }

        /// <summary>
        /// 保持视觉线距（默认 true）。换图案时按"旧线距 × 旧比例 ÷ 新图案基准线距"重算比例，
        /// 让替换后看起来的疏密与替换前一致。
        ///
        /// 为什么默认开：各图案在 .pat 里的基准线距差别很大（ANSI31 是 3.175、JIS_LC_8A 是 7.8、
        /// JIS_RC_20 是 20），沿用原比例直接换名字，视觉上会突然变密或变疏。
        ///
        /// 关掉它、或显式写了 <see cref="PatternScale"/> 时，按老办法处理（比例保持原值或取配置值）。
        /// 源填充是纯色/渐变（没有图案线）时无从比较，也会退回老办法。
        /// </summary>
        [RuleField(Label = "保持线距", Editor = RuleFieldEditor.Toggle, AppliesTo = "HatchPatternChange", Group = RuleFieldGroup.Action, Order = 345, Help = "默认是：按新图案的基准线距反算比例，替换后疏密不变；填了「图案比例」时以比例为准")]
        [JsonProperty("MatchSpacing")]
        public bool? MatchSpacing { get; set; }

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

        // ---------------- 文字替换（TextReplace） ----------------

        /// <summary>
        /// 匹配模式：Exact（整段相等）/ Contains（默认，子串）/ WholeWord（词边界）/ Regex（正则）。
        ///
        /// WholeWord 对中文会退化为 Contains——中文没有空格分词，"词边界"无从判断，
        /// 硬按 <c>\b</c> 写会一个都匹配不上。
        /// </summary>
        [RuleField(Label = "匹配模式", Editor = RuleFieldEditor.Enum, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 500, Values = "Exact=整段相等;Contains=包含;WholeWord=全词;Regex=正则", Help = "默认包含；全词对中文会退化为包含")]
        [JsonProperty("MatchMode")]
        public string MatchMode { get; set; }

        /// <summary>查找内容（TextReplace 必填）。</summary>
        [RuleField(Label = "查找内容", Editor = RuleFieldEditor.Text, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 510, Required = true, Help = "MTEXT 里只会匹配纯文字部分，格式码不会被替换")]
        [JsonProperty("SearchText")]
        public string SearchText { get; set; }

        /// <summary>替换为（可为空串，但需要 <see cref="AllowEmptyResult"/> 才生效）。</summary>
        [RuleField(Label = "替换为", Editor = RuleFieldEditor.Text, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 520, Help = "正则模式下支持 $1 捕获组")]
        [JsonProperty("Replacement")]
        public string Replacement { get; set; }

        /// <summary>是否区分大小写（默认 false）。</summary>
        [RuleField(Label = "区分大小写", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 530, Help = "默认否")]
        [JsonProperty("CaseSensitive")]
        public bool? CaseSensitive { get; set; }

        /// <summary>不可见属性是否参与替换（默认 false）。</summary>
        [RuleField(Label = "包含不可见属性", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 540, Help = "默认否：改了用户也看不见，出问题更难发现")]
        [JsonProperty("IncludeInvisibleAttributes")]
        public bool? IncludeInvisibleAttributes { get; set; }

        /// <summary>允许把文字替换成空串（默认 false，避免批量删文字）。</summary>
        [RuleField(Label = "允许替换为空", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 550, Help = "默认否；打开后匹配项会被清空")]
        [JsonProperty("AllowEmptyResult")]
        public bool? AllowEmptyResult { get; set; }

        /// <summary>是否处理 ACAD_TABLE 单元格（默认 true，但需要前置检查通过）。</summary>
        [RuleField(Label = "处理表格单元格", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 560, Help = "默认是；合并单元格只处理主格，字段单元格跳过")]
        [JsonProperty("ReplaceInTables")]
        public bool? ReplaceInTables { get; set; }

        /// <summary>是否处理 DIMENSION 的显式文字（默认 false）。测量值永远不改。</summary>
        [RuleField(Label = "处理标注文字", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextReplace", Group = RuleFieldGroup.Action, Order = 570, Help = "默认否；只改显式覆盖的文字，测量值不动")]
        [JsonProperty("ReplaceDimensionText")]
        public bool? ReplaceDimensionText { get; set; }

        // ---------------- 文字遮罩（TextMask） ----------------

        /// <summary>遮罩方式：Auto（默认，Wipeout 优先、探测失败降级 Solid）/ Wipeout / Solid。</summary>
        [RuleField(Label = "遮罩方式", Editor = RuleFieldEditor.Enum, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 600, Values = "Auto=自动（优先 Wipeout）;Wipeout=仅 Wipeout;Solid=仅 2D Solid", Help = "只影响 TEXT/ATTRIB 的新建遮罩；MTEXT 走原生背景遮罩。Auto：探测失败自动降级 Solid")]
        [JsonProperty("MaskMode")]
        public string MaskMode { get; set; }

        /// <summary>遮罩留白，按字高的倍数（默认 0.15）。</summary>
        [RuleField(Label = "留白比例", Editor = RuleFieldEditor.Double, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 610, Unit = "× 字高", Help = "默认 0.15")]
        [JsonProperty("MaskPaddingRatio")]
        public double? MaskPaddingRatio { get; set; }

        /// <summary>遮罩留白，绝对值（图纸单位，叠加在比例留白之上）。</summary>
        [RuleField(Label = "留白（绝对）", Editor = RuleFieldEditor.Double, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 620, Unit = "图纸单位")]
        [JsonProperty("MaskPadding")]
        public double? MaskPadding { get; set; }

        /// <summary>只在文字被其它图元遮挡时才加遮罩（默认 true）。</summary>
        [RuleField(Label = "仅在有遮挡时添加", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 630, Help = "默认是：先判断文字矩形被压住多少，再决定加不加")]
        [JsonProperty("MaskOnlyWhenOccluded")]
        public bool? MaskOnlyWhenOccluded { get; set; }

        /// <summary>遮挡面积占比阈值（默认 0.05）。</summary>
        [RuleField(Label = "遮挡阈值", Editor = RuleFieldEditor.Double, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 640, Unit = "0~1", Help = "文字矩形被压住的面积占比达到该值才算遮挡，默认 0.05")]
        [JsonProperty("MaskMinOccludedRatio")]
        public double? MaskMinOccludedRatio { get; set; }

        /// <summary>
        /// 块参照的包络盒是否参与遮挡判定（默认 false = 不参与）。
        ///
        /// 块参照的包围盒是**包络**：一个只画了一根管的设备块，包络可能覆盖半张图。
        /// 用它判遮挡会把整片文字都判成"被压住"，所以默认只看块内实体、不看块参照整体。
        /// </summary>
        [RuleField(Label = "忽略块参照包络", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 650, Help = "默认是（忽略）：块参照包络往往远大于实际图形，用它判遮挡会大面积误判")]
        [JsonProperty("MaskIgnoreBlockBounds")]
        public bool? MaskIgnoreBlockBounds { get; set; }

        /// <summary>
        /// 加遮罩时把"盖住文字的填充"送到绘制顺序最底层（默认 true）。
        ///
        /// 遮罩要么是 MTEXT 背景、要么在标注的 *D 块里，都跟着文字的绘制位置走。
        /// 填充如果排在文字之后（排序表里 sort handle 更大 = 画在上面），
        /// 遮罩会被填充整块盖住——图上看着和"没加遮罩"一模一样，而数据里明明有。
        /// </summary>
        [RuleField(Label = "填充置后", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 645, Help = "默认是：把盖住文字的填充送到绘制顺序最底层。关掉时若填充画在文字之上，遮罩会被填充盖住、看不见")]
        [JsonProperty("MaskSendHatchToBack")]
        public bool? MaskSendHatchToBack { get; set; }

        /// <summary>遮罩放到哪个图层（不存在会自动建）。</summary>
        [RuleField(Label = "遮罩图层", Editor = RuleFieldEditor.Text, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 660, ValidateAs = RuleFieldValidateAs.Layer, Help = "默认 _MASK；不存在时自动创建")]
        [JsonProperty("MaskLayer")]
        public string MaskLayer { get; set; }

        /// <summary>Solid 遮罩的颜色索引（默认 255）。</summary>
        [RuleField(Label = "Solid 颜色", Editor = RuleFieldEditor.Integer, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 670, Help = "默认 255（白/黑随背景）；只在降级为 Solid 时生效")]
        [JsonProperty("MaskSolidColorIndex")]
        public int? MaskSolidColorIndex { get; set; }

        /// <summary>Solid 遮罩图层是否可打印（默认 false = 不打印）。</summary>
        [RuleField(Label = "Solid 图层可打印", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 680, Help = "默认否：Solid 是不透明实心，可打印会在图上留白块")]
        [JsonProperty("MaskLayerPlottable")]
        public bool? MaskLayerPlottable { get; set; }

        /// <summary>
        /// 允许为 TEXT / ATTRIB 重建实体以把遮罩放到文字之下（默认 false）。
        ///
        /// 为什么需要这个开关：MTEXT 有原生背景遮罩，TEXT 没有；新建的 Wipeout/Solid
        /// 只能追加到实体列表末尾（画在文字上面，会把文字盖住）。要放到下面只能
        /// "移出文字 → 加遮罩 → 再加回文字"，而这一步会**重新分配文字句柄**
        /// （实测 48 → 4A，<c>Handle</c> 只读无法恢复）。
        /// 默认关：宁可不动，也不悄悄改句柄。
        /// </summary>
        [RuleField(Label = "允许重建文字实体", Editor = RuleFieldEditor.Toggle, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 695, Help = "默认否：TEXT/ATTRIB 没有原生遮罩，新建遮罩只能排在文字之后；要放到下面必须重建文字（会改句柄）")]
        [JsonProperty("MaskRebuildText")]
        public bool? MaskRebuildText { get; set; }

        /// <summary>
        /// 标注文字遮罩模式：Off（默认，记 Blocked 并说明原因）/ BlockInternal（把遮罩放进标注的 *D 块）。
        ///
        /// BlockInternal 的代价与收益：
        ///   * 收益：遮罩在标注自己的缓存块里，**跟着标注一起移动/缩放**，比放模型空间更贴合；
        ///   * 代价：标注一旦被编辑（AutoCAD 重生成 *D 块），块内手工加的图元会**被清掉**；
        ///     为了把遮罩排到文字之前，还要把块内文字实体移到末尾，它的句柄会变（缓存实体，风险较低）；
        ///   * 默认关闭：没有宿主可验证"重生成后会不会丢"，宁可先不动。
        /// </summary>
        [RuleField(Label = "标注遮罩模式", Editor = RuleFieldEditor.Enum, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 697, Values = "Off=不处理;BlockInternal=放进 *D 块", Help = "默认不处理；放进 *D 块会随标注移动，但标注重生成后会丢失")]
        [JsonProperty("DimensionMaskMode")]
        public string DimensionMaskMode { get; set; }

        /// <summary>单张图的遮罩上限（默认 2000），超出记 Blocked。</summary>
        [RuleField(Label = "单图遮罩上限", Editor = RuleFieldEditor.Integer, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 690, Help = "默认 2000，防止一张图被加上万个遮罩导致打开变慢")]
        [JsonProperty("MaxMasksPerDrawing")]
        public int? MaxMasksPerDrawing { get; set; }

        /// <summary>标注文字范围：ExplicitOnly（默认，只处理显式文字覆盖）/ All（含测量值，会告警）。</summary>
        [RuleField(Label = "标注文字范围", Editor = RuleFieldEditor.Enum, AppliesTo = "TextMask", Group = RuleFieldGroup.Action, Order = 700, Values = "ExplicitOnly=仅显式文字;All=全部（含测量值）", Help = "默认仅显式覆盖；测量值文字位置依赖缓存块，可靠性更低")]
        [JsonProperty("DimensionTextMode")]
        public string DimensionTextMode { get; set; }

        // ---------------- 审计（DimTextAudit / AttributeAudit） ----------------

        /// <summary>标注文字与测量值的绝对容差（默认 0.5，图纸单位）。</summary>
        [RuleField(Label = "绝对容差", Editor = RuleFieldEditor.Double, AppliesTo = "DimTextAudit", Group = RuleFieldGroup.Action, Order = 720, Help = "文字数值与测量值之差超过它就算不一致，默认 0.5")]
        [JsonProperty("AuditTolerance")]
        public double? AuditTolerance { get; set; }

        /// <summary>相对容差（百分比，默认 0）。与绝对容差取较大者。</summary>
        [RuleField(Label = "相对容差", Editor = RuleFieldEditor.Double, AppliesTo = "DimTextAudit", Group = RuleFieldGroup.Action, Order = 725, Unit = "%", Help = "按测量值的百分比算容差，与绝对容差取较大者")]
        [JsonProperty("AuditTolerancePercent")]
        public double? AuditTolerancePercent { get; set; }

        /// <summary>是否也报告"没有显式文字"的标注（默认 false：跟随测量值是正常的）。</summary>
        [RuleField(Label = "报告未覆盖文字", Editor = RuleFieldEditor.Toggle, AppliesTo = "DimTextAudit", Group = RuleFieldGroup.Action, Order = 730, Help = "默认否：Text 为空或 \"<>\" 的标注是跟随测量值，属正常")]
        [JsonProperty("AuditIncludeEmptyText")]
        public bool? AuditIncludeEmptyText { get; set; }

        /// <summary>必填属性 Tag 清单（AttributeAudit 必填）。</summary>
        [RuleField(Label = "必填属性 Tag", Editor = RuleFieldEditor.TagList, AppliesTo = "AttributeAudit", Group = RuleFieldGroup.Action, Order = 735, Help = "逐个块参照检查这些 Tag 是否存在且非空")]
        [JsonProperty("RequiredAttributeTags")]
        public List<string> RequiredAttributeTags { get; set; }

        /// <summary>是否按默认值填充空属性（默认 false：只报告）。</summary>
        [RuleField(Label = "按默认值填充", Editor = RuleFieldEditor.Toggle, AppliesTo = "AttributeAudit", Group = RuleFieldGroup.Action, Order = 740, Help = "默认否：只报告不写。打开后按下一项的默认值填空")]
        [JsonProperty("AttributeAuditFillDefaults")]
        public bool? AttributeAuditFillDefaults { get; set; }

        /// <summary>属性默认值，写法 <c>"TAG=值;TAG2=值2"</c>。</summary>
        [RuleField(Label = "属性默认值", Editor = RuleFieldEditor.Text, AppliesTo = "AttributeAudit", Group = RuleFieldGroup.Action, Order = 745, Help = "TAG=值;TAG2=值2（只在「按默认值填充」打开时使用）")]
        [JsonProperty("AttributeDefaults")]
        public string AttributeDefaults { get; set; }

        // ---------------- 图层映射（LayerMap） ----------------

        /// <summary>
        /// 图层映射表，每项 <c>"旧层名模式=新层名"</c>，旧名支持通配符。
        /// </summary>
        [RuleField(Label = "图层映射", Editor = RuleFieldEditor.TagList, AppliesTo = "LayerMap", Group = RuleFieldGroup.Action, Order = 750, Help = "写法：0-PIPE*=PIPE（旧名支持 * ?）；多条各占一项")]
        [JsonProperty("LayerMappings")]
        public List<string> LayerMappings { get; set; }

        /// <summary>并层后是否删除已经没人用的源图层（默认 false）。</summary>
        [RuleField(Label = "删除空源层", Editor = RuleFieldEditor.Toggle, AppliesTo = "LayerMap", Group = RuleFieldGroup.Action, Order = 755, Help = "默认否：只并层不删层。打开后只删并完之后真的没人用的源层")]
        [JsonProperty("LayerMapDeleteEmpty")]
        public bool? LayerMapDeleteEmpty { get; set; }

        // ---------------- 布局改名（LayoutRename） ----------------

        /// <summary>
        /// 布局改名映射，每项 <c>"旧名模式=新名"</c>；新名支持 <c>{name}</c>（原名）与 <c>{n}</c>（两位序号）。
        /// </summary>
        [RuleField(Label = "布局改名映射", Editor = RuleFieldEditor.TagList, AppliesTo = "LayoutRename", Group = RuleFieldGroup.Action, Order = 760, Help = "旧名模式=新名；新名可用 {name} 原名、{n} 两位序号，例如 V-{n}")]
        [JsonProperty("LayoutRenameMap")]
        public List<string> LayoutRenameMap { get; set; }

        /// <summary>改名后是否把新布局名写进图框的某个属性（可选）。</summary>
        [RuleField(Label = "同步到图框属性", Editor = RuleFieldEditor.Text, AppliesTo = "LayoutRename", Group = RuleFieldGroup.Action, Order = 765, Help = "填属性 Tag（如 TITLE）；留空则只改布局名")]
        [JsonProperty("LayoutRenameSyncTag")]
        public string LayoutRenameSyncTag { get; set; }

        // ---------------- 标题栏属性写入（TitleBlockNormalize） ----------------

        /// <summary>
        /// 标题栏属性写入表，每项 <c>"TAG=值"</c>。值支持 <c>{filename}</c> <c>{layout}</c> <c>{date}</c> <c>{index}</c>。
        /// </summary>
        [RuleField(Label = "属性写入表", Editor = RuleFieldEditor.TagList, AppliesTo = "TitleBlockNormalize", Group = RuleFieldGroup.Action, Order = 770, Help = "TAG=值；值可用 {filename} {layout} {date} {index} 占位符")]
        [JsonProperty("TitleBlockAttributeMap")]
        public List<string> TitleBlockAttributeMap { get; set; }

        /// <summary>只写空属性（默认 true），避免覆盖已经填好的内容。</summary>
        [RuleField(Label = "只填空属性", Editor = RuleFieldEditor.Toggle, AppliesTo = "TitleBlockNormalize", Group = RuleFieldGroup.Action, Order = 775, Help = "默认是：已有值不覆盖。关掉才会强制改写")]
        [JsonProperty("TitleBlockOnlyIfEmpty")]
        public bool? TitleBlockOnlyIfEmpty { get; set; }

        // ---------------- 几何清理（GeometryCleanup） ----------------

        /// <summary>清理零长度直线（默认 true）。</summary>
        [RuleField(Label = "清理零长度线", Editor = RuleFieldEditor.Toggle, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 780, Help = "默认是：长度小于容差的 LINE")]
        [JsonProperty("CleanZeroLength")]
        public bool? CleanZeroLength { get; set; }

        /// <summary>零长度判定容差（图纸单位，默认 0.001）。</summary>
        [RuleField(Label = "零长度容差", Editor = RuleFieldEditor.Double, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 785, Unit = "图纸单位", Help = "默认 0.001")]
        [JsonProperty("ZeroLengthTolerance")]
        public double? ZeroLengthTolerance { get; set; }

        /// <summary>清理空文字（默认 true）。</summary>
        [RuleField(Label = "清理空文字", Editor = RuleFieldEditor.Toggle, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 790, Help = "默认是：TEXT/MTEXT 去掉格式码后为空")]
        [JsonProperty("CleanEmptyText")]
        public bool? CleanEmptyText { get; set; }

        /// <summary>清理重复实体（默认 false：误删代价最高）。</summary>
        [RuleField(Label = "清理重复实体", Editor = RuleFieldEditor.Toggle, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 795, Help = "默认否：同层同类型且包围盒一致的实体只留一个")]
        [JsonProperty("CleanDuplicateEntities")]
        public bool? CleanDuplicateEntities { get; set; }

        /// <summary>重复判定容差（默认 0.0001）。</summary>
        [RuleField(Label = "重复容差", Editor = RuleFieldEditor.Double, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 800, Unit = "图纸单位")]
        [JsonProperty("DuplicateTolerance")]
        public double? DuplicateTolerance { get; set; }

        /// <summary>只预演不删除（默认 true）。</summary>
        [RuleField(Label = "只预演", Editor = RuleFieldEditor.Toggle, AppliesTo = "GeometryCleanup", Group = RuleFieldGroup.Action, Order = 805, Help = "默认是：只报告会删哪些，不真删。确认无误后关掉")]
        [JsonProperty("CleanupDryRun")]
        public bool? CleanupDryRun { get; set; }

        // ---------------- 材料表导出与核对（BomSync） ----------------

        /// <summary>材料表块的属性 Tag 顺序（第一项作为主键）。</summary>
        [RuleField(Label = "材料表列 Tag", Editor = RuleFieldEditor.TagList, AppliesTo = "BomSync", Group = RuleFieldGroup.Action, Order = 810, Help = "按顺序导出为列；第一项当主键做比对")]
        [JsonProperty("BomColumnTags")]
        public List<string> BomColumnTags { get; set; }

        /// <summary>导出 CSV 路径（相对路径按配置文件目录解析）。</summary>
        [RuleField(Label = "导出 CSV", Editor = RuleFieldEditor.Text, AppliesTo = "BomSync", Group = RuleFieldGroup.Action, Order = 815, Help = "留空则只做比对不导出")]
        [JsonProperty("BomExportPath")]
        public string BomExportPath { get; set; }

        /// <summary>要比对的基准 CSV（可选）。</summary>
        [RuleField(Label = "比对基准 CSV", Editor = RuleFieldEditor.Text, AppliesTo = "BomSync", Group = RuleFieldGroup.Action, Order = 820, Help = "留空则只导出，不比对")]
        [JsonProperty("BomComparePath")]
        public string BomComparePath { get; set; }

        // ---------------- 清理 ----------------

        /// <summary>
        /// 要清理的类别（默认全部）：Block / Layer / Linetype / TextStyle / DimStyle。
        /// 离线版不支持 RegApp / Group / MLeaderStyle（没有可靠的引用扫描入口，硬猜会误删）。
        /// </summary>
        [RuleField(Label = "清理类别", Editor = RuleFieldEditor.MultiEnum, AppliesTo = "PurgeUnused", Group = RuleFieldGroup.Action, Order = 420, Values = "Block=块;Layer=图层;Linetype=线型;TextStyle=文字样式;DimStyle=标注样式", Help = "留空表示全部类别")]
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
