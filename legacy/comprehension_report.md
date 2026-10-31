# Stage 6 — Comprehension Test 报告

* **目标文档**：`docs/designs/rule-editor-form.md`
  Feature spec：1–398 行；Implementation plan：399–625 行
* **评审者**：egm-comprehension
* **日期**：2026-10-29

## 本文档提出的方案

规则编辑页里那个直接编辑 JSON 的多行文本框，将被换成按规则类型裁剪的属性表单。字段清单不再单独维护：
`[RuleField]` 标注在 `RuleFilter`/`RuleAction` 的属性上，`FieldCatalog` 反射出描述符，
`FieldControlFactory` 按 `Editor` 造控件，双击规则行打开 900×600 的模态抽屉 `RuleEditorDrawer`。
保存时 `RuleValidator` 产出红/黄 issue，红级阻断、黄级放行，`NameValidator` 对照符号表给最近匹配。
配套三项：`JsonExtensionData` 保留未知字段、切换类型清理 A 类型独有字段、按 P0~P3 四阶段交付。

## 现有系统如何工作（按本文档的描述）

插件读 `config.json`，`Rules` 数组每条由 `RuleFilter`（16 字段）与 `RuleAction`（43 字段）两个共享封闭类构成，
11 种类型共用、字段全可空，哪个类型用哪些字段只存在于 handler 代码与配置注释里。现有 UI 是三张竖向卡片，
最后是多行 JSON 文本框，保存时 `ConfigSaver` 整段替换 `Rules`。`ConfigLoader` 用 `MissingMemberHandling.Ignore`
在进内存时丢弃未知字段，`ValidateByType` 只产 warning、11 类型覆盖 8 种。`RuleSummary` 遗漏若干条件字段，
未知类型返回空串。

## 发现

### 🔴 单/复数配对字段无法表达

第 19 条要求 `TagList` "1 项写单数、多项写复数，按 `RuleFieldAttribute` 上声明的配对字段名处理"，
但属性定义（第 200–214 行）里没有任何配对字段属性。`SourceBlockName/SourceBlockNames`、
`LayerName/LayerNames` 的写回规则无法表达：两侧都写、只写复数、清空另一侧，三种做法都合理。

### 🔴 保存链路没有定义回滚

字段"即时写回模型，无独立提交步骤"（第 187–193 行），红级却只阻断写盘。那么阻断后模型已被污染：
抽屉「取消」如何回滚、列表摘要与徽标是否已变、阻断后能否关闭抽屉，全部未写。
且存在两条写盘路径（表单的「保存规则到配置」与抽屉底部「保存」），哪条受红级约束未定义。

### 🔴 P0 无法独立交付

P0 的 `NameValidator` 返回 `IReadOnlyList<FieldIssue>`，而 `FieldIssue`/`IssueSeverity` 定义在 P1 第 10 条。
P0 单独交付就必须自带这两个类型，P1 再合并或改签名，与"按依赖排序"矛盾。
P0 的"硬编码字段名 → `NameKind` 映射表"内容也未给，P2 又推翻，P0 的校验覆盖面由实现者自定。

### 🔴 可见性变化不清值，与 G5 的理由冲突

G5 的理由是"字段不可见但仍在生效"很危险，但 `VisibleWhen` 隐藏一个已填值字段时
（先填 `LinetypeName` 再打开 `ForceByLayer`）既不清理也不提示。可见性变化与残留清理是两套机制，
只写了后者。另：隐藏字段若 `Required`，是否仍产红级未定；`Action.ForceByLayer != true` 在 `null` 时求值为真，
"不设置"时字段可见是否符合意图未说明。

### 🟡 数值边界没有数据来源

"数值越界"是红级，但属性上没有 Min/Max，`Unit="0~1"` 只是显示文本。每个 `Integer`/`Double` 字段的
合法区间没有数据来源。

### 🟡 `Default` 的断言被已知限制抵消

`Default` 是显示字符串（`"true"`、`"7"`），无法支撑"置 null 与置 `Default` 行为一致"的断言；
而该断言所在的测试工程按第 612 行无法实例化 handler。缺陷表的缓解措施被已知限制抵消，
`bool?` 默认值正确性无人验证。

### 🟡 `Values` 的格式未定义

`"ModelAndLayouts=模型+全部布局;ModelOnly=仅模型"` 未定义标签含 `;`/`=` 时的转义，
也未说键是枚举成员名还是序列化字符串；`Enum` 写回如何转成 `Scope` 的属性类型未定。

### 🟡 黄级语义制造常态噪声

"目标图层不存在"被判黄色提示，但该字段的 `Help` 说 handler 会按颜色/线型自动创建图层。
合法用法被当作疑似拼错，同事会看到常态噪声。

### 🟡 术语未定义

`RuleConfig` 承载 `Type`/`Filter`/`Action`，全文未定义它与 `RuleFilter`/`RuleAction` 的关系；
`IRuleHandler`、`RuleFactory`、`RuleJson`、`UiRunBridge`、`CardPanel`、`concepts_brief.md` 均无定义或链接。

### 🟡 校验时机未定

校验条要显示"红 N 项"，但校验只在保存时跑；未打开抽屉的规则何时校验、加载时已带红级的规则在整表保存时
是否阻断，未写。

### 🟢 两处细节不一致

* 第 47 行列出的摘要遗漏字段不含 `TextStyleName`、`MaxNestingDepth`，第 409 行却补了它们；
* 测试工程 `net8.0-windows` 只覆盖 AutoCAD 构建，ZWCAD（net48）分支无自动化覆盖。
