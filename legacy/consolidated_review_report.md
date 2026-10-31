# 汇总评审报告 — rule-editor-form.md

* **目标**：`docs/designs/rule-editor-form.md`（625 行，Status: Draft）
* **流程**：Stage 5 Zoom Out → Stage 6 Comprehension → Stage 7 Critic（design / structural / style）→
  Stage 8 Intermediate Zoom Out → Stage 9 Implementation Readiness（readiness / codebase-details / style）
* **日期**：2026-10-29

---

## 结论

设计在**实现保真度**上是可执行的，在**必要性**上没通过评审。

13 条阻断项里有 8 条（B2、B3、B4、B5、B6、B7、B8、B11）是表单机制自己生出来的问题——
它们不会因为把文档改得更细而消失，只会随着实现被逐个打补丁。两份独立评审（Stage 5 与 Stage 8）
指向同一个结论：当前 26 项改动里，只有摘要补全与名称校验两项直接对应用户抱怨。

先定三个决策，其余 13 条会自动收敛或变形。

---

## 必须先定的三个决策

### 决策 1：表单是本轮交付物，还是下一轮？

| 选项 | 保留 | 删除 | 代价 |
|---|---|---|---|
| A. 表单延后 | P0（摘要补全 + 名称校验）、P1（`Extra`、单一校验器） | P2、P3 全部 | G1 本轮不达；作者继续手写 JSON，与今天一致，无回退 |
| B. 表单本轮做 | 全部 | — | 需先解决 B2–B13 共 11 条阻断项 |

决策 A 会让 B3、B7、B8、B9、B10、B11、B13 直接失效（它们只存在于表单路径上），
B2、B4、B5、B6 降级为"P1 的已知限制"。这是评审里唯一能一次性删掉最多问题的一刀。

### 决策 2：若表单本轮做，模型形状是否改为"每类型 Options 类"（原 Alternative F）？

当前方案要给 59 个属性逐个标注，F 的边际成本只是旧配置迁移读取器。F 能根除 `AppliesTo`、
`VisibleWhen`、切换清理、`Extra`（B2、B3、B4、B11 全部消失）。文档以"改动面最大"否决，但没有量化
——两条路径都要动 59 个字段，差别只在迁移。

### 决策 3：`AppliesTo` 是否区分"引擎级"与"handler 级"？

`DxfFilter.Build` 无条件读取 8 个 Filter 字段（`EntityTypes`、`ColorIndex`、`ColorIndexes`、`LayerName`、
`LayerNames`、`TrueColor`、`SourceBlockName`、`SourceBlockNames`），与 `Type` 无关。
把其中任何一个标成按类型可见，就等于造出"界面上隐藏、执行时仍然生效"的字段——正是 G5 要消灭的状态。
这一条无论决策 1 选什么都要修，因为它同时影响摘要与校验。

---

## 阻断项（去重后 13 条）

| # | 问题 | 来源 | 决策 1 选 A 时 |
|---|---|---|---|
| B1 | 设计规模与用户需求不匹配：26 项里只有 2 项对应用户抱怨 | S5、S8 | 由决策 A 解决 |
| B2 | `AppliesTo` 与引擎真实读取范围冲突，隐藏字段仍生效 | S5、S7-struct | 仍需修（影响摘要） |
| B3 | `VisibleWhen` 隐藏已填值字段时不清值，与 G5 理由冲突；`TargetGradientName` 的值仲裁表达不了 | S6、S7-struct | 消失（无 `VisibleWhen`） |
| B4 | `Extra` 只加在 `Filter`/`Action`，`RuleConfig` 层未知字段仍丢失；与 `DetectUnknownFields` 冲突；与精简序列化器存在重复键风险 | S5、S7-struct、S9-code | 缩小为"是否要 `Extra`" |
| B5 | P0 依赖 P1 的 `FieldIssue`/`IssueSeverity`，"P0 可独立交付"不成立 | S6、S9 ×2 | 需修（一行改动） |
| B6 | 测试工程不可行：会被主工程 `**/*.cs` 通配编译；net8 测试工程与 ZWCAD(net48) 构建冲突；`RuleSummary` 经 `RuleExecutionContext` 间接依赖宿主类型 | S9 ×2 | 需修（缩小测试边界） |
| B7 | 模态抽屉 + `SendStringToExecute` 互锁，拾取回调永不返回 | S9 ×2 | 消失（无拾取） |
| B8 | 字段即时写回 + 红级只阻断写盘 → 模型已被污染，取消无法回滚；两条写盘路径未定义 | S6 | 消失（无表单） |
| B9 | G2 不可测；摘要单行 18px 放不下全部条件，"+N 项条件"点不开 | S7-design | **仍需修**（摘要本轮就要改） |
| B10 | `CardPanel`/`FlatButton`/配色常量为 `private sealed` 嵌套类，新文件无法复用 | S9-code | 消失 |
| B11 | 单/复数配对字段无元数据（`TagList` 写回规则无法表达） | S6、S9 | 消失 |
| B12 | 类型级校验载体缺失，与 CUJ 5"不改 if 链"矛盾 | S7-struct | 需修（`RuleValidator` 本轮就做） |
| B13 | 900×600 抽屉在 150% DPI / 1366×768 下超出屏幕 | S7-design | 消失 |

---

## 主要项（去重后）

| # | 问题 | 来源 |
|---|---|---|
| M1 | 保存时整段替换 `Rules` 必然删注释，`.bak` 是补丁不是修复；`CommentsPreserved` 恒为 `hadComments`（`ConfigSaver.cs:133`），实际已在撒谎 | S5、S9 |
| M2 | 名称校验只给黄色，而"打错一个字 0 命中"是首要痛点；应按侧别分级（过滤侧红、动作侧黄） | S7-design |
| M3 | "自动创建"字段（`TargetLayer`/`TargetTextStyle`/`TargetBlockName`）会被误判为黄，需豁免清单 | S9 |
| M4 | `Default` 是第三份真相，且其一致性断言依赖做不到的测试 | S5、S6、S8 |
| M5 | `Values` / `VisibleWhen` 两个自造字符串 DSL 无编译期检查，全项目只有一两处联动 | S5 |
| M6 | `Extra` 的保护场景只由"表单 + 手写 JSON 并存"产生，而 Non-Goals 已声明不提供第二编辑通道 | S5 |
| M7 | 摘要"补齐"基线描述有误（`TextStyleName`/`SourceBlockNames`/`LayerName`/`SourcePatternName` 已在输出） | S9 |
| M8 | README 有两个"3.2"节，按"第 3.2 节"会改错位置；交付物表、验证情况表、`config.json` 示例均未列入 | S9 |
| M9 | `HostNameLookup` 只开短事务未取文档锁 | S9 |
| M10 | 双击规则行指错控件（`EditRuleRow` 无 `RowDoubleClick`），且 `UpdateRule` 用硬编码坐标匹配标签 | S9 |
| M11 | Overview 仍写"字段清单由 handler 提供"，与 Q4 已定的属性反射矛盾 | S7-style |
| M12 | 术语三套并行（表单/面板/抽屉；生效默认值/默认值提示/`Default`；红黄/Error-Warning） | S7-style |
| M13 | 实施计划约 230 行逐文件清单，属工单而非设计；与 Technical Plan 多处重复 | S5、S7-style |
| M14 | 缺顶层 Risks 章节，风险散落在"已知缺陷与处理" | S7-struct |

---

## 可接受 / 不建议过度反应

* **G3 与 G5 在决策 A 下失去意义**：它们只为表单存在，不必强行保留到摘要阶段；
* **`Extra` 在决策 A 下可以不做**：没有第二编辑通道，就不存在"表单抹掉手写字段"的场景；
* **拾取（P3）**：两份评审都判为 YAGNI，决策 A 下直接删除；
* **测试工程**：即使决策 B，也应缩小到"不触碰宿主类型"的子集，而不是为了跑测试去改生产代码结构。

---

## 追溯矩阵

| 汇总项 | S5 | S6 | S7-d | S7-s | S7-st | S8 | S9-i | S9-c | S9-st |
|---|---|---|---|---|---|---|---|---|---|
| B1 | ● | | | | | ● | | | |
| B2 | ● | | | ● | ● | ● | | | |
| B3 | | ● | | ● | ● | | | | |
| B4 | ● | | | ● | ● | | | ● | |
| B5 | | ● | | | | | ● | ● | |
| B6 | | | | | | | ● | ● | |
| B7 | | | | | | | ● | ● | |
| B8 | | ● | ● | | | | | | |
| B9 | | | ● | | | | | | |
| B10 | | | | | | | | ● | |
| B11 | | ● | | | | | ● | | |
| B12 | | | | ● | ● | | | | |
| B13 | | | ● | | | | | | |
| M1 | ● | | | | | | ● | ● | |
| M2 | | | ● | | | | | | |
| M3 | | | | | | | ● | | |
| M4 | ● | ● | | | | ● | | | |
| M5 | ● | | | | | | | | |
| M6 | ● | | | | | ● | | | |
| M7 | | | | | | | ● | | |
| M8 | | | | | | | ● | | |
| M9 | | | | | | | ● | | |
| M10 | | | | | | | | ● | |
| M11 | | | | | | | | | ● |
| M12 | | | | | | | | | ● |
| M13 | ● | | | | | | | | ● |
| M14 | | | | ● | | | | | |

*S7-d = design，S7-s = structural，S7-st = style，S9-i = readiness，S9-c = codebase-details，S9-st = style*
