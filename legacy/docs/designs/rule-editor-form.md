# 规则编辑页：从 JSON 文本框改为 schema 驱动的表单

* **Author:** Jun
* **Status:** Proposed（本轮范围已收窄，见"本轮范围"）
* **Date:** 2026-10-29

--------------------------------------------------------------------------------

## Overview

`Jun.AutoCAD.BatchTool` 是配置驱动的 AutoCAD / 中望CAD 批处理插件。一份 `config.json` 描述若干条规则，
每条规则由 `Filter`（选中什么）与 `Action`（改什么）组成，插件按数组顺序在模型空间、全部布局与块定义内部执行。

规则模型是两个共享的封闭类（`Config/Jun.AutoCAD.BatchConfig.cs`）：`RuleFilter` 16 个字段、
`RuleAction` 43 个字段，11 种规则类型共用，字段全部可空。哪个类型用哪些字段，目前只存在于各 handler 的代码
与 `config.json` 的注释里，界面上没有任何提示。

界面的「规则编辑」页签（`UI/Jun.AutoCAD.BatchToolForm.cs`）现在是三个竖向堆叠的卡片：配置文件卡 →
规则列表卡 → 一个多行文本框，里面是当前选中规则的 JSON。用户直接改 JSON 文本，点「保存规则到配置」时
由 `Core/Jun.AutoCAD.ConfigSaver.cs` 整段替换 `Rules` 数组。

本次改动分两轮。**本轮**只做两件事：让规则摘要说真话（覆盖全部已设置条件），让保存时能发现写错的名称。
**下一轮**才把 JSON 文本框换成按规则类型生成的表单。这个拆分不是缩水，是因为评审发现表单自身会引出
七个新问题（见"下一轮遗留项"），而摘要与名称校验是任何表单的严格前置——先做它们，本轮就有可交付的价值。

使用场景决定了取舍：规则**只有作者一人编写**，写好后交给不写代码的绘图同事执行；同事不改规则，
只需要看懂"这条规则在做什么"。因此表单的首要价值是**让作者不必记字段名与取值写法**，次要价值是
让同事能读懂；它不需要承担多用户编辑、权限、并发冲突这些职责。

## 本轮范围

评审（`consolidated_review_report.md`）结论：13 条阻断项里有 8 条由表单机制自身产生，不会因文档写细而消失。
据此把交付物拆成两轮：

| 目标 | 本轮 | 下一轮 |
|---|---|---|
| G1 作者不必记字段名（表单） | — | ✔ |
| G2 同事能读懂（摘要说真话） | ✔ | 增强（字段面板上的默认值提示） |
| G3 编辑不丢未知字段 | ✔（`Extra`，P1） | 沿用 |
| G4 校验分级且全覆盖 | ✔（红级只用于必填与数值；名称类只给黄，理由见 P0） | 沿用 |
| G5 切换类型不留残留 | — | ✔ |
| G6 双宿主不退化 | ✔ | ✔ |

本轮交付物：**P0（摘要补全 + 名称校验）+ P1（`Extra` 扩展字段袋 + 单一校验器）**。
P2（表单）与 P3（拾取）保留在本文件中作为下一轮的设计输入，但不在本轮实现。


## The Problem

**问题一：不知道有哪些字段、每个字段能填什么值。**

想按文字内容筛选，找不到入口；想给块归层，不知道要写 `SourceBlockName` 还是 `SourceBlockNames`；
手敲 `AR-CONC`、`A1_TITLE`、图层名时打错一个字符，执行结果是 0 命中，命令行只告诉你"没有任何对象被修改"，
不会告诉你名字拼错了。字段的可选值（`Scope` 的四种取值、`PatternType` 的三种、颜色索引的含义）
只存在于源码注释与 README 表格里。

**问题二：改规则靠手写 JSON。**

数组、嵌套对象、可空类型的写法对非作者不友好；一条规则实际只用 3~6 个字段，但要看懂哪些字段属于
当前类型，得先知道这个类型的实现。

**现状为什么解决不了这两个问题。**

- **摘要是有损的。** `RuleSummary.DescribeFilter` 不输出 `LayerNames`、`DimensionStyleName`、`TrueColor`、
  `Scope`、`SkipLockedLayers`、`IncludeNestedBlocks`、`SourcePatternNames`；`DescribeAction` 对未知类型返回空串。
  于是 `LayerNames=[A,B] → 图层 X` 会渲染成 `(全部) → 图层 X`——同事读到"改全图"，实际只改两层。
  自研规则类型的摘要直接空白。靠它支撑"同事看得懂"是不成立的。
- **校验没有 error 级别。** `ConfigLoader.ValidateByType` 只往 `List<string>` 里塞 warning，
  没有阻断能力；11 种类型里只覆盖 8 种，`PurgeUnused` / `EntityLineweightChange` / `SetByLayer` 零校验。
- **表单编辑会丢字段。** `ConfigLoader` 用 `MissingMemberHandling.Ignore` 读配置，未知字段在进内存时就被丢弃；
  写回时 `ConfigSaver.SaveRules` 整段重写 `Rules` 数组。走"反序列化 → 改 → 序列化"的表单，
  手写在 JSON 里的自定义字段必然消失。
- **规则类型的元数据无处安放。** 新增一种规则类型，只能靠 README 表格与示例配置告诉使用者有哪些字段。

## Goals

* **G1（下一轮）作者改规则不需要记字段名。** 选中一条规则，面板只显示该类型用得上的字段，
  每个字段有中文标签、控件、单位与生效默认值；不需要翻源码或 README 才能填对。
* **G2（本轮）同事能读懂一条规则在做什么。** 规则列表的摘要覆盖全部已设置的条件；
  无法在单行里展开的条件以"+N 项条件"标注，且这个 N 必须可核对（见 P0 的验收条件）；
  自研规则类型有非空摘要。
  **验收**：取 10 条真实规则（含 2 条自研类型），让一位绘图同事只看列表回答"这条改哪些图层/对象"，
  ≥9 条正确且未打开 JSON 即通过。
* **G3（本轮）编辑不丢未知字段。** 手写在 JSON 里、模型不认识的字段，经过一次"读入 → 修改 → 保存"后
  仍然存在、值不变。范围是 `Rules` 数组元素的 `Filter` / `Action` 两层（`RuleConfig` 层的未知字段不在本轮，
  见"下一轮遗留项"）。
* **G4（本轮）校验有分级且全覆盖。** 红色阻断保存、黄色仅提醒；11 种内置规则类型全部有校验
  （当前 3 种为零校验）；未选中规则的问题在列表行上可见。
  **本轮的红色只用于**：必填缺失、数值越界、类型级前置条件不满足。名称不存在一律黄色——
  理由见 P0：批处理的目标是目录里的一批图纸，当前打开的这张不是判据。
* **G5（下一轮）切换规则类型不留残留。** 把一条规则从 A 类型改成 B 类型后，A 类型独有的字段被清空
  （有内容时先确认），不会出现"字段不可见但仍在生效"。
* **G6（两轮都要）双宿主不退化。** AutoCAD 2021~2025 与中望CAD 2020 都能编译；
  UI 层不直接引用 `Autodesk.AutoCAD.*` 或 `ZwSoft.ZwCAD.*` 的类型。

## Non-Goals

* **本轮不做表单**：`RuleEditorDrawer`、字段控件、拾取按钮全部推迟到下一轮，本轮不写一行 UI 渲染代码。
* **不做 `DryRun`（预演）**：只统计命中、不写数据库。价值高但独立，不在本次范围。
* **不新增规则类型**：`LayerMerge`、文字查找替换等只在讨论里出现过，不进本次。
* **不做多人并发编辑配置、不做权限**：规则只有作者一人写。
* **不做"从图上拾取"之外的交互式取值**：例如框选一批对象反推过滤条件。
* **不做实时下拉值域**：图层/块/样式名不实时从图纸读，改为保存时校验 + 最近匹配。
* **不提供第二个 JSON 编辑通道**：JSON 只读预览；要手改走已有的「打开配置文件」按钮。
* **不改执行引擎**：事务边界、统计口径、日志格式、规则执行顺序一律不动。

## Open Questions

* **Q1（下一轮）11 种类型的字段清单具体内容。** 类型 × 字段路径 × 中文标签 × 控件 × 单位 × 生效默认值。
  这份数据量比渲染代码大，是 P2 的入口条件，不在本文档展开。
* **Q2（本轮）最近匹配的算法与阈值。** 编辑距离还是前缀匹配、阈值取多少、多个候选如何排序。
  P0 先实现"忽略大小写相等 → 前缀 → 编辑距离 ≤ 2"，实测后调。
* **Q3（下一轮）`RuleDetailForm`（现有双击查看详情窗口）与新的只读 JSON 预览是否合并。**
* **已决：schema 的数据形状。** 属性反射推导（`[RuleField]` 标在 `RuleFilter`/`RuleAction` 的属性上）。
  这一选择推翻了 `concepts_brief.md` 的决策 1；其代价（`AppliesTo` 两份账、类型级联动要靠表达式）
  记录在"下一轮遗留项"里，不在本轮偿还。


## Critical User Journeys

### CUJ 1（下一轮）: 作者新增一条块归层规则

* **Given**：已打开一张图纸，`BATCHUI` 窗口停在「规则编辑」页签，`config.json` 已加载。
* **When**：在类型下拉里选 `BlockLayerChange` → 点「添加规则」→ 在面板里填块名、目标图层 →
  点「保存规则到配置」。
* **Then**：列表末尾出现新规则，摘要显示"块 A1_TITLE → 图层 TK-FRAME"；
  `config.json` 的 `Rules` 数组新增该条，数组之外的注释与字段顺序保持不变；
  若目标图层在当前图纸里不存在，状态栏给黄色提示但不阻断保存。

### CUJ 2（本轮）: 作者把图层名拼错

* **Given**：某条规则的目标图层写成 `TK-FRAM`，当前图纸里只有 `TK-FRAME`。
* **When**：点「保存规则到配置」。
* **Then**：保存继续完成；该字段旁出现黄色提示"图层「TK-FRAM」在当前图纸中不存在，
  最接近「TK-FRAME」"；规则列表行出现黄色徽标。

### CUJ 3（本轮，仅校验器部分）: 作者漏填必填字段

* **Given**：`BlockReplace` 规则只填了源块名，目标块名为空。
* **When**：点「保存规则到配置」。
* **Then**：保存被阻断；目标块名字段标红并显示原因，焦点跳到该字段；
  磁盘上的 `config.json` 不变；列表行显示红色徽标。

### CUJ 4（本轮）: 同事执行别人写好的规则

* **Given**：同事拿到一份 `config.json`，不了解规则语法。
* **When**：`BATCHUI` 打开 → 在规则列表里逐条看摘要 → 勾选要跑的几条 → 点「执行」。
* **Then**：同事不需要打开 JSON 就能判断每条规则会改什么；命令行输出与日志路径与改动前一致。

### CUJ 5（下一轮）: 作者新增一种自研规则类型

* **Given**：在仓库内新增一个 `IRuleHandler` 实现，并实现其 schema。
* **When**：重新编译并 `NETLOAD`。
* **Then**：规则类型下拉里出现新类型；选中它时面板自动渲染其字段；它的类型级校验、摘要、默认值
  全部可用，不需要改 UI 代码，也不需要改 `ConfigLoader` 的 if-else 链。

### CUJ 6（本轮）: 配置里存在手写的未知字段

* **Given**：作者在 `config.json` 里给某条规则手写了一个模型不认识的字段 `"Note": "临时"`。
* **When**：在表单里改这条规则的另一个字段，然后保存。
* **Then**：`"Note": "临时"` 仍然在文件里；面板底部提示"另有 1 个未识别字段（已保留）"；
  只读 JSON 预览里能看到它。

## Technical Plan (Architecture)

> **范围说明**：本节描述的是完整形态（含下一轮的表单），保留它是因为 P0/P1 的接口要按这个方向留口。
> 本轮只实现其中四项：`RuleSummary`（改造）、`RuleValidator`、`NameValidator`（含 `INameLookup` / `HostNameLookup`）、
> 以及 `RuleFieldAttribute` 之外的基础类型（`FieldIssue`、`ConfigValues`）。
> 字段元数据、`FieldCatalog`、三个 UI 组件与 `PickBridge` 属于下一轮，不在本轮实现。

### 组件划分

| 组件 | 位置 | 职责 | 是否碰宿主类型 |
|---|---|---|---|
| `RuleFieldAttribute` | `Config/` | 字段元数据载体：标签、控件、可见类型、分组、顺序、单位、候选值、默认值、条件、必填、校验种类、帮助 | 否 |
| `FieldCatalog` | `Core/` | 反射 `RuleFilter`/`RuleAction`，按 `AppliesTo` 过滤当前类型、按 `Group`+`Order` 排序，产出 `FieldDescriptor` 列表 | 否 |
| `FieldControlFactory` | `UI/` | 按 `Editor` 造控件（文本框/数值框/三态开关/下拉/多选/标签列表），并负责读写模型属性 | 否 |
| `FieldRow` | `UI/` | 一行：中文标签 + 控件 + 单位 + 生效默认值提示 + 错误边框 + 帮助气泡 | 否 |
| `RuleEditorDrawer` | `UI/` | 模态抽屉（900×600），头部 + 三个分组 + 校验条 + 只读 JSON 预览 | 否 |
| `RuleValidator` | `Core/` | 唯一的校验入口，返回 `FieldIssue(Path, Severity, Message)`；吸收并替换 `ConfigLoader.ValidateByType` 的 if 链 | 否 |
| `NameValidator` | `Core/` | 保存时对照符号表校验名称，给最近匹配；依赖 `INameLookup` | 否 |
| `INameLookup` / `HostNameLookup` | `Core/` | 接口 + 宿主实现（`#if ZWCAD` 双份），开短事务读符号表 | **是**（仅实现类） |
| `PickBridge` | `Core/` + `Commands/` | 拾取块名/图层名的异步桥：请求 → 排队命令 → 回填/取消 | **是**（仅命令侧） |
| `RuleSummary`（改造） | `Core/` | 摘要覆盖全部已设置条件；未知类型回落 | 否 |

UI 三个组件与 `RuleFieldAttribute`、`FieldCatalog`、`RuleValidator`、`NameValidator` 都不引用
`Autodesk.AutoCAD.*` 或 `ZwSoft.ZwCAD.*`。宿主只在 `HostNameLookup` 与拾取命令里出现。

### 数据流

```text
BatchToolForm (tab2)
  ├─ 规则列表（自绘卡片行，全宽）
  │    摘要 = RuleSummary.Describe(rule)  ← 行内徽标：红/黄
  └─ RuleEditorDrawer（双击规则行打开，模态 900×600）
        ├─ 头部：名称 / 类型徽标 / 启用开关
        ├─ 分组「过滤」「操作」「高级」（空组隐藏）
        │    └─ FieldRow × N  ← FieldControlFactory
        ├─ 校验条：红 N 项 / 黄 N 项 / 另有 M 个未识别字段（已保留）
        └─ JSON 只读预览（可折叠）
```

渲染链路：

```text
RuleConfig（内存对象）
  → FieldCatalog.For(rule.Type)
        反射 RuleFilter + RuleAction 的公开实例属性
        取 [RuleField]；AppliesTo 为空或不含当前类型 → 跳过
        按 Group 分桶，桶内按 Order 升序（同 Order 按属性声明顺序）
  → IReadOnlyList<FieldDescriptor>
  → FieldControlFactory.Create(descriptor, rule.Filter / rule.Action)
  → FieldRow（编辑即写回属性）
```

保存链路：

```text
点「保存规则到配置」
  → 各 FieldRow 已即时写回模型（无独立"提交"步骤）
  → RuleValidator.Validate(rule) → FieldIssue[]
        红：阻断保存，标红，焦点跳第一个红字段，磁盘不动
        黄：允许保存，标黄，列表行徽标
  → NameValidator.Validate(rule)（best-effort，读失败则跳过，绝不阻断）
  → ConfigSaver.SaveRules(path, config)
```

### 字段分级：引擎级与 handler 级（2026-10-29 评审后新增）

评审指出：`DxfFilter.Build` 对部分 `Filter` 字段是**无条件读取**的，与 `Type` 无关。
如果下一轮的表单按 `AppliesTo` 把它们在某个类型下隐藏，就会造出"界面上看不见、执行时照样生效"的字段。
因此字段必须先分成两级：

| 级别 | 判定依据 | 字段 | 读取点 |
|---|---|---|---|
| **引擎级**（永不可隐藏） | `DxfFilter` / `DatabaseWalker` / `EntityScanner` 直接读 | `EntityTypes`、`ColorIndex`、`ColorIndexes`、`TrueColor`、`LayerName`、`LayerNames`、`SourceBlockName`、`SourceBlockNames`、`Scope`、`MaxNestingDepth` | `DxfFilter.cs:74-180`、`EntityScanner.cs:29-35` |
| **引擎级（逐 handler 检查）** | 由每个 handler 自己读，但语义对所有类型成立 | `SkipLockedLayers`、`IncludeNestedBlocks` | `IRuleHandler.cs:105-124` 及各 handler |
| **handler 级**（按类型显示） | 只有某几个 handler 读 | `TextStyleName`（`TextFontChangeRule.cs:94`）、`DimensionStyleName`（`DimensionStyleChangeRule.cs:71`）、`SourcePatternName` / `SourcePatternNames`（`HatchPatternChangeRule.cs:196-203`） | 见左 |

下一轮的 `FieldCatalog` 必须能表达这个区分（`AppliesTo="*"` 不足以表达"引擎级"，
因为引擎级字段在**任何**类型下都不能隐藏，而 `AppliesTo="*"` 只是"所有类型都显示"）。
本轮不实现 `FieldCatalog`，但 P0 的摘要必须按这个分级来决定"哪些条件一定要出现在摘要里"。

**顺带发现的既有缺陷：`IncludeNestedBlocks` 是死字段。**
它在 `BatchConfig.cs:173` 声明、README 第 466 行文档化、`config.json:35` 使用、
`RuleExecutionContext.IncludeNestedBlocks`（`IRuleHandler.cs:105`）也暴露了，
但**没有任何代码读这个属性**——`DatabaseWalker.Collect` 只收 `maxDepth`，不接受"是否进入块定义"。
把它设为 `false` 当前毫无效果。

处理：本轮修掉（P0 第 6 项）。理由是零风险——默认值是 `true`，与当前行为一致；
只有显式写 `false` 的配置会从"没效果"变成"按文档生效"。这不算改执行引擎，是把一个已文档化的开关接上。


```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class RuleFieldAttribute : Attribute
{
    public string Label { get; set; }        // 中文标签
    public RuleFieldEditor Editor { get; set; }   // 控件种类
    public string AppliesTo { get; set; }    // "BlockLayerChange,EntityLayerChange"；"*" 表示全部
    public RuleFieldGroup Group { get; set; }     // Filter / Action / Advanced
    public int Order { get; set; }
    public string Unit { get; set; }         // "0.01mm"、"度"、"0~1"
    public string Values { get; set; }       // "ModelAndLayouts=模型+全部布局;ModelOnly=仅模型"
    public string Default { get; set; }      // 生效默认值的显示文本："true"、"7"
    public string VisibleWhen { get; set; }  // "Action.ForceByLayer != true"
    public bool Required { get; set; }
    public RuleFieldValidateAs ValidateAs { get; set; }  // None / Layer / Block / TextStyle / DimStyle / Linetype / Pattern
    public string Help { get; set; }
}
```

用法（伪代码，非最终代码）：

```csharp
[RuleField(Label = "目标图层", Editor = RuleFieldEditor.Text, ValidateAs = RuleFieldValidateAs.Layer,
           AppliesTo = "BlockLayerChange,EntityLayerChange", Group = RuleFieldGroup.Action, Order = 10,
           Required = true, Help = "图层不存在时按下面的颜色/线型/线宽自动创建")]
public string TargetLayer { get; set; }
```

**控件种类**：`Text`（单行）、`Multiline`（多行，用于 `Help`/`Note` 一类）、`Integer`、`Double`、
`Toggle`（`bool?` 三态）、`Enum`（单选下拉）、`MultiEnum`（多选，如 `PurgeCategories`）、
`TagList`（`List<string>`，如 `EntityTypes`、`SourceBlockNames`）、`ReadOnly`。

**`ValidateAs` 与拾取的关系**：`ValidateAs` 只声明"这个字段是一个图层名/块名/…"，
它同时驱动两件事——保存时对照符号表校验、以及是否显示「拾取」按钮（仅 `Layer` 与 `Block`）。
这样控件枚举保持与宿主无关，宿主知识不进 `UI`。

**三态开关**：`bool?` 渲染为「不设置 / 是 / 否」，标签旁显示"不设置（默认：是）"，
`默认` 取自 `Default`。不设置写回 `null`（序列化时被精简掉），"否"写回 `false`。

### 校验

`RuleValidator` 是唯一入口，返回 `FieldIssue { Path, Severity, Message }`，`Path` 形如
`Action.TargetLayer`、`Filter.SourceBlockNames`。

- **红（Error，阻断保存）**：必填缺失、数值越界、类型级前置条件不满足
  （例如 `HatchPatternChange` 的 `TargetPatternName` 与 `TargetGradientName` 同时为空；
  `EntityLinetypeChange` 既没给 `LinetypeName` 也没开 `ForceByLayer`）、
  `List<string>` 为空但该字段语义上要求至少一项。
- **黄（Warning，仅提醒）**：名称在当前图纸中不存在（带最近匹配）、`PatternScale <= 0`、
  非 `UserDefined` 图案却设了 `PatternDouble`、未知字段存在。

`ConfigLoader` 现有的 `List<string> warnings` 出口保持不变：它把 `FieldIssue` 里 Severity=Warning
的条目转成字符串塞进去，因此命令行路径与 `BATCHDIAG` 的行为不变。红级只存在于 UI 保存路径。

**校验失败不得变成阻断**：`NameValidator` 读符号表失败（无文档、锁冲突、异常）时返回"未知"，
不产生任何 issue。

### 扩展字段袋

`RuleFilter` 与 `RuleAction` 各加：

```csharp
[JsonExtensionData]
public IDictionary<string, JToken> Extra { get; set; }
```

读取时未知字段进 `Extra` 而不是被丢弃；`RuleJson` 序列化时自动带出。表单不渲染它们，
校验条显示"另有 N 个未识别字段（已保留）"，只读 JSON 预览里能看到。

### 切换类型清理残留

规则类型从 A 改为 B 时：

```text
B 的可见字段集 = FieldCatalog.For(B) 的 Path 集合
A 独有且当前非 null 的字段 = 逐个收集
  非空 → 弹确认（列出中文标签），确认后反射置 null
  为空 → 直接切
```

不清理会导致"字段不可见但仍在生效"，比 JSON 编辑更危险。

### 只读 JSON 预览

复用 `RuleJson.Serialize(rule)` 的文本，只读展示 + 「复制 JSON」。要手改走已有的「打开配置文件」
按钮，不引入第二个编辑通道，因此不需要双向同步。

### 拾取（P3）

```text
UI 点「拾取」→ 抽屉把"编辑副本 + 目标字段"装进 PickContinuation 并**关闭自己**
  → 主窗口（非模态上下文）PickBridge.Request(kind) 拿到请求号
  → SendStringToExecute("BATCHPICK")
  → 命令：TryTake → MarkStarted（界面据此停掉看门狗）→ Editor.GetEntity
       成功 → PickBridge.Complete(token, 名称)
       取消 → PickBridge.Complete(token, null)   // Esc 必须回到"取消"，不能静默
  → 回调里 BeginInvoke 重开抽屉，把名字填回原字段
  → 每一步写 DiagnosticLog（含请求号与命令回执状态）
```

**为什么不是"Hide 抽屉再排队"**（2026-10-29 实机修正）：`Form.Hide()` 会让模态窗口的
`ShowDialog` 立即返回（实测 .NET 8：Hide 后 13ms 返回 `DialogResult.Cancel`），
调用方的 `using` 随即 Dispose 抽屉——窗口凭空消失，拾取结果也无处回填。
因此抽屉必须**整个关掉**，命令才能推进、结果也才有地方落；同时看门狗只兜底"命令没跑起来"，
一旦命令进入等用户点选就停掉，不能把"用户慢慢点"当成超时（旧实现 4 秒就取消，实测就是这样丢结果的）。

与现有 `UiRunBridge` 的区别：`UiRunBridge` 是单向的（放请求、取一次即清空），拾取需要回传，
因此是**独立的一条桥**，不复用；并且每个请求带请求号，旧请求的取消/超时不能误伤新请求。

### 双宿主约束

- `RuleFieldAttribute`、`FieldCatalog`、`FieldControlFactory`、`FieldRow`、`RuleEditorDrawer`、
  `RuleValidator`、`NameValidator`：不得出现 `Autodesk.*` / `ZwSoft.*`；
- `HostNameLookup`：`#if ZWCAD` 双份实现，两套目标都要编译通过；
- 拾取命令在 `Commands/` 下，与现有命令同样处理条件编译。

### 已知缺陷与处理

| 缺陷 | 处理 |
|---|---|
| `AppliesTo` 与 handler 实际读取逻辑是两份账，断言无法证明"这个类型真的读这个字段" | 启动断言只校验"`AppliesTo` 里的类型名都已注册"；真实性靠每个类型一份最小有效配置的回归测试（P1 建立） |
| 类型级联动（`ForceByLayer` 隐藏 `LinetypeName`）单字段元数据表达不了 | 加 `VisibleWhen` 极简表达式，只支持 `路径 运算符 字面量` 与 `!=`/`==`，不做通用表达式引擎 |
| `Default` 属性里的默认值与 handler 里的运行时默认值可能不一致 | 回归测试断言：每个 `bool?` 字段置 `null` 与置 `Default` 声明的值，行为必须一致 |
| 呈现层元数据落在 `Config` 模型上（层依赖倒置） | `RuleFieldAttribute` 只含数据、无行为；`Config` 仍是 POCO，不引用 UI 程序集 |
| `RuleFactory.RegisterBuiltIns()` 晚于面板构建，自研类型不进下拉 | 在 `BuildEditPanel` 开头显式调用（幂等），不依赖 `PluginEntry` 的调用顺序 |
| 保存时整段替换 `Rules` 会删掉数组内的注释 | 保存前把原数组文本一并写入 `.bak`，状态栏提示"数组内注释已备份" |

## Alternatives Considered

### Alternative A: 保留 JSON 编辑，只加字段骨架插入与自动补全

* **Pros**：改动最小，不动 UI 结构；作者已熟悉 JSON；不需要为 11 种类型各维护一份字段清单。
* **Cons**：解决不了"不知道有哪些字段"——补全列表本身也需要一份字段清单，等于把 schema 换个地方写；
  自建补全引擎（候选、触发、插入位置、与注释共存）的成本接近表单的一半。
* **Reason for Rejection**：投入产出比不成立。真正的痛点不是打字慢，是不知道能填什么。

### Alternative B: WinForms `PropertyGrid` + `TypeConverter`

* **Pros**：几乎零渲染代码；自带排序、分组、下拉、类型转换；`PropertyGrid` 是成熟控件。
* **Cons**：中文标签与单位要硬塞进 `DisplayName`/`Description`；视觉与现有自绘卡片风格完全不搭
  （深色标题栏、系统字体、无圆角）；无法按规则类型裁剪字段——只能靠 `BrowsableAttribute` 在运行时反射改写，
  而 `PropertyGrid` 按类型缓存特性，多类型共用一个模型时行为不稳定。
* **Reason for Rejection**：与既有视觉体系冲突，且"按类型裁剪字段"这个核心需求恰好是它最弱的地方。

### Alternative C: 每种规则手写一个 `Form`

* **Pros**：布局可以逐类型精调；不需要元数据层；每个类型的字段与校验写在一起，读代码直观。
* **Cons**：11 个类型 × 平均 10 个字段，改一个字段要动多处；新增自研类型必须再写一个 Form；
  校验逻辑会在 11 个 Form 里重复。
* **Reason for Rejection**：与"自研类型要能被表单支持且不改 UI 代码"（CUJ 5）直接冲突。

### Alternative D: JSON Schema 文件 + 生成表单

* **Pros**：schema 与代码解耦，可被外部工具复用；标准格式，有现成校验器。
* **Cons**：要与 `RuleFilter`/`RuleAction` 双向保持一致，漂移风险比属性标注更高；
  需要引入 schema 加载与解析；WinForms 侧仍要自己写渲染器，标准格式带来的收益在本项目里用不上。
* **Reason for Rejection**：收益与属性标注相同，成本更高，还多一个必须同步的文件。

### Alternative E: 属性标注 + 独立的"类型 → 字段清单"注册表（brief 原设计）

* **Pros**：标签跟着属性走，不必重复写；类型清单独立成表，一眼能看出某个类型有哪些字段。
* **Cons**：**两份账**。标签在属性上、分组与可见性在注册表上，同一字段被两个类型引用且分组不同时无裁决规则；
  防漂移断言恒真——字段都躺在共享模型里，"路径能找到属性"必然成立，
  检不出真正要防的"清单声明了 X、handler 不读 X"。
* **Reason for Rejection**：评审阶段被三个视角独立否决。最终采用"属性标注是唯一事实源"，
  把 `AppliesTo` 也写在属性上，只保留一份。

### Alternative F: 每类型一个强类型 `Options` 类

* **Pros**：编译期安全，字段与类型一一对应；切换类型天然不留残留；JSON 不再是一个 59 字段的大袋子。
* **Cons**：改动面最大——11 个 handler、`DxfFilter`（读 `Filter.*`）、`RuleSummary`、`ConfigLoader`、
  示例配置、`ConfigSaver` 全要动；必须保证已部署的旧配置（扁平结构）继续能读，迁移期两套 JSON 形状并存；
  跨类型公共字段（`Scope`、`IncludeNestedBlocks`）仍需要基类，复杂度被搬走而非消失。
* **Reason for Rejection**：迁移风险与改动面在当前阶段不可接受；属性标注方案能在同一份模型上达到等效效果。

### Alternative G: 描述符列表（每类型返回 `RuleFieldDescriptor` 集合）

* **Pros**：完全数据驱动，一个渲染器吃所有类型；描述符可序列化，能直接生成 README 的字段表；
  字段清单与模型解耦，便于外部注册。
* **Cons**：路径是字符串（`Action.TargetLyer` 拼错照样编译通过），只能靠启动断言兜；
  IDE 搜不到引用；集合类字段需要额外路径语法；定制编辑逻辑（拾取按钮）要在描述符上开逃生口。
* **Reason for Rejection**：与 C 相比，主要优势是"清单可独立注册"，但本项目的自研规则写在同一仓库内，
  这个优势用不上；而字符串路径的代价是长期的。

### Alternative H: 表单与 JSON 双向实时同步

* **Pros**：作者既可以用表单也可以用 JSON，随时切换；高级用户不受表单限制。
* **Cons**：冲突合并、报错定位、光标与选区保持、局部非法文本如何暂存，成本极高；
  两套编辑器对同一个对象写值，时序问题难排查。
* **Reason for Rejection**：改为"JSON 只读预览 + 打开配置文件按钮"后，这类问题整体消失。

### Alternative I: 值域实时从图纸读取（下拉候选来自符号表）

* **Pros**：彻底消灭手敲名字；候选即真实存在的图层/块/样式，体验最直接。
* **Cons**：图纸 ≠ 批处理目标目录——在 A 图打开面板选中的候选，目标图里可能没有，静默 0 命中；
  填充图案名来自 `acad.pat` 不在符号表；非模式窗口读符号表的锁、事务、切图刷新全要新写；
  上千候选时的下拉性能与 `*U##` 匿名块过滤另算。
* **Reason for Rejection**：解决的不是同一个问题。改为保存时对照符号表校验 + 最近匹配，
  成本低一个数量级，且不受"当前打开哪张图"影响。

### Alternative J: 常驻右侧属性面板（左右分栏）

* **Pros**：列表与属性同屏，改完一条立刻看下一条，不需要开关抽屉。
* **Cons**：现窗口 735 宽，右面板需 380+，列表摘要会被截断；`MinimumSize` 要抬到 860 以上，
  1366×768 的笔记本上吃紧；`FlowLayoutPanel` 做不出分组标题吸顶，需自绘滚动容器。
* **Reason for Rejection**：为"同屏"付出布局重写 + 小屏不可用的代价。改为双击打开模态抽屉（900×600），
  列表保持全宽，窗口尺寸不变。

## Risks & Mitigations

| 风险 | 影响 | 缓解 |
|---|---|---|
| 名称校验误报：当前图纸不是批处理目标 | 用户被"图层不存在"的黄色提示反复干扰，最终学会忽略提示 | 全部为 `Warning` 不阻断；文案点明"在当前图纸 X 中不存在"；`CreateIfMissing` 类字段进豁免清单 |
| 接上 `IncludeNestedBlocks` 后行为变化 | 显式写了 `false` 的配置，改动后不再处理块内嵌套，统计数会变小 | 默认 `true` 与现状一致；README 写明"此前该开关无效，本次起生效"；人工验收用含嵌套块的图对比 |
| `Extra` 与未知字段告警重复 | 同一字段既被保留又被报"未知"，用户困惑 | `DetectUnknownFields` 在 `Extra` 非空时跳过该节告警 |
| `Extra` 平铺后与新增属性重名 | 输出重复键，`JToken.Parse` 不报错，静默落盘 | 序列化前做键集不相交断言，冲突键丢弃并计数 |
| 测试覆盖不到宿主路径 | 名称查询、遍历、保存的真实行为无自动化保障 | 明确列为已知限制，靠人工验收 1–6 条兜底 |
| 摘要文本变化影响既有使用者 | 依赖摘要文本的日志检索或人工习惯被打断 | 只增不改：已有片段保持原样，新条件追加在末尾 |

--------------------------------------------------------------------------------

## Detailed Implementation Plan

本轮只有两个阶段。每阶段结束时项目必须能编译、能 `NETLOAD`。
P2（表单）与 P3（拾取）已移出本轮，见文末"下一轮遗留项"。

### 阶段 P0：摘要说真话 + 名称校验

#### 1. `Core/Jun.AutoCAD.FieldIssue.cs`（创建）

* **Type / Scope**：Core 层，无宿主依赖。
* **Proposed Changes**：`enum IssueSeverity { Error, Warning }`；
  `sealed class FieldIssue { string Path; IssueSeverity Severity; string Message; }`；
  `Path` 约定为相对 `RuleConfig` 的点路径（`Filter.LayerName`、`Action.TargetLayer`），空串表示规则级问题。
* **为什么在 P0**：评审指出 `NameValidator` 返回 `FieldIssue`，若该类型定义在 P1，P0 无法编译。
  提到 P0 后，P1 的 `RuleValidator` 直接复用，不再改动签名。

#### 2. `Core/Jun.AutoCAD.ConfigValues.cs`（创建）+ `Core/Jun.AutoCAD.IRuleHandler.cs`（修改）

* **Type / Scope**：Core 层，无宿主依赖。
* **Proposed Changes**：把 `RuleExecutionContext` 里的 `Bool(bool?, bool)` / `Double` / `Int` 三个静态方法
  迁到新类 `ConfigValues`；`RuleExecutionContext` 上的同名方法保留为一行委托（不破坏现有 handler）。
* **为什么**：`RuleSummary.DescribeAction` 调用 `RuleExecutionContext.Bool`，而 `RuleExecutionContext` 的
  字段类型是 `Document`/`Transaction`，测试进程加载它会连带加载 `acdbmgd` 并失败。
  迁出后 `RuleSummary` 才可被测试覆盖。

#### 3. `Core/Jun.AutoCAD.RuleSummary.cs`（修改）

* **Type / Scope**：Core 层，无宿主依赖。
* **Proposed Changes**：
  * `DescribeFilter` **追加**当前遗漏的 8 个字段：`LayerNames`、`SourcePatternNames`、`TrueColor`、
    `DimensionStyleName`、`Scope`（非默认时）、`IncludeNestedBlocks=false`、`SkipLockedLayers=false`、
    `MaxNestingDepth`（非默认时）。**不要重复添加已有的** `SourceBlockName`、`SourceBlockNames`、
    `EntityTypes`、`ColorIndex`、`ColorIndexes`、`LayerName`、`SourcePatternName`、`TextStyleName`
    （`RuleSummary.cs:30-93` 已输出）。
  * 追加顺序固定：块名 → 实体类型 → 颜色 → 图层 → 图案 → 样式 → 标注样式 → 真彩色 → 范围/遍历开关 → `+N 项条件`。
  * `+N 项条件` 的计数口径：**已设置但未渲染的条件字段数**（按字段计，不按取值计）。
  * `DescribeAction` 对未识别类型返回 `"→ " + 类型名 + "（已设 N 项）"`，不再返回空串。
  * 新增只读属性 `HasUnrenderedConditions`，供后续 UI 判断是否需要展开态。

#### 4. `Core/Jun.AutoCAD.INameLookup.cs`（创建）

* **Type / Scope**：Core 层接口 + 枚举，无宿主依赖。
* **Proposed Changes**：`enum NameKind { Layer, Block, TextStyle, DimStyle, Linetype }`（**不含 Pattern**：
  填充图案名来自 `acad.pat`，不在符号表里，本轮不校验）；
  `interface INameLookup { NameLookupResult Lookup(NameKind kind, string name); }`；
  `NameLookupResult { bool Known; bool Found; string Nearest; }`，`Known=false` 表示查不了
  （无文档 / 读失败 / 锁冲突），调用方据此**不产生任何提示**。

#### 5. `Core/Jun.AutoCAD.NameValidator.cs`（创建）

* **Type / Scope**：Core 层，无宿主依赖。
* **Proposed Changes**：`Validate(RuleConfig rule, INameLookup lookup) → IReadOnlyList<FieldIssue>`，
  全部为 `Warning` 级。
  * 字段 → 种类映射（硬编码，P2 改为读 `[RuleField].ValidateAs`）：
    `Action.TargetLayer`→Layer、`Action.LayerLinetype`→Linetype、
    `Filter.LayerName`/`Filter.LayerNames`→Layer、
    `Filter.SourceBlockName`/`Filter.SourceBlockNames`/`Action.TargetBlockName`→Block、
    `Filter.TextStyleName`/`Action.TargetTextStyle`→TextStyle、
    `Filter.DimensionStyleName`/`Action.TargetDimensionStyle`→DimStyle。
  * **豁免清单**（这些字段允许指向不存在的名字，因为 handler 会自动创建）：
    `Action.TargetLayer`（仅当 `CreateIfMissing` 为 true 或未设置）、`Action.TargetTextStyle`、
    `Action.TargetDimensionStyle`。`Action.TargetBlockName` 不豁免（`BlockReplaceRule` 要求目标块已存在）。
  * 提示文案必须点明查的是哪张图：`"图层「A1_TITL」在当前图纸（<文件名>）中不存在，最接近「A1_TITLE」"`。
  * **为什么名称类只给黄色**：批处理的目标是目录里的一批图纸，当前打开的这张不是判据。
    把它做成红色会阻止用户保存一份对目标图纸完全正确的配置。

#### 6. `Core/Jun.AutoCAD.HostNameLookup.cs`（创建）

* **Type / Scope**：Core 层，**唯一**碰宿主的名称查询实现，`#if ZWCAD` 双份。
* **Proposed Changes**：`Lookup` 内部 `using (doc.LockDocument())` + 一个短事务读五张符号表，
  读全量名称后立即释放；块名候选排除 `*` 开头的匿名块；任何异常返回 `Known=false` 并写 `DiagnosticLog`
  （记录异常类型，不记录全栈到 UI）。

#### 7. `Core/Jun.AutoCAD.DatabaseWalker.cs`（修改）+ `Core/Jun.AutoCAD.EntityScanner.cs`（修改）

* **Type / Scope**：Core 层遍历。
* **Proposed Changes**：`Collect` 增加 `bool includeNested` 参数；为 `false` 时只处理顶层空间，
  不把块参照的块定义入队。`EntityScanner.Scan` 传入 `ctx.IncludeNestedBlocks`。
* **为什么**：`IncludeNestedBlocks` 当前是死字段（声明、文档化、示例配置都在用，但无人读取）。
  默认 `true`，与现状一致，只有显式写 `false` 的配置行为会改变。

#### 8. `UI/Jun.AutoCAD.BatchToolForm.cs`（修改）

* **Type / Scope**：UI 层，不得引用宿主类型。
* **Proposed Changes**：
  * 构造函数增加重载 `BatchToolForm(string configPath, INameLookup nameLookup)`；
    原单参构造保留并委托给 `this(configPath, null)`，不破坏既有调用点与测试。
  * `OnSaveRules`：在写盘前调用 `NameValidator`（本轮尚无 `RuleValidator`，**不要**提前调用它）；
    黄色 issue 显示在状态栏，不阻断；`INameLookup` 为 `null` 时整体跳过。
  * 规则卡片行增加黄色徽标（红级在 P1 接入）。
* **注意**：`BatchToolForm` 是 `sealed` 且只有一个构造函数（`BatchToolForm.cs:100`），
  唯一调用点是 `BatchProcessCommand.cs:167`。

#### 9. `Commands/Jun.AutoCAD.BatchProcessCommand.cs`（修改）

* **Type / Scope**：命令层，宿主感知。
* **Proposed Changes**：`BATCHUI` 里构造 `new HostNameLookup(doc)` 并注入 `BatchToolForm`。

#### 10. `README.md`（修改）

* **Type / Scope**：文档。
* **Proposed Changes**：
  * 第 1 节交付物表加 `Core/Jun.AutoCAD.FieldIssue.cs`、`ConfigValues.cs`、`INameLookup.cs`、
    `NameValidator.cs`、`HostNameLookup.cs`；
  * `#### 3.2 规则编辑页签`（第 270 行那个，**不是**第 362 行的 `### 3.2 多轮替换`）补"摘要覆盖全部条件、
    未显示条件以 +N 标注"与"保存时校验名称，黄色提示不阻断"；
  * 第 8 节验证情况表补本次构建与测试结果；
  * 修正 `IncludeNestedBlocks` 的行为说明（原文档说"默认 true"，未说明此前无效）。

### 阶段 P1：扩展字段袋 + 单一校验器

#### 11. `Config/Jun.AutoCAD.BatchConfig.cs`（修改）

* **Type / Scope**：配置模型。
* **Proposed Changes**：`RuleFilter` 与 `RuleAction` 各加
  `[JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }`，并 `using Newtonsoft.Json.Linq`。
  **本轮不加在 `RuleConfig` 上**（见"下一轮遗留项"）。
* **注意**：加了 `Extra` 后，`ConfigLoader.DetectUnknownFields` 仍会对同一个字段报"未知字段"警告
  （它读的是原始 JSON，与模型无关）。见第 14 项的配套改动。

#### 12. `Core/Jun.AutoCAD.RuleJson.cs`（修改）

* **Type / Scope**：序列化。
* **Proposed Changes**：
  * 序列化前断言 `Extra.Keys` 与同一层的 `[JsonProperty]` 名集合不相交；冲突时丢弃冲突键并记入
    "未识别字段"计数（避免 `[JsonExtensionData]` 平铺后产生重复键）；
  * 确认 `CompactContractResolver` 不会把空的 `Extra` 当作"空对象"丢弃（`JsonExtensionData` 走
    Newtonsoft 内部路径，不经过 `ShouldSerialize`，需一条 round-trip 测试固定这个行为）。

#### 13. `Core/Jun.AutoCAD.RuleValidator.cs`（创建）

* **Type / Scope**：Core 层，无宿主依赖。
* **Proposed Changes**：`static IReadOnlyList<FieldIssue> Validate(RuleConfig rule)`。
  把 `ConfigLoader.ValidateByType`（`ConfigLoader.cs:198-308`）的 if 链整体搬入并改造为带 `Path` 与
  `Severity`；为 `PurgeUnused`、`EntityLineweightChange`、`SetByLayer` 补校验（当前为零）。
  * `Error`：必填缺失、数值越界（`PatternScale <= 0`、`PatternSpace <= 0`、`Height <= 0`、
    `GradientShift` 不在 0~1、`ColorIndex` 不在 0~256）、类型级前置条件不满足
    （`HatchPatternChange` 两个目标皆空；`EntityLinetypeChange` 无 `LinetypeName` 且未开 `ForceByLayer`）；
  * `Warning`：其余原有告警保持原级别，避免把既有"能跑但可疑"的配置变成存不下去。
* **类型级校验为什么在这里而不在属性上**：`ForceByLayer` 与 `LinetypeName` 的关系是跨字段条件，
  单字段元数据表达不了。这一条与"属性标注是唯一事实源"并不冲突——属性描述**字段**，
  `RuleValidator` 描述**类型语义**，两者职责不同，但必须在文档里说清，避免又变成两份账。

#### 14. `Config/Jun.AutoCAD.ConfigLoader.cs`（修改）

* **Type / Scope**：配置加载。
* **Proposed Changes**：
  * 删除 `ValidateByType`，改为调用 `RuleValidator.Validate`；
  * `Severity == Warning` 的 issue 拼成字符串进 `List<string> warnings`（对外行为不变）；
    `Error` 级在加载路径同样只记 warning——加载不阻断，只有 UI 保存阻断；
  * `DetectUnknownFields`：某节（`Filter`/`Action`）的 `Extra` 非空时，跳过该节的未知字段告警，
    避免同一字段既被保留又被报警。

#### 15. `Core/Jun.AutoCAD.ConfigSaver.cs`（修改）

* **Type / Scope**：配置保存。
* **Proposed Changes**：
  * 修正 `CommentsPreserved`：当前恒等于 `hadComments`（`ConfigSaver.cs:133`），实际数组内注释已丢。
    改为在写盘后重新解析输出文本，检查 `Rules` 数组区间内是否还有 `//`；
  * `SaveRules` 在替换前把原数组文本写入 `config.json.rules.bak`，**仅当该区间内确实含注释时**；
  * `ConfigSaveResult` 增加 `RulesCommentsBackedUp`。

#### 16. `Jun.AutoCAD.BatchTool.csproj`（修改）

* **Type / Scope**：工程文件。
* **Proposed Changes**：加 `<ItemGroup><Compile Remove="tests/**/*.cs" /></ItemGroup>`。
* **为什么**：工程在仓库根，默认 `**/*.cs` 通配会把测试源码编进插件 DLL，`dotnet build` 直接失败。

#### 17. `tests/Jun.AutoCAD.BatchTool.Tests/`（创建，**不加入 `.sln`**）

* **Type / Scope**：测试工程（xunit，`net8.0-windows`，`PlatformTarget=x64`），通过 `ProjectReference`
  引用主工程。
* **Proposed Changes**：见"验证方案"。
* **为什么不进 `.sln`**：一旦进解决方案，`dotnet build -p:AcadVersion=ZWCAD2020` 会连带构建测试工程，
  而它引用的是 net8 目标 → 与"两套 0 错误 0 警告"冲突。用 `dotnet test tests/Jun.AutoCAD.BatchTool.Tests`
  单独运行。

### Verification Plan

**编译**

* `dotnet build`（AutoCAD 2025 / net8.0-windows）：0 错误 0 警告；
* `dotnet build -p:AcadVersion=ZWCAD2020`（net48）：0 错误 0 警告；
* 两套产物分别落在 `bin\Debug\` 与 `bin\Debug\ZWCAD2020\`。

**自动化测试**（`dotnet test tests/Jun.AutoCAD.BatchTool.Tests`）

* `RuleSummary`：`LayerNames=[A,B]`、`Scope=ModelOnly`、`SkipLockedLayers=false` 必须出现在摘要里；
  未识别类型摘要非空；`+N 项条件` 的计数与预期一致；
* `RuleValidator`：每个类型至少一条 `Error` 用例与一条 `Warning` 用例；三个原本零校验的类型各一条；
* `NameValidator`：`Known=false` 不产出 issue；豁免清单里的字段不产出 issue；
  `Found=false` 且最近匹配存在时产出 `Warning`；
* `RuleJson`：`Extra` 往返；冲突键被丢弃并计数；精简规则仍不输出 null/空集合；
* 已知边界：测试进程**不得触碰**任何规则 handler、`RuleExecutionContext`、`BatchRunner`。
  一旦引入 `acdbmgd` 类型，测试会因原生依赖缺失而失败（已实测）。

**人工验收**（必须记录结果）

1. CUJ 2：把 `TargetLayer` 写成 `TK-FRAM`，保存 → 黄色提示出现且**未阻断**，文件已写入；
2. CUJ 3：`BlockReplace` 漏填 `TargetBlockName`，保存 → 红色阻断，磁盘文件不变，焦点定位到该字段；
3. CUJ 4：拿现有 `config.json`（12 条规则）打开列表，逐条核对摘要是否与规则实际行为一致
   （重点：带 `LayerNames` 的那条不能再显示"(全部)"）；
4. CUJ 6：手工在 `Filter` 里加 `"Note": "临时"`，改另一字段后保存 → `Note` 仍在，底部提示"N 个未识别字段"；
5. 回归：`BATCHPROCESS` / `BATCHAGAIN` 行为与日志格式不变；`IncludeNestedBlocks=false` 的规则
   现在真的不进入块定义（用一张含嵌套块的图对比修改前后统计）；
6. 双宿主 `NETLOAD` + `BATCHDIAG` 各一次。

### 下一轮遗留项（P2 / P3，暂缓）

表单与拾取的设计见本文"Technical Plan"与"Alternatives Considered"，实施前必须先解决下表：

| 遗留项 | 来源 | 状态 | 说明 |
|---|---|---|---|
| 双击语义二义 | S7-design | 待办 | `EditRuleRow` 无 `RowDoubleClick`；决定：双击打开抽屉，`RuleDetailForm` 由抽屉内的只读 JSON 预览取代 |
| 抽屉尺寸与 DPI | S7-design | 待办 | 决定：按 `Screen.WorkingArea` 夹取，下限 760×520 |
| 红级校验的回滚语义 | S6 | **已定** | 抽屉编辑**深拷贝**，点「保存」才写回 `Rules` 并落盘；取消即丢弃副本，模型不会被污染 |
| `VisibleWhen` 隐藏不清值 | S6、S7-struct | **已定** | 取消 `VisibleWhen`：联动改为**禁用 + 说明**（`FieldRules` 硬编码），不隐藏。另：类型用不上但已有值的字段仍然显示（禁用态），见 `FieldCatalog.For` |
| 单/复数配对元数据 | S6、S9 | **已完成** | `RuleFieldAttribute.PairPath` 已加 |
| `Default` 第三份真相 | S5、S8 | **已定** | 属性上不声明默认值；三态开关只显示"不设置"，含义写在 `Help` 里 |
| `Values` / `VisibleWhen` 两个字符串 DSL | S5 | **已定** | `Values` 保留（枚举候选必须可声明）；`VisibleWhen` 取消，改硬编码 |
| `Extra` 未覆盖 `RuleConfig` 层 | S7-struct | 待办 | 需在 `RuleConfig` 上加 `Extra`（低风险，与 P2 一起做） |
| 复用主题与控件 | S9-code | **已完成** | `CardPanel` / `FlatButton` / `InputPanel` / `FlatButtonKind` / 配色常量改为 `internal`，新文件可直接复用，无需另建 Theme |
| 拾取与模态互锁 | S9 ×2 | **已解决（2026-10-29）** | 模态抽屉 `Hide()` 会让 `ShowDialog` 立即返回（实测 13ms）→ 改为"关掉抽屉 → 主窗口排队 `BATCHPICK` → 带 `PickContinuation` 重开抽屉"；看门狗只兜底"命令没跑起来"，命令开始等用户点选即停 |
| Q1 字段清单 | S5、S7 | **已完成** | 59 个字段已标注（标签 / 控件 / AppliesTo / 分组 / 顺序 / 单位 / 候选值 / 必填 / ValidateAs / Help） |
