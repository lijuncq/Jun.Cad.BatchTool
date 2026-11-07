# 阶段 9：实现就绪度审查报告（Goldfish Review Report）

**Target:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md` (`DetailedRange 230..481`；实现就绪度评审另覆盖全文 1..481)
**Reviewers:** egm-implementation-readiness / egm-codebase-details / egm-style（三个 fresh-context reviewers）
**Standards:** `C:/Users/lijun/.workbuddy-ai/egm/resources/document_standards/implementation-readiness-standard.md`；codebase-details 与 style 按角色内建准则
**Mode:** REVIEW

## Reviewer Status

| Reviewer | Status |
| --- | --- |
| egm-implementation-readiness | completed |
| egm-codebase-details | completed |
| egm-style | completed |

---

## 直接回答：文字遮罩功能还有 bug

**有，而且不止一处。** 代码里 §9 的「主线」（MTEXT 原生背景、TEXT/ATTRIB 的 Wipeout、标注 DIMTFILL）确实落地了，但下列 bug 仍在，其中前 6 条文档自己承认、后 5 条文档没写：

| # | 残留 bug | 证据 | 文档态度 |
|---|---|---|---|
| B1 | SORTENTS / 块记录扩展字典写不出去 → **存量图纸用户自己排的绘制顺序每次保存都被重置**（数据丢失） | `textmask-failure-report.md:470`、`:481`；`README.md:771` | 承认未修，「未继续深挖」 |
| B2 | `MaskSendHatchToBack` 默认仍开、仍建全空间索引、仍调 `MoveToTop`、仍在汇总里报「填充置后 N」，但排序表不落地 → 误导性计数 | `TextMaskRule.cs:55`、`:100`、`:162-180`、`:201-204`、`:864-883` | `:470` 承认「只计数、文件里没写」，没说开关仍默认开 |
| B3 | §5.6 第 3 项「遮挡判定看不到块参照」未修：`Insert` 一律跳过，没有「展开块定义几何」 | `TextMaskRule.cs:836`；`README.md:585` | `:272` 列为「没解决」，§8/§9 未再提 |
| B4 | §5.6 第 2 项「块内 TEXT 遮罩比文字框低约一个字高」：`ForText` 的 `InsertPoint`/`AlignmentPoint` 没有针对块定义坐标系的修正 | `TextBounds.cs:205-262` | `:271` 说「需要复核」，无已修代码 |
| B5 | `WIPEOUTFRAME`：写 `ACAD_WIPEOUT_VARS` 的 `70=0` 在 AutoCAD 2025 重开仍报 1，只靠真彩色白规避，深色屏幕仍有白框 | `MaskService.cs:48-69`、`:205-208`；`:360-364` | 承认的已知限制 |
| B6 | 画在标注之上的几何仍会盖住文字背景 | `:423`；`TextMaskRule.cs:410-415` | 承认 |
| B7 | **`MaskBringTextToFront=false` 对 Wipeout/紧贴路径无效**：两处 `PlaceMaskBefore` 无条件执行，文字仍被 Remove+Add、仍改句柄、仍挪到末尾 | `TextMaskRule.cs:290`、`:332`；`bringToFront` 只在 `:244`、`:412`、`:206` 用到 | 文档未写（`:472` 称「默认开」，暗示关掉就不动） |
| B8 | **编码自检只查本轮第一个 Wipeout**：`_encodingChecked` 置 true 后同图其余 Wipeout 不再自检 | `TextMaskRule.cs:1114-1124` | `:395` 称「编码自检不通过时直接抛出」，未说只查一次 |
| B9 | **`DimensionMaskMode` / `DimensionTextMode` 非法值静默归一**（非 Off 一律变 Native / All），与 §9.2「写别的值都是硬错误」原则相反，且 `RuleValidator` 无对应校验 | `TextMaskRule.cs:1011-1023`、`:1049-1062`；`RuleValidator.cs:176-251` | `:404-405` 只给了旧→新默认值 |
| B10 | §4:226 要求「`CapabilityProbe.WipeoutWriter` 必须验证 AutoCAD 认不认」未落地，探测仍只做 DWG 往返 + 几何 | `CapabilityProbe.cs:70-71`、`:104-213`；`:326` | 注释明确「不代表出图渲染验证」 |
| B11 | 表格 / 多重引线 / 动态匿名块文字不支持 | `README.md:773`；`Jun.Cad.Batch.json:170` | 承认 |

另有 §9.7 自己写的第二处库缺陷（字典条目改 HardOwnership 后仍会丢，`:470`），属 B1 的同源未修项。

**codebase-details 评审者的收敛结论：** 默认配置下存在 **6 个 Major 级真实功能 bug**（F1–F6），集中在绘制顺序开关失效、标注 XData 数据丢失、默认开启的失效路径仍报成功、重复运行绕过上限、已存在图层不可打印、动态块/嵌套属性漏遮；另有 8 个 Nit 级缺陷与 4 类文档漂移。

---

## 一、§5–§9 声称「已实施 / 已验证」逐条核对

| 声称 | 判定 | 证据 |
|---|---|---|
| §5.1 停止改写 `RectangleWidth` | **真实落地**，但描述过期 | 代码确实不碰 41/42：`MaskService.cs:121-146`；但最终签名是 `(MText,double)` 且写 `BackgroundColor`/`BackgroundScale`，不是文档说的 `(MText)`「只置 BackgroundFillFlags」 |
| §5.2 Wipeout 不可用 → 降级 Solid（`WipeoutWriteSupported=false`、`MaskSolidColor(255)`） | **纯文档自述**：全仓库 `src` 无这些符号 | 仅报告 `:242`、`:246` 有文字；`MaskService.cs` 全文无 `CreateSolid`/`MaskSolidColor`/`WipeoutWriteSupported` |
| §5.2 `MaskLayerPlottable` 默认 false→true | **真实落地**（§5.2 五条里唯一落地的） | `TextMaskRule.cs:1213`；`README.md:588` |
| §5.3 块内文字写进块定义 | **真实落地** | `TextMaskRule.cs:56`、`:493-519`、`:588`、`:595-596`、`:118`、`:690`；去重在 `DatabaseWalker.cs:116-187` |
| §5.4 计数改 `out MaskPath` | **真实落地** | `TextMaskRule.cs:141`、`:146-157`、`:1064-1071` |
| §5.5 「212 个测试 / TextMaskRuleTests 从 7 改 10 个」 | **文档自述**，与当前不符 | 编排层直接核验：`TextMaskRuleTests.cs` 现有 **40 个** `[Fact]/[Theory]`；`dotnet test` 当前 **301 total / 296 passed / 5 skipped** |
| §5.6 第 1 项 `Wipeout.SetMaskingArea` | **部分落地**：产品侧调用了，库层已核实 | `MaskService.cs:191`；外部克隆 `Wipeout.cs:50-86` 确有 `SetMaskingArea(IEnumerable<XY>)` |
| §5.6 第 2、3 项 | **残留 bug** | B4、B3 |
| §6 第 4 项「MTEXT 偏移因子仍是尽力而为」 | **已被 §9.6 推翻但未改** | `:282` vs `:453` |
| §7 复现材料（tmpv/、.workbuddy-ai/bgprobe/ 下全部 .lsp/.png/.txt/.py/.cs） | **存在**（评审者 Glob 未命中是因目录被 gitignore） | 编排层逐个 `test -e` 全部 `EXISTS`，`tmpv` 260 文件 / `bgprobe` 131 文件 |
| §8 csproj 改用本地克隆 | **真实落地** | `src/Jun.Cad.Batch.Core/Jun.Cad.Batch.Core.csproj:20`、`:4` |
| §8 「`MaskMode=Auto` 优先 Wipeout，探测不通过才降级 Solid」 | **与代码相反** | `TextMaskRule.cs:992-1009`、`MaskService.cs:235-271`、`TextMaskRule.cs:1114-1124` 均为「抛异常，无降级」 |
| §8 「215/215 通过」、ACadSharp 单体 2/2、`ssget` 回读 | **文档自述** | 库层 + 宿主运行，未核实 |
| §8.1 / 9.1 / 9.4 逐像素扫描（y=639 / y=358 / 90=0,2,3,1） | **文档自述**：数值只写在叙述里，产品代码无渲染级断言 | `CapabilityProbe.cs:70-71`；`README.md:919` |
| §8.2 真彩色白边框、`HideWipeoutFrame` | **真实落地** | `MaskService.cs:192`、`:205-208`、`:48-69`；`TextMaskRuleTests.cs:495-509` |
| §9.1 「手写 `DxfClassMap`、只放组码 69」、「建在 `new DimensionStyle("*override")` 上」 | **与代码不符** | 实际 `GetStyleOverrideMap()` → `map.DxfProperties[69]=fill` → `SetStyleOverrideMap()`：`TextMaskRule.cs:398-408` |
| §9.2 Solid 退役、`MaskMode` 只 Auto/Wipeout、非法值抛异常、编码自检失败抛异常 | **真实落地** | `TextMaskRule.cs:992-1009`、`:1114-1124`；`RuleValidator.cs:178-185`；`TextMaskRuleTests.cs:475-487` |
| §9.3 默认值表 7 行 | **大部分落地，但缺字段、缺契约、有两行反了** | `TextMaskRule.cs:43,45,46,50,54,55,56,474-478`；`TextMaskRule.cs:1011-1023,1049-1062` |
| §9.3 「MTEXT 写 `UseDrawingWindowColor`（bit1，不是 bit0）」 | **与代码、与 §9.6 相反** | 实际 bit0+bit1：`MaskService.cs:138-139`；`:445` |
| §9.3 「`DimensionMaskMode` 非 Off 时再加 DIMENSION」 | **字段名错** | 实际由 `dimMode`(`DimensionTextMode`) 控制：`TextMaskRule.cs:51,475-478` |
| §9.4 「291 passed / 端到端 195 处 / 结构扫描 347 total」 | **文档自述**，无命令、无基线 | 当前真实值 301/296/5 |
| §9.5 第一条「偏移因子尽力而为」 | **已失效陈述** | `:453` 明确「不复存在」；`MaskService.cs:143` 写 `BackgroundScale` |
| §9.5 「别的几何要靠 `SortEntitiesTable`」 | **与 §9.7 冲突** | `:423` vs `:470` |
| §9.5 「交互式会话观感未人工确认」 | **与 §9.6 冲突** | `:424` vs `:428`（用户已在交互式 AutoCAD 复查） |
| §9.6 bit0+bit1 + ACI 7 + 判重只看 bit0 | **真实落地** | `MaskService.cs:121-146`；`TextMaskRuleTests.cs:131-163` |
| §9.7 `MaskTextBox` Auto/Native/Tight、1.05 阈值、清原生背景、不动 41 | **真实落地** | `TextMaskRule.cs:53`、`:565-577`、`:285`、`:1029-1047`；`TextBounds.cs:553-586` |
| §9.7 占位实体重排、`BringToFront`、幂等 | **真实落地**（但开关语义有 B7） | `MaskService.cs:297-346`；`TextMaskRule.cs:1139-1197` |
| §9.7 「旧版文件重跑会自动升级：换紧贴遮罩」 | **过度概括**：只有框松（`RectangleWidth > tight×1.05`）才换；框贴文字走原生路径补 bit0 | `TextMaskRule.cs:565-577`；`MaskService.cs:131` |
| §9.7 「296 处遮罩（289+2+10）+13 跳过」、「测试 301/301」 | **数字自相矛盾**：289+2+10=301≠296；296+13=309；且与 291/215/212 无基线关系 | `:478`、`:481`、`:416`、`:325`、`:264`；`README.md:863`、`:1028` |

---

## 二、Prioritized Findings（Blockers → Major → Nits）

### Blockers

**[Blocker] §5 / §8 / §9.2 是三套互斥实现，文档没有声明以哪套为准** — [文档与代码漂移]
* **影响范围:** `textmask-failure-report.md:230-274`、`:310-329`、`:391-395`；`TextMaskRule.cs:992-1009`、`MaskService.cs` 全文
* §5.2 说 Wipeout 不可用、改走 2D Solid（`WipeoutWriteSupported=false`、Solid 降级、`MaskMode=Wipeout` 抛异常）；§8 说接入修复版 ACadSharp、Auto 优先 Wipeout、探测不过降级 Solid；§9.2 说 Solid 退役、Auto/Wipeout 同义、编码自检不过直接抛。当前代码**只对应 §9.2**，§5.2 的符号在源码里根本不存在，§8 的「降级 Solid」也没有。文档没有一处写明「§5/§8 已作废，唯一真源是 §9」。
* **要求:** 在 §5 与 §8 开头加显式作废声明，或把两份历史实现移出正文；给出「当前唯一有效实现 = §9.2 + 下列文件」的一句话清单。

**[Blocker] 全部验收结论没有可执行的复现命令，也没有基线文件** — [文档质量问题]
* **影响范围:** `:260-266`（212）、`:321-327`（215/215）、`:337-352`（逐像素）、`:411-418`（291/195）、`:478-481`（296/301）；`:286-306`（§7 复现材料）；`README.md:856-871`
* 没有一条 `accoreconsole.exe` 完整命令行、没有 `-PLOT` 参数、没有 LISP 加载方式、没有 `dotnet test` / `Jun.Cad.Batch.Cli run` 命令、没有 expected PNG / expected 统计 JSON / 像素断言脚本。逐像素数值只是叙述，不是断言。212 / 215 / 291 / 301 / 195 / 296 之间没有一份标注为最终基线。
* **要求:** 补一份 `VERIFY.md`，逐条给出命令 + 输入图路径 + 预期输出断言；把脚本/基线 PNG 纳入仓库或删掉 §7 的引用。

**[Blocker] §9.3 / §9.6 / §9.7 没有完整配置契约，也没有迁移步骤** — [文档质量问题]
* **影响范围:** `:397-409`、`:426-455`、`:458-481`；`BatchConfig.cs:663-780`、`RuleValidator.cs:176-251`、`ConfigLoader.cs:168-210`
* 缺三样东西。① **文件改动清单**：§9.3 说「默认值改成…」，但默认值实际落在两处且都不在 `RuleAction` 属性上——`TextMaskRule.cs:43-56` 的 `RuleContext.Bool(...,默认)` 和 `Normalize*()`，而 `BatchConfig.cs` 里 `MaskMode`、`MaskInsideBlocks` 等属性都没有初始化默认值。② **数据结构/取值域**：`MaskTextBox`（类型、Auto/Native/Tight、默认、1.05 阈值的依据）、`MaskBringTextToFront`（`bool?`、默认 true、句柄变化语义）、`DSTYLE` 覆盖的 XData 结构，文档都没有定义表。③ **迁移步骤**：旧配置里的 `MaskMode=Solid`、`MaskPadding`（绝对值）、`MaskSolidColorIndex`、`DimensionMaskMode=BlockInternal` 该怎么改、改错报什么、旧输出 DWG 怎么升级、能否回滚，正文一句没有。
* **要求:** 补字段契约表（字段名/类型/合法值/默认/落点文件:行号）+ 一份旧→新配置 diff + 旧输出图重跑的前置条件与回滚方式。

**[Blocker] §9.3 一行与代码、与 §9.6 直接相反，照抄会写出被 AutoCAD 判「无遮罩」的文件** — [文档与代码漂移]
* **影响范围:** `:409`、`:445-446`；`MaskService.cs:131-143`
* §9.3 写「MTEXT 写 `BackgroundFillFlags.UseDrawingWindowColor`（bit1，**不是 bit0** 的显式 RGB）」，§9.6 和代码写的都是 bit0+bit1（`90=3`）+ `BackgroundColor=ACI 7` + `BackgroundScale`。按 §9.3 实现就退回 §9.6 已证实「AutoCAD 当没有遮罩」的 `90=2` 缺陷。**这不是措辞差异，是会把功能改坏的指令。**
* **要求:** 删除 `:409` 该句或改写为「`90=3`，详见 §9.6」，并在 §9.3 标注「本节为默认值变更记录，标志编码以 §9.6 为准」。

**[Blocker] §9.7 / §8.2 三处未修但写成「已解决 / 已缓解」** — [真实功能 bug]（style 评审者）
* SORTENTS/块记录扩展字典写出丢失 → 每次保存重置用户绘制顺序（L470/L481 自认）；Wipeout 边框没关掉，只靠白实体色规避，深色屏仍可见（L360–364 自认）；`MaskSendHatchToBack` 默认仍开、仍执行、仍上报「填充置后 N」，但 L470 承认它从未生效。另有一处已修但证据不可信：MTEXT 背景 bit0 bug 是「仍需人工确认」那次交互式复查才发现的（L428–430），而 L279 早已写「已确认」。

### Major（真实功能 bug）

**F1 / B7 — `MaskBringTextToFront=false` 对最常见实体类型完全无效** — `TextMaskRule.cs:54,141,225,244,262-303,310-346,412`（尤其 `:290`、`:332`）；`MaskService.cs:297-300,321-346`
* `bringToFront` 只出现在 MTEXT 原生分支（`:244`）与 DIMENSION 分支（`:412`）；MTEXT 紧贴 Wipeout（`:290`）和 TEXT/ATTDEF/ATTRIB Wipeout（`:332`）**无条件**调用 `PlaceMaskBefore` → `ReorderToTop`（`MaskService.cs:330-345` 用占位实体把遮罩+文字推到集合末尾并分配新句柄）。因此设 `false` 时 TEXT/ATTRIB/ATTDEF、宽框 MTEXT 仍被重排、句柄仍变；而汇总 `:206-209` 与 `RuleSummary.cs:358` 按配置值决定是否显示「文字前置 N」，造成「日志说不前置、实际已前置」。附带幂等性问题：已带遮罩的实体重跑时被判重跳过，连 `BringToFront` 都不再调用，开关对已处理实体彻底失效。`TextMaskRuleTests.cs:351-362` 的「关闭前置时不挪动文字」只用 `RectangleWidth=0` 的 MTEXT（原生路径），没有覆盖 Wipeout 路径。
* **触发:** `MaskBringTextToFront=false` + 任意 TEXT/ATTDEF/ATTRIB，或 `RectangleWidth` 明显大于文字宽的 MTEXT。

**F2 — 标注 XData 两处数据破坏** — `TextMaskRule.cs:396-408,431-452`；外部克隆 `ACadSharp/Entities/Dimension.cs:101-117,422-450,514-526`
* (1) `SetStyleOverrideMap` 先 `edata.Records.Clear()`（`Dimension.cs:517`，编排层已核实）清掉 `AppId.DefaultName`（即 ACAD）下**全部** XData 记录，再写入单一 DSTYLE 段。`GetStyleOverrideMap` 只取第一段控制串之间的键值对，ACAD 下该段之后或 DSTYLE 段之外的任何记录都不会回到 map，随后被 Clear 抹掉。`ApplyDimensionMask` 对每个待处理标注都调一次 → 静默删除用户数据。报告 `§9.1:382` 只核对了 DSTYLE 段内「原有 140 等键全部保留」，未提示段外 ACAD XData 会丢。
* (2) `HasDrawingBackgroundFill` 仅在 69==1 时返回 true（`:439-447`）；用户已有的 `DIMTFILL=0`（无填充）或 `2`（指定色）覆盖被判为「无遮罩」，随后 `:406` `map.DxfProperties[69] = 1` 无条件改写。
* **触发:** 带 `(1070 . 69) (1070 . 2)` 的标注；或 ACAD XData 含 DSTYLE 段之外记录的标注。**这是本次审查中唯一确认的用户数据丢失路径，优先处置。**

**F3 / B2 — `MaskSendHatchToBack` 默认 true 产出「成功计数却不改变输出文件」的假成功** — `TextMaskRule.cs:55,100-102,162-180,201-204,864-883`；`README.md:583,847,864`
* `PushHatchesToBack` 对每个 Hatch 调 `table.MoveToTop(hatch)` 并 `moved++`（`:877-878`），返回值累加进 `movedToBack`（`:178`），汇总打印「填充置后 N」（`:203-204`）。而 `README.md:583`、报告 `§9.7:470`、源码注释 `TextMaskRule.cs:856-860` 与 `MaskService.cs:305-307` 一致声明：SORTENTS 排序表经 ACadSharp 写出后 AutoCAD 读不回、文件里没写。`§9.4:417` 与 `README.md:864` 还把「填充置后 6」列为端到端成功指标。
* **触发:** 默认配置 + 输入 DWG 带可读 SORTENTS 排序表。

**F4 — `MaxMasksPerDrawing` 是「每次执行新增上限」，不是「单图上限」** — `TextMaskRule.cs:50,73,132-138,143,267,321,388`；`BatchConfig.cs:772-775`
* `created` 是 `Execute` 内局部变量（`:73`），每次执行归零，只统计本次 `ApplyMask` 返回 true 的数量；已有遮罩被判重跳过、不计入预算。对同一已处理文件重复运行，每轮预算全额恢复，每轮最多再加 `maxMasks` 个，直到全部文字都加上。
* **触发:** `MaxMasksPerDrawing=100` + 一张 1000 处文字的图，重复执行同一输出文件 10 次即加满 1000。

**F5 — 已存在且不可打印的 `_MASK` 图层不会被改回可打印** — `TextMaskRule.cs:1199-1220`；`LayerService.cs:24,34-38,112-114`（编排层已核实）
* `MaskLayer()` 用 `LayerService.EnsureLayer(doc, "_MASK", ...)`，该服务命中已存在图层直接 `return existing`，`PlotFlag` 只在新建图层时按 `action.LayerPlottable` 设置。`MaskLayer()` 随后**只在** `MaskLayerPlottable=false` 时把 `_layer.PlotFlag=false`；为 true（默认）时什么都不做。结果：图纸里已存在且被设为不可打印的 `_MASK` 图层，在默认配置下不会被改回可打印，Wipeout 落在不打印图层 → 出图没有遮罩，而汇总仍报「共为 N 处文字加了遮罩」。另外 `EnsureLayer` 读的是通用字段 `action.LayerPlottable` 而非 `MaskLayerPlottable`，两个字段语义重复且未文档化（形成反向不一致：`false` 会改已存在图层，`true` 却不会）。
* **触发:** 图纸已存在 `_MASK` 且 `PlotFlag=false` + 默认配置。

**F6 — 动态块匿名实例块文字零覆盖、嵌套 Insert 的 ATTRIB 全部漏遮** — `DatabaseWalker.cs:168-184`（`:181`，编排层已核实）；`TextMaskRule.cs:502-509,600-653`
* (a) `CollectFrom` 对 `nested.IsAnonymous || IsExternalReference(nested)` 直接 `continue`；`IsAnonymous` 是标志位判定 `Flags & BlockTypeFlags.Anonymous`（`BlockRecord.cs:160-165`），覆盖 `*U` 动态块匿名实例块、`*D` 标注缓存块、`*T` 表块等**全部**匿名块。`CollectTargets` 又对 `hit.Owner.IsAnonymous` 二次跳过（`:504`）。因此动态块可见几何里的文字永远拿不到遮罩，与「默认给所有文字加遮罩」冲突。此取舍在 `Jun.Cad.Batch.json:170` 有说明，但报告 `§5.3:251` 只列 `*D/*T`，枚举不完整。
* (b) ATTRIB 收集循环在 `hit.Depth > 0` 时直接 `continue`（`:609-612`）。ATTRIB 从不在 `BlockRecord.Entities` 里、只挂在 Insert 上，该循环是唯一收集入口，于是**嵌套块参照上的属性全部漏遮**，且此处无任何注释说明，报告也未提。
* **触发:** 文字位于动态块的 `*U` 匿名实例块内；或块定义 A 内嵌了带属性的块参照 B。

**[Major] §9.5 与 §6 保留了三处已被推翻的结论，实施者按它写代码会回退修复** — `:282`、`:420-424` vs `:453`、`:470`、`:428`
* ① `:282`/`:422` 说「MTEXT 偏移因子在宿主侧仍是尽力而为、用 90=2 时没有 45」，`:453` 说该限制「不复存在」；② `:423` 说「别的几何要靠 `SortEntitiesTable`」，`:470` 说 SORTENTS 路线「彻底不可用」；③ `:424` 说「交互式会话观感未人工确认」，`:428` 说用户已在交互式 AutoCAD 复查并发现问题。

**[Major] §5.1 / §9.1 的函数签名与实现细节是历史中间态** — `:234-238`、`:388-389`
* §5.1 说签名改成 `ApplyMTextBackground(mtext)` 且「只置 BackgroundFillFlags」，实际是 `(MText,double)` 并写 `BackgroundColor`/`BackgroundScale`；§9.1 说「手写 `DxfClassMap`、只放组码 69」「建在 `new DimensionStyle("*override")` 上」，实际是 `GetStyleOverrideMap` → 追加 69 → `SetStyleOverrideMap`（全 `src` 检索 `SetDimensionOverride` 与 `new DimensionStyle` 均零命中）。维护者照它改会破坏「追加不清空」的行为。

**[Major] §9.7「旧版文件重跑会自动升级为紧贴遮罩」是过度概括** — `:466`、`:446`；`TextMaskRule.cs:565-577`
* 只有 `RectangleWidth > tightWidth×1.05`（框松）才走「清原生背景 + 换紧贴 Wipeout」；框贴文字或 41=0 的 MTEXT 走原生路径补 bit0，不会换成 Wipeout。另外 `MaskTextBox=Tight` 时 `HasOverlappingWipeout` 命中会直接跳过，也不「升级」。

**[Major] §4:226 要求的「渲染级验收」未落地** — `CapabilityProbe.cs:70-71,104-213`；`README.md:919`
* `WipeoutWriter()` 仍只做 `DwgWriter.Write` → `DwgReader.Read` → 几何检查，类注释自己写「本身不代表出图渲染验证」。更根本的是（阶段 8 发现 3.1）`TextMaskRule` 从不调用 `CapabilityProbe.RunAll()`，probe 与生产执行是两套判据。

**[Major] 标准要求的文件改动清单、数据结构、测试断言在 §9 全缺，设计决策留给实现者** — `:397-481`
* 回答不了三件事：改哪个文件、改什么（没有字段契约、没有 `1.05` 阈值来源、没有 `1+ratio` 与 `height×ratio` 两套偏移公式的适用边界）、怎么验（没有一条测试断言内容，只有测试**数量**）。按 Zero-Decision Rule，`1.05`、`0.15`、`45/90/63 成套`、`占位实体` 都是结论不是决策依据。

### Nit（真实功能 bug / 文档问题）

| # | 发现 | 证据 |
|---|---|---|
| F11 / B8 | `_encodingChecked` 只让第一个 Wipeout 受检，语义与「逐实体自检」宣传不符（所有 Wipeout 由同一构造路径产生，实际触发概率低） | `TextMaskRule.cs:1087,1114-1124`（编排层已核实）；`MaskService.cs:235-271` |
| F12 | 代码没有「文字为空则跳过」的判断：空 MTEXT + 非零 DXF 41 会置原生背景；`MaskTextBox=Tight` 更会写出面积≈0 的 Wipeout | `TextBounds.cs:266-283,527-542,553-587`；`TextMaskRule.cs:548-583`；`MaskService.cs:154-166` |
| F13 | `HasOverlappingWipeout` 用 AABB「中心 + Width/Height」判重，旋转角完全不参与；同位置 0° 与 90° 文字会互相误判为「已有遮罩」 | `TextMaskRule.cs:1139-1197`；`TextBounds.cs:423-458,493-497` |
| F14 | `MaskTextBox=Auto`（默认）用自述「误差 ±10%」的估测宽度配 1.05 阈值，阈值附近分支随机翻转，两份近似 MTEXT 得到两种明显不同的遮罩 | `TextBounds.cs:522-525,625-639`；`TextMaskRule.cs:569`；报告 `§9.7:465` 未提误差 |
| F15 | `Bounds2d.IsValid` 只查 `MinX`/`MaxY` 的 NaN，`MinY`/`MaxX` 为 NaN 或 ±∞ 时仍判有效；`NaN <= 0` 为 false 会绕过后续检查，写出坏 Wipeout | `TextBounds.cs:25-28`；`MaskService.cs:169-172` |
| F16 | `PlainTextOf` 在 `TryGetDimensionTextAnchor` 返回 true 后无条件 `foreach (Entity child in dim.Block.Entities)`，而该函数在 `dim.Block == null` 时也可返回 true → `NullReferenceException`（当前无调用点，属 public 辅助方法潜在缺陷） | `TextBounds.cs:666-696`、`:369-414` |
| F17 | `ReorderToTop` 的 `if (first != null && first.Owner == space.Owner)` 近乎恒 false（`space.Entities` 内实体的 `Owner` 指向 BlockRecord 本身，`space.Owner` 是块表/文档），`Remove(first)` 实际从不执行；当前无功能影响 | `MaskService.cs:321-346`（`:331`）；`CadObjectCollection.cs:36-38,61-67` |
| F8 | `MaskIgnoreBlockBounds` XML `<summary>` 字面写「默认 false = 不参与」，与属性有效默认 `true` 字面相反；同文件 `[RuleField] Help` 是对的。行为一致，属 IntelliSense 措辞漂移 | `BatchConfig.cs:708` vs `:713`；`TextMaskRule.cs:45` |
| F9 | 同一份文档对 `MaskSendHatchToBack` 给出互斥结论（「已被取代/从未生效」 vs 「62 个填充被置后」「填充置后 6 当成功指标」） | `README.md:583` vs `:847,:864`；报告 `§9.5:423` vs `§9.7:470` |
| F10 | `§5.3:251` 把跳过范围写成「`*D`/`*T` 匿名块」，实际 `IsAnonymous` 标志位覆盖 `*U` 动态块实例块在内的所有匿名块，枚举不完整 | `DatabaseWalker.cs:181`；`ACadSharp/Tables/BlockRecord.cs:160-165` |
| F7 | §5.2 的 `WipeoutWriteSupported`/`MaskSolidColor`/`CreateSolid` 全 `src` 零命中；`§5.2:244` 仍写「MaskMode=Wipeout 直接抛」，与当前代码（接受 Wipeout）和 `§9.2:395` 冲突 | 全 `src` 符号检索；`TextMaskRule.cs:992-1009` |
| — | §9.3 L407 把加 `DIMENSION` 的开关写成 `DimensionMaskMode`，实际是 `DimensionTextMode` | `TextMaskRule.cs:51,475-478`（编排层已核实） |
| — | §9.1 L384「不改句柄」在默认配置下为假：`MaskBringTextToFront` 默认开，标注整体前置 → 新句柄 | `TextMaskRule.cs:54,311-314,412-415`；`MaskService.cs:309`；`README.md:591` |
| — | 主观质量判断与冗余：「那不是遮罩，是另一块脏」「静默降级比报错糟得多」「纯属幸运」「干净切断」不可测量；「不能改 RectangleWidth」在 5 处反复出现 | `:393,:395,:471,:238`；`:236,:238,:279,:422,:465` |
| — | 删除线 + 新结论就地覆盖（L270/L279/L280/L453）让同句含两个互斥事实；该删的 §5.2、§9.5 三条限制、L282 却原样留着 | 报告 + `README.md:850,871` |
| — | 数字不自洽：13+8=21≠26（差 5 无交代）；289+2+10=301≠296；分项和 314、遮罩和 301 | `:455,:478` |

### 前序误报（明确撤销）

| # | 误报 | 裁定 |
|---|---|---|
| F18 | 「退役字段 `MaskSolidColorIndex`/`MaskPadding` 仍被校验」与「`Auto`/`Wipeout` 完全同义」被当 bug | **不是缺陷，是有意设计**：`README.md:596` 明写 "Validation does not silently discard or migrate the old data"，`ConfigLoader` 保留 `Action.Extra` 往返；`RequireNativeMaskMode`（`:992-1009`）与 `RuleSummary`（`:342-344`）都刻意让两者同义。报告 `§9.2:395`、`Jun.Cad.Batch.json:175` 均已文档化。 |
| F19 | 「§7 复现材料 / bgprobe 证据全部不存在」 | **不成立**：编排层逐个 `test -e` 全部 EXISTS。评审者用的是仓库 Glob，这些目录被 `.gitignore` 排除。 |
| — | 「`MaskIgnoreBlockBounds` 注释默认值写反导致功能错误」 | **降级为 Nit 措辞漂移**，无功能错误（阶段 8 发现 4.3）。 |
| — | 疑点 2「默认配置下仍用 0.05 阈值」 | **部分不成立**：`ComputeOcclusion` 在 `onlyOccluded=false` 时根本不调用（`TextMaskRule.cs:107`）；阈值与 0.25 权重只在显式打开 `MaskOnlyWhenOccluded` 时生效。 |

---

## 三、范围内源文件 / 符号映射核对

- 必读源码均已映射到真实代码并逐段核对：`TextMaskRule.cs`（1344 行）、`Mask/MaskService.cs`（348 行）、`TextBounds.cs`（701 行）、`DatabaseWalker.cs`、`EntityScanner.cs`、`Preflight/CapabilityProbe.cs`、`DimStyleService.cs`（无 DIMTFILL 实现，与报告 §9.1「实现在规则本体」一致）、`Config/BatchConfig.cs`、`Config/ConfigLoader.cs`、`Editing/RuleValidator.cs`、`Editing/RuleSummary.cs`、`Engine/LayerService.cs`、`Jun.Cad.Batch.Core.csproj`（`net8.0`、`Nullable` 关闭、项目引用外部 ACadSharp 克隆、嵌入默认配置）、`tests/Jun.Cad.Batch.Tests/TextMaskRuleTests.cs`、`DwgSampleTests.cs`、`TestPaths.cs`、`README.md`、`Jun.Cad.Batch.json`、`samples/text-mask.json`。
- 本地 ACadSharp 克隆真实存在：`ACadSharp.csproj:10` 已改为只编 `net8.0`（与报告 §8:329 一致）；`Entities/Wipeout.cs:50` 的 `SetMaskingArea(IEnumerable<XY>)` 存在且被 `MaskService.cs:191` 调用；`Entities/Dimension.cs:514-526` 的 `SetStyleOverrideMap` 实现如 F2 所述；`CadObjectCollection.cs:40` 确为 `HashSet<T>`（印证报告 §9.7:471）。
- 一处补充事实：`Jun.Cad.Batch.json:168` 的 TextMask 规则是 `"Enabled": false`，`samples/text-mask.json` 才是 `"Enabled": true`。报告 §9.3:397 的「默认给所有文字加遮罩」指的是 `Action` 各字段默认值，不是规则默认启用状态；二者不冲突，但容易被标题误导。

---

## 四、未核实项

- **AutoCAD 宿主 / 出图实验结果:** `§8.1:324,339-347`（accoreconsole `ssget` 计数、像素级扫描）、`§8.2:356-365`（`WIPEOUTFRAME` / 真彩色白边框 / `70=0` 在 AutoCAD 2025 不生效）、`§9.1:380-382`（`entget` 读回 DSTYLE、覆盖键 750→780）、`§9.6:450-451`（291 个 MTEXT `flags=3, scale=1.15`、标注 10/10 DIMTFILL=1）、`§9.7:478`（296 处遮罩）、`§5.5:265-266` / `§9.4:417-418` 的端到端统计——无 AutoCAD 会话、未运行渲染，**未核实**。代码侧只能确认 `MaskService.cs:196-208` 写真彩色白、`HideWipeoutFrame` 写 `70=0`。
- **SORTENTS 写出器前提:** 报告 §9.7:470 / `README.md:583` / 源码注释所称「ACadSharp 写出的块记录扩展字典 AutoCAD 读不到、SORTENTS 不落盘、存量绘制顺序保存时被重置」——未展开 `DwgObjectWriter.Objects.cs` / `DxfObjectsSectionWriter.cs` 逐行复核，该前提**依据项目记录，未独立复核写出器源码**。F3 中「计数 + `MoveToTop` + 默认 true」的代码事实已核实。
- **测试 / 编译经验结论:** 编排层已补跑 `dotnet test`，当前为 301 total / 296 passed / 5 skipped / 0 failed；但报告里 212 / 215 / 291 对应的是历史轮次，无法回溯复现，**历史数字未核实**。
- **`DimensionTextBackgroundFillMode.DrawingBackgroundColor` 是否等于 `DIMTFILL=1`**（代码只写枚举名，`TextMaskRule.cs:405`）——库枚举值，未核实。
- **`BackgroundColor = new Color(7)` 是否真被写出为 DXF 63=7、`BackgroundScale` 是否真被写出为 45=1.15**——库写出器行为，未核实。
- **占位实体重排改变的是内存 HashSet 枚举顺序，是否等于 DWG 写出后的实体顺序/宿主绘制顺序**——库 + 宿主，未核实；且 §9.7 自己承认排序表写不出去，此假设是「绘制顺序已验收」结论的薄弱处。
- **§5.6 第 2 项块内 TEXT 偏移**是否仍在出图中复现——除「未见修正代码」外，未核实。
- **交互式会话观感:** `§8:335`、`§9.5:424` 报告自己也声明未做界面截图，未核实。

---

## 五、按实现就绪标准的结论

按 Zero-Decision Rule，报告 230..481 行把「已实施的修复」与「仍然没解决的」写得较完整，但存在三类系统性问题：

1. **真实功能 bug 未被报告识别**：F1（`MaskBringTextToFront=false` 对 Wipeout 无效）、F2（`SetStyleOverrideMap` 清空 ACAD XData / 改写已有 DIMTFILL）、F3（默认开启的失效路径仍报「填充置后 N」成功）、F4（上限可重复运行绕过）、F5（已存在非打印 `_MASK` 图层无法修复）、F6（嵌套 Insert 的 ATTRIB 漏遮）在 230..481 范围内均未被列为待修缺陷，部分还被当作已解决项。**这些直接回答「文字遮罩功能还有 bug」——有，且默认配置下可触发。**
2. **「现在存在」的符号与实现细节漂移**（F7）：`§5.2` 的新增符号、`§5.1` 的函数签名、`§9.1` 的两个实现细节都与当前代码不符，且 `§5.1`/`§5.2` 未被标注为已被 `§9.2`/`§9.6` 取代，读者按 5 节会写出错误代码。
3. **内部自相矛盾**（F9）与**边界/异常/可空处理遗漏**（F11、F12、F13、F15、F16、F17）：报告多处把判重、自检、遮挡、绘制顺序写成可靠保证，源码里却是「尽力而为」或一次性探测。

**优先处置顺序建议：F2（数据丢失）> F1 / F3 / F5（默认配置下静默产生错误输出或假成功）> F4 / F6 > F7（文档）> 其余 Nit。**
