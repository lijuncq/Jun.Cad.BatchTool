# Goldfish Annotated Review Report

**Target:** `textmask-failure-report.md` (full file, 1..481) + production sources / configs / README / tests
**LineRange:** full file（用户选择「源码 + 文档，全量修」，TargetFile 扩为代码树 + 文档）
**FindingsList:** `docs/review/textmask-2026-11-05/consolidated_review_report.md` 的去重结论 C01–C21、D01–D14、R01–R08（D14 与 R01 是同一条误报，已合并）
**Reviewers:** egm-zoom-out / egm-comprehension / egm-design / egm-structural / egm-style / egm-implementation-readiness / egm-codebase-details + 编排层交叉核验
**Mode:** RESOLVE ｜ **Status:** resolved（30 条 PROPOSE FIX 已全部落地）
**执行轨迹：** 初次 Step 4 用户回复 `continue`，按 skill 语义未应用任何修复；随后用户明确追加指令
「继续 源码+文档 全量修」，据此把第 2 节的 30 条 PROPOSE FIX 全部落地到源码、测试、配置、README、
目标文档与 `VERIFY.md`。上一轮同名且已 `resolved` 的旧报告备份在
`docs/review/textmask-2026-11-05/goldfish_review_report-2026-10-31-resolved.md`。
**第 1 节 3 条 ESCALATE 与第 3 节 9 条 CONTEST 一律未改**（前者需用户拍板，后者为撤销误报）。
**宿主门禁已补跑（2026-11-05，用户「继续未完工作」）**：`VERIFY.md` 第 2–5 项已在 AutoCAD 2025
Core Console 上用修复后产物实跑。`ssget` / `entget` / `-PLOT` 逐像素差分通过，`WIPEOUTFRAME` 仍为已知
未修复项；签发范围严格限定，C01 / C20 / C21 与交互式界面观感**仍不签发**。详见第 5 节与 `VERIFY.md`。

---

## 0. 分类汇总

| 类别 | 条数 | 处置 |
|---|---:|---|
| **ESCALATE** | 3 | C01 / C20 / C21，需要用户的领域知识 / 架构决策，不擅自改（见第 1 节） |
| **PROPOSE FIX** | 30 | 批评成立，**已按用户「源码+文档 全量修」指令全部落地**（见第 5 节）；第 2 节的精确替换文本原样保留作为审计轨迹 |
| **CONTEST** | 9 | 批评不成立，附可核对的源码 / 文件证据，不改 |
| **DECLINE** | 0 | 用户选了全量修，Nit 也一并修 |

第 2 节共 31 条 Issue：其中 Issue 2 是 C01 在正文里的 ESCALATE 占位（与第 1 节同一条，不重复计数），其余 30 条为 PROPOSE FIX。

严重度映射：`High` = Blocker / P0，`Medium` = Major / P1，`Low` = Nit / P2。

---

## 1. ESCALATE — 需要你拍板（gate 前置）

这三条**不是**「要不要修」的问题，而是「选哪条路」的产品 / 架构决策，且证据部分落在仓库外或需要宿主环境。AI 单方面选错会直接破坏现有出图。

| ID | 决策点 | 为什么不能替你定 | 选项 |
|---|---|---|---|
| **C01** | 绘制顺序的**唯一权威来源** | 综合报告 §三 P0 明确警告：现在的「集合顺序 + 占位实体」workaround **只在 SORTENTS 写不出去时才生效**；**先修写出器而不动规则会立刻破坏现有出图**。且丢表根因位于仓库外 ACadSharp 克隆的 `DwgObjectWriter.Objects.cs`，综合报告 §八.2 列为未独立复核 | **A**：修写出器让 SORTENTS 落盘 + 给遮罩/文字写 sort handle（同时重写占位实体路径，工作量大，需宿主验证）；**B**：明确「绘制顺序不在本工具范围内」，删掉重排 + 「填充置后」整条路径，保留 C02 的停用但不再声称保证顺序；**C**：冻结现状，只在文档 / README 写破坏性副作用与 `--out` 到副本的运维规避指引 |
| **C20** | 私有 ACadSharp 克隆如何纳入构建链 | `Jun.Cad.Batch.Core.csproj:20` 引用 `..\..\..\ACadSharp-v3.8.35\...`，干净检出编不过。打包方式取决于你的发布流程 / 是否允许 vendor 第三方源码，仓库内无依据 | **A**：submodule 钉死提交；**B**：私有 NuGet 包；**C**：把 `SetMaskingArea` 等库层改动以最小 patch 形式 vendor 进本仓库；**D**：暂不改，仅写进构建前置条件文档 |
| **C21** | 块内 TEXT 遮罩比文字框低约一个字高 | 报告 `:271` 说「需要复核」，未核实是否仍复现；判定需要 AutoCAD 2025 出图（`TextBounds.ForText` 的 `InsertPoint`/`AlignmentPoint` 坐标系），本轮无宿主会话 | **A**：你确认仍复现 → 按 `TextBounds.cs:205-262` 补块定义坐标系修正 + 回归；**B**：确认已消失 → 把 `:271` 从「未解决」改为「已消除」；**C**：本轮跳过，留作 `VERIFY.md` 的宿主门禁项 |

> 另有一项**事实性依赖**（非决策）：§4 的 5 条验收、C11 的 `WIPEOUTFRAME=1` 复现、C01 的 `dictsearch` 结论，全部需要 AutoCAD 宿主。本轮只能修代码与文档，不能替你签发「宿主验收通过」。

---

## 2. Prioritized Findings & AI Suggestions

### High

#### [Issue 1] 标注 ACAD XData 被清空 + 已有 DIMTFILL 被无条件改写 [`egm-design` / `egm-codebase-details`] — Severity: High

* **Affected Scope:** [396-408, 431-452](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L396-L452)（C03）；外部克隆 `Entities/Dimension.cs:514-526`
* **Discrepancy Description:** `SetStyleOverrideMap` 先 `edata.Records.Clear()` 清掉 `AppId.DefaultName`（ACAD）下**全部** XData 记录，再写回单一 DSTYLE 段；`GetStyleOverrideMap` 只取第一段控制串之间的键值对，段外记录不会回来。`ApplyDimensionMask` 对每个待处理标注都调一次 → 静默删除用户数据。另 `HasDrawingBackgroundFill` 只认 `69==1`，用户已设的 `DIMTFILL=0/2` 被判「无遮罩」后无条件改成 1。
* **Rationale:** 本次审查中唯一确认的**用户数据丢失**路径；报告 `§9.1:382` 只核对了 DSTYLE 段内「原有 140 等键全部保留」，未提段外数据会丢。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。库层在仓库外私有克隆里（见 C20 / ESCALATE），因此修在产品侧：不再调用 `SetStyleOverrideMap`，改为在现有 `ExtendedData` 上做 **DSTYLE 段的局部替换**，段外记录原样保留。
* ***Proposed Edit:***
  * Location: `TextMaskRule.cs:431-452`
  * Change: 把 `HasDrawingBackgroundFill` 整体替换为「69 键存在即跳过」：
    ```csharp
    /// <summary>该标注是否已有 DIMTFILL（组码 69）样式覆盖。只看键是否存在，不看取值：
    /// 用户显式设的 0（无填充）/2（指定色）是有效配置，不能被本规则静默改成 1。</summary>
    private static bool HasDimensionFillOverride(Dimension dimension)
    {
        if (!dimension.HasStyleOverride) return false;
        DxfClassMap map = dimension.GetStyleOverrideMap();
        return map != null && map.DxfProperties.ContainsKey(69);
    }
    ```
  * Location: `TextMaskRule.cs:388-393`
  * Change: `if (HasDrawingBackgroundFill(dimension))` → `if (HasDimensionFillOverride(dimension))`；跳过原因 `"标注文字已有背景遮罩"` → `"标注已有 DIMTFILL 覆盖，保留用户设置"`。
  * Location: `TextMaskRule.cs:396-408`
  * Change: 删除 `GetStyleOverrideMap()` + `map.DxfProperties[69] = fill` + `SetStyleOverrideMap(map)` 整块，改调新增的 `ApplyDimensionFillOverride(dimension)`。该方法：取 `dimension.ExtendedData` 的 ACAD 记录 → 定位第一个 `ExtendedDataControlString(false)` 到配对 `true` 之间的 DSTYLE 区间 → 区间内若无 `1070 . 69` 则插入 `ExtendedDataInteger16(69)` + `ExtendedDataInteger16(1)`，有则**不动** → 区间外记录一条不删；若根本不存在 DSTYLE 段，则在 ACAD 记录**末尾追加**完整段（`ExtendedDataString("DSTYLE")` + `{` + 69/1 + `}`），**不调用 Clear**。执行时复读 `ACadSharp/XData` 下 `ExtendedDataRecord` 的确切类型名。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增 3 条断言 —— ① ACAD XData 含 DSTYLE 段之外的记录时，跑完仍存在；② 已有 `69=2` 的标注不被改写；③ 已有 `69=0` 的标注不被改写。

#### [Issue 2] SORTENTS / 块记录扩展字典写不出去，保存重置用户 DRAWORDER [`egm-zoom-out` / `egm-structural`] — Severity: High

* **Affected Scope:** [470, 481](textmask-failure-report.md#L470-L481)（C01）；`README.md:771`；`MaskService.cs:305-307`；`TextMaskRule.cs:864-883`
* **Discrepancy Description:** ACadSharp 写出的块记录扩展字典 AutoCAD 读不到，`ACAD_SORTENTS` 挂在下面，连 AutoCAD 自己写的排序表经库往返也会丢。这是**与遮罩无关但由本工具触发的用户数据丢失**。
* **Rationale:** 阶段 8 的单点结论：占位实体技巧（`MaskService.cs:321-346`）只在排序表被写丢时才「生效」，两处互相依赖，单改一处即破坏现有出图。
* ***AI Suggestion:*** **ESCALATE**
* ***Justification:*** 修复方向是互斥的架构决策（`A` 修写出器 / `B` 删掉整条重排 / `C` 冻结并只写规避指引），且丢表根因在仓库外克隆、未独立复核。见第 1 节 C01。
* ***Proposed Edit:*** 无（待用户选 A/B/C）。选定后：`A` 需同时改库写出器与 `MaskService.ReorderToTop`；`B` 需删 `PlaceMaskBefore`/`BringToFront` 调用点、`MaskBringTextToFront` 配置与相关测试；`C` 只需改文档（见 D13）。

#### [Issue 3] `MaskSendHatchToBack` 假成功：默认开、每次跑、报成功、文件里没写 [`egm-design` / `egm-structural`] — Severity: High

* **Affected Scope:** [55, 77-78, 100-101, 162-180, 201-204, 853-883](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L55-L883)（C02）
* **Discrepancy Description:** `PushHatchesToBack` 对每个 Hatch 调 `table.MoveToTop(hatch)` 并 `moved++`，累加进 `movedToBack`，汇总打印「填充置后 N」，日志打「已把 N 个…送到最底层」。而 `README.md:583`、报告 `§9.7:470`、源码注释 `TextMaskRule.cs:856-860` 一致声明排序表不落盘，`:470` 明说「从未真正生效过——只计数、文件里没写」。
* **Rationale:** 附带成本：`:100-101` 因它每次执行都建整空间 `BoundsIndex`，即使 `MaskOnlyWhenOccluded=false`。综合报告明确警告「不要只把『填充置后 6』改成『历史数据』——那会删掉唯一可见症状」。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，且是所有 P0 里唯一可以**独立、安全**停用的路径（不触碰 C01 的绘制顺序耦合）。
* ***Proposed Edit:***
  * Location: `TextMaskRule.cs:55` — `bool sendHatchToBack = RuleContext.Bool(action.MaskSendHatchToBack, true);` → `false`（保留字段读取以兼容旧配置，但默认停用；显式写 true 时打一条 `Warn` 说明不产生文件变更）。
  * Location: `TextMaskRule.cs:100-101` — `(onlyOccluded || sendHatchToBack) ? BuildOccluders(...) : null` → `onlyOccluded ? BuildOccluders(...) : null`。
  * Location: `TextMaskRule.cs:77-78` — 删除 `int movedToBack = 0;` 与 `var hatchesToBack = new List<Hatch>();`。
  * Location: `TextMaskRule.cs:162-172`、`:176-180`、`:201-204` — 删除三整块。
  * Location: `TextMaskRule.cs:853-883` — 删除 `PushHatchesToBack` 方法及其 XML 注释整块。
  * Location: `Jun.Cad.Batch.json:205` / `samples/text-mask.json:24` — `"MaskSendHatchToBack": true` → `false`；模板注释 `// Move overlapping hatches behind text...` → `// Deprecated: SORTENTS is not persisted; this switch never changed the output file. Kept false for compatibility.`
  * Location: `README.md:583` — 表格行改为「默认 `false`，**已停用**；原实现只计数不改变输出文件，绘制顺序由 `MaskBringTextToFront` 负责」。
  * Location: `README.md:847` / `:864` / `textmask-failure-report.md:417` — 「62 个填充被置后」「填充置后 6」标注为历史轮次的**无效指标**，不再当成功证据。

#### [Issue 4] §9.3「bit1，不是 bit0」与代码 / §9.6 相反，照抄会写出无遮罩文件 [`egm-implementation-readiness` / `egm-style`] — Severity: High

* **Affected Scope:** [409](textmask-failure-report.md#L409-L409)（D02）
* **Discrepancy Description:** `§9.3:409`「MTEXT 写 `BackgroundFillFlags.UseDrawingWindowColor`（bit1，**不是 bit0** 的显式 RGB）」与 `MaskService.cs:138-143`（bit0+bit1 → `90=3` + `BackgroundColor=ACI 7` + `BackgroundScale`）及 `§9.6:445` 直接相反。
* **Rationale:** 按 §9.3 实现就退回 §9.6 已证实「AutoCAD 当没有遮罩」的 `90=2` 缺陷。**这不是措辞差异，是会把功能改坏的指令。**
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，且是全文档危害最高的一句。
* ***Proposed Edit:***
  * Location: `textmask-failure-report.md:409`
  * Change: 末句 `MTEXT 写 \`BackgroundFillFlags.UseDrawingWindowColor\`（bit1，不是 bit0 的显式 RGB），标注写 \`DIMTFILL=1\`` → `MTEXT 写 \`UseBackgroundFillColor | UseDrawingWindowColor\`（bit0+bit1，DXF 90=3，并带 63/45 成套组码；**标志编码以 §9.6 为准**，只写 bit1 的 90=2 会被 AutoCAD 判为「没有遮罩」），标注写 \`DIMTFILL=1\``；并在 §9.3 标题下补一行 `> 本节是默认值变更记录；MTEXT 背景标志的编码以 §9.6 为唯一准绳。`

#### [Issue 5] §5 / §8 / §9.2 三套互斥实现，无作废声明 [`egm-structural` / `egm-design`] — Severity: High

* **Affected Scope:** [230-274, 310-329, 391-395](textmask-failure-report.md#L230-L395)（D01）
* **Discrepancy Description:** §5.2（Wipeout 不可用 → 2D Solid）、§8（Auto 优先 Wipeout、探测不过降级 Solid）、§9.2（Solid 退役、Auto/Wipeout 同义、自检不过直接抛）三套互斥；当前代码**只对应 §9.2**，`WipeoutWriteSupported`/`MaskSolidColor`/`CreateSolid` 全 `src` 零命中；`§5.2:244` 仍写「`MaskMode=Wipeout` 直接抛」。
* **Rationale:** 陌生读者只看 1..229 会得出 5 个全是错的结论。综合报告第七节要求「先改代码、后改文档」，但作废声明属于纯文档、可先落。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:***
  * Location: `textmask-failure-report.md:230`（`## 5.` 标题下）
  * Change: 插入 `> **状态：本节（§5）与 §8 为历史实施记录，已作废。** 当前唯一有效实现见 §9.2 / §9.6 / §9.7：只有原生遮罩一条路（MTEXT 背景 90=3、TEXT/ATTRIB 的 Wipeout、标注 DIMTFILL=1），没有 2D Solid 兜底，也没有 `MaskMode=Wipeout` 抛异常的行为。本节提到的 `WipeoutWriteSupported`、`MaskSolidColor`、`CreateSolid` 在当前源码中均不存在。`
  * Location: `textmask-failure-report.md:310`（`## 8.` 标题下）
  * Change: 插入 `> **状态：§8 的「Auto 优先 Wipeout、探测不通过降级 Solid」已作废**（见 §9.2）。§8 的 ACadSharp 库层接入与渲染实验仍有效，但产品规则的降级路径从未落地。`
  * Location: `textmask-failure-report.md:391`（`### 9.2` 标题下）
  * Change: 插入 `> **当前唯一有效实现**：\`TextMaskRule.RequireNativeMaskMode\`（Auto/Wipeout 同义，Solid 与未知值硬错误）+ \`MaskService.IsWipeoutEncoded\`（自检失败直接抛）+ \`MaskService.ApplyMTextBackground\`（bit0+bit1）。`

#### [Issue 6] 全部「已确认 / 已验收」无可复现命令，也没有基线 [`egm-design` / `egm-structural`] — Severity: High

* **Affected Scope:** [216-226, 260-266, 321-327, 337-352, 411-418, 474-481](textmask-failure-report.md#L216-L481)（D11）
* **Discrepancy Description:** 没有一条 `accoreconsole.exe` 完整命令行、`-PLOT` 参数、LISP 加载方式、`dotnet test` / `Jun.Cad.Batch.Cli run` 命令、expected PNG / 统计 JSON / 像素断言脚本。§4 自定的 5 条「缺一条都算没修好」没有转成任何自动化断言。`README.md:851` 说渲染对照「还需完成」，报告 §8.1 标题说「已完成」。
* **Rationale:** 诊断阶段亲手证伪过「能写能读 ≠ 有效」，实施阶段又用「能写能读」签发「已解决」。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。用户选了全量修，补一份可执行验收门禁是必要交付。
* ***Proposed Edit:**
  * Location: 新建 `VERIFY.md`（仓库根）
  * Change: 逐条给出「命令 + 输入图路径 + 预期断言」，至少覆盖：① `dotnet test Jun.Cad.Batch.sln --nologo -v minimal` → `Failed 0 / Passed 296 / Skipped 5 / Total 301`（当前基线）；② `accoreconsole.exe /i <dwg> /s <scr> /l en-US` 的 `ssget` WIPEOUT 计数；③ `-PLOT` + `PublishToWeb PNG.pc3` 的逐像素区间断言（引用 §7 的 `pngscan.py`）；④ `(90 . 3) (63 . 7) (45 . 1.15)` 与 `(1070 . 69) (1070 . 1)` 的 `entget` 回读；⑤ `WIPEOUTFRAME` 重开值。每条标注「宿主门禁（需 AutoCAD）/ 可自动断言」两类。
  * Location: `textmask-failure-report.md:226`
  * Change: 末段追加 `> 本节 5 条验收的可执行命令与断言见仓库根 \`VERIFY.md\`；其中宿主渲染项需 AutoCAD 会话，库层往返不构成验收。`
  * Location: `tests/Jun.Cad.Batch.Tests/`
  * Change: 新增一条「TextMask × 真实 DWG × 写盘回读」端到端用例（`CadFileSession.Save` → 重新 `Open` → 断言 Wipeout 数 / MTEXT `flags=3` / DIMENSION 69），并把样图入库以消除 `SampleDwgFactAttribute` 的 Skip。

### Medium

#### [Issue 7] 已存在且不可打印的 `_MASK` 图层不会被改回可打印 [`egm-design`] — Severity: Medium

* **Affected Scope:** [1212-1217](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L1212-L1217)（C06）；`LayerService.cs:34-38`
* **Discrepancy Description:** `EnsureLayer` 命中已存在图层直接 `return existing`，`PlotFlag` 只在新建时设；`MaskLayer()` 只在 `MaskLayerPlottable=false` 时写 `false`，默认 `true` 时什么都不做 → 老版本产出的非打印 `_MASK` 图层不会被纠正，Wipeout 落在不打印图层，出图没遮罩而汇总报成功。
* **Rationale:** 报告 `§5.2:245` 记录过「默认从 false 改成 true」，即更早版本确实产出过这类文件。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，改动局部、无耦合。
* ***Proposed Edit:***
  * Location: `TextMaskRule.cs:1212-1217`
  * Change: 整块替换为
    ```csharp
    // EnsureLayer 命中已存在图层时直接返回、不改 PlotFlag，这里必须显式按配置纠正：
    // 老版本曾默认 false，留下过一批带非打印 _MASK 图层的输出图。
    bool plottable = RuleContext.Bool(_action.MaskLayerPlottable, true);
    if (plottable && !_layer.PlotFlag)
    {
        _context.Log.Info(string.Format("遮罩图层「{0}」原本不可打印，已改为可打印。", _layerName));
    }
    else if (!plottable && _layer.PlotFlag)
    {
        _context.Log.Warn("MaskLayerPlottable=false：该图层上的 Wipeout 遮罩不会出现在打印结果里。");
    }

    _layer.PlotFlag = plottable;
    ```
  * Location: `README.md:588` / `BatchConfig.cs` 的 `MaskLayerPlottable` Help
  * Change: 补一句「对已存在的 `_MASK` 图层同样生效」。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增断言：预置 `_MASK` 且 `PlotFlag=false`，默认配置跑完 `PlotFlag=true`。

#### [Issue 8] `MaskBringTextToFront=false` 对 Wipeout / 紧贴路径完全无效 [`egm-codebase-details` / `egm-design`] — Severity: Medium

* **Affected Scope:** [244-247, 290, 332, 206-209](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L206-L332)（C04）；`MaskService.cs:297-346`
* **Discrepancy Description:** `bringToFront` 只包住 MTEXT 原生与 DIMENSION 分支；两处 `PlaceMaskBefore` **无条件**执行，文字仍被 Remove+Add、拿新句柄、挪到末尾。汇总与 `RuleSummary` 却按配置值决定是否显示「文字前置 N」→ 日志说不前置、实际已前置。
* **Rationale:** `TextMaskRuleTests.cs:351-362` 只测了 `RectangleWidth=0` 的原生路径，没抓到。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，但**彻底消除**需要一种「不改句柄又能把遮罩插到文字之前」的机制——`CadObjectCollection` 底层是 `HashSet<T>`，做不到按位插入。因此本条修为**语义对齐 + 回归覆盖**，并在配置契约里写死限制，而不是假装开关生效。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:206-209`
  * Change: `if (bringToFront && created > 0) parts.Add("文字前置 " + created);` → 按实际重排数统计：新增 `int reordered` 计数，在两处 `PlaceMaskBefore` 与 `BringToFront` 处自增，汇总用 `reordered` 而非 `created`；文案改为「重排到最上层 N（含 Wipeout 路径，句柄已变）」。
  * Location: `src/Jun.Cad.Batch.Core/Editing/RuleSummary.cs:358` / `README.md:581` / `Jun.Cad.Batch.json` 的 `MaskBringTextToFront` 注释
  * Change: 明确写「该开关仅控制 MTEXT 原生路径与标注；TEXT/ATTRIB/ATTDEF 及紧贴 Wipeout 路径为保证遮罩排在文字之前，**始终重排且句柄必变**」。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增 2 条：`MaskBringTextToFront=false` + TEXT（Wipeout 路径）断言文字仍被重排；`false` + 宽框 MTEXT（紧贴路径）同上。

#### [Issue 9] 动态块 `*U` 文字零覆盖、嵌套 Insert 的 ATTRIB 全部漏遮 [`egm-design` / `egm-structural`] — Severity: Medium

* **Affected Scope:** [502-509, 600-653](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L502-L653)（C07）；`DatabaseWalker.cs:168-184`
* **Discrepancy Description:** `CollectFrom` 对 `nested.IsAnonymous || IsExternalReference(nested)` 直接 `continue`；`IsAnonymous` 是标志位，覆盖 `*U` 动态块实例块、`*D`、`*T` 全部匿名块。ATTRIB 收集循环在 `hit.Depth > 0` 时 `continue`，而 ATTRIB 只挂在 Insert 上、这是唯一入口 → 嵌套块参照上的属性全部漏遮。
* **Rationale:** §3.3:183 承诺过复用 `BlockLayerChangeRule.MoveDefinitionContent` 的动态块处理，README 四处已悄悄改成 "unsupported" 但没回写报告。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。范围控制：先补**嵌套 ATTRIB**（改动小、无宿主依赖），动态块 `*U` 按「复用已有去重 + `ownDef.Source`」实现；外参与 `*D`/`*T` 继续跳过并写成显式 Non-Goal。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:609-612`
  * Change: 删除 `if (hit.Depth > 0) { continue; }`，改为仅跳过 `hit.Owner` 为外参 / 匿名块的情形（与 `:502-509` 同口径），并补注释说明「ATTRIB 不在 `BlockRecord.Entities` 里，嵌套 Insert 上的属性只能在这个循环收集」。
  * Location: `DatabaseWalker.cs:176-184`
  * Change: `if (nested.IsAnonymous || IsExternalReference(nested)) continue;` → 区分动态块匿名实例块：保留 `IsExternalReference` 跳过；`IsAnonymous` 时，若 `nested.Name` 以 `*U` 开头且能通过 `Insert` 的 `DynamicBlockTableRecord`/`ownDef.Source` 找到宿主定义则入队（复用 `BlockLayerChangeRule.cs:248-356` 的 `processed` 去重），`*D`/`*T` 仍跳过。
  * Location: `TextMaskRule.cs:504` / `textmask-failure-report.md:251` / `README.md:586,594,654,773`
  * Change: `*D/*T` 枚举补全为「`*D`（标注缓存）、`*T`（表）、`*U`（动态块匿名实例）」；动态块若实现则把 README 的 "unsupported" 改为「已支持」并说明限制，否则统一写成显式 Non-Goal。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增嵌套 Insert 带 ATTRIB 的用例，断言属性被收集。

#### [Issue 10] `MaxMasksPerDrawing` 是「每次执行上限」，重复运行可绕过 [`egm-design`] — Severity: Medium

* **Affected Scope:** [50, 73, 132-138](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L50-L138)（C05）；`BatchConfig.cs:772-775`
* **Discrepancy Description:** `created` 是 `Execute` 局部变量，每次归零，只计本次新增；已有遮罩判重跳过不计入预算 → `MaxMasksPerDrawing=100` 对同一输出文件跑 10 次即加满 1000。
* **Rationale:** 字段叫「单图遮罩上限」、`README.md:589` 说 "limits masks"，实际是「单次执行上限」。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。选「让行为符合字段名」而不是「改字段名」——后者会让现有配置语义漂移。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:73`
  * Change: `int created = 0;` → 在 `bySpace` 构建完成后插入预算初值：
    ```csharp
    // 单图上限要跨次执行生效：把图纸里已经存在的遮罩计入预算，
    // 否则同一输出文件每跑一次就再加一批，直到全部文字都加上。
    int created = maxMasks > 0 ? CountExistingMasks(bySpace.Keys, maskLayerName) : 0;
    ```
    并新增 `CountExistingMasks`：统计各空间内 `_MASK` 图层上的 `Wipeout` + `BackgroundFillFlags` 含 bit0 的 MTEXT + 带 DSTYLE 69 的 DIMENSION。
  * Location: `TextMaskRule.cs:134-136`
  * Change: 跳过文案 `"已达单图上限 {0}（含本次执行前已有的遮罩）"`。
  * Location: `README.md:589` / `BatchConfig.cs:772-775` Help
  * Change: 补「统计包含本次执行前图纸中已有的遮罩，因此对同一输出文件重复运行也成立」。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增断言：同一文档连续执行 2 次，第 2 次新增数为 0（上限为 1 时）。

#### [Issue 11] 两个 Dimension 枚举的非法值静默归一 [`egm-codebase-details`] — Severity: Medium

* **Affected Scope:** [1011-1023, 1049-1062](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L1011-L1062)（C09）；`RuleValidator.cs:176-251`
* **Discrepancy Description:** `NormalizeDimensionMaskMode` 对任何非 `Off`（含 `Solid`、拼写错误）一律返回 `Native`；`NormalizeDimensionMode` 对任何非 `Off`/`ExplicitOnly` 一律返回 `All`。`RuleValidator` 校验了 `MaskMode`/`MaskTextBox`/`MaskPaddingRatio`/`MaskMinOccludedRatio`，唯独没校验这两个。与 §9.2「写别的值都是硬错误」原则相反。
* **Rationale:** `BlockInternal` 是有意保留的兼容别名，不能一刀切报错。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:1011-1023`
  * Change: 改为显式白名单 `Off` / `Native` / `BlockInternal`（→ `Native`，注释说明是有意兼容），其它值 `throw new RuleValidationException("DimensionMaskMode=\"" + mode + "\" 不受支持：可选 Off / Native（默认）；旧值 BlockInternal 按 Native 处理。")`。
  * Location: `TextMaskRule.cs:1049-1062`
  * Change: 改为 `All` / `ExplicitOnly` / `Off` 三值白名单，其它抛 `RuleValidationException`。
  * Location: `RuleValidator.cs:227`（`MaskTextBox` 校验之后、`MaskMinOccludedRatio` 之前）
  * Change: 插入两段 `Error(...)` 校验，`DimensionMaskMode` 允许 `Off/Native/BlockInternal`、`DimensionTextMode` 允许 `Off/ExplicitOnly/All`，大小写不敏感；文案与运行期异常一致。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增非法值 → `RuleValidationException` 的断言，以及 `BlockInternal` 仍被接受的断言。

#### [Issue 12] preflight 与生产执行两套判据，且 `TextMaskRule` 从不调 `CapabilityProbe` [`egm-zoom-out`（阶段 8）] — Severity: Medium

* **Affected Scope:** [1100-1129](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L1100-L1129)（C10 / C14）；`CapabilityProbe.cs:104-213`；`MaskService.cs:235-271`
* **Discrepancy Description:** `RunAll()` 只被 `Program.cs:929` 与 `PreflightDialog.cs:123` 调用。preflight 判据（固定轴对齐矩形、顶点数 ==4、包围盒严格 ==(0,0)-(100,50)）与生产判据（旋转四角、`IsWipeoutEncoded` 纯内存、顶点 ≥3、`_encodingChecked` 只查第一个）完全不同；`§9.2:395` 却把自检写成全量安全网。
* **Rationale:** 阶段 8 的核心发现：加强一个没人调用的函数毫无意义；必须让两边共用同一个判据。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。与 C14（只查第一个）同源，合并处置。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:1114-1124`
  * Change: 删除 `if (!_encodingChecked) { ... _encodingChecked = true; }` 外层与字段 `:1087`，改为**每个 Wipeout 都自检**：
    ```csharp
    string reason;
    if (!MaskService.IsWipeoutEncoded(wipeout, out reason))
    {
        throw new RuleValidationException("Wipeout 编码不可用：" + reason + "。本规则没有 Solid 兜底。");
    }
    ```
  * Location: `CapabilityProbe.cs:162-184`
  * Change: 在既有 4 项几何断言**之后**补一行 `MaskService.IsWipeoutEncoded(probeWipeout, out reason)` 断言，让 preflight 与生产共用同一函数；保留原有轴对齐 / 包围盒断言作为 probe 专有的额外检查，并在类注释里写明「probe 额外断言不代表生产保证」。
  * Location: `CapabilityProbe.cs:70-71` 类注释 / `README.md:919`
  * Change: 补「本探测仍非宿主渲染验收；生产路径的逐实体自检在 `MaskRuntime.CreateWipeout`」。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`
  * Change: 新增「第二个 Wipeout 编码异常也抛」的用例（当前 `:854-863` 只直接调 `IsWipeoutEncoded`）。

#### [Issue 13] 默认值整体翻转但无配置版本迁移 [`egm-design`] — Severity: Medium

* **Affected Scope:** [397-409](textmask-failure-report.md#L397-L409)（C08）；`BatchConfig.cs:15`；`ConfigLoader.cs:34,168-210`
* **Discrepancy Description:** `Version` 固定 1、`MissingMemberHandling.Ignore`、无按版本迁移代码。旧配置缺字段时静默继承新默认值：全图加遮罩 + 改句柄 + 改块定义影响所有插入 + 测量值也加 + 无上限。
* **Rationale:** `samples/text-mask.json` 和 `Jun.Cad.Batch.json` 把值钉死了，但那只是样例。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。不引入破坏性的配置格式变更，采用「加载时补显式值 + 告警」的最小迁移。
* ***Proposed Edit:**
  * Location: `ConfigLoader.cs`（加载完成、校验之前，约 `:168-210`；执行时复读该段确认插入点）
  * Change: 新增 `MigrateTextMaskDefaults(BatchConfig config, BatchLogger log)`：对 `Version <= 1` 且 `Type == TextMask` 的规则，若 `Action` 中缺少 `MaskOnlyWhenOccluded` / `MaskInsideBlocks` / `MaskRebuildText` / `DimensionTextMode` / `DimensionMaskMode` / `MaxMasksPerDrawing` / `MaskSendHatchToBack` 任一键，则按当前默认**写入显式值**并打一条 `Warn` 列出被补齐的字段与升级前后的行为差异。已显式写过任何值的配置不改动。
  * Location: `README.md`（配置参考表之后）
  * Change: 新增「旧配置升级后的行为变化清单」小节，逐字段列旧默认 → 新默认 → 影响面，并给出回滚方式（把显式值写回旧值）。
  * Location: `tests/Jun.Cad.Batch.Tests/`
  * Change: 新增断言：一份只含 `Type: TextMask` 的旧配置加载后，关键字段被补齐且日志含告警。

#### [Issue 14] `WIPEOUTFRAME` 边框实际没关掉，文档却写「已缓解 / 已验收」 [`egm-zoom-out` / `egm-style`] — Severity: Medium

* **Affected Scope:** [354-365](textmask-failure-report.md#L354-L365)（C11）；`MaskService.cs:48-69,196-208`；`TextMaskRuleTests.cs:495-509`
* **Discrepancy Description:** `HideWipeoutFrame` 写 `ACAD_WIPEOUT_VARS` 的 `70=0`，报告 `:360` 自认在 AutoCAD 2025 重开仍报 `WIPEOUTFRAME=1`；实际只靠真彩色白实体色规避，深色屏幕仍见白框。测试恰好把这次无效写入锁成断言。
* **Rationale:** 上游 DWG 写出器缺陷，产品侧无法真正关闭。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。产品侧可做的是「诚实标注 + 测试口径」，真正的边框修复属于上游，列入未修清单（不擅自改库）。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:354` — 标题 `### 8.2 边框问题（2026-11-02 晚，已缓解）` → `### 8.2 边框问题（未修复，仅白色实体色规避）`。
  * Location: `textmask-failure-report.md:364` — `结论：擦除已验收；边框在上游修好前靠白色实体颜色规避（深色屏幕上仍能看见白色细框，已知限制）。` → `结论：**擦除已验证，边框未修复**。写入 \`70=0\` 在 AutoCAD 2025 重开后仍为 \`WIPEOUTFRAME=1\`，白色实体色只在白底出图上让边框不可见，深色绘图区仍可见白色细框。需上游 DWG 写出器修复，产品侧无解。`
  * Location: `README.md:775` — 「frame limitation remains」保留，补「`WIPEOUTFRAME` 无法通过该写入关闭」。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs:495-509`
  * Change: 测试名与断言改为「已发出 `DisplayImageFrame=false` 写入」，去掉任何「边框已隐藏」的语义。
  * Location: `VERIFY.md` — 把 `WIPEOUTFRAME` 重开值列为宿主门禁项。

#### [Issue 15] 遮挡判定整类排除 `Insert`（根因 C 未修），开关可达但明知有错 [`egm-design` / `egm-structural`] — Severity: Medium

* **Affected Scope:** [43-45, 107-129, 836-839, 895-943](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L43-L943)（C12）；`textmask-failure-report.md:187-194`
* **Discrepancy Description:** `BuildOccluders:836-839` 默认排除 `Insert`，没有展开块定义几何；`ComputeOcclusion` 仍用 0.05 阈值 + 每条线 0.25 权重。默认 `MaskOnlyWhenOccluded=false` 只是绕过，显式打开即暴露。
* **Rationale:** §3.4 的四条修改一条都没落地，文档不能默认承诺该开关可用。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。完整实现「二值相交 + Insert 展开」需要图形变换与缓存，工作量大且无宿主验证手段；采用「显式声明不可用 + 阻止误用」的保守修法，并把它列为后续工作。
* ***Proposed Edit:**
  * Location: `RuleValidator.cs:241`（现有 `Filter.EntityTypes` 警告之后）
  * Change: 插入：当 `MaskOnlyWhenOccluded=true` 时 `Warning("Action.MaskOnlyWhenOccluded", "遮挡判定未纳入块参照几何，0.05 面积比阈值与每条线 0.25 权重仍为启发式；该开关当前不可靠，建议保持 false（默认）。")`。
  * Location: `README.md:584` / `Jun.Cad.Batch.json` 的 `MaskOnlyWhenOccluded` 注释 / `textmask-failure-report.md:401`
  * Change: 明确「该开关为**实验性、结果不可靠**；`Insert` 整类未参与判定，块参照下的文字会被判为无遮挡」。
  * Location: `textmask-failure-report.md:187`（§3.4 标题下）
  * Change: 插入 `> **状态：本节方案未实施**（2026-11-05 复核）。当前实现仍是面积比 + 0.25 权重 + 排除 Insert；默认把 \`MaskOnlyWhenOccluded\` 关掉以绕过，不是修复。`

#### [Issue 16] §9.3 把加 DIMENSION 的开关写成 `DimensionMaskMode` [`egm-style`] — Severity: Medium

* **Affected Scope:** [407](textmask-failure-report.md#L407-L407)（D03）
* **Discrepancy Description:** 编排层核实 `TextMaskRule.cs:51,475-478` 用的是 `dimMode`（来自 `DimensionTextMode`）。
* **Rationale:** 字段名错会让人在配置里改错开关。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，已直接核源码。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:407`
  * Change: `（`DimensionMaskMode` 非 Off 时再加 `DIMENSION`）` → `（`DimensionTextMode` 非 Off 时再加 `DIMENSION`）`；并补 `(原文误写为 \`DimensionMaskMode\`，2026-11-05 更正)`。

#### [Issue 17] 函数签名与 3 个已不存在的符号仍是「现在存在」口吻 [`egm-structural` / `egm-design`] — Severity: Medium

* **Affected Scope:** [236, 252, 388-389](textmask-failure-report.md#L236-L389)（D04）
* **Discrepancy Description:** `§5.1:236` 说签名改成 `ApplyMTextBackground(mtext)`「只置 BackgroundFillFlags」，实际是 `(MText,double)` 并写 `BackgroundColor`/`BackgroundScale`；`§9.1:388-389` 说「手写 `DxfClassMap` 只放 69」「建在 `new DimensionStyle("*override")` 上」，实际是 `GetStyleOverrideMap` → 追加 69 → `SetStyleOverrideMap`；`§5.3:252` 把去重归给 `EntityScanner`，实际在 `DatabaseWalker.CollectFrom`。
* **Rationale:** 维护者照它改会破坏「追加不清空」的行为；全 `src` 检索 `SetDimensionOverride`/`new DimensionStyle` 零命中。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:236` — 改为 `\`ApplyMTextBackground(MText, double paddingRatio)\`：不再补宽度 / 高度，只置背景标志（bit0+bit1）、\`BackgroundColor\`、\`BackgroundScale\`；**绝不碰 \`RectangleWidth\`**。（本节原写的单参数签名是中间态，见 §9.6。）`
  * Location: `textmask-failure-report.md:388-389` — 两条改为 `当前实现：\`dimension.GetStyleOverrideMap()\` 取已有覆盖 → \`map.DxfProperties[69] = fill\` → \`dimension.SetStyleOverrideMap(map)\`。早期试过的 \`SetDimensionOverride(clone)\` 会带出多余 \`340=0\` 已弃用；\`new DimensionStyle("*override")\` 从未进入产品代码。（注：2026-11-05 审查发现 \`SetStyleOverrideMap\` 会清空段外 ACAD XData，已按 C03 修为局部替换。）`
  * Location: `textmask-failure-report.md:252` — `\`EntityScanner\` 本身对每个块定义只遍历一次` → `\`DatabaseWalker.CollectFrom\` 用 \`visited\` 对每个块定义只遍历一次`。

#### [Issue 18] §6 / §9.5 三处已被推翻的结论未作废 [`egm-comprehension` / `egm-style`] — Severity: Medium

* **Affected Scope:** [282, 420-424](textmask-failure-report.md#L282-L424)（D05）
* **Discrepancy Description:** `:282`/`:422`「MTEXT 偏移因子仍是尽力而为、90=2 时没有 45」vs `:453`「不复存在」；`:423`「别的几何要靠 SortEntitiesTable」vs `:470`「该路线彻底不可用」；`:424`「交互式观感未人工确认」vs `:428`「用户已在交互式 AutoCAD 复查并查出 bit0 真 bug」。
* **Rationale:** 实施者按旧结论写代码会回退修复。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:282` — 删末句 `MTEXT 偏移因子在宿主侧仍是"尽力而为"（9.5 节）。`，替换为 `（MTEXT 偏移因子"尽力而为"的旧结论已由 §9.6 推翻，见 §9.6:453。）`
  * Location: `textmask-failure-report.md:422` — 整条改为 `~~**MTEXT 的偏移因子是"尽力而为"**……~~ **该限制已消除（见 §9.6:453）**：因子丢失的真因是只写 bit1（90=2）导致 45/63 不落盘，改 bit0+bit1 后 AutoCAD 回读 \`(45 . 1.15)\`。`
  * Location: `textmask-failure-report.md:423` — `别的几何要靠 \`SortEntitiesTable\`。` → `别的几何目前**没有可靠手段**（SORTENTS 排序表经 ACadSharp 写出后 AutoCAD 读不回，见 §9.7:470）；\`MaskSendHatchToBack\` 已于 2026-11-05 停用，见 C02。`
  * Location: `textmask-failure-report.md:424` — 改为 `交互式会话的观感已在 §9.6:428 那次用户复查中部分确认（正是那次查出了 bit0 缺陷）；完整界面截图仍未做，列为 \`VERIFY.md\` 宿主门禁项。`

#### [Issue 19] 删除线让同句承载两个互斥事实，该删的反而没删 [`egm-style`] — Severity: Medium

* **Affected Scope:** [270, 279, 280, 453](textmask-failure-report.md#L270-L453)（D06）
* **Discrepancy Description:** `~~没解出来~~ **已解出**` 等让同句含两个互斥事实；而该删的 §5.2、§9.5 三条限制、L282 原样留着。
* **Rationale:** 删除线文本视觉权重与新结论相同，读者无法判断哪个有效。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:270, 279, 280, 453` — 把所有 `~~旧结论~~ **新结论**` 结构改为「只保留新结论 + 一行 `（历史：早期曾认为……，于 X 节推翻）`」；§5、§8 开头已加作废横幅（Issue 5），正文旧句直接删而非划线。
  * Location: `README.md:850, 871` — 同样处理 `~~MTEXT 遮罩背景不显示~~` / `~~MTEXT 的偏移因子…~~` 两处。

#### [Issue 20] 测试数 212 / 215 / 291 / 301 互斥，无基线 [`egm-zoom-out` / `egm-style`] — Severity: Medium

* **Affected Scope:** [264, 325, 416, 481](textmask-failure-report.md#L264-L481)（D07）；`README.md:863, 1028`
* **Discrepancy Description:** 四个互斥值无基线说明。编排层实测当前真值：`Failed 0 / Passed 296 / Skipped 5 / Total 301`（211 ms），5 个 Skip 全来自 `DwgSampleTests`（外部样图目录不存在）；`TextMaskRuleTests.cs` 有 **40 个** `[Fact]/[Theory]`，不是 `:264` 的「10 个」。
* **Rationale:** 历史数字对应历史轮次，无法回溯复现。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，且本轮已跑出唯一可复现基线。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:264` — `212 个全过（\`TextMaskRuleTests\` 从 7 个改写为 10 个，锁的是新行为）` → `212 个全过（历史轮次；\`TextMaskRuleTests\` 当时从 7 个改写为 10 个）`。
  * Location: `:325` — `215/215 通过` → `215/215 通过（历史轮次）`。
  * Location: `:416` — `291 passed / 0 skipped / 0 failed` → `291 passed / 0 skipped / 0 failed（历史轮次，需外部 \`JUN_CAD_SAMPLE_DWG\`）`。
  * Location: `:481` — `测试 301/301 通过` → `测试基线：**301 total / 296 passed / 5 skipped / 0 failed**（2026-11-05 实跑 \`dotnet test Jun.Cad.Batch.sln --nologo -v minimal\`；5 个 Skip 全来自缺外部样图的 \`DwgSampleTests\`）。\`TextMaskRuleTests\` 当前 40 个测试方法。`
  * Location: 全文（`:1-10` 头部之后）
  * Change: 插入 `> **测试基线（唯一准绳）**：301 total / 296 passed / 5 skipped / 0 failed（2026-11-05）。正文出现的 212 / 215 / 291 均为历史轮次记录，不代表当前状态；逐条命令见 \`VERIFY.md\`。`
  * Location: `README.md:863, 1028` — 同口径统一到 301/296/5。

#### [Issue 21] §9.1「不改句柄」在默认配置下为假 [`egm-style`] — Severity: Medium

* **Affected Scope:** [384](textmask-failure-report.md#L384-L384)（D09）；`README.md:591, 600`
* **Discrepancy Description:** `MaskBringTextToFront` 默认开，`ApplyDimensionMask` 调 `BringToFront`，`MaskService.cs:309` 明写分配新句柄。
* **Rationale:** 用户据此判断「无损」，与实际不符。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:384` — `不新增实体、不改句柄、重生成保留` → `不新增实体、重生成保留；**默认配置下 \`MaskBringTextToFront=true\` 会把整个标注前置，标注获得新句柄**（见 §9.7:472）`。
  * Location: `README.md:591`（Dimension text masks 行）与 `:600`（Current defaults 段）
  * Change: `no handle change` → `no *new entity*; the dimension itself gets a new handle when \`MaskBringTextToFront\` (default on) reorders it`。
  * Location: `README.md:771` — `no handle change` 同样修正。

#### [Issue 22] §9.7「旧图重跑自动升级为紧贴遮罩」过度概括 [`egm-implementation-readiness`] — Severity: Medium

* **Affected Scope:** [466](textmask-failure-report.md#L466-L466)（D10）
* **Discrepancy Description:** 只有 `RectangleWidth > tightWidth × 1.05` 才换；框贴文字或 41=0 走原生路径补 bit0；`Tight` 时 `HasOverlappingWipeout` 命中直接跳过。
* **Rationale:** 缺一张「框松/框紧 × Auto/Native/Tight × 旧 bit0 有无」结果矩阵。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:466`
  * Change: 整行替换为限定描述 + 矩阵：
    ```
    - 旧版处理过的文件重跑时按「框宽 × MaskTextBox × 已有标志」分派，并非一律换成紧贴遮罩：

    | 框宽 vs 实测文字宽 | 已有 bit0 | Auto（默认） | Native | Tight |
    |---|---|---|---|---|
    | 41=0 或 ≤ 宽×1.05 | 无 | 补 bit0+bit1，保持原生背景 | 同 Auto | 清原生背景，换紧贴 Wipeout |
    | 41 > 宽×1.05 | 无 | 清原生背景，换紧贴 Wipeout | 补 bit0+bit1，保持原生背景（框宽留白） | 清原生背景，换紧贴 Wipeout |
    | 任意 | 有 | 判重跳过 | 判重跳过 | 判重跳过（\`HasOverlappingWipeout\` 命中亦跳过） |

    \`RectangleWidth\` 全程不动，零折行风险。
    ```

#### [Issue 23] README 对「填充置后」和「渲染验收」自相矛盾 [`egm-zoom-out` / `egm-codebase-details`] — Severity: Medium

* **Affected Scope:** [583, 847, 851, 863-869, 919](README.md#L583-L919)（D13）
* **Discrepancy Description:** `:583`「已被取代/从未生效」vs `:847`「62 个填充被置后」/`:864`「填充置后 6」当成功指标；`:851`「渲染对照还需完成」vs `:868-869` 逐像素结果。
* **Rationale:** 同一验收在项目自身两份文档里同时是「已完成」和「未完成」。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `README.md:847` — 「62 个填充被置后」后追加 `（历史轮次记录；2026-11-05 复核确认该路径只计数、不改变输出文件，见 \`textmask-failure-report.md\` §9.7）`。
  * Location: `README.md:864` — 「填充置后 6」从成功指标表移到「历史轮次 / 无效指标」注记。
  * Location: `README.md:868-869` — 逐像素结果与 `:851` 统一口径：改为 `受控测试图的历史出图证据（2026-11-02，未纳入自动化断言）；当前自动化验收状态见 \`VERIFY.md\`，宿主渲染项仍为人工门禁`。
  * Location: `README.md:771` — 保留「existing draw order stored in SORTENTS is not preserved」并前置加粗 `**破坏性副作用**`，补运维规避：`含 SORTENTSTABLE 的图纸请勿原地覆盖，先 \`--out\` 到副本并核对 DRAWORDER`（C01 选 C 时必做）。

### Low

#### [Issue 24] `MaskTextBox=Auto` 的 ±10% 估测宽配 1.05 阈值抖动 [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [569](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L569-L569)（C13）；`TextBounds.cs:522-525, 625-639`
* **Discrepancy Description:** `MeasureMTextTightWidth` 自述「误差 ±10% 量级」且无字形度量，框宽落在阈值附近时分支随机翻转，两份近似 MTEXT 得到两种明显不同的遮罩。
* **Rationale:** 真正修好需要字形度量（库没有），超出本轮范围；但文档必须写清限制。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立但只能缓解：把不确定性显式化，避免用户误以为是精确判定。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:565-569` — 在 `useTight` 计算上方补注释：`// 注意：MeasureMTextTightWidth 是字符宽度估算（误差约 ±10%，无字形度量）；// 1.05 阈值附近的框宽可能落在两条不同路径，属已知限制，见 README / VERIFY.md。`
  * Location: `README.md:579`（`MaskTextBox` 行）/ `textmask-failure-report.md:465`
  * Change: 补 `Auto 的分支依据是估算文字宽度（误差约 ±10%），框宽接近阈值时结果可能不稳定；需要确定性行为请显式指定 Native 或 Tight`。

#### [Issue 25] 空 MTEXT 仍加遮罩 / Tight 生成近零面积 Wipeout [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [548-583](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L548-L583)（C15）；`TextBounds.cs:266-283, 553-587`
* **Discrepancy Description:** 无「文字为空则跳过」判断；空 MTEXT + 非零 DXF 41 会得到 `Width>0 && Height>0` 并加遮罩。`:455` 那 13 个空文本没加遮罩只是恰好框宽为 0。
* **Rationale:** 逻辑保证缺失。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，改动小。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:548`（`Bounds2d bounds;` 之前）
  * Change: 插入 `if (IsBlankText(entity)) { result.MarkSkipped("空文字"); continue; }`，新增 `IsBlankText`：`Text`/`AttributeEntity` 取 `Value`、`MText` 取 `Text`/`PlainText`，`string.IsNullOrWhiteSpace` 且去掉 `{}` / `\P` / 格式码后为空即返回 true。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs` — 新增断言：空 MTEXT 且 `RectangleWidth=100` 不产生遮罩。

#### [Issue 26] `HasOverlappingWipeout` 用 AABB 判重，旋转角不参与 [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [1139-1197](src/Jun.Cad.Batch.Core/Rules/TextMaskRule.cs#L1139-L1197)（C16）
* **Discrepancy Description:** `SameBox` 只比 AABB 中心 + W/H（容差 1%），同位置 0° 与 90° 文字互相误判为「已有遮罩」。
* **Rationale:** 会静默漏加遮罩。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。
* ***Proposed Edit:**
  * Location: `TextMaskRule.cs:1190-1197` / `_wipeoutBoxes` 结构
  * Change: 判重键从 `Bounds2d` 扩展为「AABB + 旋转角」：新增 `WipeoutBox { Bounds2d Rect; double Rotation; }`，`HasOverlappingWipeout` 与 `RegisterWipeout` 改用它；`SameBox` 增加 `Math.Abs(a.Rotation - b.Rotation) <= 1e-3` 条件。已有 Wipeout 的旋转角从 `UVector` 反算（`Math.Atan2`），取不到时记 0。
  * Location: `tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs` — 新增断言：同 AABB、0° 与 90° 两个文字各得一个遮罩。

#### [Issue 27] `Bounds2d.IsValid` 漏检 NaN / Infinity [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [25-28](src/Jun.Cad.Batch.Core/Engine/TextBounds.cs#L25-L28)（C17）
* **Discrepancy Description:** 只查 `MinX`/`MaxY` 的 NaN；`MinY`/`MaxX` 为 NaN 或 ±∞ 时仍判有效，`NaN<=0` 为 false 绕过后续检查。
* **Rationale:** 会写出坏 Wipeout。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，一行修复。
* ***Proposed Edit:**
  * Location: `TextBounds.cs:27`
  * Change: `get { return MaxX >= MinX && MaxY >= MinY && !double.IsNaN(MinX) && !double.IsNaN(MaxY); }` →
    ```csharp
    get
    {
        return !double.IsNaN(MinX) && !double.IsNaN(MinY)
            && !double.IsNaN(MaxX) && !double.IsNaN(MaxY)
            && !double.IsInfinity(MinX) && !double.IsInfinity(MinY)
            && !double.IsInfinity(MaxX) && !double.IsInfinity(MaxY)
            && MaxX >= MinX && MaxY >= MinY;
    }
    ```
  * Location: `tests/Jun.Cad.Batch.Tests/` — 新增 4 个分量各自的 NaN/∞ 断言。

#### [Issue 28] `PlainTextOf` 潜在 `NullReferenceException` [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [666-696](src/Jun.Cad.Batch.Core/Engine/TextBounds.cs#L666-L696)（C18）
* **Discrepancy Description:** 在 `TryGetDimensionTextAnchor` 返回 true 后无条件 `foreach (Entity child in dim.Block.Entities)`，而该函数在 `dim.Block == null` 时也可返回 true。
* **Rationale:** 当前无调用点，属 public 辅助方法潜在缺陷。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立，防御性一行。
* ***Proposed Edit:**
  * Location: `TextBounds.cs:666-696`
  * Change: 在 `foreach` 之前插入 `if (dim == null || dim.Block == null) { return string.Empty; }`（返回类型按实际签名调整）。

#### [Issue 29] `ReorderToTop` 的 Owner 条件近乎恒 false [`egm-codebase-details`] — Severity: Low

* **Affected Scope:** [321-346](src/Jun.Cad.Batch.Core/Engine/Mask/MaskService.cs#L321-L346)（C19）
* **Discrepancy Description:** `if (first != null && first.Owner == space.Owner)` 近乎恒 false（`space.Entities` 内实体 `Owner` 指向 BlockRecord 本身，`space.Owner` 是块表/文档），`Remove(first)` 从不执行；当前无功能影响。
* **Rationale:** 死分支会误导后续维护者以为遮罩可能已在集合中。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。注意：**仅在 C01 选 B/C 时应用**；若选 A（重写占位实体路径），此处会被整体替换。
* ***Proposed Edit:**
  * Location: `MaskService.cs:330-336`
  * Change: 删除 `if (first != null && first.Owner == space.Owner) { ... space.Entities.Remove(first); }` 整块，替换为注释 `// first（遮罩）是新建实体，尚未加入集合，无需 Remove；// 只有 second（文字）需要摘出以触发槽位复用。`；保留 `space.Entities.Remove(second);`。

#### [Issue 30] 算术不自洽与 `MaxNestingDepth` 默认值不符 [`egm-style`] — Severity: Low

* **Affected Scope:** [455, 478](textmask-failure-report.md#L455-L478)（D08）
* **Discrepancy Description:** `:478`「296 处遮罩（289+2+10）」但 289+2+10=301≠296、296+13=309；`:455`「仍未加遮罩 26 个：13 个…8 个…」13+8=21，差 5 无交代；`MaxNestingDepth` 报告写 4，`BatchConfig.cs:303-304` / `README.md:318` 默认 8。
* **Rationale:** 数字被放在「本轮实测」标题下当验收证据，却不可复算。
* ***AI Suggestion:*** **PROPOSE FIX**
* ***Justification:*** 批评成立。历史数字无法回溯，改为如实标注而非编造补齐。
* ***Proposed Edit:**
  * Location: `textmask-failure-report.md:478` — `296 处遮罩（MTEXT 289 原生 flags=3 + 2 紧贴 Wipeout + 标注 10/10 DIMTFILL=1 + 13 跳过）` → `（历史轮次记录：MTEXT 289 原生 flags=3 + 2 紧贴 Wipeout + 标注 10/10 DIMTFILL=1 + 13 跳过；原文字中的「296 处」与分项 301 不符，属当时统计口径差异，不可复算，保留原样仅作过程记录）`。
  * Location: `textmask-failure-report.md:455` — `26 个 MTEXT（该图）：13 个…8 个…` → `（历史统计：26 个未加遮罩，其中 13 个空文本、8 个嵌套超深，其余 5 个当时未归类）`；`MaxNestingDepth: 4` → `MaxNestingDepth: 8（项目默认；当时配置写 4）`。

#### [Issue 31] `MaskIgnoreBlockBounds` XML 注释措辞歧义 [`egm-codebase-details` / `egm-comprehension`] — Severity: Low

* **Affected Scope:** `BatchConfig.cs:708`（D12 / R05）
* **Discrepancy Description:** 「默认 false = 不参与」里的 false 描述的是「是否参与」的有效值，不是配置字段值；同文件 `[RuleField] Help`(`:713`)「默认是（忽略）」才对应字段。
* **Rationale:** 阶段 8 已撤销「注释写反」的功能性指控：行为一致，仅 IntelliSense 措辞歧义。
* ***AI Suggestion:*** **PROPOSE FIX**（Nit 但改一个词即可，全量修下值得做）
* ***Justification:*** 不是功能错误，但消除歧义成本为零。
* ***Proposed Edit:**
  * Location: `BatchConfig.cs:708`
  * Change: `（默认 false = 不参与）` → `（配置字段默认 true = 忽略；换算后的「是否参与」默认 false）`。

### 合并处置说明

* **C10 与 C14** 在 Issue 12 合并（同一个 `_encodingChecked` 短路 + 同一个 `IsWipeoutEncoded` 判据）。
* **C02 与 D13 的 README 部分** 在 Issue 3 / Issue 23 分别落位，不重复编辑同一行。
* **C01 与 C19** 存在依赖：Issue 29 仅在 C01 选 B/C 时应用，选 A 时并入重写。
* **Issue 6（VERIFY.md）** 是其余所有「已验收」类文档修订的落点，先建文件再改引用。

---

## 3. CONTEST — 已撤销的前序误报（不改）

以下批评**不成立**，证据都在 reviewer 可读的文件里（或编排层已逐个 `test -e` 核实），因此不改任何内容。综合报告第五节已记录撤销依据。

| # | 批评 | AI Suggestion | Justification（可核验证据） |
|---|---|---|---|
| R01 / D14 | 「§7 复现材料与 bgprobe 证据全部不存在」 | **CONTEST** | 编排层逐个 `test -e` 全部 `EXISTS`：3 个 `.lsp`、6 个 `.png`、2 个 `.txt`、`pngscan.py`、`DimFillProbe.cs`/`DimScan.cs`、`dim-ac.png`、`m90.dwg`、`m90.png`；`tmpv` 260 文件、`.workbuddy-ai/bgprobe` 131 文件。评审者用仓库 Glob，这些目录被 `.gitignore` 排除 |
| R02 | 阶段 5 #3「约 200 行遮挡判定在默认配置下是死代码」 | **CONTEST** | `TextMaskRule.cs:100-101` 是 `(onlyOccluded \|\| sendHatchToBack)`，默认 `sendHatchToBack=true` → `BuildOccluders`/`EstimateCellSize`/`BoundsIndex`/`occluders.Query`/`PushHatchesToBack` 每次都跑；被跳过的只有 `ComputeOcclusion`。正确描述是「跑了、产出不落盘、还报成功」（已由 C02 处置） |
| R03 | 「CapabilityProbe 仍是只验能写能读的探测」 | **CONTEST** | `CapabilityProbe.cs:162-184` 实际断言 4 项几何不变量（`ClippingState`、顶点数 ==4、`Definition==null`、包围盒 ==(0,0)-(100,50)）。真问题是 C10 的两套判据与生产不调用 |
| R04 | 「测试套件没有 DWG 往返覆盖」 | **CONTEST** | `DwgSampleTests.cs:59-87` 有 `CadFileSession.Save` → 重新 `Open` 的真往返，`:92-125`/`:130-181` 有跑 `BatchRunner` 并写盘的端到端用例。真实缺口更窄：没有 `TextMask` × 真实 DWG × 回读（已由 Issue 6 补） |
| R05 / D12 | 「`MaskIgnoreBlockBounds` 注释写反，会导致 Insert 参与判定」 | **CONTEST**（措辞部分另立 Issue 31） | `useBlockBounds` 默认算出 false（不参与），与 `BuildOccluders:836-839` 一致，无功能错误；降级为 Nit 措辞 |
| R06 | 「退役字段仍被校验」「`Auto`/`Wipeout` 完全同义」是 bug | **CONTEST** | 均为有意设计并已文档化：`README.md:596` "Validation does not silently discard or migrate the old data"、`BatchConfig.cs:920-925`、`ConfigLoader` 保留 `Action.Extra` 往返；`RequireNativeMaskMode`(`:992-1009`) 与 `RuleSummary`(`:342-344`) 刻意让两者同义 |
| R07 | 「`MaskMode=Wipeout` 直接抛 `RuleValidationException`」 | **CONTEST** | 当前接受 `Auto`/`Wipeout`，只有 `Solid` 与未知值抛（`TextMaskRule.cs:992-1009`）。文档侧的错误陈述已由 Issue 5 的作废横幅处理 |
| R08 | 「默认配置下仍启用 0.05 阈值」 | **CONTEST** | `ComputeOcclusion` 只在 `onlyOccluded=true` 时调用（`:107`）；阈值与 0.25 权重只在显式打开开关后生效（已由 Issue 15 标注） |
| — | 「`DimensionTextMode=All` 在手写 `EntityTypes:["MTEXT"]` 时失效」是 bug | **CONTEST** | `RuleValidator.cs:241-249` 已就该组合发出显式 Warning，且默认模板不写 `EntityTypes`；是有意的过滤器语义，不是缺陷 |

---

## 4. 应用后的验收门禁（Step 5 之前）

1. `dotnet test Jun.Cad.Batch.sln --nologo -v minimal` → 必须 `Failed 0`；新增用例后总数与 `VERIFY.md` 基线同步更新。
2. `git diff --stat` 复核：只改本报告列出的文件；**不触碰** `src/Jun.Cad.Batch.Win/bin/publish/`（用户实际在用目录）。
3. 目标文档 `textmask-failure-report.md` 结构复核：H1→H2→H3 连续、表格与代码块闭合、`VERIFY.md` 引用可达。
4. 所有「已确认 / 已验收 / 已完成」必须有 `VERIFY.md` 中的命令或明确标为「历史轮次 / 宿主门禁」。
5. AutoCAD 宿主侧（`ssget` / `entget` / `-PLOT` / 逐像素 / 交互式观感 / `WIPEOUTFRAME`）：
   报告成稿时未执行；2026-11-05 追加补跑后，`ssget` / `entget` / `-PLOT` 逐像素已在受控图上通过
   （见 `VERIFY.md` 与报告 §8.3），**交互式观感与 C01 / C11 / C20 / C21 仍不签发验收通过**。

---

## 5. 处置结果（Step 5 · 最终）

**用户选择（两段）：**

1. 初次 Step 4 回复 `continue`，按 skill 语义「Continue to next phase / No changes」，当时未应用任何修复；
2. 随后用户明确追加自定义指令 **「继续 源码+文档 全量修」**，据此执行第 2 节全部 30 条 `PROPOSE FIX`
   （覆盖源码、回归测试、配置模板、README、目标文档），并按第 4 节门禁复跑验证。
   两段轨迹均如实保留，不把后者伪装成前者的重解释。

| 项目 | 数量 | 说明 |
|---|---:|---|
| **已接受并应用的 PROPOSE FIX** | **30** | 第 2 节 Issue 1、3–31 全部落地（Issue 2 是 C01 的 ESCALATE 占位，不计数） |
| CONTEST（撤销误报） | 9 | 维持原判，见第 3 节，未改 |
| ESCALATE（待用户决策） | 3 | C01 / C20 / C21，**均未处置**，见第 1 节与下文「未解决」 |
| DECLINE | 0 | 用户选了全量修，Nit 也一并修 |
| 未应用的 PROPOSE FIX | 0 | — |

### 5.1 源码修复落点

| 文件 | 对应 Issue / 缺陷 | 关键改动 |
|---|---|---|
| `Rules/TextMaskRule.cs` | 1 (C03)、3 (C02)、7 (C06)、8 (C04)、9 (C07)、10 (C05)、11 (C09)、12/14 (C10/C14)、15 (C15)、24、25、26 | ① 弃用 `SetStyleOverrideMap`，改为 ACAD XData 的 **DSTYLE 段内局部插入**（`ApplyDimensionFillOverride` / `FindDStyleSection` / `AcadaXDataOf`），不再 `Records.Clear()`；已有 `69=0/2` 覆盖一律保留。② `MaskSendHatchToBack` 默认 `false`、显式 `true` 只告警，删除 `PushHatchesToBack` 与全部计数。③ `CountExistingMasks` 把执行前已有遮罩计入 `MaxMasksPerDrawing`。④ `reordered` 按实际重排数汇总。⑤ `MaskLayer()` 对**已存在** `_MASK` 图层也按 `MaskLayerPlottable` 纠正。⑥ 两个 Dimension 枚举改白名单 + 非法值硬报错。⑦ 动态块 `*U` 与嵌套 `ATTRIB` 纳入收集。⑧ `IsBlankText` 拦空文字。⑨ Wipeout 判重加入旋转角。⑩ 取消 `_encodingChecked` 短路，**每个** Wipeout 都过 `MaskService.IsWipeoutEncoded`。 |
| `Engine/Mask/MaskService.cs` | 29 | 删除 `ReorderToTop` 里近乎恒 false 的 `first.Owner == space.Owner` 死分支，改为说明「新建遮罩尚未入集合」。 |
| `Engine/TextBounds.cs` | 27、28 | `Bounds2d.IsValid` 四分量全查 NaN / ±∞；`PlainTextOf` 加 `dim.Block == null` 保护。 |
| `Engine/DatabaseWalker.cs` | 9 (C07)、17 | 嵌套遍历放行 `*U` 动态实例块（外参与 `*D`/`*T` 仍跳过），新增 `IsDynamicInstanceBlock`，类注释同步。 |
| `Engine/Preflight/CapabilityProbe.cs` | 12 (C10) | 增加 `IsWipeoutEncoded` 自检，与生产判据对齐；注明 preflight 不是宿主渲染验收。 |
| `Editing/RuleValidator.cs` | 11、15 | 两个 Dimension 枚举白名单校验；`MaskOnlyWhenOccluded=true` 发实验性 / 不可靠告警。 |
| `Config/ConfigLoader.cs` | 13 | 新增 `MigrateTextMaskDefaults`：旧版（`Version<=1`）TextMask 补齐 7 个显式默认值并只报一条迁移告警，不动用户已写的值。 |
| `Config/BatchConfig.cs` | 8、10、13、31 | 修正 `MaskIgnoreBlockBounds` 注释；`MaskSendHatchToBack` 标注停用；`MaskLayerPlottable` / `MaxMasksPerDrawing` / `MaskBringTextToFront` / `DimensionMaskMode` 的 Help 文案补齐限制。 |
| `Editing/RuleSummary.cs` | 8 | 汇总文案改为「文字前置（仅 MTEXT 原生/标注）」，不再对 Wipeout 路径撒谎。 |

### 5.2 测试、配置与文档

| 文件 | 改动 |
|---|---|
| `tests/…/TextMaskRuleTests.cs` | 新增 10 个测试方法 / 15 条用例，覆盖 C02、C03（XData 保留 + `69=0/2` 保留）、C05、C06、C07（`*U` 动态块）、C09、C15、C27；Issue 14 把「建Wipeout时关掉边框」改名为「建Wipeout时发出关闭边框的写入」，去掉「边框已隐藏」语义。 |
| `tests/…/ConfigLoaderTests.cs` | 旧零值配置用例改为「补齐显式默认值 + 恰一条迁移告警」；模板用例随模板显式化改为断言 `MaxMasksPerDrawing=0` / `MaskSendHatchToBack=false`。 |
| `tests/…/DwgSampleTests.cs` | 追加 `TextMask在真实图上写盘回读后遮罩仍在()`：真实 DWG 上执行 `TextMask` → 写盘 → 回读，断言遮罩指示数增加且 MTEXT bit0+bit1 仍在；补 `AllEntities` / `CountMaskIndicators` 辅助方法。 |
| `Jun.Cad.Batch.json` | `MaskSendHatchToBack` 改 `false`；补齐全部会触发迁移的显式键（含 `MaxMasksPerDrawing: 0`），保证嵌入模板自身不产生升级告警；更新动态块 / 遮挡开关注释。 |
| `samples/text-mask.json` | `MaskSendHatchToBack` 改 `false`；补 `MaxMasksPerDrawing: 0` 避免样例触发迁移告警。 |
| `README.md` | 统一「填充置后」为已停用、补充 SORTENTS / DRAWORDER 破坏性副作用与 `--out` 规避、旧配置升级行为变化清单、动态块 `*U` / 上限 / 前置开关的准确口径，测试基线同步到 317/311/6（带样图 317/317/0），并补记宿主补跑结论与签发范围。 |
| `textmask-failure-report.md` | 三套互斥实现加作废横幅；修正 §9.3「bit1，不是 bit0」危险结论；`DimensionMaskMode` → `DimensionTextMode`；删除线互斥事实改写；补 §9.7 旧图重跑矩阵；标注 WIPEOUTFRAME 未修复；新增 §8.3 修复后宿主复验；测试基线同步。 |
| `VERIFY.md`（新增） | 5 条可执行验收门禁 + 宿主实跑结果与签发范围；澄清 `ssget "_X"` 不枚举块定义内实体导致的计数口径；列出 C01 / C20 / C21 未决。 |

### 5.3 验证结果（第 4 节门禁）

- `dotnet test Jun.Cad.Batch.sln --nologo -v minimal` →
  **`Failed 0 / Passed 311 / Skipped 6 / Total 317`**（默认；修复前 `301 / 296 / 5`；
  新增 15 条回归用例 + 1 条真实 DWG 往返用例）。6 个 Skip 全部来自缺外部样图的 `DwgSampleTests`。
- 设置 `JUN_CAD_SAMPLE_DWG="D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/tmpv/sampledwg"` 后 →
  **`Failed 0 / Passed 317 / Skipped 0 / Total 317`**（5 个历史 Skip + 新增用例全部实跑通过）。
- `dotnet build Jun.Cad.Batch.sln`：`Build succeeded. 0 Warning(s) 0 Error(s)`。
- `git diff --stat`：只改本报告列出的源码 / 测试 / 配置 / 文档；**未触碰** `src/Jun.Cad.Batch.Win/bin/publish/`。
- 目标文档结构：H1→H2→H3 连续，代码块与表格闭合，`VERIFY.md` 引用可达；基线数字在
  `VERIFY.md` / `README.md` / `textmask-failure-report.md` 三处一致为 317/311/6（带样图 317/317/0）。
- 本报告 H1→H2→H3 连续，代码块成对闭合（12 个围栏 = 6 组），表格列一致，链接均为相对路径。

### 5.3.1 追加：宿主门禁实跑（2026-11-05，用户「继续未完工作」）

工作目录 `tmpv/hostverify-2026-11-05/`，AutoCAD 2025 `accoreconsole.exe /l en-US`，
CLI 输出 `out/control.dwg` 报 `11 处遮罩（MTEXT 原生 4 / 标注 1 / Wipeout 6）`：

| 门禁 | 结果 |
|---|---|
| §2 `ssget` 计数 / 组码 | 通过：模型空间 5 个 WIPEOUT，`71=2 / 91=5 / 280=1 / 340=nil / 360=nil`；块 `TBLK` 内另有 1 个（`2B2`）。CLI 的 `6` 与宿主 `5` 的差额**全部**来自 `ssget "_X"` 不枚举块定义内部实体，非丢实体 |
| §3 `-PLOT` 逐像素 | 通过：源图 / 输出图 extents 一致，`y=746–748` 由 `0–289` 变 `0–20 / 255–289`；全图 `erased=8729 / added=0` |
| §4 `entget` 组码 | 通过：MTEXT 全为 `90=3 / 63=7 / 45=1.15`；DIMENSION DSTYLE 保留 `140/3.5` 并追加 `69/1`（C03 宿主侧确认） |
| §5 `WIPEOUTFRAME` | 已知未修复：重开仍为 `1`，`ACAD_WIPEOUT_VARS` 的 `70=1` |

产物：`verify.scr` / `host-result.txt`、`blocks.scr` / `block-result.txt`、
`detail.scr` / `detail-result.txt`、`plot-src.scr` / `host-plot-src.png`、
`plot.scr` / `host-plot.png`、`extents.scr` / `extents-result.txt`。
`sortents= missing`，故 C01 的破坏性路径未被触发。

### 5.4 未解决（不得写成已修复）

- **C01**：`SORTENTS` / 块记录扩展字典写不出去，保存会重置用户 `DRAWORDER`。本轮只停用假成功开关、
  写清破坏性副作用与 `--out` 运维规避，**没有改变绘制顺序架构**，需用户在 A/B/C 中拍板。
- **C20**：`Jun.Cad.Batch.Core.csproj` 仍引用仓库外私有 ACadSharp 克隆，干净检出编不过。
- **C21**：块内 TEXT 遮罩是否仍比文字框低约一个字高，需 AutoCAD 2025 出图复核。
- **C11 / WIPEOUTFRAME**：`70=0` 写入在 2025 重开后仍为 `WIPEOUTFRAME=1`（2026-11-05 再次实测确认），
  属上游 DWG 写出器缺陷。
- **宿主侧验收（补跑后）**：`VERIFY.md` §2–§4 已在受控图上通过，§5 为已知未修复项；
  **仍未签发**：含 `SORTENTSTABLE` 图纸的 `DRAWORDER`（本图 `sortents=missing`，C01 未触发）、
  块内 TEXT 偏移（C21）、交互式界面观感（屏幕 / 打印预览 / 深色绘图区白框）、以及 C20 构建链。
  受控图上的通过不代表任意真实图纸的绘制顺序安全。

> `Status: resolved` 表示本次 resolve 流程结束且 30 条 PROPOSE FIX 已落地；**不等于上述
> 3 条 ESCALATE、C11 与未签发的宿主项已解决**。
