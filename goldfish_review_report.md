# Goldfish Annotated Review Report

**Target:** `Jun.Cad.Batch`（`README.md` 全文 + `src/` 实现）
**Source findings:** `docs/review/consolidated_review_report.md`（12 Blocker / 14 Major 组 / 9 Nit 组）
**Status:** resolved

分类约定：**CONTEST**（批评不成立，附可核查证据）｜**ESCALATE**（需作者决策，不动手）｜**PROPOSE FIX**（成立，给精确改法）｜**DECLINE**（Nit，记录不修）

---

## P0 · 数据损坏与承诺失效

### B1 规则中止后仍写盘 — `PROPOSE FIX`
- **Scope:** `Engine/BatchRunner.cs:131`、`Cli/Program.cs:272`、`Win/OfflineRunner.cs:79`
- **Discrepancy:** 文档三处承诺"失败不写盘"，代码写盘路径从不读 `Aborted`。
- **Justification:** 核实成立。`grep Aborted` 在两条写盘路径零命中。但"中止即丢弃整个文件"会丢掉已完成规则的有效改动，这是**产品决策**，不该由修复者单方面决定 → 提供 `OnRuleFailure` 开关，默认 `DiscardFile`（安全侧），`WritePartial` 保留旧行为。
- **Proposed Edit:** `BatchConfig` 加 `OnRuleFailure`；CLI/UI 在 `Save` 前检查 `summary.Results.Any(r => r.Aborted)`。

### B2 `--in-place --no-backup` 先删源文件再改名 — `PROPOSE FIX`
- **Scope:** `Engine/CadFileSession.cs:289-296`、`Cli/Program.cs`（选项校验）
- **Discrepancy:** `File.Delete` + `File.Move` 之间存在目标缺失窗口，失败即丢源图。
- **Justification:** 成立。修法两层：无备份时改用 `File.Replace(temp, target, null, true)`（原子）；同时拒绝 `--in-place --no-backup` 组合。
- **Proposed Edit:** `ReplaceAtomically` 无备份分支改 `File.Replace`；`RunOptions.Parse` 拒绝该组合。

### B3 预览对改样式表/删表项失明，反向报告 `No change` — `ESCALATE`
- **Scope:** `Engine/Preview/EntitySnapshot.cs:65-123`、`Engine/BatchRunner.cs:219`
- **Discrepancy:** 快照只抓实体属性，`TextFontChange`/`DimensionStyleChange`/`PurgeUnused` 的改动不进快照，`diff==0` 时被写成"均已是目标状态"。
- **Justification:** 成立且严重。但修法有两条互斥路线：(a) 快照扩展到表对象（内存与耗时翻倍）；(b) 给 handler 加 `ReportsOwnChanges`，由规则自己保证上报（需要改 3 条规则）。**这是架构取舍，需要你选**。
- **Proposed Edit:** 待决策。

### B4 预览句柄去重跨规则吞行 — `PROPOSE FIX`
- **Scope:** `Engine/Preview/ChangeRecord.cs:130`、`Engine/BatchRunner.cs:206`
- **Justification:** 成立。`_handles` 是 sink 级单集合，`Clear()` 无调用者。按规则重置即可。
- **Proposed Edit:** `IPreviewSink` 加 `BeginRule(int index)`，只清句柄集；`BatchRunner` 每条规则执行前调用。

### B5 `GeometryCleanup` 删了对象但 `Changed=0` — `PROPOSE FIX`
- **Scope:** `Rules/GeometryCleanupRule.cs:150-165`
- **Justification:** 成立。真删分支只 `removed++`，从不 `MarkChanged`，导致 `--skip-unchanged` 丢弃删除。
- **Proposed Edit:** 删除成功分支加 `result.MarkChanged(...)`。

### B6 配置校验静默 + 同错字后果相反 — `PROPOSE FIX`（部分）
- **Scope:** `Engine/EntityFilter.cs:33-40`、`Config/ConfigLoader.cs`
- **Justification:** 成立。本轮先修"同错字后果相反"这一半：`HasCondition` 计入空间/区域条件。未知键告警涉及 `JsonExtensionData` 的设计，留到 P1。
- **Proposed Edit:** `EntityFilter` 增加 `_hasSpatial`，`Build` 时按 `LayoutPatterns`/`ContainerBlockPattern`/`RegionMode` 置位。

### B7 输入/输出集合相交 — `PROPOSE FIX`
- **Scope:** `Cli/Program.cs:815`（`CollectFiles`）
- **Justification:** 成立。`AllDirectories` 不排除 `output\` 与 `.jun-runs\`，递归运行会自我喂食。
- **Proposed Edit:** 枚举时跳过输出根与 `.jun-runs` 子树。

### B8 配置契约层缺失 — `ESCALATE`
- **Scope:** `Config/BatchConfig.cs`
- **Justification:** 成立，是 13 条发现的共同根因。但引入显式 schema 是**重构级**改动（影响界面渲染、校验、摘要、legacy 兼容四处），不能在修 bug 的批次里顺手做。

### B9 文档缺问题陈述/非目标/备选方案 — `ESCALATE`
- **Scope:** `README.md` 结构
- **Justification:** 成立。但重写正文骨架需要你先定"主产品是变换器还是三合一"（Stage 8 · S1）。

### B10 `TitleBlockNormalize` 无块名时写进所有块 — `PROPOSE FIX`
- **Scope:** `Rules/TitleBlockNormalizeRule.cs:67`
- **Justification:** 成立。`Wildcard.IsMatch(x, null)` 恒真。与 `BlockLayerChange` 同构加护栏。
- **Proposed Edit:** 块名与容器都为空 → `RuleValidationException`。

### B11 `PurgeUnused` 未知类别 → 清全部 — `PROPOSE FIX`
- **Scope:** `Rules/PurgeUnusedRule.cs:347`
- **Justification:** 成立且不可逆。全部未识别时抛异常，列出可用类别。
- **Proposed Edit:** `set.Count == 0` 分支改为 `throw`。

### B12 CLI `--out` 指向输入目录会覆盖源图 — `PROPOSE FIX`
- **Scope:** `Cli/Program.cs:404`
- **Justification:** 成立。UI 侧有 same-file 护栏，CLI 没有。
- **Proposed Edit:** 解析后若与源文件同路径 → 改 `<名>_out.dwg`。

---

## P1 · 可用性与可维护性

| ID | 结论 | 说明 |
|---|---|---|
| M1 批处理语义 | `PROPOSE FIX`（部分） | 本轮修 `--recursive` 同名冲突（并入 B7 的输出路径改造）；manifest 增量落盘、零改动仍升版本留 P2 |
| M2 校验双轨 | `ESCALATE` | 合并 `ConfigLoader.ValidateByType` 到 `RuleValidator` 需要先确认命令行输出格式不变，属重构 |
| M3 计数与术语 | `PROPOSE FIX`（部分） | 本轮修 `TextMask` 的 `Hit`/`MarkSkipped` 顺序；"成功"改"改动"涉及 README 与汇总表，留 P2 |
| M4 Filter 语义 | `PROPOSE FIX`（部分） | 本轮修 B6；区域 fail-open、三条规则丢 Filter 条件留 P2（涉及语义变更） |
| M5 规则契约 | `ESCALATE` | 加不变式断言会改变现有 20 条规则的行为，需先决定"违规是警告还是中止" |
| M6 重复实现 | `DECLINE` | 6 处重复不造成当前错误，重构收益低于风险 |
| M7 可复现 | `PROPOSE FIX`（部分） | 本轮修 `RuleJson` 命名空间（属 M14）；图案库日志留 P2 |
| M8 规模 | `DECLINE` | 无 10 万实体样本，加指标是空谈 |
| M9–M12 文档 | `ESCALATE` | 与 B9 同一件事，等骨架决策 |
| M13 测试 | `PROPOSE FIX`（部分） | 本轮不建测试项目，但把 P0 修复点用可运行命令验证 |
| M14 死代码 | `PROPOSE FIX` | `RuleJson` 命名空间判断永远不成立（已复核），一条字符串修好 |

---

## P2 · Nit

`DECLINE`（记录不修）：术语统一、用户文案、注释与代码矛盾、帮助文本、路径风格、精度、`MaskIgnoreBlockBounds` 标签方向、死代码清理。

例外：`MaskIgnoreBlockBounds` 标签方向**本轮修**——标签写"包络参与判定"而默认值表示"忽略"，用户会读反，属误导而非风格。

---

## 汇总

| 分类 | 数量 | 本轮执行 |
|---|---|---|
| PROPOSE FIX | 11 | 9 项本轮落地（B1/B2/B4/B5/B6/B7/B10/B11/B12 + M14） |
| ESCALATE | 5 | 不动手，需决策 |
| DECLINE | 9 组 | 记录 |

---

## 处置结果（按三轮执行）

| 轮次 | 项 | 结论 | 验证 |
|---|---|---|---|
| P0 | B1 B2 B4 B5 B6 B7 B10 B11 B12 M14 | 已修 | README 7.10 |
| 第 2 步 | B3（`ReportsOwnChanges` 路线） | 已修 | README 7.11 |
| 第 1 步 | M1a M1b M3 M4 M7 | 已修 | README 7.11 |
| 第 3 步 | B8（schema 生成器）、B9（文档按 A 骨架重写） | 已修 | `jun-cad-batch schema`；README 1–13 节 |
| 第 4 步 | M2 校验合并（命令行格式不变）、M5 计数不变式告警 | 已修 | README 6.3 / 7.6 |
| 保留 | B6 的"未知键告警"一半 | 未修 | 记在 README 9.4 |
| CONTEST | 回滚该不该存在、4 条规则与宿主重复、未建模对象是否真会丢 | 维持原判 | 见本文件"P2 · Nit"前的说明 |

文档侧的三处事实性错误（组码"逐字节相同"、配置"完全一致"、7.1/7.5 数字对不上）
在重写时已改正：配置一致性改成"字段能往返、语义会丢"（见 README 9.4 与第 6 节），
7.5 的数字改成 `匹配 299 = 改动 71 + 跳过 228`（根因是 `TextMask` 的 `Hit` 顺序，已修）。
