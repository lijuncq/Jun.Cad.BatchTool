# 阶段 7：Critic Review 报告（Goldfish Review Report）

**Target:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md` (`FeatureRange 1..229`)
**Reviewers:** egm-design / egm-structural / egm-style
**Standards:** `feature-spec-standard.md`（design、structural）；style 按角色内建准则
**Mode:** REVIEW

## Reviewer Status

| Reviewer | Status |
| --- | --- |
| egm-design | completed |
| egm-structural | completed |
| egm-style | completed |

---

## 直接回答：文字遮罩功能还有 bug

**有。** 报告 1..229 行描述的四个根因，当前代码里 A 和 B 基本修掉，C 没修（只是用默认值绕过），D 是"靠上游 bug 兜住"而非真修；此外新引入/遗留了若干边界缺陷，且 1..229 的诊断结论已被同文件 8–9 节推翻。下表为设计评审者的收敛结论：

| 根因 | 报告 1..229 的说法 | 当前代码 |
|---|---|---|
| A Wipeout 写出不可用 | "这条路走不通，改用 2D Solid" | **已修**（库层 `SetMaskingArea`），但 §1–4 结论被 §8 推翻，文档自相矛盾 |
| B 块内文字无条件跳过 | — | **已修**（写进块定义），但动态块/嵌套属性/预览计数仍有缺口 |
| C 遮挡判定看不到块参照 | 提议改二值 + Insert 展开 | **没修**，只把默认翻成"全部加"绕过；开关一开缺陷全回 |
| D 绘制顺序只改集合顺序 | 提议进排序表 | **没按提议修**，靠"ACadSharp 保存时丢排序表"这个上游 bug 兜住，且会重置存量图纸顺序 |

---

## Prioritized Findings（Blockers → Major → Nits）

### 1. 1..229 的诊断与"当前状态"整体失效，无单一权威章节 — Severity: Blocker
* **Reviewer Lens:** egm-structural（F1）、egm-design、egm-style(#1/#2)
* **Affected Scope:** §0 (L12–14)、§1–§4 (L18–228) vs §5–§9 (L230–481)
* **归类:** [文档质量问题] / [文档与代码漂移]
* **Discrepancy Description:** "当前状态"被拆散进 0–9 节至少 7 处且互相矛盾。只看 1..229 的读者会得出 5 个全是错的结论：Wipeout 写不出、MTEXT 被折行、块内文字全跳过、22 个标注只活 1 个、推荐落地 2D Solid。逐对矛盾：§0 L12"遮罩加上了但根本不存在" vs §8.1 L347"擦除生效"；§3.1 L161"先做方案 2（Solid）" vs §9.2 L393"CreateSolid / MaskSolidColor 一并删掉"；§5.1 L236 签名 `ApplyMTextBackground(mtext)` vs 当前 `MaskService.cs:121–146`（两参数、置 flags + Color(7) + BackgroundScale）；§5.2 整节 vs §9.2；§3.7 待验证问题答案在 §6/§9.1；§4 L224"排序表可查" vs §9.7 L470"排序表路线彻底不可用"；§5.5/§8/§9.4/§9.7 四个互斥的测试总数 212/215/291/301；§9.5 L422"偏移因子尽力而为" vs §9.6 L453"不复存在"。§6 标题"必须承认的未验证项"下 4 项里 3 项被划掉改成"已验证"。
* **Rationale & Recommendation:** 标准 Core Principle 要求陌生读者能正确理解特性现状。文件顶部加"当前状态"横幅，§1–4 标注为"历史诊断，已被 §8/§9 取代"；删除旧句而非用删除线留着。

### 2. 验收标准不可自动化复现，且第 6 条未实施 — Severity: Blocker / Major
* **Reviewer Lens:** egm-design、egm-structural（F22）
* **Affected Scope:** §4（216–226 行）、`CapabilityProbe.cs:104-213`
* **归类:** [文档与代码漂移] / [真实功能 bug]
* **Discrepancy Description:** §4 把 5 条写成"缺一条都算没修好"的硬门槛：第 1、4、5 条依赖 `accoreconsole` 出图 + 逐像素 PNG 扫描，材料是 `tmpv/`、`bgprobe/` 下的一次性脚本，**不在 `tests/` 里**。`TextMaskRuleTests.cs` 全部用例都是内存 `CadDocument` 断言，没有 `DwgWriter.Write`/`DwgReader.Read`、没有 PNG、没有宿主探测。§4 L226 要求"`CapabilityProbe.WipeoutWriter` 必须验证 AutoCAD 认不认（`ssget` 计数）"，当前实现只做 ACadSharp 自己的往返，检查 clipping/顶点数/IMAGEDEF/WCS 包围盒（132–190 行），从不调用 AutoCAD；文件头注释 `CapabilityProbe.cs:70-71` 自己承认"本身不代表出图渲染验证"。L224 要求"（排序表可查）"本身不可达，因为排序表根本不落盘。
* **Rationale & Recommendation:** 把 5 条拆成"可自动断言"（实体数、组码 90/45/63、集合顺序）与"宿主渲染门禁"（PNG 对比）两类；后者标人工项并给出脚本入库路径与基线图片。要么明确"库层往返 ≠ 宿主验收"。

### 3. 遮挡判定 0.05 / 0.25 权重仍在，根因 C 未修 — Severity: Major
* **Reviewer Lens:** egm-design、egm-structural（F20）、egm-style(#9)
* **Affected Scope:** 报告 line 110、192；`TextMaskRule.cs:43-44, 836-839, 892, 895-943`；`BatchConfig.cs:702-705`
* **归类:** [真实功能 bug] + [文档与代码漂移]
* **Discrepancy Description:** 文档称这是"拍出来的权重"并提议改二值相交。核对：`line 44` 阈值仍是 `0.05`；`line 942` 仍是 `crossings * 0.25`；`line 939` 仍用包围盒面积比（长斜管虚高/漏判原样保留）；`line 836-839` 在默认 `useBlockBounds=false` 时把 `Insert` 整类排除。§3.4 的四条修改一条都没落地。真正变的是 `TextMaskRule.cs:43` 默认 `MaskOnlyWhenOccluded=false`，使 `ComputeOcclusion` 在默认配置下根本不调用（`line 892` 注释自认）。用户一旦打开该开关，旧缺陷全部复现。
* **Rationale & Recommendation:** 要么删掉该开关和这套启发式，要么按 §3.4 实现"几何相交二值 + Insert 展开"；不要留一个可达但明知有错的分支。

### 4. "填充置后"是默认开的静默空操作，还报成功计数 — Severity: Major
* **Reviewer Lens:** egm-design、egm-structural（F23）、egm-style(#1 结论)
* **Affected Scope:** `TextMaskRule.cs:55, 176-180, 201-204, 856-883`；`BatchConfig.cs:738`；README:583/864
* **归类:** [真实功能 bug]
* **Discrepancy Description:** `MaskSendHatchToBack` 默认 true，`PushHatchesToBack`（`:864-883`）只在有 `SortEntitiesTable` 时调 `MoveToTop` 并累加计数 `movedToBack`（L874–882），汇总里输出"填充置后 N"（`:201-204`），但 SORTENTS 表写不进 DWG（§9.7 L470、`MaskService.cs:305-307`），文件里没有任何顺序变化。源码 `TextMaskRule.cs:856-860` 已承认失效但保留开关。§4 验收第 5 条"填充在文字之下"因此无法判定。默认 true 还让 `onlyOccluded=false` 时仍建整空间 `BoundsIndex`（line 100），纯性能损耗。
* **Rationale & Recommendation:** 删掉计数与默认开关，或降为一行日志并注明"不产生文件变更"；把验收 5 改成"文字在填充之上（集合顺序可查）"。

### 5. 靠上游"保存时丢排序表"兜住绘制顺序，会重置用户已有 DRAWORDER — Severity: Major
* **Reviewer Lens:** egm-design、egm-structural（F24）、egm-style(#4)
* **Affected Scope:** 报告 line 112–116、196–202、470、481；`MaskService.cs:297-346, 305-308`
* **归类:** [真实功能 bug] / 架构风险
* **Discrepancy Description:** 根因 D 说有排序表时绘制顺序由排序表决定；§3.5 要求"遮罩和文字都要有明确排序值 / 把文字从排序表里移除"。当前实现完全不碰 `SortEntitiesTable`，只做集合末尾追加（占位实体技巧）。它之所以"生效"，依赖 §9.7 line 470 承认的事实：ACadSharp 写出的块记录扩展字典 AutoCAD 读不到，`ACAD_SORTENTS` 在每次保存时丢失——**即靠一个上游数据丢失 bug 把排序表抹掉，集合顺序才成为权威**。两个后果：(1) 存量图纸里用户手工排的绘制顺序在批量保存时被整体重置（README:581 只写 "moved entity gets a new handle"，没把破坏性副作用写进参数说明）；(2) 上游 issue #897 修好排序表序列化后，带排序表的图上文字仍在表里、新遮罩不在表里，现有技巧立刻失效。1..229 范围内没有任何缓解方案。
* **Rationale & Recommendation:** 把"排序表会被清空"写成显式需确认的破坏性副作用；上游修好前，输入图含 `SORTENTSTABLE` 时告警或拒绝，不要静默处理。

### 6. 动态块匿名实例块文字一个遮罩都加不上 — Severity: Major
* **Reviewer Lens:** egm-design、egm-structural（F11）
* **Affected Scope:** §3.3 line 183、251；`DatabaseWalker.cs:181`；`TextMaskRule.cs:502-509`；README:586/594/654/773
* **归类:** [真实功能 bug] + [文档与代码漂移]
* **Discrepancy Description:** §3.3 承诺"动态块要同时处理匿名实例块与 `DynamicBlockTableRecord`……可以直接复用 `BlockLayerChangeRule.MoveDefinitionContent` 的去重逻辑"。当前实现没照做：`DatabaseWalker.cs:181` 在遍历阶段就 `nested.IsAnonymous || IsExternalReference(nested)` 直接不入队，动态块的 `*U` 匿名实例块根本不产生 `ScanHit`；`TextMaskRule.cs:502-509` 再兜一层跳过匿名/外参/布局块。README 四处已把 "dynamic anonymous-block text" 列为 unsupported——等于 §3.3 的承诺被撤回却没回写。动态块在海工/机械图里是常见场景。
* **Rationale & Recommendation:** 回写 §3.3 为"动态块不支持"，或实现匿名块遍历；把 xref 的跳过理由写成显式 Non-Goal（区分"不该改"与"没能力改"）。

### 7. DIMTFILL 覆盖会改掉已有 69 项，且可能抹掉其它 ACAD XData — Severity: Major
* **Reviewer Lens:** egm-design
* **Affected Scope:** 报告 line 358、384、396；`TextMaskRule.cs:398-408, 431-452`；`ACadSharp.../Dimension.cs:422-464, 514-526`；`TextMaskRuleTests.cs:673-706, 727-756`
* **归类:** [真实功能 bug] + 未核实
* **Discrepancy Description:** (a) `line 406` 用 `map.DxfProperties[69] = fill` 索引器赋值。`HasDrawingBackgroundFill`（`:431-452`）只在 `69 == DrawingBackgroundColor(1)` 时返回 true 才跳过；用户已设 `DIMTFILL=2`（DIMTFILLCLR 指定色）或 `=0` 会被静默改成 1。报告"只改 DIMTFILL 一项 / 原有键全部保留"对 69 这个键不成立。测试只覆盖"已有 =1 跳过"和"77 保留"，没有 =2/=0 用例。(b) `SetStyleOverrideMap`（`Dimension.cs:514-526`）先 `edata.Records.Clear()` 再重建，清的是整个 `AppId.DefaultName`("ACAD") 扩展数据，不只是 DSTYLE 段；`GetStyleOverrideMap`（`:422-464`）在 DSTYLE 头不是第一条记录时返回 null，此时规则新建 map 写回，会抹掉该标注上其它 ACAD XData。(c) 父样式继承：代码没碰 `dimension.Style`，但 §9.1 没有任何 `entget` 回读证据，测试夹具 `DimensionCase` 甚至没给 `dim.Style` 赋值 → **未核实**。
* **Rationale & Recommendation:** 判重改成"69 已存在即跳过（不论取值）"；把 `SetStyleOverrideMap` 包一层只重建 DSTYLE 段；补带父样式的 `entget` 回读。

### 8. 默认值整体翻转但无配置版本迁移 — Severity: Major
* **Reviewer Lens:** egm-design
* **Affected Scope:** §9.3 line 399-407；`BatchConfig.cs:13-15, 697-700, 773`；`ConfigValues.cs:15-28`；`ConfigLoader.cs:34`
* **归类:** [真实功能 bug] / [文档质量问题]
* **Discrepancy Description:** 六个默认值被翻转（`MaskOnlyWhenOccluded` true→false、`MaskInsideBlocks` 新增 true、`MaskRebuildText` false→true、`DimensionMaskMode` BlockInternal→Native、`DimensionTextMode` ExplicitOnly→All、`MaxMasksPerDrawing` 2000→0）。字段都是 nullable，`ConfigValues.cs:15-28` 语义是"null 取调用方默认"，`ConfigLoader.cs:34` 用 `MissingMemberHandling.Ignore`，`BatchConfig.cs:13-15` 的 `Version` 固定 1 且 loader 无按版本迁移代码。因此旧配置只要没显式写这些字段，升级后会静默变成"整张图所有文字加遮罩 + 重建文字改句柄 + 改块定义影响所有插入 + 标注测量值也加 + 无数量上限"。`samples/text-mask.json` 和 `Jun.Cad.Batch.json` 把值钉死了，但那只是样例。
* **Rationale & Recommendation:** 走 `Version` 迁移，或在 `ConfigLoader` 对缺失的关键开关补显式迁移 + 警告；README 列出"旧配置升级后的行为变化清单"。

### 9. 已有 `_MASK` 图层不可打印时不会被纠正 — Severity: Major
* **Reviewer Lens:** egm-design
* **Affected Scope:** 报告 line 245；`TextMaskRule.cs:1199-1220`；README:588
* **归类:** [真实功能 bug]
* **Discrepancy Description:** `MaskRuntime.MaskLayer()` 只在 `MaskLayerPlottable=false` 时设 `_layer.PlotFlag=false`（line 1213-1217），默认 true 时什么也不做，不会把已存在的非打印图层改回可打印。§5.2 line 245 记录过"默认从 false 改成 true"，即更早版本产出过带非打印 `_MASK` 图层的 DWG。用当前代码重跑这些文件，遮罩仍不出现在出图里，且无日志提示。
* **Rationale & Recommendation:** 默认 true 时显式 `_layer.PlotFlag = true`，或检测已存在且不可打印时告警。

### 10. `MaxMasksPerDrawing` 为正数时重复运行不幂等 — Severity: Major
* **Reviewer Lens:** egm-design
* **Affected Scope:** 报告 line 479；`TextMaskRule.cs:73, 132, 267, 321`；`tests:364-379`；`BatchConfig.cs:773`
* **归类:** [真实功能 bug]
* **Discrepancy Description:** 默认（0=不限）时判重成立：TEXT/ATTRIB/紧贴 MTEXT 用 `HasOverlappingWipeout`（`:1139-1197`），MTEXT 原生用 bit0（`MaskService.cs:131`），DIMENSION 用 `HasDrawingBackgroundFill`。**但配了正的上限就不成立**：`created` 每次 `Execute` 重置为 0（line 73），line 132 判断 `created >= maxMasks`，而"已有遮罩"走 `MarkSkipped`、不计入 `created`。于是同一文件每跑一次就新加上限条数的遮罩，直到全部文字遮完。字段叫"单图遮罩上限"、README:589 说 "limits masks"，实际是"单次执行上限"。
* **Rationale & Recommendation:** 上限判定改成"空间内已有遮罩数 + 本次新建数"，或把字段/文档改名为"每次运行上限"。

### 11. Wipeout 编码自检只跑一次且检测项不全 — Severity: Major
* **Reviewer Lens:** egm-design
* **Affected Scope:** §9.2 line 395；`TextMaskRule.cs:1100-1129`；`MaskService.cs:235-271`
* **归类:** [真实功能 bug] + 未核实
* **Discrepancy Description:** `MaskRuntime.CreateWipeout`（line 1100-1129）用 `_encodingChecked` 标志，**只在第一个 Wipeout 上跑一次**（1114-1124），之后所有遮罩不再校验；`IsWipeoutEncoded`（`MaskService.cs:235-271`）只查 ClippingState / Definition / 顶点数 / WCS 包围盒，**不查顶点归一化、`Size`、`UVector`/`VVector` 一致性**。测试 `tests:854-863` 只直接调 `IsWipeoutEncoded`，没有测"CreateWipeout 抛异常"这条真实执行路径。`MaskMode` 非法值（`RequireNativeMaskMode`, `:992-1009`）与 `MaskTextBox` 非法值（`NormalizeMaskTextBox`, `:1029-1047`）硬报错已落实。`MaskPaddingRatio` NaN/∞ 在 `NormalizePaddingRatio`（`:973-986`）静默回退 0.15，CLI 加载路径是否调用 editor 校验 — **未核实**。

### 12. `MaskBringTextToFront=false` 对 Wipeout 路径不生效 — Severity: Nit
* **Reviewer Lens:** egm-design
* **Affected Scope:** `TextMaskRule.cs:206-209, 244-247, 290, 332`；`tests:352-362`
* **归类:** [真实功能 bug：计数/预览与执行路径不一致]
* **Discrepancy Description:** MTEXT 原生分支用 `if (bringToFront)` 包住 `BringToFront`（244-247），但 MTEXT 紧贴（290）与 TEXT/ATTRIB（332）的 `PlaceMaskBefore` 无条件调用，同样 Remove+Add → 换句柄、挪到集合末尾。测试只验证原生路径"保持原位"。关掉前置并不能阻止 Wipeout 文字被重排，配置语义与实现不一致；汇总 line 206 在关闭时不报"文字前置"，与实际发生的重排不符。
* **Rationale & Recommendation:** 统一所有分支的开关语义，或把文档/汇总改成"仅 MTEXT 原生路径受控"。

### 13. 其它边界：空文字 / 旋转文字 AABB / 嵌套属性 — Severity: Nit
* **Reviewer Lens:** egm-design
* **Affected Scope:** `TextBounds.cs:266-323`；`TextMaskRule.cs:549, 575, 609`；`TextBounds.cs:463-490, 553-619`
* **归类:** [真实功能 bug]（前两项）
* **Discrepancy Description:** 空图纸 `TextMaskRule.cs:67-71` targets==0 直接返回，OK。空文字：`TextBounds.ForMText` 空值 → `maxLine=0`，`RectangleWidth=0` 时 `width=0` → `TextMaskRule.cs:549` 跳过；**但若该空 MTEXT 有非零 `RectangleWidth`**，`TextBounds.cs:283` 取框宽作宽，会生成空文字的遮罩实体。§9.6 L455 那 13 个"空文本 {}"没加遮罩只是恰好框宽为 0，不是逻辑保证。旋转文字已修（`TextBounds.cs:463-490`、`MaskService.cs:175-194`、`tests:838-852`），但 `MaskTarget.Rect` 仍是 AABB（`:575`），用于 `HasOverlappingWipeout` 与遮挡/填充查询：两段不同角度但 AABB 相近的旋转文字会被误判"已有遮罩"而跳过其一。嵌套块：`CollectTargets` 第二段属性扫描 line 609 对 `hit.Depth>0` 直接 `continue`，**嵌套插入上的 ATTRIB 不处理**。预览 `BuildReferenceCounts`（`:694-724`）只数直接引用，嵌套块实际受影响实例数是各层插入数乘积，低估影响面，§3.3 line 185 要求的"写明将影响 N 个块参照"没达到。

### 14. 结构：标题层级合格，但必备章节缺失且"四个根因"下有 5 个 H3 — Severity: Major / Nit
* **Reviewer Lens:** egm-structural（F2/F3/F5）
* **Affected Scope:** 1..229 全局
* **归类:** [文档质量问题]
* **Discrepancy Description:** H1→H2→H3 连续，无跳级、无 H4，编号 0–4 与 3.1–3.7 无乱序；表格 4 处格式一致、代码块 3 处全部闭合。但对照 `feature-spec-standard.md`：Goals & Non-Goals 无、Proposed Architecture 无（§3 是补丁清单，没有组件布局/数据流/生态集成）、Risks & Mitigations / Risks & Limits 无（最接近的 §6 在 L276，**范围外**）、Problem Statement 无独立命名章节。Alternatives Rule 只有 §3.1 列了 3 条方案；§3.2–§3.7 全是单路径，尤其 §3.5 没评估"库写不出排序表时怎么办"——而这恰恰是后来实际发生的。H2「四个根因」下却有 5 个同级 H3（根因 A/B/C/D + 附带缺陷 L118），与 L14「四个独立缺陷」冲突。L136 代码块无语言标记；范围内 0 个 Markdown 链接，所有 `文件:行号` 与「见第 8 节」不可跳转；§9 用 `*` 而 1..229 用 `-`，全文档列表符号不一致。
* **Rationale & Recommendation:** 该标准是"实现前的高层特性规格"模板，本文是"事后调查 + 实施日志 + 修复方案"混合体，类型本身不匹配；补状态章节与 Non-Goals，把裸引用改成锚点链接。

### 15. 代码引用逐条核验：9 条带行号引用全部失效 — Severity: Major
* **Reviewer Lens:** egm-structural（F8–F22）、egm-style(#8)
* **Affected Scope:** L46/59/80/85/98/100/114/154/165 等
* **归类:** [文档与代码漂移]
* **Discrepancy Description:**
  - L46 `MaskService.cs:137` → 现为空行；`CreateWipeout` 在 `:154`（Bounds2d）与 `:175`（IEnumerable<XY>），实现改为 WCS 四角 + `SetMaskingArea`（`:191`）。
  - L59/L165 `MaskService.cs:76`、`:76-101` → 现为 `MaskOffsetFactor` XML 注释；`ApplyMTextBackground` 在 `:121-146`，L102 明写"绝不碰 RectangleWidth"。
  - L80 `Rules/TextMaskRule.cs:72` → 该文件 1345 行内没有 `CapabilityProbe` 或 `WipeoutWriter`；实际在 `CapabilityProbe.cs:104`，由 `Program.cs:929`、`PreflightDialog.cs:123` 调用。规则内把关是 `MaskService.IsWipeoutEncoded`（`TextMaskRule.cs:1117`）。
  - L85/L176 `TextMaskRule.cs:433` → 现为 `HasDrawingBackgroundFill` 里的 `if (!dimension.HasStyleOverride)`；`Depth > 0` 处理在 `:493-519`，按 `MaskInsideBlocks`（`:56` 默认 true）判断。L93「`IncludeNestedBlocks` 打开也没用」双重失效——实际控制项是 `MaskInsideBlocks`（`BatchConfig.cs:727`），`IncludeNestedBlocks`（`:300`）只管扫描递归。
  - L98 `TextMaskRule.cs:55` → 现 `:45`（`useBlockBounds` 语句），纯 10 行漂移；`:55` 现为 `sendHatchToBack`。
  - L100 `TextMaskRule.cs:600` → 现为注释；判断实际在 `:836-839`。且当前默认 `MaskOnlyWhenOccluded=false`（`:43`），`ComputeOcclusion` 默认不调用（`:893` 注释明说），L104–110 的"22 个标注只活 1 个"在默认配置下已不再发生。
  - L114 `MaskService.cs:180` → 现为 `CreateWipeout` 里 `XY[] points = vertices.ToArray();`；`PlaceMaskBefore` 在 `:297-300`，只调 `ReorderToTop`（`:321-346`），实现改用占位实体；注释 `:279-291` 说明旧三步法因 HashSet 槽位复用失效。§3.5 L201「sort handle 取文字 − 1」从未实现。
  - L154 `TextMaskRule.cs:103`（`MaskLayerPlottable` 默认 false）→ 现 `:103` 为空行，实际使用点 `:1213` 默认已是 **true**（`BatchConfig.cs:748`）。
  - 无行号但失效的符号：`CreateSolid`、`MaskSolidColor(...)`、`WipeoutWriteSupported` 在全 `src` grep 无命中；仅 `RuleValidator.cs:191-194` 保留 `MaskSolidColorIndex` 废弃告警。
  - L118-120 / L206 "旋转信息已丢" → 已修：`TextBounds.RotatedRectVertices`（`:463-490`）、`TextMaskVertices`（`:553-619`）、`MaskTarget.MaskVertices`（`TextMaskRule.cs:1228`）、`MaskService.CreateWipeout(IEnumerable<XY>)`（`:175-194`）；`TextMaskRuleTests.cs:838` 锁定该行为。
* **Rationale & Recommendation:** L84–91、L97–102 两个 `csharp` 块是"当时源码摘录"，内容与当前源码已不一致，作为代码块呈现会误导读者以为是现行实现。逐条更新或删除。

### 16. 文风：绝对化断言、主观判断、含糊词 — Severity: Blocker / Major / Nit
* **Reviewer Lens:** egm-style
* **Affected Scope:** §0 L12/L14、§1 L46/L55/L61/L63-70/L114、§2 L78/L80/L82-93/L110/L114-120、§3.1 L149/L154/L161/L172、§3 L126/L169/L183/L190/L194/L200、§4 L226
* **归类:** [文档质量问题] / [文档与代码漂移]
* **Discrepancy Description:** "根本不存在""完全无效""它没有失效""这条路走不通""凡是……都是无效遮罩""唯一""探测一直通过""不看配置""只支持轴对齐""90° 遮罩永远是错的""彻底不可用"——均无可复现判据，且与 §8/§9 互斥。`CreateSolid`「是正确的」「没有编码陷阱」是主观质量判断且方法已不存在；L154"当前 MaskLayerPlottable 默认 false"不成立；L161「唯一」「长期必须靠它」与三种机制并存矛盾；L172「离线工具做不到」无判据、未核实。「全是白工」「比没加遮罩严重」「误判多」「碰巧对」「思路是对的」「项目已有这条经验」「可以直接复用」——无数据的主观/含糊措辞。实测数量表（62/22/318）、「5572 条」、「西文 0.7 上下、偏窄 10% 以上」均无复现材料；`TextBounds.cs:622-638` 的 `LatinWidthFactor=0.62` 与 L114 的"±10% 量级"不等于"10% 以上"，SHX 系数与排序表条数 **未核实**。「偏窄就会折行」把推测写成因果。
* **Rationale & Recommendation:** 用可复现判据替换绝对断言；删除主观评价；把推测明确标为推测。WIPEOUT / MTEXT / DIMTFILL / SORTENTSTABLE / CTB / 组码 41 / AABB 是通用术语，未按项目黑话报告。

### 17. 范围外（230–482）补充：被"已解决/已确认"盖住的真实问题 — Severity: Blocker / Major
* **Reviewer Lens:** egm-style（范围外条目）
* **Affected Scope:** §5.6 L270、§6 L279-282、§8 L333、§8.1 L351、§9.7 L470-471、§9.5 L422、§9.6 L453
* **归类:** [真实功能 bug] / [文档质量问题]
* **Discrepancy Description:** 三处删除线让同一句同时含两个互斥事实（"没解出来"+"已解出"、"本次没能确认"+"已确认"）。§6 L279「已确认（下午）：原生背景能干净切断」被 §9.6 推翻——交互式 AutoCAD 属性面板显示"否"，真因是只写 bit1、缺 bit0、45/63 不落盘；旧"已确认"未收回，用一句肯定断言盖掉实际 bug。§6 L282 / §9.5 L422「偏移因子仍是尽力而为 / 文件里根本没有 45」与 L453「不复存在」互斥且都无删除线，核对 `MaskService.cs:138-143`、`TextMaskRuleTests.cs:138-142,170-177` 锁 45=1.15 落盘，L282/L422 不成立。同一「线穿过遮罩」现象给了三种互斥根因：绘制顺序错误、几何编码错误、HashSet 槽位复用「纯属幸运」；「已解决」只覆盖第一种。§9.7 L470「SORTENTS 路线彻底不可用」同段又写「还有第二处缺陷，未继续深挖」「上游 issue #897 在跟」，而 `TextMaskRule.cs:867,877` 仍在访问 SortEntitiesTable 调 MoveToTop；「彻底」掩盖了存量绘制顺序每次保存被重置的未修数据丢失 bug。`MaskService.cs:48-69` 写 `DisplayImageFrame=false`，但 §8.2 L360 记 AutoCAD 2025 重开仍报 `WIPEOUTFRAME=1`；现靠真彩色白框规避（`:196-208`），§8.2 L364 / §9.5 L424 承认深色屏幕仍可见白框、交互式观感未人工确认。
* **Rationale & Recommendation:** 删除旧句而非划掉留着；所有"已解决/已确认"必须附可复现判据与证据路径。

---

## 仍有效的引用（对照，说明不是全盘失效）

- `TextBounds.MeasureWidth`（L61）仍在 `TextBounds.cs:625`，`LatinWidthFactor=0.62`（L120）、`WideWidthFactor=1.0`（L123）；只是已不再用于补 `RectangleWidth`。
- `MaxMasksPerDrawing`（`BatchConfig.cs:774` + `TextMaskRule.cs:50/132`，0=不限）、`MaskPaddingRatio` 默认 0.15（`MaskService.cs:31`）、`MaskInsideBlocks`（`BatchConfig.cs:727`）、`MaskTextBox`（`:685`）、`MaskBringTextToFront`（`:694`）、`MaskMode` 仅 Auto/Wipeout（`BatchConfig.cs:666`、`TextMaskRule.cs:992`）：§9.3 默认值表基本准确。
- `ApplyMask` 用 `out MaskPath` 报真实分支（§5.4 L258）：`TextMaskRule.cs:226`、`MaskPath` 枚举 L1065、汇总 switch L145–157，已落地。
- §8.1 / §9.1 的组码结论（`340=0/360=0/280=1/71=2/91=5`、DSTYLE 组码 69、`(90 . 3) (63 . 7) (45 . 1.15)`）与 `MaskService.cs:138-143`、`TextMaskRule.cs:404-408`、`CapabilityProbe.cs:170-184` 一致。
- 当前 `TextMaskRuleTests.cs` 有 40 个 `[Fact]`/`[Theory]` 方法（L132–L855），§5.5 L264「从 7 个改写为 10 个」已失效。

## 明确"未核实"清单（读到证据不足，不猜）

1. 块参照**非等比缩放 / 镜像**插入时，Wipeout 裁剪路径在 AutoCAD 2025 里是否仍正确渲染。文档 §3.3 L178–185 把"所有插入共享一次"与"支持非等比缩放/镜像"对立，**这个推断是反的**：遮罩写进 `BlockRecord.Entities` 与文字同处块定义坐标系，插入变换会同时作用到两者；反倒是"每个插入按 WCS 算框"才要处理非等比/镜像。但 `Wipeout.SetMaskingArea` 用 `frameSize = max(w,h)` 构造方形像素帧（`Wipeout.cs:72-84`），渲染是否仍正确无实验支撑。
2. `DIMTFILL=1` 覆盖对**带父 DIMSTYLE 的标注**是否真不影响继承（无 `entget` 回读证据、测试夹具无 `Style`）。
3. CLI 加载 `config.json` 时是否在 `RuleValidator` 之外还会校验；`MaskPaddingRatio` 的 NaN/∞ 是否可能在运行期被静默回退。
4. §1 的 5 组 DXF 组码实验、§8.1 的像素扫描、§9.1 的 DIMTFILL 回读——材料都在 `tmpv/`、`bgprobe/` 下的一次性脚本与 PNG，本次只读核对了源码与测试，**未复现这些出图实验**，其数值结论按"文档自述"采信。
5. `SetStyleOverrideMap` 全清 "ACAD" XData 对真实存量图纸的影响面。
6. 库层 `Wipeout.SetMaskingArea(IEnumerable<XY>)` 实现位于外部克隆 `D:\Dev\CSharp\AutoCAD\ACadSharp-v3.8.35`，不在给定源码内。
