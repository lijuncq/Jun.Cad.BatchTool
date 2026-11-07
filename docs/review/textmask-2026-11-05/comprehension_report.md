# 阶段 6：Comprehension Test 报告

**Target:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md`
**FeatureRange:** 1..229 ｜ **DetailedRange:** 230..481
**Reviewer:** egm-comprehension（Naive Reader）
**Standard:** 无（本阶段不套用文档标准）
**Mode:** REVIEW

---

## What this proposes

这份报告针对 `Jun.AutoCAD.Batch` 的 TextMask 规则，先给出按优先级排列的修复方案（第 3 节）：修 Wipeout 几何编码或改用 2D Solid、停止改写 MTEXT 的 `RectangleWidth`、把块内文字写进块定义、把遮挡判定改成二值相交并纳入块参照、进 SORTENTSTABLE 排序表、处理旋转文字、复验 DIMTFILL，外加 6 条出图级验收标准（第 4 节）。随后它按时间顺序记录这些方案被实施、又被后续实验逐条推翻的过程：5 节先落到 Solid，8 节接入修复版 ACadSharp 让 Wipeout 重新成为主路径，9 节让 Solid 与 `*D` 缓存块方案退役并把默认值改成"默认给所有文字加原生遮罩"，9.6/9.7 再补 MTEXT 标志位和绘图次序的修正。所以文档实际"提出"的不是一套稳定方案，而是一份带多次结论覆盖、且前文未同步修订的演进记录。

## How the existing system works, per this document

按文档描述，这是一个不启动 AutoCAD 的离线批处理工具，用 ACadSharp 3.8.35 读整张 DWG 到内存、改完再整张写回，没有内核、没有事务、没有交互选择集。TextMask 规则靠 EntityScanner 递归扫描模型空间/布局/块定义里的文字目标，按 Action 上的 `Mask*` 开关和 Filter 的类型/图层/嵌套条件，给 MTEXT 写原生背景填充、给 TEXT/ATTRIB/ATTDEF 建 WIPEOUT、给 DIMENSION 写 `DIMTFILL=1` 样式覆盖，并用 TextBounds 估算文字矩形、用 MaskService 构造几何和调整绘制顺序。默认行为（9.3/9.7 节）是：`MaskOnlyWhenOccluded=false` 即匹配上的文字都加、块内文字写进共享块定义、文字与遮罩用占位实体推到集合末尾、15% 字高留白、只走原生遮罩没有 Solid 兜底。

---

## 源码核对：当前实际取值（默认 / 开关 / 分支）

- `MaskMode`：`Auto`/`Wipeout` 同义走原生，其余（含 `Solid`）抛 `RuleValidationException` — `TextMaskRule.cs:60`、`:992-1009`；`RuleValidator.cs:176-185`；`BatchConfig.cs:665-668`；`Jun.Cad.Batch.json:179`
- `MaskOnlyWhenOccluded` 默认 `false`（全部加）— `TextMaskRule.cs:43`；`BatchConfig.cs:697-700`；`Jun.Cad.Batch.json:180`；`README.md:582`
- 遮挡判定仍是"面积比 ≥ 0.05 + 每条线 0.25"，不是二值 — `TextMaskRule.cs:44`、`:107-129`、`:895-943`；`BatchConfig.cs:702-705`；`README.md:584`
- `MaskIgnoreBlockBounds` 默认 `true`（Insert 不参与）— `TextMaskRule.cs:45`、`:836-839`；`README.md:585`；**但** `BatchConfig.cs:708` 注释写"默认 false = 不参与"
- MTEXT 背景：`bit0+bit1 (90=3)` + `BackgroundColor=ACI 7` + `BackgroundScale=1.15`，绝不改 `RectangleWidth` — `MaskService.cs:121-146`、`:84-97`；`TextMaskRuleTests.cs:131-143`、`:186-197`；`README.md:602`
- 标注：`DIMTFILL=1` DSTYLE 覆盖，不建 `*D` 实体 — `TextMaskRule.cs:348-428`；`TextMaskRuleTests.cs:708-723`；`Jun.Cad.Batch.json:212`；`README.md:591,600`
- `MaskSendHatchToBack` 默认 `true`，但仍走 `SortEntitiesTable.MoveToTop`，代码注释自承"已失效" — `TextMaskRule.cs:55`、`:176-180`、`:855-863`、`:864-883`；`Jun.Cad.Batch.json:205`；`README.md:583`
- 绘制顺序：占位实体 `ReorderToTop`，明确不用 SORTENTS — `MaskService.cs:297-346`
- `MaskInsideBlocks` / `MaskBringTextToFront` / `MaskRebuildText` / `MaskLayerPlottable` 默认 `true` — `TextMaskRule.cs:46,54,56,1213`；`Jun.Cad.Batch.json:196,199,201,203`
- `DimensionMaskMode=Native`、`DimensionTextMode=All`、`MaxMasksPerDrawing=0(不限)`、`MaskTextBox=Auto`、`MaskPaddingRatio=0.15` — `TextMaskRule.cs:50-53`、`:1011-1062`；`Jun.Cad.Batch.json:187,193,207,212`；`MaskService.cs:31`
- Wipeout：4 点归一化像素坐标 + `SetMaskingArea` + 真彩色白边框 — `MaskService.cs:154-208`；`TextBounds.cs:423-458`；`TextMaskRuleTests.cs:816-835`
- 退役字段 `MaskSolidColorIndex` / `MaskPadding` 只在 `Action.Extra` 里校验，`ConfigLoader` 未改 — `RuleValidator.cs:187-208`；`BatchConfig.cs:920-925`；`ConfigLoader.cs:31-46`；`README.md:596`

---

## Prioritized Findings

### 1. 前文结论未作废，按前文实现会删掉正确的 Wipeout — Severity: Blocker
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 第 0 节(12-14 行)、1 节实验 3(48-55 行)、2 节根因 A(76-80 行) 对 8 节(310-329 行)、8.1 节(349-352 行)、9.2 节(391-395 行)
* **Discrepancy Description:** 前文给的是确定性结论："ACadSharp 3.8.35 的 Wipeout 写出器不可用。不是参数没给对，是这条路走不通"(55 行)、"凡是走新建遮罩实体的路径……产出的都是无效遮罩"(78 行)。8.1 节(351 行)明确判这两条"错，是几何编码不对"，8 节把 Wipeout 恢复成主路径，9.2 节把 Solid 删掉。但这些前文一个字没改。**按 55/78 行去做 → 把 Wipeout 整条删掉、只留 MTEXT/Solid，等于删掉最终正确实现**；按 0 节"四个独立缺陷"去做 → 少算 9.6 的标志位、9.7 的排序表/HashSet 两类问题。代码反证：`TextMaskRule.cs:60,328-345,992-1009`、`MaskService.cs:154-194`、`TextMaskRuleTests.cs:816-835`。

### 2. 最尖锐的一处：9.3 仍写"只置 bit1"，9.6 实验证明 bit1 无效 — Severity: Blocker
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 5.1(234-238 行)、5.5(266 行)、6 节第 1 条(279 行)、9.3(409 行)、9.5(422 行) 对 9.6(428-451 行)
* **Discrepancy Description:** 9.3 节在"最终默认值"里仍白纸黑字写"MTEXT 写 `UseDrawingWindowColor`（bit1，不是 bit0 的显式 RGB）"(409 行)，6 节第 1 条用加粗"已确认……不碰 RectangleWidth 时，原生背景在出图里能干净切断背后的横线"覆盖旧结论，5.1/5.5 同样说"只置 BackgroundFillFlags"。9.6 节(437 行)的对照实验却给出 `90=2`（只 bit1）"**无填充，横线穿过**——与用户所见一致"，修法是 bit0+bit1(90=3)+63+45。**按 409/279/238 行去做 → 只写 bit1，AutoCAD 属性面板显示"背景遮罩 否"、渲染不画，正是 9.6 修掉的原 bug**。两处都声称是 Core Console 受控出图，结论完全相反，文档没有调和。代码反证：`MaskService.cs:131,138-143`、`TextMaskRuleTests.cs:131-163`、`:221-245`、`README.md:602,869`。

### 3. 5.2 整节是废弃实现，没有作废标记 — Severity: Blocker
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 5.2 整节(240-246 行) 对 8 节(319 行)、9.2 节(391-395 行)
* **Discrepancy Description:** 5.2 以"已实施的修复"列出：`MaskService.WipeoutWriteSupported = false`、`MaskMode=Auto 落到 Solid`、`MaskMode=Wipeout 直接抛 RuleValidationException`、新增 `MaskSolidColor(255)`。9.2 节(393 行)写"5.2 那条……方案撤销了，代码里 `CreateSolid` / `MaskSolidColor` 一并删掉"，395 行写"`MaskMode` 只剩 `Auto`/`Wipeout`（两者同义）……写 `Solid` 或任何别的值都是硬错误"。**按 5.2 去做 → 让 Wipeout 抛异常、Auto 生成 Solid，与现行校验直接冲突、启动即 `RuleValidationException`**。当前 `MaskService.cs` 全文没有 `WipeoutWriteSupported`/`CreateSolid`/`MaskSolidColor` 三个成员。

### 4. 删除线覆盖后的"新结论"又被推翻，没有第二次删除线 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** 6 节第 1/2 条(279-280 行)、5.6 第 1 条(270 行)；README.md:850,871
* **Discrepancy Description:** 6 节第 1 条的"已确认 90=2 有效"被 9.6 否掉；6 节第 2 条"已解出"的编码口径与 3.1 的伪代码并不一致；README.md:850 说"41=0 到底画不画得出背景……既没证真也没证伪"，而 README.md:869 与报告 9.6 已给出 90=2/90=3 的确定性逐像素结果。读者只扫加粗句会拿到错结论，且无法从版式判断哪一层才是当前有效结论。

### 5. 3.1 的 Wipeout 编码伪代码与现行实现三处不符 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 3.1 方案 1 伪代码(134-143 行) 对 5.6(270 行)、6 节第 2 条(280 行)、8 节(316-319 行)、测试(816-835 行)
* **Discrepancy Description:** 3.1 给的编码是"InsertPoint = 矩形中心、UVector=(Width,0,0)、VVector=(0,Height,0)、闭合环 5 点、ClassVersion=1"；后续改成"插入点=包围框最小角、U/V=长边、归一化像素坐标且 Y 翻转、ClippingState=1、340/360 写 0"，测试断言 `InsertPoint=(0,0,0)`、`UVector=(100,0,0)`、`VVector=(0,100,0)`（100 是长边不是 Height=50）、裁剪顶点 4 个。**按 134-143 行伪代码写 → 中心点、V 向量取高、5 点环三处都错，Wipeout 不擦除**。另 8 节(326 行)"4 点多边形"与(324 行)/(280 行)"`91=5`"并存，写出器是否补闭合点未核实。

### 6. 3.1/3.2 推荐的 2D Solid 已被 9.2 判定为废弃路径 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 3.1 结论(161 行)、3.2(172 行) 对 9.2(391-395 行)
* **Discrepancy Description:** 3.1 写"先做方案 2 把'能用'这条线补上……"，3.2 写"那就退回方案 2 的 Solid"，3.1(149 行)还称"`MaskService.CreateSolid` 是正确的"。实际 8 节直接修了库（方案 1），9.2 把 Solid 全删。**按 161/172 行去做 → 实现 2D Solid + 真彩色白 + 可打印图层，正是 9.2 判定"不是遮罩，是另一块脏"的废弃路径，且现在会触发硬错误**。附带：5.2(246 行)说"用 ACI 1 时出图是一块黑板"，9.6(439 行)说"ACI 7 被 monochrome.ctb 打黑"，同一现象两种颜色号。

### 7. 5.5"验证结果"未标作废，与 9.4/9.7 三个互斥的当前测试数 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 5.5(262-266 行) 对 9.4(415-418 行)、9.7(481 行)
* **Discrepancy Description:** 5.5 标着"验证结果"：212 个测试、"输出无 Wipeout"、"标注 *D 4"、"块内 MTEXT 背景有效"。9.4 是 291 passed、"Wipeout 5"、"标注文字 15"（DIMTFILL），9.7 是 301/301，9.1(384 行)已让 `*D` 缓存块方案退役。**按 5.5 判断现状 → 以为不产出 Wipeout、标注走 `*D` 块、测试仅 212 个**。代码反证：`TextMaskRule.cs:305-428`、`:328-345`、`TextMaskRuleTests.cs:708-723`。

### 8. "填充置后 6"是默认开却空转且报成功的真实 bug — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [真实功能 bug]
* **Affected Scope:** 9.4(417 行)、README.md:768、README.md:847 对 9.7(470 行)、README.md:583
* **Discrepancy Description:** 9.4 把"填充置后 6"列进明细，README.md:768 说"use `MaskSendHatchToBack` for overlapping hatches"，README.md:847 说"62 个填充被置后"；9.7(470 行)却写"这意味着此前'填充置后'（MaskSendHatchToBack）**从未真正生效过**——只计数、文件里没写"。当前代码里它是默认开却空转且报成功：`TextMaskRule.cs:55`（默认 true）、`:176-180`（仍调用）、`:864-883`（仍 `table.MoveToTop`）、`:881`（仍打"已把 N 个……送到最底层"）、`:201-204`（仍计入汇总）；`:855-863` 注释自己承认"实际上已失效……不再依赖"却没删调用。另 9.4(417 行)把非遮罩的"填充置后 6"混进应合计 195 的清单（175+15+5=195）。

### 9. 3.5/9.5 要求写排序表，9.7 判定该路线彻底不可用 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 3.5(196-202 行)、9.5(423 行) 对 9.7(470-472 行)
* **Discrepancy Description:** 3.5 要求"遮罩的 sort handle 取文字 sort handle − 1"、"MaskSendHatchToBack 要在所有遮罩创建完之后统一执行"；9.5(423 行)说"别的几何要靠 `SortEntitiesTable`"。9.7(470 行)判定"SORTENTS 排序表路线彻底不可用……块记录扩展字典 AutoCAD 读不到"，改用占位实体技巧。**按 3.5/9.5 去做 → 写排序表，AutoCAD `dictsearch` 返回 nil，绘制顺序调整完全落空**。证据：`MaskService.cs:297-346`、`TextMaskRule.cs:864-883`、`README.md:771`。

### 10. 3.4 的四条修改一条都没落地 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 3.4(187-194 行) 对 5.6 第 3 条(272 行) 与现行代码
* **Discrepancy Description:** 3.4 要求"判定口径从'面积比 ≥ 5%'改成二值""把 `Insert` 纳入遮挡源""展开块定义几何"。代码两者都没做：`TextMaskRule.cs:44` 仍是阈值 0.05、`:942` 仍是 `covered/rect.Area + crossings*0.25`、`:836-839` 仍 `if (!useBlockBounds && entity is Insert) continue;`。5.6 第 3 条自己也承认"遮挡判定仍看不到块参照"。**一旦有人把 `MaskOnlyWhenOccluded` 重新打开，拿到的正是 3.4(192 行)自己说"必然出错"的拍脑袋权重**。

### 11. 4 节验收第 6 条"必须验证 AutoCAD 认不认"未完成 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 4 节验收第 6 条(226 行) 对 README.md:919
* **Discrepancy Description:** README.md:919 明确 "The existing probe logic is **unchanged** in this update. It checks round-trip serialization and geometry, **not host rendering acceptance**"。这条验收要求既未完成也没被报告标成"未达成/取消"。**按 226 行去做 → 以为 preflight 已含宿主渲染验收，直接放行只有往返一致、但 AutoCAD 不认的 Wipeout**。`CapabilityProbe` 源码实现未核实；README 的证据已足够判定"未改"。

### 12. 3.7 的条件分支未标答案 — Severity: Nit
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 3.7(208-212 行) 对 6 节第 4 条(282 行)、9.1(373-384 行)
* **Discrepancy Description:** 3.7 留着"如果读得回来……`*D` 块方案可以退役。如果读不回来，`*D` 块方案必须补两条限制说明"，未标注答案。6 节第 4 条/9.1 已给答案"读得回来，`*D` 缓存块方案因此退役"。按 3.7 的"如果读不回来"分支实现会退回已废弃的 `*D` 路径。

### 13. 全文 `文件:行号` 引用全部指向重写前的旧版代码 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档与代码漂移]
* **Affected Scope:** 行 46/59/80/85-91/99-102/114/116/154/165/183/210/226 等
* **Discrepancy Description:** 例：2 节根因 B(85-91 行)引用 `TextMaskRule.cs:433` 并贴出"无条件跳过块内文字"的代码，而当前 `:493-519` 已改成按 `MaskInsideBlocks` 处理、`:433` 落在 `HasDrawingBackgroundFill` 里；`MaskService.cs:137`(46 行)、`:76`/`:76-101`(59/165 行)、`:180`(114 行) 现在分别是闭合大括号、XML 注释、`ClearMTextBackground`；`TextMaskRule.cs:103`(154 行) 说"MaskLayerPlottable 默认 false"，当前 `:1213` 默认 `true`；`TextMaskRule.cs:72`(80 行) 的 `CapabilityProbe.WipeoutWriter` 在该文件里已不存在。**按这些引用去找代码会定位到无关行，甚至把根因 B 那段"无条件跳过块内文字"复制回代码，直接复现 318 个块内文字被丢的 bug**。

### 14. `MaskIgnoreBlockBounds` 的 XML 注释默认值写反 — Severity: Nit
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** `BatchConfig.cs:708-712`
* **Discrepancy Description:** 注释第一句写"块参照的包络盒是否参与遮挡判定（**默认 false = 不参与**）"，Help 又写"默认是（忽略）"。实际默认 `true`（`TextMaskRule.cs:45` 的 `Bool(..., true)`、`README.md:585`）。读者若照"默认 false"写出 `Bool(action.MaskIgnoreBlockBounds, false)`，Insert 包围盒就会参与判定，与默认行为相反。

### 15. 文档自承两处当前仍未修的 bug — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [真实功能 bug]
* **Affected Scope:** 9.7(470、481 行)、README.md:771、8.2(364 行)
* **Discrepancy Description:** (a) 块记录扩展字典写出不修，"存量图纸里用户自己排的绘制顺序在每次保存时都会被重置（上游数据丢失 bug）……未继续深挖（上游 issue #897 在跟）"；(b) `WIPEOUTFRAME` 上游未修，`ACAD_WIPEOUT_VARS` 写 70=0 后 AutoCAD 2025 重开仍报 1，只能靠真彩色白边框规避，"深色屏幕上仍能看见白色细框，已知限制"（364 行；`MaskService.cs:196-208`、`README.md:598`）。证据：`MaskService.cs:305-307,321-346`、`TextMaskRule.cs:864-883`。

### 16. 5.6 第 2 条"块内 TEXT 的 Solid 位置略偏"从未关闭 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** 5.6 第 2 条(271 行) 对 6/8/9 各节
* **Discrepancy Description:** 之后 6/8/9 节没有任何一处把它标为已修，9.2 只是删掉了 Solid。当前 `TextBounds.cs:214-228`（`ForText`）与 `:595-599`（`TextMaskVertices`）仍直接读 `InsertPoint`/`AlignmentPoint`，看不到针对块内坐标的修正。是否随 Solid 退役而消失、Wipeout 路径下是否仍偏，报告没交代也无法出图复核 → **未核实**；按"遗留项从未关闭"处理会漏掉一个疑似现存 bug。

### 17. 未定义、未链接的项目专有名词 — Severity: Major
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** 2 节(80、116 行)、3.3(183 行)、3.7(210 行)、5.2(242-246 行)、5.4(258 行)、8 节(326 行)、7 节(299 行)
* **Discrepancy Description:** `CapabilityProbe.WipeoutWriter()`、"项目自己已经记过这条"（实际记录在 `TextMaskRule.cs:862-863` / `README.md:583,771`，报告不给指针）、`BlockLayerChangeRule.MoveDefinitionContent`、"项目记录说"、`MaskPath`/`ApplyMask`、`preflight`、`bgprobe/DimFillProbe.cs`、`MaskSolidColor`/`WipeoutWriteSupported`（代码中并不存在）。这些是未在文档内定义、也未附链接的自研概念与含糊引用。

### 18. 7 节没有一条完整可执行的复现命令 — Severity: Nit
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** 7 节(286-306 行)、3.5(201 行)、5.6(270 行)
* **Discrepancy Description:** 7 节列了一堆 `.lsp`/`.png` 路径和"accoreconsole 的两个坑"，但无完整复现命令；3.5"sort handle 取文字 sort handle − 1"假定读者懂排序表句柄；5.6"库层新增 `Wipeout.SetMaskingArea`"没说明它不在本仓库、也没给库路径（8 节 314 行才提本地克隆）。新成员照 7 节无法复现任一实验。

### 19. 算术与默认值不自洽 — Severity: Nit
* **Reviewer Lens:** egm-comprehension ｜ **归类:** [文档质量问题]
* **Affected Scope:** 9.6(455 行)、9.7(478 行)、9.4(417 行)、`BatchConfig.cs:303-306` / `README.md:318`
* **Discrepancy Description:** 9.6 说"仍未加遮罩的 **26** 个"却只解释 13+8=**21**；9.7 说"**296** 处遮罩"但明细 289+2+10=**301**；9.4 明细把"填充置后 6"混入应合计 195 的清单；9.6 的"嵌套超过 `MaxNestingDepth: 4`"与 `BatchConfig.cs:303-306`、`README.md:318` 的默认 **8** 不一致（照 4 配会平白漏掉嵌套块文字）。
