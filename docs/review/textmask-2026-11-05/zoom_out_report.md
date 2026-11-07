# 阶段 5：初始 Zoom Out 审查报告

**Target:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md` (全文 1..481)
**Reviewer:** egm-zoom-out（Skeptical Architect）
**Standard:** `C:/Users/lijun/.workbuddy-ai/egm/resources/document_standards/zoom-out-standard.md`
**Mode:** REVIEW

## Reviewer Status

| Reviewer | Status |
| --- | --- |
| egm-zoom-out | completed |

## 总判断

**还有 bug，而且不止一处。** 当前实现并没有"修好文字遮罩"，而是用"默认给所有文字加遮罩"绕过了根因 C（遮挡判定看不到块参照/块内几何），同时把 Wipeout 修复绑死在一个仓库外的私有 ACadSharp 克隆上。9.7 自认 SORTENTS 路线彻底不可用，但对应的开关、代码路径、汇总计数和"成功"日志全都没删，默认还是开的。这几处都符合"反复推翻结论后残留废弃路径/默认值"。

以下每条都到源码核对过。凡是文档声称"当前行为"的地方，给了 `文件:行号`；读不到证据的写"未核实"。

---

## Prioritized Findings

### 1. SORTENTS 已判失效，但"填充置后"仍默认开、仍执行、仍计数、仍报告成功 — Severity: Blocker
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 9.7 节（行 470）、第 9.5 节（行 423）、第 3.5 节、第 5.5/9.4 节明细、samples/text-mask.json:24
* **归类:** [真实功能 bug]
* **Discrepancy Description:** 文档 9.7 明说"此前'填充置后'从未真正生效过——只计数、文件里没写"、"保留本开关只为兼容旧配置，不再依赖"；README.md:583 也写 "Effectively superseded... Kept for configuration compatibility"。但源码里这条路完整活着且默认开：`TextMaskRule.cs:55` 默认 `true`，`:100-102` 因它触发全空间包围盒索引 `BuildOccluders`，`:162-172` 收集填充，`:176-180` 调用 `PushHatchesToBack`，`:877` 仍执行 `table.MoveToTop(hatch)`，`:881` 仍打日志 `"已把 {0} 个遮挡文字的填充送到最底层（绘制顺序）"`，`:201-204` 仍把 `movedToBack` 计入汇总（这正是 9.4 行 417、README.md:864 "填充置后 6" 的来源）。函数自己的注释 `TextMaskRule.cs:853-863` 承认失效，代码却没停。
* **Rationale & Recommendation:** 一个已被证伪的机制，正确做法是从默认配置、执行路径、计数、日志四处一起删掉，不是留着"兼容"却继续谎报结果——这是 YAGNI 反例，也是误导用户的假成功。

### 2. 保存任何带 SORTENTS 的图纸都会重置用户绘制顺序，报告把它降级成"与遮罩无关的通病" — Severity: Blocker
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 9.7 节（行 470、481）、README.md:771
* **归类:** [真实功能 bug]
* **Discrepancy Description:** 文档 9.7 行 470 写"存量图纸里用户自己排的绘制顺序在每次保存时都会被重置（上游数据丢失 bug）"，行 481 又写成"与遮罩无关的通病"。README.md:771 原文："existing draw order stored in SORTENTS is not preserved by this pipeline"。这不是 TextMask 的局部限制，是整条批处理流水线的数据破坏：默认流程对每张图写出 DWG 都会丢排序表。具体丢表位置在 ACadSharp 写出器，**未在源码中定位，未核实**。
* **Rationale & Recommendation:** 把 Blocker 级数据丢失压在"遗留"一句话里，是 Root Problem Focus 失职；必须改根写出器，而不是"上游 issue #897 在跟"就结案。

### 3. "默认给所有文字加遮罩"绕过根因 C，不是解决；整套遮挡判定成了死代码，且兜底上限被取消 — Severity: Blocker
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 3.4 节（行 187-194）、5.3（行 254）、5.6（行 272）、9.3（行 401、406）、`TextMaskRule.cs`
* **归类:** [真实功能 bug]
* **Discrepancy Description:** 3.4 的方案是"判定改成二值、把 Insert 纳入、去掉 0.05 阈值和 0.25 权重"；9.3 实际做法是把 `MaskOnlyWhenOccluded` 默认改成 `false`（`TextMaskRule.cs:43`），根本不判遮挡了。`ComputeOcclusion` 仍是 `covered / rect.Area + crossings * 0.25`（`:942`），Insert 仍被无条件排除（`:45` + `:836`），根因 C 一行没改。`BuildOccluders`（`:807-851`）、`ComputeOcclusion`（`:895-943`）、`BoundsIndex` 网格索引（`:1258-1342`）、`EstimateCellSize`（`:945-969`）约 200 行在默认配置下不参与实际行为——`:1257` 注释还写着"1 万实体的图上是必须的"，属于未证明瓶颈就做的 premature optimization 死代码。更矛盾的是 5.3 明说"按'宁可多加'处理，`MaxMasksPerDrawing` 兜底"，9.3 却把上限改成 `0 = 不限`（`:50`，`:132` 只在 `maxMasks > 0` 时限制），兜底没了。副作用：需求原话是"底下有其它线条就加"（行 192），现在无遮挡的孤立文字也一律加遮罩、新增实体、改句柄（`:340-341`）。
* **Rationale & Recommendation:** 绕过了根问题。要么删掉开关与启发式，要么按 3.4 实现，不要留一个可达但已知有错的分支。

### 4. Wipeout 修复绑死在仓库外的私有 ACadSharp 克隆，干净环境编不过 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 8 节（行 314、319、329）、3.1、5.2、`Jun.Cad.Batch.Core.csproj:20`
* **归类:** [文档与代码漂移]
* **Discrepancy Description:** csproj:20 是 `<ProjectReference Include="..\..\..\ACadSharp-v3.8.35\src\ACadSharp\ACadSharp.csproj" />`，指向仓库同级目录的私有分支；csproj:14-18 注释承认本机只编 net8.0，第 8 节行 329 又说"提 PR 前要还原成原来的多目标列表"。整条 Wipeout 链路都依赖这个 fork 的 API：`MaskService.cs:191` 调 `wipeout.SetMaskingArea(...)`、`TextBounds.cs:423-458` `ForWipeout` 按像素坐标反算、`CapabilityProbe.cs:132` 用 `CreateWipeout`。换回 NuGet 3.8.35 就编译失败。同时 5.2（行 240-246）"Wipeout 标记为不可用"与 8/9 节"已解出"完全相反，报告没把 5.2 整节标作废。
* **Rationale & Recommendation:** 把一个上游缺陷私有化 fork 进构建链，却没有落地路径（既不是已合并的上游版本，也不是可复现的 patch），是架构死胡同。

### 5. `CapabilityProbe.WipeoutWriter` 仍是当初被列为根因的"只验能写能读"，第 4 节验收标准没落实 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 2 节根因 A（行 80）、第 4 节（行 226）、第 8 节（行 326）、`CapabilityProbe.cs`
* **归类:** [文档与代码漂移]
* **Discrepancy Description:** 第 4 节要求"必须验证 AutoCAD 认不认（用 accoreconsole 读回 `ssget` 计数）"。当前 `CapabilityProbe.cs:104-213` 仍是构造 → `DwgWriter.Write` → `DwgReader.Read` → 查 `ClippingState`/顶点数/`Definition`/`ForWipeout` 包围盒，纯 ACadSharp 往返，没有任何宿主验证；`:69-71` 注释自己写"本身不代表出图渲染验证"，README.md:919 也写 "The existing probe logic is unchanged... not host rendering acceptance"。8 节把 Wipeout 翻转成"可用"之后，把关用的还是报告第 2 节点名的那套假阳性探测。
* **Rationale & Recommendation:** 根因 A 只改了几何，没改验收口径，回归时会继续"通过"。

### 6. 后半部分不断推翻前半部分，但前 4 节结论没有任何作废标注，全文没有一处"当前实现是什么" — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 0 节（行 12）、实验 3（行 48-55）、3.1（行 147-161）、3.2（行 170）、3.7（行 210-212）、5.6（行 270）、第 6 节（行 279-282）
* **归类:** [文档质量问题]
* **Discrepancy Description:** 第 0 节"遮罩'加上了'但根本不存在""Wipeout 在 AutoCAD 里不擦除任何东西"是诊断阶段结论，8.1/9.2 已完全翻转，但没划掉；实验 3 的"ACadSharp 3.8.35 的 Wipeout 写出器不可用…这条路走不通"、3.1 的"先做方案 2（2D Solid）"、6 节标题"必须承认的未验证项"下 4 条有 3 条用删除线改成"已确认/已解出"——标题与内容直接打架。一份 481 行、前半（1-4 节约 213 行）全部失效的报告，读者无法回答"现在到底怎样"。同时是 altitude drop：大量 DXF 组码表（行 35-44）、逐像素扫描表（行 341-345、432-439）、accoreconsole 参数坑（行 303-306）挤掉了本该有的高层架构总览。
* **Rationale & Recommendation:** 在文件顶部加"当前状态"横幅，把 1–4 节标为历史诊断。

### 7. 文档里的 `文件:行号` 几乎全部过时或错位，不能当作当前代码索引 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 行 46、59、80、85、98-102、114、120、165、234-236、242、246
* **归类:** [文档与代码漂移]
* **Discrepancy Description:** 逐条核对：`MaskService.cs:137` 现为注释区，`CreateWipeout` 在 `MaskService.cs:154-194` 且已重写为 `SetMaskingArea` 像素坐标；`MaskService.cs:76` 现为 `:121`；`:180`（`PlaceMaskBefore`）现为 `:297`；行 80 称 `CapabilityProbe.WipeoutWriter()` 在 `Rules/TextMaskRule.cs:72`，实际该函数在 `CapabilityProbe.cs:104`，TextMaskRule 运行期根本不调用它，而是用 `MaskService.IsWipeoutEncoded`（`:1117`）；行 85-90 的 `TextMaskRule.cs:433` 无条件跳过块内文字，现为 `:493-519` 且已受 `MaskInsideBlocks` 控制；行 98-102 的 `:55`/`:600` 现对应 `:45` 与 `:836`；行 165 的 `MaskService.cs:76-101`"补宽度两段"已不存在。行 120 把"旋转文字遮罩是轴对齐大盒子"列为缺陷，但 `TextBounds.cs:463-490` 已有 `RotatedRectVertices`、`TextMaskRuleTests.cs:838-852` 有回归用例，该缺陷实际已修，报告没标。
* **Rationale & Recommendation:** 逐条更新或删除，否则照 1–4 节实现会复现已修好的 bug。

### 8. 9.3 / 9.6 / 9.7 三节对"当前行为"的描述互相打架；9.3 默认值表不完整 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 9.3（行 399-409）、9.5（行 422）、9.6（行 430、445-446、453）、9.7（行 465-466、478）
* **归类:** [文档与代码漂移]
* **Discrepancy Description:** 9.3 行 409 写"MTEXT 写 `UseDrawingWindowColor`（bit1，不是 bit0）"，9.6 行 445 写"bit0+bit1"，源码 `MaskService.cs:138-143` 是 `UseBackgroundFillColor | UseDrawingWindowColor` 并给 `BackgroundColor = new Color(7)`、`BackgroundScale = MaskOffsetFactor(...)` —— 9.3 错、9.6 对。9.5 说偏移因子"尽力而为、文件里根本没有 45"，9.6 行 453 又宣布该限制"不复存在"。9.7 才首次引入 `MaskTextBox=Auto`（全文仅行 465 出现一次）与 `MaskBringTextToFront`，而 9.3 的默认值表（行 399-407）没有列这两项，表格已不完整。更要紧的是行为冲突：9.7 默认 Auto 下框宽的 MTEXT 走 `ClearMTextBackground`（`TextMaskRule.cs:285`）改 Wipeout，根本不调用 `ApplyMTextBackground`，所以 9.6 行 446"旧版只写过 bit1 的文件重跑一次就能补齐 bit0"只对 `NativeMTextMask == true`（框贴文字）成立；框宽文件重跑会被清掉换成 Wipeout，不会"补齐"。9.3 表格其余 7 项与 `TextMaskRule.cs:43,46,50,56,474-478,1049-1062,1011-1023` 一致，但分支判定用 `TextBounds.cs:527` 自述"误差 ±10%"的估测宽度做 `RectangleWidth > tightWidth * 1.05`（`:569`），阈值附近会随机落到两条不同路径——残留的实现级 bug 风险。
* **Rationale & Recommendation:** 统一"当前默认值"单一来源；补齐表格；用实测字形宽度或滞回区间消除阈值抖动。

### 9. 占位实体技巧依赖 HashSet 内部槽位行为，且每次移动都改句柄 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 9.7（行 471-472）、`MaskService.cs:279-291, 321-346`
* **归类:** [真实功能 bug]
* **Discrepancy Description:** `ReorderToTop`（`:321-346`）靠 `CadObjectCollection` 底层 HashSet 的 freeList 槽位复用这一未文档化内部行为，用占位 `Point` 填槽来逼后续 `Add` 落到末尾；注释 `:279-291` 把这个内部假设写成了设计依据。库版本一变就静默失效。文档 9.7 行 472 自认"被移动的实体会拿新句柄"。
* **Rationale & Recommendation:** 正确位置是修根写出器的排序表（见发现 2），而不是把文字全推到集合末尾、顺带放弃用户原有绘制顺序。

### 10. 配置面反而膨胀到 15 个开关，其中多个是废弃路径残留，还写进了启用样例 — Severity: Major
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 9.2/9.3、`BatchConfig.cs:663-780`、`RuleValidator.cs:176-251`、`samples/text-mask.json:13-27`
* **归类:** [文档与代码漂移]
* **Discrepancy Description:** 9.2 自称"收敛到原生路径、只有一条主线"，但 `MaskMode` 只剩 `Auto`/`Wipeout` 两个完全同义的值（`TextMaskRule.cs:992-1009` 两者都直接 return）；`DimensionMaskMode` 还收着 `BlockInternal` 当 Native 的兼容别名（`:1011-1023`）；`RuleValidator.cs:187-208` 还在校验 `MaskSolidColorIndex`/`MaskPadding` 两个已退役键；`MaskMinOccludedRatio`、`MaskIgnoreBlockBounds` 在默认关闭遮挡判定时不产生有效行为。这些连同明知失效的 `MaskSendHatchToBack: true` 一起写进了 `samples/text-mask.json:16,24` —— 一个给用户照抄的启用样例。`MaskRebuildText`（默认 true）设 false 只会让 TEXT/紧框 MTEXT 全部 `MarkSkipped("需要重建文字")`（`:274-282, 310-318`），等于一个纯粹的"关掉功能"开关。
* **Rationale & Recommendation:** 反复推翻方案时只加新开关、不删旧的，配置面就成了方案变更的沉积层。

### 11. 3.3 声称可复用 `BlockLayerChangeRule.MoveDefinitionContent` 的去重逻辑，实际没复用 — Severity: Nit
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 3.3（行 183）、5.3（行 252）、`BlockLayerChangeRule.cs:248-356`
* **归类:** [文档质量问题]
* **Discrepancy Description:** `BlockLayerChangeRule.cs:250,274` 用 `HashSet<BlockRecord> processed` 做"同一块定义只处理一次"，并处理外部参照、匿名块、嵌套递归；`TextMaskRule` 用自建 `BuildReferenceCounts`（`:694-724`）统计"被多少 Insert 直接引用"，仅为预览文案。块内可写判定 `:502-509` 用了同款 `DatabaseWalker.IsLayoutBlock/IsExternalReference`，但没复用 `processed`，也没做 3.3 要求的"动态块匿名实例块 + DynamicBlockTableRecord"处理，而是一律 `hit.Owner.IsAnonymous` 跳过（`:504`）。
* **Rationale & Recommendation:** 3.3 的"复用去重逻辑"既没落地也没必要，属 Leverage Existing 维度的错误建议。

### 12. 第 7 节复现材料与 9.1/9.6 引用的证据文件在仓库中不存在 — Severity: Nit
* **Reviewer Lens:** egm-zoom-out
* **Affected Scope:** 第 7 节（行 288-301）、9.1（行 299-300）、9.6（行 432）
* **归类:** [文档质量问题]
* **Discrepancy Description:** `tmpv/masktest/*`、`tmpv/diag/`、`tmpv/maskrun/*` 全部 `未核实`（`**/tmpv/**` 无匹配）；`.workbuddy-ai/bgprobe/DimFillProbe.cs`、`DimScan.cs`、`out/dim-ac.png`、`dimacscan.txt`、`out/m90.dwg`、`m90.png` 同样 `未核实`。仓库里只有 `.workbuddy-ai/probe2/` 下名字不同的探针（BgFix.cs、BgFlag.cs、DimFill.cs、SortProbe.cs 等），与文档列的路径对不上。9.1、9.6 的关键出图证据因此无法从仓库内核验。`C:\Users\lijun\Downloads\SampleDwg`（行 5）也不在仓库内。`samples/text-mask.json` 确实存在，已核对。
* **Rationale & Recommendation:** 把复现脚本与基线 PNG 入库，或改成指向实际存在的路径。

---

## 补充核对（关系到"是否还有 bug"的直接回答）

- 9.2 声称 `CreateSolid`/`MaskSolidColor` 已删除属实——全仓库搜索这两个符号只出现在报告正文，`MaskService.cs` 里对应的是 `MaskFrameColor()`（`:205-208`）。
- 5.2 声称新增的 `MaskService.WipeoutWriteSupported = false`（行 242）与 `MaskService.MaskSolidColor(255)`（行 246）在当前源码中**不存在**，这两条是过期描述。
- 5.1 声称签名改成 `ApplyMTextBackground(mtext)`（行 236），当前实际是 `ApplyMTextBackground(MText mtext, double paddingRatio)`（`MaskService.cs:121`）且会写 `BackgroundScale`，描述不符。
- 5.4 的 `out MaskPath` 计数修正属实（`TextMaskRule.cs:226, 1065-1071`）。
