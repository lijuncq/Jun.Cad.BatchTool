# 文字遮罩功能：EGM 综合审查报告（阶段 5–9 合并）

**审查目标:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md`（481 行）
**原始问题:** `@skill:egm-review 检查文字遮罩功能是否有bug`
**执行模式:** 一次性合并报告（五阶段全跑，不停顿）
**语义分段:** `FeatureRange = 1..229`（诊断 / 根因 / 修复方案 / 验收标准）；`DetailedRange = 230..481`（已实施修复 / 验证 / 遗留限制）；分界 `## 5. 已实施的修复`（L230）
**GoldfishContext:** `TextMaskRule.cs`、`MaskService.cs`、`TextBounds.cs`、`DatabaseWalker.cs`、`EntityScanner.cs`、`EntityFilter.cs`、`CapabilityProbe.cs`、`DimStyleService.cs`、`LayerService.cs`、`BatchConfig.cs`、`ConfigLoader.cs`、`RuleValidator.cs`、`RuleSummary.cs`、`TextMaskRuleTests.cs`、`DwgSampleTests.cs`、`TestPaths.cs`、`README.md`、`Jun.Cad.Batch.json`、`samples/text-mask.json`、外部 ACadSharp 克隆
**Mode:** REVIEW ｜ **本轮未修改任何生产代码、配置或目标文档**

## Reviewer Status

| 阶段 | Reviewer（fresh context） | Status | 产物 |
| --- | --- | --- | --- |
| 5 初始 Zoom Out | egm-zoom-out | completed | `zoom_out_report.md` |
| 6 可理解性测试 | egm-comprehension | completed | `comprehension_report.md` |
| 7 对抗式设计评审 | egm-design / egm-structural / egm-style | completed | `critic_report.md` |
| 8 中期 Zoom Out | egm-zoom-out（中期） | completed | `intermediate_zoom_out_report.md` |
| 9 实现就绪度 | egm-implementation-readiness / egm-codebase-details / egm-style | completed | `implementation_readiness_report.md` |
| 交叉汇总 | 编排层（含直接源码核验 + `dotnet test`） | completed | 本报告 |

---

# 一、直接回答

## **有 bug，而且不止一处。**

不是「文档写得不清楚」这一种问题，而是**两类并存**：

1. **真实功能 bug**：默认配置下可触发，其中至少 1 条会造成**用户数据丢失**（标注的 ACAD XData 被清空 / 存量绘制顺序每次保存被重置），至少 4 条会**静默产生错误输出或假成功**（日志说成功、文件里没有）。
2. **文档与代码漂移**：`textmask-failure-report.md` 是一份按时间推进的修复日志，前后结论多次互相推翻却没有作废声明，其中 **§9.3「bit1，不是 bit0」照抄会直接写出被 AutoCAD 判「没有遮罩」的 `90=2` 文件**——这是会把功能改坏的指令，不只是措辞。

四个原始根因的处置状态：

| 根因 | 1..229 的方案 | 当前实际 |
|---|---|---|
| A Wipeout 写出不可用 | 改走 2D Solid | **基本修掉**（外部 ACadSharp 克隆的 `SetMaskingArea`），但 §1–4 的「此路不通」结论已被 §8/§9 推翻且未标作废 |
| B 块内文字无条件跳过 | 写进块定义 | **基本修掉**，但动态块 `*U` 匿名实例块与嵌套 Insert 的 `ATTRIB` 仍零覆盖 |
| C 遮挡判定看不到块参照 | 二值相交 + 展开 Insert | **没修**：`Insert` 整类排除；只是把 `MaskOnlyWhenOccluded` 默认翻成 `false` 绕过，开关一开缺陷全回 |
| D 绘制顺序 | 进 SORTENTSTABLE | **没按方案修**：靠「ACadSharp 保存时把排序表写丢」这个上游 bug 兜住，代价是每次保存重置用户 DRAWORDER |

**关键判断：** 当前实现不是「修好了文字遮罩」，而是「用默认给所有文字加遮罩绕过了根因 C，并把绘制顺序绑在一个上游数据丢失 bug 上」。文档越清理，这些路径越像已解决——所以**修复顺序必须是先代码、后文档**。

---

# 二、跨阶段可追溯矩阵（去重后）

| ID | 结论 | 阶段 5 | 阶段 6 | 阶段 7 | 阶段 8 | 阶段 9 | 交叉裁定 |
|---|---|---|---|---|---|---|---|
| C01 | SORTENTS/块记录扩展字典写出丢失，保存重置用户 DRAWORDER | #2 Blocker | — | 根因 D | P0 / 2.1 单点 | B1 / style Blocker | **确认，Blocker，必须先定绘制顺序权威来源** |
| C02 | `MaskSendHatchToBack` 默认开、假成功计数 | #1 Blocker | 危险矛盾 | #4 | P0 / 1.1 | B2 / F3 | **确认，Blocker；且 #3「死代码」说法已撤销** |
| C03 | `SetStyleOverrideMap` 清空 ACAD XData + 覆盖已有 DIMTFILL 69 | — | — | #7 | 1.1 P1 | F2 | **确认，Blocker（用户数据丢失），优先处置** |
| C04 | `MaskBringTextToFront=false` 对 Wipeout 路径无效 | — | — | 发现 | 4 备注 | F1 / B7 | **确认，Major** |
| C05 | `MaxMasksPerDrawing` 是每次执行上限，不是单图上限 | — | — | 发现 | 1.1 P1 | F4 | **确认，Major** |
| C06 | 已存在且不可打印的 `_MASK` 图层不会被改回可打印 | — | — | 发现 | 1.1 P1 | F5 | **确认，Major** |
| C07 | 动态块 `*U` 文字 / 嵌套 ATTRIB 漏遮 | — | — | 根因 B 缺口 | 2.2 复用缺失 | F6 / B11 | **确认，Major** |
| C08 | 默认值翻转无版本迁移，旧配置静默改行为 | — | 当前默认值表 | 发现 | 1.1 P1 | 契约缺迁移 | **确认，Major** |
| C09 | `DimensionMaskMode`/`DimensionTextMode` 非法值静默归一 | — | — | 发现 | — | B9 | **确认，Major** |
| C10 | preflight 与生产执行两套判据，且 `TextMaskRule` 从不调 `CapabilityProbe` | #5（描述偏差） | #11（描述偏差） | #6 | **3.1 新发现** | B10 | **确认，Major；阶段 5/6 的「probe 只验能写能读」表述已修正** |
| C11 | `WIPEOUTFRAME` 边框实际没关掉，只靠白实体色规避 | — | — | — | 1.1 | B5 / style Major | **确认，Major（文档自认「已缓解」）** |
| C12 | 遮挡判定整类排除 `Insert`（根因 C 未修） | #3 主结论 | 危险矛盾 | 根因 C | 1.1 P1 | B3 | **确认，Major（默认绕过）** |
| C13 | `MaskTextBox=Auto` 的 ±10% 估测宽配 1.05 阈值抖动 | — | — | 发现 | 1.1 P1 | F14 | **确认，Nit/Major 边界** |
| C14 | Wipeout 编码自检只查第一个 | — | — | 发现 | 1.1 P1 | F11 / B8 | **确认，Nit** |
| C15 | 空 MTEXT 仍加遮罩 / Tight 生成近零面积 Wipeout | — | — | — | — | F12 | **确认，Nit** |
| C16 | AABB 判重忽略旋转角 | — | — | 发现 | — | F13 | **确认，Nit** |
| C17 | `Bounds2d.IsValid` 漏检 NaN/Inf | — | — | — | — | F15 | **确认，Nit** |
| C18 | `PlainTextOf` 潜在 `NullReferenceException` | — | — | — | — | F16 | **确认，Nit（当前不可达）** |
| C19 | `ReorderToTop` 的 Owner 条件近乎恒 false | — | — | — | — | F17 | **确认，Nit（当前无功能影响）** |
| C20 | 项目依赖仓库外私有 ACadSharp 克隆，干净检出编不过 | 发现 | — | — | 1.1 P2 | 符号映射 | **确认，Major（构建链）** |
| C21 | 块内 TEXT 遮罩比文字框低约一个字高 | — | — | — | — | B4 | **未核实**：未见修正代码，未重跑出图 |
| D01 | §5 / §8 / §9.2 三套互斥实现，无作废声明 | 发现 | 主发现 | 根因表 | 1.1 | Blocker | **确认，Blocker 文档缺陷** |
| D02 | §9.3「bit1，不是 bit0」与代码/§9.6 相反 | — | 危险矛盾 | 发现 | — | Blocker | **确认，Blocker（会把功能改坏）** |
| D03 | §9.3 把加 DIMENSION 的开关写成 `DimensionMaskMode` | — | — | — | — | style Major | **确认（编排层核实 `TextMaskRule.cs:51,475-478`）** |
| D04 | 函数签名与 3 个已不存在的符号仍是「现在存在」口吻 | 发现 | 危险矛盾 | 发现 | — | F7 | **确认** |
| D05 | §6 / §9.5 三处已被推翻的结论未作废 | — | 危险矛盾 | 发现 | — | Major / style | **确认** |
| D06 | 删除线让同句承载两个互斥事实，该删的反而没删 | — | — | style | — | style Major | **确认** |
| D07 | 测试数 212/215/291/301 互斥，无基线 | 发现 | — | 发现 | 3.5 | style / Nit | **确认；当前真值 301/296/5（已跑）** |
| D08 | §9.7/§9.6 算术不自洽（301≠296、21≠26） | — | — | 发现 | 3.5 | Nit | **确认** |
| D09 | §9.1「不改句柄」在默认配置下为假 | — | — | — | — | style Major | **确认** |
| D10 | §9.7「旧图重跑自动升级为紧贴遮罩」过度概括 | — | — | — | — | Major | **确认** |
| D11 | 「实测/全过/已验收」无命令、无基线、无断言 | — | — | 发现 | 3.3/3.4/3.5 | Blocker | **确认，Blocker 文档缺陷** |
| D12 | `MaskIgnoreBlockBounds` XML 注释字面默认值歧义 | — | #14（判为写反） | — | **4.3 撤销** | F8 | **降级为 Nit 措辞漂移，无功能错误** |
| D13 | README 对「填充置后」和「渲染验收」自相矛盾 | 发现 | — | 发现 | 3.4 | F9 | **确认** |
| D14 | 「§7 复现材料不存在」 | — | — | 部分评审者 | **5 核验补记撤销** | F19 | **误报**：全部 EXISTS |

---

# 三、确认的真实功能 bug（按处置优先级）

> 以下每条都在源码中核对过；`文件:行号` 指向当前工作副本。编排层另对带 ★ 的条目做了直接复读。

## P0 — 数据丢失 / 绘制顺序权威来源

### C03 ★ [Blocker] 标注 XData 被清空 + 已有 `DIMTFILL` 被无条件改写
- **位置:** `TextMaskRule.cs:396-408,431-452`；外部克隆 `ACadSharp/Entities/Dimension.cs:514-526`
- **事实:** `SetStyleOverrideMap` 先 `edata.Records.Clear()` 清掉 `AppId.DefaultName`（ACAD）下**全部** XData 记录，再写回单一 DSTYLE 段；`GetStyleOverrideMap` 只取第一段控制串之间的键值对，段外记录不会回来。`ApplyDimensionMask` 对每个待处理标注都调一次 → 静默删除用户数据。另外 `HasDrawingBackgroundFill` 只认 69==1，用户已设的 `DIMTFILL=0/2` 被判「无遮罩」，随后 `map.DxfProperties[69] = fill` 无条件改成 1。
- **触发:** 带 `(1070 . 69) (1070 . 2)` 的标注；或 ACAD XData 含 DSTYLE 段之外记录的标注。
- **报告态度:** `§9.1:382` 只核对了 DSTYLE 段内「原有 140 等键全部保留」，未提段外数据会丢；`:388` 把覆盖描述成「只放组码 69、原有键全保留」。
- **建议:** 改为在现有 `ExtendedData` 上做 DSTYLE 段的局部替换，不 Clear；对已有 69 值按用户配置决定是否覆盖并给出跳过原因。

### C01 ★ [Blocker] 保存任何带 SORTENTS 的图纸都会重置用户绘制顺序
- **位置:** 报告 `§9.7:470,481`；`README.md:771`；`MaskService.cs:305-307`；`TextMaskRule.cs:864-883`
- **事实:** ACadSharp 写出的块记录扩展字典 AutoCAD 读不到，`ACAD_SORTENTS` 挂在下面，连 AutoCAD 自己写的排序表经库往返也会丢。这是**与遮罩无关但由本工具触发的用户数据丢失**。
- **耦合风险（阶段 8 单点结论）:** 现在的「集合顺序 + 占位实体」workaround（`MaskService.cs:321-346`）**只在排序表被写丢时才「生效」**；而 `CadObjectCollection` 底层是 `HashSet<T>`（`CadObjectCollection.cs:40`），枚举顺序由哈希槽位决定，§9.7 行 471 自己承认旧路径「纯属幸运」。**先修写出器而不动规则，会立刻破坏现有出图。**
- **建议:** 先确定并实现唯一的绘制顺序权威来源（修写出器让 SORTENTS 落盘并给遮罩/文字写 sort handle，**或**明确绘制顺序不在范围内、把重排与「填充置后」整条删掉）。在此之前，README/报告应给出运维规避指引：不要在含 SORTENTSTABLE 的图上原地覆盖，先 `--out` 到副本并核对 DRAWORDER。

### C02 ★ [Blocker] `MaskSendHatchToBack` 假成功：默认开、每次跑、报成功、文件里没写
- **位置:** `TextMaskRule.cs:55`（默认 `true`）、`:100-101`、`:162-180`、`:201-204`、`:864-883`；`Jun.Cad.Batch.json:205`；`samples/text-mask.json:24`
- **事实:** `PushHatchesToBack` 对每个 Hatch 调 `table.MoveToTop(hatch)` 并 `moved++`（`:877-878`），累加进 `movedToBack`，汇总打印「填充置后 N」（`:203-204`），日志 `:881` 打「已把 N 个…送到最底层」。而 `README.md:583`、报告 `§9.7:470`、源码注释 `TextMaskRule.cs:856-860`/`MaskService.cs:305-307` 一致声明排序表不落盘、`§9.7:470` 明说「从未真正生效过——只计数、文件里没写」。`§9.4:417` 与 `README.md:864` 还把「填充置后 6」当端到端成功指标。
- **附带成本:** `:100-101` 因它每次执行都建整空间 `BoundsIndex`（`BuildOccluders` / `EstimateCellSize` / 4096 cell 上限），即使 `MaskOnlyWhenOccluded=false`。
- **建议:** 默认改 `false` 或直接停用该分支；从默认配置、执行路径、计数、日志四处一起删，并去掉无谓的索引构建。**不要只把「填充置后 6」改成「历史数据」——那会删掉唯一可见症状。**

## P1 — 默认配置下静默产生错误输出或错误覆盖面

| ID | 严重度 | 事实 | 位置 | 建议 |
|---|---|---|---|---|
| C04 | Major | `bringToFront` 只用于 MTEXT 原生(`:244`)与 DIMENSION(`:412`)；两处 `PlaceMaskBefore`(`:290`,`:332`) **无条件**执行，文字仍被 Remove+Add、拿新句柄、挪到集合末尾。汇总/RuleSummary 却按配置值决定是否显示「文字前置 N」，造成「日志说不前置、实际已前置」。已带遮罩的实体重跑判重跳过，开关对它们彻底失效。`TextMaskRuleTests.cs:351-362` 只测了原生路径，没抓到 | `TextMaskRule.cs:54,141,225,244,262-303,310-346,412`；`MaskService.cs:297-346` | 要么在两处 `PlaceMaskBefore` 前加分支（遮罩仍需排在文字之前，需另给不改句柄的方案），要么明确写「Wipeout 路径不受此开关控制，句柄必变」，并补 Wipeout 路径 + 开关关闭的回归断言 |
| C05 | Major | `created` 是 `Execute` 局部变量(`:73`)，每次归零，只计本次新增；已有遮罩判重跳过不计入预算 → `MaxMasksPerDrawing=100` 对同一输出文件跑 10 次即加满 1000 | `TextMaskRule.cs:50,73,132-138,143,267,321,388`；`BatchConfig.cs:772-775` | 改为统计图纸内已有遮罩总数，或把字段改名为「每次执行新增上限」并同步文档/UI |
| C06 ★ | Major | `LayerService.EnsureLayer` 命中已存在图层直接 `return existing`（`LayerService.cs:35-38`），`PlotFlag` 只在新建时设；`MaskLayer()` 只在 `MaskLayerPlottable=false` 时写 `_layer.PlotFlag=false`，默认 `true` 时什么都不做 → 已存在且不可打印的 `_MASK` 图层不会被修回，Wipeout 落在不打印图层，出图没遮罩但汇总报成功。另 `EnsureLayer` 读的是通用 `action.LayerPlottable`，与 `MaskLayerPlottable` 语义重复（`false` 会改已存在图层，`true` 却不会） | `TextMaskRule.cs:1199-1220`；`LayerService.cs:24,34-38,112-114` | 在 `MaskLayer()` 里显式校验并纠正 `PlotFlag`，或至少在检测到 `PlotFlag=false` 时告警；统一两个字段 |
| C07 ★ | Major | `DatabaseWalker.cs:181` 对 `nested.IsAnonymous \|\| IsExternalReference(nested)` 直接 `continue`；`IsAnonymous` 是标志位，覆盖 `*U` 动态块实例块、`*D`、`*T` **全部**匿名块；`TextMaskRule.cs:504` 二次跳过 → 动态块可见几何里的文字永远拿不到遮罩。ATTRIB 收集循环在 `hit.Depth > 0` 时 `continue`(`:609-612`)，而 ATTRIB 只挂在 Insert 上、这是唯一入口 → 嵌套块参照上的属性**全部漏遮**，且无注释无文档 | `DatabaseWalker.cs:168-184`；`TextMaskRule.cs:502-509,600-653`；`ACadSharp/Tables/BlockRecord.cs:160-165` | 复用 `BlockLayerChangeRule.MoveDefinitionContent`(`BlockLayerChangeRule.cs:248-356`) 已有的 `processed` 去重 + 动态块 `ownDef.Source` + 嵌套递归处理；报告 `§5.3:251` 的 `*D/*T` 枚举要补全 |
| C08 | Major | `BatchConfig.Version` 固定 1、`ConfigLoader` 用 `MissingMemberHandling.Ignore`、无按版本迁移代码。旧配置缺字段时静默继承新默认值：全图加遮罩 + 改句柄 + 改块定义影响所有插入 + 测量值也加 + 无上限 | `BatchConfig.cs:15`；`ConfigLoader.cs:34,168-210`；`Jun.Cad.Batch.json:13` | 加配置版本迁移，或在升级时对缺失的关键 `Mask*` 字段显式告警 |
| C09 | Major | `NormalizeDimensionMaskMode` 对任何非 `Off`（含 `Solid`、拼写错误、`BlockInternal`）一律返回 `Native`；`NormalizeDimensionMode` 对任何非 `Off`/`ExplicitOnly` 一律返回 `All`。`RuleValidator` 的 TextMask 分支校验了 `MaskMode`/`MaskTextBox`/`MaskPaddingRatio`/`MaskMinOccludedRatio`，唯独没校验这两个枚举。与 §9.2「写别的值都是硬错误」原则相反 | `TextMaskRule.cs:1011-1023,1049-1062`；`RuleValidator.cs:176-251` | 补 `RuleValidator` 校验；运行期对未知值抛 `RuleValidationException`；`BlockInternal` 单独白名单并给一次警告 |
| C10 | Major | `CapabilityProbe.RunAll()` 只被 `Program.cs:929`（CLI）与 `PreflightDialog.cs:123`（Win）调用，`TextMaskRule` 全文不引用。preflight 判据（固定轴对齐矩形、顶点数==4、`Definition==null`、包围盒严格 ==(0,0)-(100,50)）与生产执行判据（`CreateWipeout(IEnumerable<XY>)` 旋转四角、`IsWipeoutEncoded` 纯内存、顶点≥3、`_encodingChecked` 只查第一个）**完全不同**。通过的 preflight 对运行时产出的任何 Wipeout 都不构成保证 | `CapabilityProbe.cs:104-213`；`MaskService.cs:231-271`；`TextMaskRule.cs:1100-1129` | 把同一个编码自检函数同时用于 preflight 与生产，并让生产路径调用它；不要把修复引向「加强一个没人调用的函数」 |
| C11 | Major | `HideWipeoutFrame` 写 `ACAD_WIPEOUT_VARS` 的 `70=0`，报告 `:360` 自认在 AutoCAD 2025 重开仍报 `WIPEOUTFRAME=1`；实际只靠真彩色白实体色规避，深色屏幕仍见白框。`TextMaskRuleTests.cs:495-509` 恰好把这次无效写入锁成断言。文档标题却写「已缓解 / 擦除已验收 / 已知限制」 | `MaskService.cs:48-69,192,205-208`；报告 `:354-365` | 明确标注为未修 bug，别用「已缓解」；测试断言改成「已发出该写入」而非「边框已隐藏」 |
| C12 | Major | 根因 C 未修：`BuildOccluders:836-839` 默认整类排除 `Insert`，没有展开块定义几何；`ComputeOcclusion` 仍用 0.05 阈值 + 每条线 0.25 权重。默认 `MaskOnlyWhenOccluded=false` 只是绕过，显式打开即暴露 | `TextMaskRule.cs:43-45,107-129,836-839,895-943` | 修遮挡判定或显式声明该开关不可用；文档不能默认承诺它可用 |
| C20 | Major | `Jun.Cad.Batch.Core.csproj:20` 引用仓库外的 `..\..\..\ACadSharp-v3.8.35\src\ACadSharp\ACadSharp.csproj`。该克隆在本机存在（已核实），但不在版本库内，干净检出编不过。报告 §8:329 自认「提 PR 前要还原成原多目标列表」 | `src/Jun.Cad.Batch.Core/Jun.Cad.Batch.Core.csproj:20`；外部克隆 `ACadSharp.csproj:10` 已被改成只编 `net8.0` | 把库修复以可引用形式（NuGet 包 / submodule / 明确版本）纳入构建链，别让核心功能依赖本机私有目录 |

## P2 — 边界 / 判重 / 自检

| ID | 事实 | 位置 |
|---|---|---|
| C13 | `MaskTextBox=Auto`（默认）用 `RectangleWidth > tightWidth*1.05` 二选一；`MeasureMTextTightWidth` 自述「误差 ±10% 量级」且无字形度量，框宽落在阈值附近时分支随机翻转，两份近似 MTEXT 得到两种明显不同的遮罩 | `TextBounds.cs:522-525,625-639`；`TextMaskRule.cs:569`；报告 `§9.7:465` 未提误差 |
| C14 ★ | `_encodingChecked` 在首个 Wipeout 通过后置 true，同图其余 Wipeout 不再自检；`§9.2:395` 却写成全量安全网 | `TextMaskRule.cs:1087,1114-1124`；`MaskService.cs:235-271` |
| C15 | 无「文字为空则跳过」判断；空 MTEXT + 非零 DXF 41 会得到 `Width>0 && Height>0` 并加遮罩；`MaskTextBox=Tight` 时更写出面积≈0 的 Wipeout | `TextBounds.cs:266-283,553-587`；`TextMaskRule.cs:548-583`；`MaskService.cs:154-166` |
| C16 | `HasOverlappingWipeout` 用 `SameBox` 只比 AABB 中心 + W/H（容差 1%），旋转角不参与；同位置 0° 与 90° 文字互相误判为「已有遮罩」 | `TextMaskRule.cs:1139-1197`；`TextBounds.cs:423-458,493-497` |
| C17 | `Bounds2d.IsValid` 只查 `MinX`/`MaxY` 的 NaN；`MinY`/`MaxX` 为 NaN/±∞ 时仍判有效，`NaN<=0` 为 false 绕过后续检查，写出坏 Wipeout | `TextBounds.cs:25-28`；`MaskService.cs:169-172` |
| C18 | `PlainTextOf` 在 `TryGetDimensionTextAnchor` 返回 true 后无条件遍历 `dim.Block.Entities`，而该函数在 `dim.Block == null` 时也可返回 true → 潜在 `NullReferenceException`（当前无调用点） | `TextBounds.cs:666-696,369-414` |
| C19 | `ReorderToTop` 的 `if (first != null && first.Owner == space.Owner)` 近乎恒 false（`space.Entities` 内实体 `Owner` 指向 BlockRecord 本身，`space.Owner` 是块表/文档），`Remove(first)` 从不执行；当前调用方传入的新建 Wipeout 未入集合，暂未暴露 | `MaskService.cs:321-346`；`CadObjectCollection.cs:36-38,61-67` |
| C21 | 块内 TEXT 遮罩比文字框低约一个字高（`ForText` 的 `InsertPoint`/`AlignmentPoint` 无块定义坐标系修正）：报告 `:271` 说「需要复核」，**未见修正代码，本轮未重跑出图，未核实** | `TextBounds.cs:205-262`；报告 `:271` |

---

# 四、确认的文档缺陷（按危害排序）

| ID | 严重度 | 事实 |
|---|---|---|
| D02 | **Blocker** | `§9.3:409`「MTEXT 写 `BackgroundFillFlags.UseDrawingWindowColor`（bit1，**不是 bit0** 的显式 RGB）」与 `MaskService.cs:138-143`（bit0+bit1 → `90=3` + `BackgroundColor=ACI 7` + `BackgroundScale`）及 `§9.6:445` 直接相反。**照 §9.3 实现就退回 §9.6 已证实「AutoCAD 当没有遮罩」的 `90=2` 缺陷。** 必须删/改，并在 §9.3 标注「标志编码以 §9.6 为准」 |
| D01 | **Blocker** | §5.2（Wipeout 不可用 → 2D Solid，`WipeoutWriteSupported=false`/`MaskSolidColor(255)`/`MaskMode=Wipeout` 抛异常）、§8（Auto 优先 Wipeout、探测不过降级 Solid）、§9.2（Solid 退役、Auto/Wipeout 同义、自检不过直接抛）是三套互斥实现，当前代码**只对应 §9.2**；`WipeoutWriteSupported`/`MaskSolidColor`/`CreateSolid` 全 `src` 零命中；`§5.2:244` 仍写「MaskMode=Wipeout 直接抛」。文档没有一处写明「§5/§8 已作废」 |
| D11 | **Blocker** | 全部「已确认/已解出/已验收/已完成」无一条可复现命令：没有 `accoreconsole.exe` 完整命令行、`-PLOT` 参数、LISP 加载方式、`dotnet test` / `Jun.Cad.Batch.Cli run` 命令、expected PNG / 统计 JSON / 像素断言脚本。§4 自定的 5 条「缺一条都算没修好」没有转成任何自动化断言。`README.md:851` 说渲染对照「还需完成」，报告 §8.1 标题说「已完成」 |
| D03 | Major | `§9.3:407` 把「`DimensionMaskMode` 非 Off 时再加 `DIMENSION`」写错；编排层核实 `TextMaskRule.cs:51,475-478` 用的是 `dimMode`（来自 `DimensionTextMode`） |
| D04 | Major | `§5.1:236` 说签名改成 `ApplyMTextBackground(mtext)`「只置 BackgroundFillFlags」，实际是 `(MText,double)` 并写 `BackgroundColor`/`BackgroundScale`；`§9.1:388-389` 说「手写 `DxfClassMap` 只放 69」「建在 `new DimensionStyle("*override")` 上」，实际是 `GetStyleOverrideMap` → 追加 69 → `SetStyleOverrideMap`（全 `src` 检索 `SetDimensionOverride`/`new DimensionStyle` 零命中），且与 `:382`「原有 140 等键全部保留」自相矛盾；`§5.3:252` 把去重归给 `EntityScanner`，实际在 `DatabaseWalker.CollectFrom` |
| D05 | Major | `§6:282`/`§9.5:422` 说「MTEXT 偏移因子仍是尽力而为、90=2 时没有 45」，`§9.6:453` 说「不复存在」；`§9.5:423` 说「别的几何要靠 SortEntitiesTable」，`§9.7:470` 说该路线「彻底不可用」；`§9.5:424` 说「交互式观感未人工确认」，`§9.6:428` 说用户已在交互式 AutoCAD 复查并查出 bit0 真 bug |
| D06 | Major | `~~没解出来~~ **已解出**`、`~~没能确认~~ **已确认**`、`~~尽力而为~~ **已消除**` 让同句承载两个互斥事实，删除线文本视觉权重与新结论相同；而该删的 §5.2、§9.5 三条限制、L282 原样留着 |
| D07 | Major | 测试数 212（`:264`）/ 215（`:325`）/ 291（`:416`、`README.md:863`）/ 301（`:481`）四个互斥值无基线说明。**本轮实测当前真值：`Failed 0 / Passed 296 / Skipped 5 / Total 301`**，`TextMaskRuleTests.cs` 有 **40 个** `[Fact]/[Theory]` 方法，不是 `:264` 的「10 个」 |
| D08 | Nit | `:478`「296 处遮罩（289+2+10）」但 289+2+10=301≠296、296+13=309；`:455`「仍未加遮罩 26 个：13 个…8 个…」13+8=21，差 5 无交代；`MaxNestingDepth` 报告写 4，`BatchConfig.cs:303-304`/`README.md:318` 默认 8 |
| D09 | Major | `§9.1:384`「不新增实体、**不改句柄**、重生成保留」在默认配置下为假：`MaskBringTextToFront` 默认开(`:54`)，`ApplyDimensionMask` 调 `BringToFront`(`:412-415`)，`MaskService.cs:309` 明写分配新句柄。`README.md:591` 同错、`:600`/`:581` 自相矛盾 |
| D10 | Major | `§9.7:466`「旧版文件重跑会自动升级：换紧贴遮罩」过度概括：只有 `RectangleWidth > tightWidth×1.05` 才换；框贴文字或 41=0 走原生路径补 bit0；`Tight` 时 `HasOverlappingWipeout` 命中直接跳过。缺一张「框松/框紧 × Auto/Native/Tight × 旧 bit0 有无」结果矩阵 |
| D13 | Major | `README.md:583`「已被取代/从未生效」vs `:847`「62 个填充被置后」/`:864`「填充置后 6」当成功指标；`README.md:851`「渲染对照还需完成」vs `:868-869` 逐像素结果 |
| D12 | Nit | `BatchConfig.cs:708`「默认 false = 不参与」里的 false 描述的是「是否参与」的有效值，不是配置字段值；同文件 `[RuleField] Help`(`:713`)「默认是（忽略）」才对应字段。行为一致，仅 IntelliSense 措辞歧义 |
| — | Nit | `§9.6:446` 判重只看 bit0，用户自定义显式背景色会被当「已处理」跳过，文档未写（`TextMaskRuleTests.cs:233-245` 锁定了该行为，可能是有意取舍） |

---

# 五、明确撤销的前序误报

跨阶段合成时，**下列结论已被源码 / 文件系统 / 测试实测推翻，不应进入修复清单**：

| # | 误报 | 撤销依据 |
|---|---|---|
| R01 | 「§7 复现材料与 bgprobe 证据全部不存在」 | 编排层逐个 `test -e` 全部 `EXISTS`：3 个 `.lsp`、6 个 `.png`、2 个 `.txt`、`pngscan.py`、`DimFillProbe.cs`/`DimScan.cs`、`dim-ac.png`、`m90.dwg`、`m90.png`；`tmpv` 260 文件、`.workbuddy-ai/bgprobe` 131 文件。评审者用的是仓库 Glob，这些目录被 `.gitignore` 排除 |
| R02 | 阶段 5 #3「约 200 行遮挡判定在默认配置下是死代码」 | `TextMaskRule.cs:100-101` 是 `(onlyOccluded \|\| sendHatchToBack) ? BuildOccluders(...) : null`，默认 `sendHatchToBack=true` → `BuildOccluders`/`EstimateCellSize`/`BoundsIndex`/`occluders.Query`/`PushHatchesToBack` **每次执行都跑**；被跳过的只有 `ComputeOcclusion`。正确描述是「跑了、产出不落盘、还报成功」，比死代码更糟。「绕过根因 C」主结论仍成立 |
| R03 | 「CapabilityProbe 仍是当初那个只验能写能读的探测」 | `CapabilityProbe.cs:162-184` 实际断言 4 项几何不变量（`ClippingState`、顶点数==4、`Definition==null`、包围盒==(0,0)-(100,50)）。真问题是 C10：判据与生产不同、且生产不调用它。加强一个没人调用的函数毫无意义 |
| R04 | 「测试套件没有 DWG 往返覆盖」 | `DwgSampleTests.cs:59-87` 有 `CadFileSession.Save` → 重新 `Open` 的真往返，`:92-125`/`:130-181` 有端到端跑 `BatchRunner` 并写盘。真实缺口更窄：**没有任何用例把 `TextMask` 规则跑在真实 DWG 上再写盘回读**，且 `SampleDwgFactAttribute`(`TestPaths.cs:87-111`) 在外部样图目录缺失时自动 Skip（本轮 5 个 Skip 全部来自这里） |
| R05 | 「`MaskIgnoreBlockBounds` 注释写反，会导致 `Insert` 参与判定」 | 见 D12，`useBlockBounds` 默认算出 false（不参与）与注释一致，无功能错误；降级为 Nit 措辞 |
| R06 | 「退役字段仍被校验」「`Auto`/`Wipeout` 完全同义」是 bug | 均为有意设计并已文档化：`README.md:596` "Validation does not silently discard or migrate the old data"、`BatchConfig.cs:920-925`、`ConfigLoader` 保留 `Action.Extra` 往返；`RequireNativeMaskMode`(`:992-1009`) 与 `RuleSummary`(`:342-344`) 刻意让两者同义 |
| R07 | 「`MaskMode=Wipeout` 直接抛 `RuleValidationException`」 | 当前接受 `Auto`/`Wipeout`，只有 `Solid` 与未知值抛异常（`TextMaskRule.cs:992-1009`） |
| R08 | 「默认配置下仍启用 0.05 阈值」 | `ComputeOcclusion` 只在 `onlyOccluded=true` 时调用（`TextMaskRule.cs:107`）；阈值与 0.25 权重只在显式打开开关后生效 |

---

# 六、验证状态（本轮编排层实测）

| 项目 | 结果 |
|---|---|
| `dotnet test Jun.Cad.Batch.sln --nologo -v minimal` | **Failed 0 / Passed 296 / Skipped 5 / Total 301**，211 ms；5 个 Skip 全为 `DwgSampleTests`（外部 `SampleDwg` 目录不存在）→ 恰好印证 C10/R04 的可复现性缺口 |
| `TextMaskRuleTests.cs` 测试方法数 | **40** 个 `[Fact]/[Theory]`（阶段 9 两个评审者分别报 36 / 40，以 40 为准） |
| `.NET` SDK | 8.0.425 可用；`ACadSharp`、`Jun.Cad.Batch.Core`、`Jun.Cad.Batch.Tests` 三个项目均构建成功 |
| 外部 ACadSharp 克隆 | `D:/Dev/CSharp/AutoCAD/ACadSharp-v3.8.35` 存在；`Wipeout.cs:50` 的 `SetMaskingArea(IEnumerable<XY>)`、`Dimension.cs:514-526` 的 `edata.Records.Clear()`、`CadObjectCollection.cs:40` 的 `HashSet<T>` 均已核实 |
| §7 / bgprobe 复现材料 | 全部存在（见 R01） |
| AutoCAD 宿主 / 出图 | **本轮未运行**。`ssget`、`entget`、`-PLOT`、逐像素 PNG 扫描、交互式会话观感、`WIPEOUTFRAME=1` 复现等结论，仍按「文档自述」采信，属未核实 |
| 生产代码改动 | **无**。本轮只新增 6 份 `docs/review/textmask-2026-11-05/*.md` 审查产物 |

---

# 七、建议的处置顺序

**原则（阶段 8 的核心边界）：先改代码，后改文档。只清理文档会让「默认开着、每次跑、写盘改用户数据、报成功」的路径看起来更像已解决。**

1. **P0 数据完整性**：C03（修 XData 清空/69 覆盖）→ C01（定绘制顺序权威来源；注意与占位实体 workaround 的耦合，顺序不能反）→ C02（停用假成功路径并删索引构建）。
2. **P1 默认配置正确性**：C06 → C04 → C07 → C05 → C09 → C10 → C08 → C11 → C12 → C20。
3. **P2 边界**：C13–C19，每个补一条对应回归断言（尤其 Wipeout 路径 + `MaskBringTextToFront=false`、旋转文字判重、空 MTEXT、`MaxMasksPerDrawing` 重复运行）。
4. **验收门禁**：补一份 `VERIFY.md`——逐条命令 + 输入图路径 + 预期断言；把 §7 的 LISP/PNG 基线纳入版本库或删掉引用；新增一条「`TextMask` × 真实 DWG × 写盘回读 × 宿主 `ssget`/出图」的端到端测试，并入库样图，消除 Skip。
5. **最后改文档**：在 §5 与 §8 开头加显式作废声明；删/改 `§9.3:409`（D02）与 `:407`（D03）；修正函数签名与已不存在符号（D04）；给 §6/§9.5 加删除线并指向 §9.6/§9.7（D05/D06）；统一到唯一测试基线 301/296/5（D07）；修正算术与「不改句柄」（D08/D09）；补旧图重跑矩阵（D10）与配置契约 / 迁移步骤；README 与报告对「填充置后」「渲染验收」给同一答案（D13）。

---

# 八、未核实 / 遗留风险

1. **AutoCAD 宿主侧全部结论**：本轮无 AutoCAD 会话，`ssget` 计数、`entget` 回读 `(90.3)(63.7)(45.1.15)`、`DSTYLE` 覆盖键 750→780、逐像素出图数值、`WIPEOUTFRAME=1` 重开仍报到、交互式会话观感，均**未复现**。
2. **SORTENTS 写出器根因**：未逐行复核 `DwgObjectWriter.Objects.cs` / `DxfObjectsSectionWriter.cs`；「块记录扩展字典丢失」依据项目记录与 `README.md:771`，未独立定位丢表点。C01/C02 的「不落盘」前提依赖此记录；但「默认 true + `MoveToTop` + 计数」的代码事实已核实。
3. **库写出器行为**：`DimensionTextBackgroundFillMode.DrawingBackgroundColor` 是否等于 `DIMTFILL=1`、`new Color(7)` 是否写出 DXF 63=7、`BackgroundScale` 是否写出 45=1.15，均按枚举/代码推断，未用 `accoreconsole` 回读。
4. **占位实体重排**：改变的是内存 `HashSet<T>` 枚举顺序，是否等于 DWG 写出后的实体顺序 / 宿主绘制顺序，未核实；§9.7 自己承认排序表写不出去，这是「绘制顺序已验收」结论最薄弱的一环。
5. **C21（块内 TEXT 偏移一个字高）**：未见修正代码，但未重跑出图确认是否仍复现。
6. **历史测试数字 212/215/291**：对应历史轮次，无法回溯复现。
7. **§5.6 第 2 项、§4 的 5 条验收标准**：至今没有任何自动化断言与之对应。

---

*本报告为阶段 5–9 的交叉汇总；分项详情见同目录 `zoom_out_report.md`、`comprehension_report.md`、`critic_report.md`、`intermediate_zoom_out_report.md`、`implementation_readiness_report.md`。*
