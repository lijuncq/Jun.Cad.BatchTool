# Stage 9 · Implementation Readiness Test

- 规范：`README.md`（承诺）
- 实现：`src/`（Core / Cli / Win）
- 标准：`resources/document_standards/implementation-readiness-standard.md`
- 审查者：egm-implementation-readiness、egm-codebase-details、egm-style

## 结论

**四条安全承诺里有三条在代码里不成立**：`失败不写盘`、`预览 = 执行`、`原子替换`。根因是同一个缺口——`BatchRunner` 把规则异常吞成 `Aborted` 标记，而**写盘路径从不读这个标记**。

已由 Elephant 亲自复核（`grep` 证据附在每条后）：

| 承诺 | 位置 | 实测 |
|---|---|---|
| 失败不写盘 | README L270 / L597 | `grep Aborted Program.cs OfflineRunner.cs` 只命中 `CollectRuleStats`，写盘路径无门禁 |
| 预览 = 执行 | README L641 | 快照只抓实体属性，样式表/块表变更不进快照 |
| 原子替换 | README L555 | `File.Delete` + `File.Move` 两条回退路径存在目标缺失窗口 |
| 计数不变式 | `RuleExecutionResult.cs:24` | `TextMaskRule` 在 `Hit()` 之前 `MarkSkipped` |

---

## A. 承诺被代码否定（Blocker）

### I1 [Blocker·已复核] 规则抛异常后照样写盘，写的是半成品
- `BatchRunner.cs:297-302` catch 只设 `Aborted`，不回滚内存、不丢弃文档；`ContinueOnError` 默认 true，后续规则继续在**已被部分修改**的文档上跑。
- `Program.cs:272-285` 写盘条件只有 `TotalChanged == 0 && SkipUnchanged`；`OfflineRunner.cs:79` 无条件 `session.Save`。
- `CadFileSession.cs:14-16` 与 `BatchRunner.cs:30-31` 的注释都写"规则失败时整个文档丢弃"——**这条代码路径不存在**。
- 实例：`BlockReplaceRule` 里 `Remove(src)` 成功、`Add(newRef)` 失败 → 块参照被删且异常被吞 → 文件带着"少一个块参照"写出去。
- 这也废掉了 `--preview` 的信任基础：预览同样吞异常，用户看到"被挡住"，执行得到"部分改动已落盘"。

### I2 [Blocker·已复核] `--in-place --no-backup` 先删源文件再改名
- `CadFileSession.cs:295-296`：`File.Delete(target); File.Move(temp, target);`
- 回退路径 `:289-291` 同样 `Copy → Delete → Move`。
- Move 失败（杀软、句柄占用、磁盘满）→ **源 DWG 已消失**，临时文件在 `finally` 里被 `TryDelete` 一起删掉。本项目唯一不可恢复的失败模式，恰好落在"用户明确要求不备份"的路径上。

### I3 [Blocker·已复核] 预览对"改样式表/删表项"完全瞎，还会打印"均已是目标状态"
- `EntitySnapshot.cs:65-123` 只存实体自身属性；`Style` 字段存的是**样式名**不是样式内容。
- `TextFontChangeRule:35` 改的是 `TextStyle`（实体上的名字不变）；`DimensionStyleChange`、`PurgeUnused` 同理。
- `BatchRunner.cs:219-230`：`diff.Count == 0 && Matched > 0` → 写一行 `No change`，备注"命中 N 处，均已是目标状态"。
- 于是：**预览说"什么都没变"，执行把整张图的字体换掉、把 3 个块定义删掉**。不是缺失，是显示了相反的结论。

### I4 [Blocker·已复核] 预览句柄去重是全局的，跨规则互相吞行
- `ChangeRecord.cs:130-135` 的 `_handles` 是 `PreviewSink` 上单个 `HashSet`，`Clear()` 存在但 `BatchRunner` 从不调用。
- `BatchRunner.cs:206-211` 用它去重。规则 1 记一条 `RecordSkip`（"无遮挡"）→ 规则 2 真改了同一句柄 → 该行被吞。
- `TextReplace` 后紧跟 `TextHeightChange`（README 7.2 的 smoke 组合）时，字高改动完全不可见。

### I5 [Blocker·已复核] `GeometryCleanup` 真删了对象，却全部记"跳过"，`Changed = 0`
- `GeometryCleanupRule.cs:92/102/116` 收集阶段一律 `MarkSkipped`；`:157` 真删时只 `removed++`，从不 `MarkChanged`。
- 后果两层：报表"匹配 1200，成功 0，跳过 1200"而文件少了 1200 个对象；`--skip-unchanged` 判 `TotalChanged == 0` → **删除被静默丢弃**，日志报"没有任何改动"。

## B. 失败模式无处理（Major）

- **I6** 运行清单只在整批跑完后写一次；中断 = 无清单，`--latest` 会**静默回滚到上一次**（`Program.cs:168-181`、`RunManifest.cs:185-188`）。双重破坏：用户以为撤销了刚才那批，实际把更早的一批覆盖回去。
- **I7** `--recursive` 遇同名文件时输出与备份互相覆盖：`A\1.dwg` 与 `B\1.dwg` 都写 `out\1.dwg`、都备份到 `backups\1.dwg.bak`；汇总仍报"成功 2 个"，回滚把 B 的备份还原到 A 的位置。
- **I8** `BomSync` 在 `--dry-run` 下仍写 CSV（只判 `context.Preview != null`）；该 CSV 不在 manifest 里，回滚无法撤销。
- **I9** 显式 `--pat`/`--lin` 路径不存在时静默回退到本机 AutoCAD 安装目录（`ResourceLocator.cs:152`），两次运行都无提示。
- **I10** 零改动也重写文件并升级 DWG 版本（只有 `--skip-unchanged` 才跳过）；批量跑 500 张只读检查会把 500 张图格式全改掉。
- **I11** 引用扫描不覆盖 `*D` 标注块、视口冻结层列表、匿名块内部（`ReferenceScanner.cs:60-96`）→ `PurgeUnused` 可能删掉仍被引用的图层。
- **I12** 大图无边界：预览是 O(规则数 × 全库遍历) + 常驻两份全量快照；实测上限只有 1.1 万实体，10 万级无数据。
- **I13** 界面「回滚」用可能过期的 `LastRunDirectory`（方法开头不重置），清单写失败时会回滚**上一次**那批。

## C. 文档有 / 代码无（Major）

- **I14** 计数不变式无强制，`TextMask` 自己就破（已复核：`MarkSkipped` 在 `Hit()` 之前）；`TextReplaceRule:100` 同样。
- **I15** 无过滤护栏：README 列 4 条，代码 7 条；且护栏位置太晚——`EntityLayerChangeRule:35` 先建图层、`:43` 才检查；`TextFontChangeRule:35` 先改样式、`:57` 才检查。**中止 ≠ 无副作用**，而中止的副作用会被写盘。
- **I16** 三条"按块写属性"的规则把用户 `Filter` 的大部分字段丢掉（只固定 `INSERT` + `Container*`），`LayerName`/`ColorIndex` 不生效。
- **I17** `TitleBlockNormalize` 没写 `SourceBlockName` 时写进**所有**带该 Tag 的块（`Wildcard.IsMatch(x, null)` 恒真，已复核）。
- **I18** `LayerMap` 注释说"当前层永不动"，`IsProtected` 只判 `0`/`Defpoints`；配合 `LayerMapDeleteEmpty` 可删掉当前层。
- **I19** 区域过滤对无包围盒对象是**放行**（`SpatialFilter.cs:288-292` 返回 true），与同文件 `:44` 注释"直接判 Skipped"和 README"表格不参与"都相反。
- **I20** `PurgeUnused` 未知清理类别 → 兜底清**全部**类别（`PurgeUnusedRule.cs:347`），而校验文案说"会被忽略"。

## D. 契约强度（回答"新人写规则会不会出事"）

**不够。** 具体缺口：

1. `IRuleHandler.Execute` 无返回契约；`Hit/Changed/Skipped/Failed` 是四个独立计数器，没有收尾校验（I14、I5 的直接产物）。
2. 没有"我改了什么"的必填上报：`RecordChange` 可选，引擎用快照兜底，但快照只覆盖实体属性 → **改表的规则天然无法被兜底**，`TextFontChange`/`DimensionStyleChange`/`PurgeUnused` 三条一行预览都不报（I3）。
3. `ChangeStatus` 只有三态，没有"表项已删除/已新增"，`PurgeUnused` 想报也无处可报。
4. `HasRecordFor` 是全局句柄集合，把跨规则串扰写进了接口语义（I4）。
5. `RuleContext.Filter` 可写 → 规则会**改写用户配置**（`TextMaskRule:350` 等把默认 `EntityTypes` 写回），界面跑完一次再保存，`config.json` 里凭空多出字段。

最小修复方向：`ExecuteRule` 收尾加不变式校验（`Matched != Changed+Skipped+Failed` → Warn）；给 handler 加 `ReportsOwnChanges`；`_handles` 按 `(RuleIndex, Handle)` 建键。

## E. 代码库细节（egm-codebase-details）

**重复实现（已漂移）**

| # | 内容 | 位置 |
|---|---|---|
| D1 | CLI 与 UI 各写一套输出路径解析；UI 有"绝不覆盖源图"护栏，CLI 没有 | `Program.cs:404` vs `OfflineRunner.cs:189` |
| D2 | 类型级校验两套，覆盖已不一致（非法正则只有 UI 拦得住） | `ConfigLoader.cs:181-305` vs `RuleValidator.cs` |
| D3 | 文件级处理循环两份，行为分叉（版本/预览/manifest 字段） | `Program.cs:193-331` vs `OfflineRunner.cs:60-108` |
| D4 | `"旧=新"` 解析四份，右侧是否允许为空不一致 | `LayerMapRule` / `LayoutRenameRule` / `TitleBlockNormalizeRule` / `AttributeAuditRule` |
| D5 | `IsWide` 两份且区间不同（遮罩宽度与汇总表列宽对同一字符判断不一致） | `TextBounds:518` vs `TextColumns:85` |

**脆弱假设**

- `TextBounds.PlainTextOf` 在"有 TextMiddlePoint、无 *D 块"的标注上必空引用（`:553` 直接遍历 `dim.Block.Entities`）。
- 颜色索引过滤不排除真彩色实体（`EntityFilter.cs:121`）：真彩色的 ACI `Index` 为 0，`"ColorIndex": 0` 会连带命中所有真彩色对象。
- `BomSync` 相对路径按 CWD 解析，与 `BatchConfig` 注释"按配置文件目录"相反。
- 数值格式化未固定 `InvariantCulture`（de-DE 下输出 `0,385`，README 7.3 的证据对不上）。

**死代码 / 写了不读**

- `RuleJson.cs:355` 判断 `StartsWith("Jun.AutoCAD.Config")`——迁移后命名空间已改，**该分支永远不成立**（已复核）→ 空 `Filter`/`Action` 不被省略，噪声写回 `config.json`。
- `MarkChanged/MarkSkipped(detail)` 完全忽略参数，全项目 100 处仍在拼详情字符串。
- `LastRunStore.cs` 整文件零调用（legacy `BATCHAGAIN` 残留）。
- `CapabilityProbe` 表格缓存键 `"table-cell:"+path` 永远不命中（`Finish` 只缓存 wipeout）。
- `NameMatcher`（73 行）、`IsFrozen`、`ScaleForSpacing`、`DescribeColor`、`DescribeLineWeight` 零调用。

**测试**

`src` 下没有测试项目；`legacy/tests/` 有 8 个测试文件 1161 行，移植时整套丢掉——`RuleJsonTests` 覆盖的正是被废掉的那条分支。无需 DWG 就能测的：`RuleJson` 往返、`RuleValidator` 全类型、`TextMatcher`、`MTextTokenizer`、`Wildcard`、`Geo2d`、`TextBounds`、`TextColumns`、`HatchPatternMetrics`、`EntityFilter.MatchesType`（CIRCLE vs ARC）、计数不变式。

## F. 语言与信息质量（egm-style）

- **J1** `MarkSkipped(detail)` 丢参数 → 所有"跳过"都是无信息数字；README 7.5 写的"其中 24 处无遮挡、204 处是块内/锁定图层"当前代码产不出来。
- **J2** "成功"列统计的是 `Changed`，审计类规则永远"成功 0"；表头应改"改动"，只读规则另设"报告"列。
- **J3** "命中"与"匹配"是两个不同的数（扫描数 vs `Hit()` 数），同一运行里两个词给出不同数字。
- **J4** 同一事件三个词：统计记"跳过"、预览记 `Blocked`、CLI 汇总叫"被挡住"。
- **J5** `MaskIgnoreBlockBounds` 标签写"块参照包络参与判定"，默认 `true` = **忽略**——开关方向与标签相反。
- **J6** `BatchConfig.ExecutionMode` 注释仍在讲事务（"共用同一个事务""Parallel 降级为 Isolated"），与 `BatchRunner` 直接矛盾。
- **J7** `TextMaskRule` 类注释说标注遮罩"当前版本不支持"，代码里有 60 行完整实现。
- **J8** `RuleSummary` 的"说真话"约定被自己的 `rendered.Add` 绕过：`RegionPredicate`/`RegionIncludesNested`/`ContainerAttributePattern` 被标记已渲染却不输出，`unrendered` 恒为 0。
- **J9** 用户可见帮助文本说"插件不会跨图导入"（产品是独立程序）；界面开关叫"命令行输出明细"（GUI 里没有命令行）。
- **J10** 状态列英文、其余列中文（同一预览窗口两种语言）。

## 复核意见（Elephant）

I1–I5 全部亲自复核成立，**这五条必须在任何其他工作之前修**。I6/I7/I10 是同一类"批处理语义"缺陷，可一起设计。J5 是我自己写反的（标签与字段同向但默认值是"忽略"），属低级错误。E 节的 `RuleJson` 命名空间是移植遗留，一条字符串修复 + 一个测试就能永久锁住。
