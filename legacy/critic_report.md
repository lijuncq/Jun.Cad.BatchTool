# Stage 7 — Critic Review 报告（Feature Spec，1–398 行）

* **评审者**：egm-design、egm-structural、egm-style
* **标准**：`feature-spec-standard.md`
* **日期**：2026-10-29

---

## 一、设计视角（egm-design）

### 🔴 G2「同事能读懂」不可测

位置：Goals / G2、CUJ 4。
"同事能读懂"没有任何验收判据；G1、G3、G4、G5、G6 都能被测试证明，G2 不能。
**建议**：换成可判定的验收——取 10 条真实规则（含 2 条自研类型），让一位绘图同事只看列表，
回答"这条改哪些图层/对象"，≥9 条正确且不打开 JSON 即通过。

### 🔴 G2 与渲染现实冲突

位置：技术方案"数据流" vs `BatchToolForm.cs:2113-2116`。
摘要只有 18px 单行 + `EndEllipsis`，行高 58，约 600px 宽。"覆盖全部已设置条件"在物理上做不到；
"+N 项条件"是死胡同——同事点不开、展开不了，等于告诉他"还有你看不见的东西"。
**建议**：明确摘要的信息优先级（类型 + 过滤对象 + 目标，其余折叠），并给行一个只读展开态。

### 🔴 模态抽屉 900×600 与 735×700 父窗口 + DPI 缩放冲突

位置：技术方案"组件划分" vs 文档 Goals / Alternative J。
父窗口是 `AutoScaleMode.Dpi`；1366×768 笔记本 150% 缩放下，900×600 逻辑尺寸落到 1350×900 设备像素，
垂直放不下。Alternative J 正是以"小屏吃紧"被否，但选中的抽屉比被否的面板更宽。
**建议**：按 `Screen.WorkingArea` 夹取尺寸，给出 760×520 下限与非模态降级方案。

### 🔴 唯一入口是双击行，且与现有 `RuleDetailForm` 争用同一手势

行上已有开关，双击无任何可见提示；作者与同事共用同一个可写抽屉，同事误双击就进入编辑态。
**建议**：行上加显式「编辑」/「查看」按钮并定义只读态；先解决 Q3，否则双击语义二义。

### 🔴 G3 与 G5 冲突：`Extra` 不参与类型切换清理

切类型时清空"A 独有字段"，但 `Extra` 里的未知字段不在任何可见集里，既不提示也不清理，
会静默带入 B 并继续序列化。**建议**：显式规定 `Extra` 在类型切换时的归属，写进 G5 验收。

### 🟡 三态开关的 `Default` 是静态字符串

同一 `bool?` 字段在不同类型下运行时默认值可能不同，标签却只显示一份字面量，可能撒谎。
**建议**：`Default` 按（类型, 字段）取值或由 handler 提供；摘要里必须渲染**生效值**，不能出现"不设置"。

### 🟡 作者最痛的那类错误只给黄色

Problem 一明说"打错一个字符 → 0 命中"，而名称不存在恰恰是黄色放行。
**建议**：按侧别分级——过滤侧名称不存在（必然 0 命中）= 红；动作侧目标图层（可自动创建）= 黄。

### 🟡 字段即时写回，但校验只在保存时

关闭抽屉不保存，改动已在内存模型里生效，列表徽标仍显示上次保存的状态。
**建议**：定义脏状态与关闭语义（保存 / 丢弃 / 草稿），徽标反映当前模型。

### 🟡 Q1 是最大交付物却被推迟

G1、G4 全部依赖这张 11×N 字段表，文档不给规模、不给失败兜底。
**建议**：至少给出字段总数与逐类型覆盖的核对清单，标为 P2 入口条件。

### 🟢 两处细节

* "高级"分组无归属规则，易沦为垃圾桶——建议定义为"该类型最小有效配置之外的字段"；
* CUJ 4 的"勾选要跑的几条"与行内启用开关是两个概念，同一行承载两种选择语义。

---

## 二、结构视角（egm-structural）

### 🔴 `AppliesTo` 与引擎的真实读取范围冲突，会造出"不可见但生效"的字段

位置：字段元数据、示例、切换类型清理。
`DxfFilter.Build` 对 `EntityTypes / ColorIndex / ColorIndexes / LayerName / LayerNames / TrueColor /
SourceBlockName / SourceBlockNames` 是**无条件读取**的（`DxfFilter.cs:74-180`），与 `Type` 无关。
把 `SourceBlockName` 标成 `AppliesTo="BlockReplace,BlockLayerChange"`，UI 会在其余 9 个类型里隐藏一个
**仍然参与过滤**的字段——正是 G5 声称要消灭的状态。反之把 Filter 字段全标 `*`，11 种类型的分组会全亮，
G1 落空。
**建议**：元数据必须区分"引擎级（全局生效，永不可隐藏）"与"handler 级（按类型）"，
并补一张 `字段 → 读取点(文件:行)` 追溯表，作为 `AppliesTo` 的推导依据。

### 🔴 `Extra` 只加在 `Filter`/`Action` 上，G3 只兑现一半

位置：G3、CUJ 6、扩展字段袋。
`RuleConfig` 自身（`BatchConfig.cs:52`）没有 `Extra`；`ConfigSaver.SaveRules` 整段重写 `Rules` 数组。
写在**规则对象层**的未知字段（`"Note": "临时"` 放错一层即可复现）仍会被丢弃。
`ConfigLoader.DetectUnknownFields` 恰恰检查三层（`ConfigLoader.cs:346-355`），说明三层都存在未知字段。
**建议**：`Extra` 同时加在 `RuleConfig` 上，G3 写明"三层"，CUJ 6 明确字段所在层级。

### 🔴 "新类型需要独有字段"没有结构答案

`RuleFilter`/`RuleAction` 是 `sealed` 的共享类，任何新类型的新字段都只能加到这两个类上——
59 个字段里已经存在大量单类型字段（`PurgePasses`、`GradientShift`、`BigFont`）。
文档只用"改动面大"否决 Alternative F，但 F 的 Pros（编译期隔离、切换天然无残留、字段不进大袋子）
**正是本方案自认的缺陷**。这是一次决策反转（Q4 推翻 brief 决策 1），却没有一条 Alternatives 针对"模型形状"。
**建议**：补一条中间方案——`RuleAction` 保留旧字段供旧配置读取，新类型只在自己的 `Options` 子对象上加
`[RuleField]`，`FieldCatalog` 支持多源反射。

### 🟡 类型级校验的载体缺失，与 CUJ 5 直接矛盾

红级例子（`HatchPatternChange` 两字段皆空、`EntityLinetypeChange` 无 `LinetypeName` 且未开 `ForceByLayer`）
是**跨字段、类型级**条件，单字段 attribute 表达不了，`VisibleWhen` 也表达不了。组件表没定义它写在哪、怎么注册；
若写回 if 链，CUJ 5 的"不改 `ConfigLoader` 的 if-else 链"落空。
**建议**：明确 `IRuleHandler` 增加 `Validate(rule) → FieldIssue[]` 并列入组件表；否则删掉 CUJ 5 的承诺。

### 🟡 `VisibleWhen` 只能表达可见性，表达不了仲裁

`TargetGradientName` 与 `TargetPatternName` 的关系是"同时存在时渐变优先"（`BatchConfig.cs:342-347`），
是**值仲裁**，不是可见性。用 `VisibleWhen` 隐藏后，该字段既有值又不可见、也不会被清理，
再次制造"不可见但生效"。校验节只把"两者同时为空"列为红，未定义"两者同时有值"的级别。
**建议**：引入互斥组（`ExclusiveGroup`）与"冲突即红 / 自动清空"的显式语义，并把同类型内被隐藏字段的
非空值纳入 G5 清理范围。

### 🟡 `Extra` 与精简序列化器存在未验证的重复键路径

`CompactContractResolver` 给所有属性挂 `ShouldSerialize`，`Extra` 作为字典会被当作集合走 `IsEmptyValue`；
而 `[JsonExtensionData]` 序列化时把键平铺进 `Filter`/`Action`。一旦模型日后新增的属性名与 `Extra` 里已有键同名，
输出会产生重复键，而 `JToken.Parse` 在默认设置下不报错，重复键会静默落盘。
**建议**：序列化前断言 `Extra.Keys` 与 `JsonProperty` 名集合不相交，冲突时丢弃并计入提示，补 round-trip 测试。

### 🟡 结构合规缺口

* 无顶层 `Risks & Mitigations` 章节：风险内容散落在"已知缺陷与处理"与 Open Questions，应提升为 H2；
* "承认两份账"与"唯一事实源"在同一文档内自相矛盾，必须改写；
* Q1 把核心交付物整体推迟，导致"结构是否撑得住 11 种类型"在评审阶段无法被证伪。

---

## 三、风格视角（egm-style，节选）

### 🔴 Overview 与后文冲突

Overview 写"字段清单与校验规则由各自的 handler 提供"，而 Q4 已定属性反射、`RuleFieldAttribute` 放在
`Config/`、类型遍历靠 `AppliesTo`。handler 不提供清单。
**改为**："字段清单由 `[RuleField]` 属性标注声明，`FieldCatalog` 反射产出；校验由 `RuleValidator` 统一提供。"

### 🔴 实施计划与前文重复

`#10` 重述"校验"节，`#8/#9` 重述"扩展字段袋"，`#17` 重述"字段元数据"，`#20` 重述"数据流"。
同一设计写两遍，两处措辞不同即产生歧义。

### 🟡 术语三套并行

表单 / 面板 / 抽屉 / `RuleEditorDrawer`；生效默认值 / 默认值提示 / `Default`；红黄 / Error-Warning / 红级黄级。
首次定义后各留一个。

### 🟡 判断句缺度量

"最容易被低估的部分"、"比 JSON 编辑更危险"、"投入产出比不成立"、"被三个视角独立否决"、"不可接受"
均为无度量的判断或过程叙事，应改为可观察的后果或删去评审过程。

### 🟢 其他

* 已解决的 Q4 仍留在 Open Questions 靠删除线标记，纯文本渲染不可靠，移入"已决"；
* 标题全英文、正文全中文；`### CUJ 1` 与 `#### 1. Core/...` 同为工作项却差两级。
