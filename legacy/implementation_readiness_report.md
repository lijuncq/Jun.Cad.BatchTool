# Stage 9 — Implementation Readiness 报告（Detailed Plan，399–625 行）

* **评审者**：egm-implementation-readiness、egm-codebase-details
* **标准**：`implementation-readiness-standard.md`
* **日期**：2026-10-29

---

## 一、实施就绪性

### 🔴 P0 并不独立，它依赖 P1 的类型

`NameValidator` 返回 `IReadOnlyList<FieldIssue>`、需要 `IssueSeverity.Warning`，二者在 P1 第 10 项才定义；
第 5 项还要按 Severity 渲染徽标。P0 阶段无法编译，"P0 可独立交付"不成立。
**补充**：把 `FieldIssue`/`IssueSeverity` 提为 P0 独立文件，或整体前移第 10 项。

### 🔴 映射表缺失，无法实现

只说"先用硬编码的字段名 → `NameKind` 映射表"，未给表内容；`NameKind` 无 `Pattern`，
而设计里的 `ValidateAs` 含 `Pattern`，`HatchPatternChange` 的 `SourcePatternName` 校验行为未定。
**补充**：列出映射条目（含 `LayerLinetype`、`TargetDimensionStyle`）与 Pattern 的处置结论。

### 🔴 缺"自动创建"豁免

`TargetLayer`（`CreateIfMissing=true`）、`TargetTextStyle`、`TargetBlockName` 等在 handler 里会自动创建，
`NameValidator` 会把合法配置判为黄，CUJ 2 的判定基准无法确定。
**补充**：给出豁免字段清单及其对 `CreateIfMissing` 的依赖。

### 🔴 引用了不存在的元数据

第 19 项要求"按 `RuleFieldAttribute` 上声明的配对字段名处理"，但 attribute 定义里没有配对属性，
`TagList`（`EntityTypes`/`SourceBlockNames` 单复数）无法实现。
**补充**：在第 15 项增加 `PairPath` 并写进 attribute 定义。

### 🔴 拾取签名与设计冲突，线程与生命周期未定

设计是 `Request(kind, fieldPath)` / `Complete(kind, name)`，计划是 `Request(kind, Action<string>)` /
`Complete(null)`。回调在哪个线程、抽屉已关闭时回调怎么办、连续两次拾取如何区分，全部未定。
**补充**：定死一套签名 + `Control.Invoke` + 无窗口时丢弃 + 请求覆盖策略。

### 🔴 模态窗口 + `SendStringToExecute` 互锁

主窗走 `ShowModelessDialog`（`BatchProcessCommand.cs:178`）。抽屉定为模态 900×600，
而拾取在模态期间 `SendStringToExecute("BATCHPICK")`：命令要等模态循环退出才执行，回调永不回来。
`UiRunBridge` 的存在正是这个先例。
**补充**：抽屉改非模式，或拾取前 `Hide()`，计划必须二选一写死。

### 🔴 验证计划漏了连带依赖

`RuleSummary` 的每个分支都调 `RuleExecutionContext.Bool`（`RuleSummary.cs:109,120,131,136,154-157`），
该类型字段是 `Document`/`Transaction`（`IRuleHandler.cs:63-87`）。测试一旦断言摘要就会加载宿主类型，
`acdbmgd` 不在探测路径 → `TypeLoadException`。计划把限制写成"不能实例化 handler"，边界判错。
**补充**：把 `Bool`/`Double`/`Int` 提到无宿主的静态类，或把该用例限定为 `DescribeFilter` 并写入已知限制。

### 🔴 测试工程不可执行且与双构建冲突

缺 TFM、xunit / Test.Sdk 版本、引用方式（ProjectReference 还是 DLL 路径）、`PlatformTarget`、
acdbmgd 解析策略；一旦进 `.sln`，`dotnet build -p:AcadVersion=ZWCAD2020` 会让 net8 测试工程引用 net48
插件而失败，与"两套 0 错误 0 警告"直接矛盾。
**补充**：写死 csproj 要点，并规定 ZWCAD 构建只编主工程或条件排除测试工程。

### 🟡 基线描述有误

`DescribeFilter` 已输出 `TextStyleName`、`SourceBlockNames`、`LayerName`、`SourcePatternName`
（`RuleSummary.cs:87/35/77/82`），按"补齐"写会重复输出；"+N 项条件"的计数口径未定；
"`DescribeFilterForTest` 之类（或直接断言 `Describe`）"是未决二选一。
**补充**：给精确字段清单与追加顺序、计数规则、选定的测试契约。

### 🟡 缺文档锁

`BATCHUI` 是非模式窗口、无命令上下文，保存时 `HostNameLookup` 只"开短事务"未取文档锁，
锁冲突时的返回值未定。**补充**：`doc.LockDocument()` 包裹 + 异常 → `Known=false`。

### 🟡 备份项冗余且未决

`SaveRules` 已写 `config.json.bak`（`ConfigSaver.cs:144`），再写 `rules.bak` 重复；
"或写入 `.bak` 的注释头"是二选一；何时写、是否覆盖、状态栏文案均未定。
**建议**：删除该项，或定死"仅当 `Rules` 数组内含注释时覆盖写 `config.json.rules.bak`"。

### 🟡 README 定位错误 + 遗漏

README 有两个"3.2"（270 行 `#### 3.2 规则编辑页签`、362 行 `### 3.2 多轮替换`），按"第 3.2 节"会改错位置。
另漏：第 1 节交付物表、3.1 表格"双击某行 → 只读 JSON"一行、第 8 节验证情况、以及 `config.json` 示例——
验证第 2 条依赖的未知字段 `"Note"` 没有任何一项负责添加。

### 🟡 `VisibleWhen` 语义未定

示例 `"Action.ForceByLayer != true"` 带 Owner 前缀，而属性挂在 `RuleAction` 上：路径相对谁、
`true` 与 `"true"` 如何比较、目标为 `bool?` 时 `null` 算哪边，都未写；`ResolveFieldSet` 返回类型也未写。
**补充**：定义路径语法（相对 `RuleConfig` 根）、比较规则、返回 `ISet<string>`。

---

## 二、代码库实况

### 🔴 测试目录会被主工程当源码编译

`Jun.AutoCAD.BatchTool.csproj` 位于仓库根，未设 `EnableDefaultCompileItems=false`，
默认 `**/*.cs` 通配会把新建的 `tests/Jun.AutoCAD.BatchTool.Tests/*.cs` 编进插件 DLL，
xunit 引用不存在 → `dotnet build` 直接失败。计划未把 `Jun.AutoCAD.BatchTool.csproj` 列入修改清单。
**改**：csproj 加 `<Compile Remove="tests/**/*.cs" />`，并把 csproj 补为 Modify。

### 🔴 "不实例化 handler"挡不住宿主依赖

见上"验证计划漏了连带依赖"。测试边界判断错误。

### 🔴 P0 无法编译

见上。违反"每阶段都能编译"。

### 🔴 "复用 CardPanel / 配色常量"不成立

`CardPanel`（`BatchToolForm.cs:1493`）、`FlatButton`（1643）、`RuleDetailForm`（2122）均为
`private sealed` 嵌套类，配色常量是 `private static readonly`（28-37）；仅 `RoundRect`/`EffectiveBackground`
是 internal（2200、2216）。新文件无法引用。
**改**：计划写明先把主题与控件提到 `UI/Jun.AutoCAD.Theme.cs`（internal）。

### 🔴 模态抽屉 + `SendStringToExecute` 互锁

见上。必须二选一写死。

### 🟡 "双击规则行"指错控件

`EditRuleRow` 只有 `RowClick`（`BatchToolForm.cs:1535`），无 `RowDoubleClick`；带双击的是执行页 `RuleRow`
（1965/2025 → `OnRowDoubleClick` 1206 → `ShowDetail` 1423）。且 `EditRuleRow.UpdateRule` 用硬编码
`Left/Top` 匹配标签（1606-1618），加徽标会撞坐标。
**改**：明确给 `EditRuleRow` 新增 `RowDoubleClick`，并说明 `UpdateRule` 改为命名字段。

### 🟡 徽标重复 + 构造点假设

第 5 项与第 21 项都要求"卡片行显示红/黄徽标"，未定归属。第 5 项称加构造参数"不影响既有调用点"，
但 `BatchToolForm` 为 sealed 且仅一个构造（`BatchToolForm.cs:100`），唯一调用点是
`BatchProcessCommand.cs:167`。**改**：写明加重载或直接改调用点。

### 🟡 与 `ConfigLoader` 的未知字段检测冲突

`KnownNames`（`ConfigLoader.cs:380-393`）按 `JsonPropertyAttribute` 收名单，`DetectUnknownFields`（313-362）
读原始 JSON：进了 `Extra` 的 `"Note"` 仍会被报"存在未知字段"，与"0 警告 + Note 保留"矛盾。
**改**：第 8 项追加"`Extra` 非空即跳过该节未知字段告警"。

### 🟡 备份语义与既有实现冲突

`SaveRules` 已写 `path+".bak"`（`ConfigSaver.cs:144-153`），且 `CommentsPreserved` 恒赋 `hadComments`（133），
数组内注释实际已丢；`TryReplaceRulesArray` 返回 null 时整体重写会丢全文件注释（136-142）。
计划只加 `RulesCommentsBackedUp`，未修 `CommentsPreserved`，也未覆盖整段重写分支。

### 🟡 移除 `_jsonEditor` 的连带改动未列

`TryApplyJsonToRule`（897）及其 5 处调用（880/936/1021/1039/1057）、`RefreshEditPanel`（840）、
`SelectEditRow`（894）都依赖该框；不列即留死代码或漏改。
另 `GetRegisteredTypes` 不自注册（`RuleFactory.cs:97-103`），`BuildEditPanel` 早于 `LoadConfig`
（`BatchToolForm.cs:118/123`），当前类型下拉框为空——应写成"修既有缺陷"，而非"显式调用"。
