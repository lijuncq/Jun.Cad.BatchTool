# 阶段 8：中期 Zoom Out 审查报告

**Target:** `D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/textmask-failure-report.md` (全文 1..481)
**Reviewer:** egm-zoom-out（Skeptical Architect，fresh context）
**Standard:** `C:/Users/lijun/.workbuddy-ai/egm/resources/document_standards/zoom-out-standard.md`
**Mode:** REVIEW ｜ **输入:** 阶段 5/6/7 三份报告 + `GoldfishContext` 源码与配置
**只读:** 评审者未创建/修改/删除任何文件，未编译、未运行测试

## Reviewer Status

| Reviewer | Status |
| --- | --- |
| egm-zoom-out（中期） | completed |

---

## 总判断

前序三阶段给出了大量「文档与代码漂移」和一批「真实功能 bug」，但它们的缓解建议是混合的：一部分要求动代码（删开关与计数、Version 迁移、判重改口径、修 XData、动态块遍历），一部分纯改叙述（状态横幅、标历史、删旧句、更新行号、补迁移说明、把复现路径改对）。

**如果只执行后者，文档会变得更干净，而代码里「默认开着、每次执行都跑、写盘改用户数据、报成功」的路径一条没少，且因为旧句被删除，连原来那点自认缺陷的痕迹都没了。**

另有一个前序三阶段都没碰到的层面：整份报告的「已确认 / 已解决 / 实测通过」没有一条可复现的验收链，preflight 与真实执行路径根本不是同一套判据，甚至不在同一条调用链上。

---

## 1. 文档修复掩盖什么：边界与优先级

### 1.1 [Blocker] 边界判断：文档修复只能改「读者以为发生了什么」，改不了「程序实际做了什么」

- **影响范围:** §9.7(470-481)、§8.2(354-365)、§9.5(420-424)、§5.6(268-272)、§7(286-306)
- **理由:** 前序建议里的纯文档动作（顶部加「当前状态」横幅、§1-4 标「历史诊断」、删除旧句不用删除线、逐条更新 `文件:行号`、补 Non-Goals/状态章节、把 §7 路径改对、在 README 写「旧配置升级后的行为变化清单」）全部只作用于叙述。**必须动代码、文档替换不了的四类**：(1) 默认配置为 true 且每次 `Execute` 都执行；(2) 产出会写进 DWG（改句柄、改块定义、清排序表、写 DSTYLE XData）；(3) 汇总/日志宣称成功但文件里没有对应变化；(4) 存在可达开关能触发已知错误行为。文档越干净，(3) 越像「已解决」。前序建议中真正要求动代码的部分若被跳过，只做文档部分即构成洗白。
- **归类:** [文档质量问题]

按此边界，以下真实 bug 会被掩盖，按优先级：

| 优先级 | 严重度 | 影响范围 | 理由 | 归类 |
|---|---|---|---|---|
| P0 | **Blocker** | §9.7(470-481)、§2 根因 D(112-116)、§3.5(196-202)；`MaskService.cs:305-307`、`TextMaskRule.cs:864-883`、`CadObjectCollection.cs:40,97-100` | 报告自认「存量图纸用户绘制顺序每次保存被重置（上游数据丢失 bug）」与「issue #897 在跟」。改成 Known Limitation 后，每次批量保存仍重置 DRAWORDER。更紧的耦合：占位实体技巧(`MaskService.cs:321-346`)只在排序表被写丢时才「生效」，两处互相依赖，单改一处会直接让现有绘制顺序失效。 | [真实功能 bug] |
| P0 | **Blocker** | §9.4(417)、§9.7(470)；`Jun.Cad.Batch.json:205`、`samples/text-mask.json:24`、`TextMaskRule.cs:55,100-101,162-180,201-204,864-883` | `MaskSendHatchToBack` 默认 true，仍调 `table.MoveToTop`、仍累加 `movedToBack`、仍打「已把 N 个…送到最底层」、仍因 `:100-101` 每次建整空间 `BoundsIndex`。前序建议「把'填充置后 6'改成历史数据/删掉」恰好删掉唯一可见症状；两个随产品发布的配置模板都把它写成 `true` 并注明「Move overlapping hatches behind text」。文档改完，用户连「它在说谎」都看不出。 | [真实功能 bug] |
| P1 | **Major** | §9.3(397-409)；`BatchConfig.cs:15`、`ConfigLoader.cs:34`、`Jun.Cad.Batch.json:13` | 前序建议「README 列出旧配置升级行为变化清单」是文档动作；`Version` 固定 1、`MissingMemberHandling.Ignore`、无按版本迁移代码不变，旧配置升级后仍静默翻成「全图加遮罩 + 改句柄 + 改块定义影响所有插入 + 测量值也加 + 无上限」。 | [真实功能 bug] |
| P1 | **Major** | §3.4(187-194)；`TextMaskRule.cs:43-44,107-129,836-839,942,569`、`TextBounds.cs:525` | 把 §3.4 标成「历史方案，已被 §9 取代」后，`MaskOnlyWhenOccluded=true` 仍启用 0.05 阈值 + 每条线 0.25 权重 + 整类排除 `Insert`；`MaskTextBox=Auto` 又用自述「误差 ±10%」的估测宽度配 `RectangleWidth > tightWidth*1.05`，阈值附近随机落到两条不同路径。文档不写「这个开关是坏的」就等于承诺它可用。 | [真实功能 bug] |
| P1 | **Major** | §9.1(386-389)、§9.7(479)、§5.2(245)、§9.2(395) | 一组与措辞无关、清理文档一行都改不动的缺陷：`map.DxfProperties[69]=fill` 会静默改掉用户已设的 69=2/0(`TextMaskRule.cs:404-408,431-452`)；`MaxMasksPerDrawing>0` 时 `created` 每次归零、判重走 `MarkSkipped` 不计入，跨次运行叠加(`:73,132,267,321`)；`_MASK` 已存在且不可打印时 `LayerService.EnsureLayer` 直接返回、默认 true 分支不设 `PlotFlag`(`LayerService.cs:35-38`、`TextMaskRule.cs:1212-1217`)；`_encodingChecked` 只验第一个 Wipeout(`TextMaskRule.cs:1114-1124`)；动态块匿名实例块在 `DatabaseWalker.cs:181` 就被跳过。 | [真实功能 bug] |
| P2 | **Major** | §8(314,329)、`Jun.Cad.Batch.Core.csproj:20` | 项目引用指向仓库外的 `..\..\..\ACadSharp-v3.8.35\src\ACadSharp\ACadSharp.csproj`；该克隆在本机确实存在（已核实），但不在版本库内，干净检出编不过。§8 行 329 自认「提 PR 前要还原成原多目标列表」。写成「已知限制」不解决构建链。 | [文档与代码漂移] |

---

## 2. 是否收敛到少数根问题，哪个是必须先解决的单点

### 2.1 [Blocker] 能收敛的只有「绘制顺序权威来源缺失」这一簇，它就是单点

- **影响范围:** §2 根因 D(112-116)、§3.5(196-202)、§9.7(468-481)、§9.4(417)、§5.5(265)、`MaskService.cs:279-346`、`TextMaskRule.cs:864-883`、`CadObjectCollection.cs:40,97-100`
- **理由:** 以下六处是同一条因果链的症状——`ACAD_SORTENTS` 挂在块记录扩展字典下，ACadSharp 写出时丢失 → §3.5 的排序表方案作废、§9.7 判「彻底不可用」 → 改用「集合顺序 + 占位实体」(`MaskService.cs:321-346`) → 而 `CadObjectCollection` 底层是 `HashSet<T>`(`CadObjectCollection.cs:40`)，枚举顺序由哈希槽位决定，所谓「Add 落到新末尾槽」是对 HashSet 的误解（它没有「末尾」，只有按哈希分桶 + freeList 槽位复用），§9.7 行 471 自己承认旧路径「纯属幸运」 → 同一份写出缺陷同时让 `MaskSendHatchToBack` 变成空转报成功、让存量 DRAWORDER 每次保存被重置。
- **必须先解决的单点：确定并实现唯一的绘制顺序权威来源**——要么修写出器让 SORTENTS 落盘并给遮罩/文字写 sort handle，要么明确绘制顺序不在范围内、把重排与「填充置后」整条删掉。**顺序不能反**：现在这个 workaround 之所以「能用」，靠的正是那个数据丢失 bug；先修写出器而不动规则，会立刻破坏现有出图。
- **归类:** [真实功能 bug]

### 2.2 [Major] 不是所有发现都是它的症状；至少有 5 个独立根问题

- **影响范围:** §9.3(397-409)、§9.1(386-389)、§9.6(443-446)、§3.3(174-185)、§9.2(391-395)
- **理由:** 把全部发现压成「一个上游 bug」会漏掉独立缺陷：(a) 默认值翻转无版本迁移；(b) DIMTFILL 覆盖对既有 69 键与 ACAD XData 的处理；(c) 动态块/匿名块遍历；(d) `MaxMasksPerDrawing` 跨次幂等；(e) 验证手段本身不可信。这些与排序表无关，修好 issue #897 一条都不会自动消失。
- **复用 / YAGNI 核对:** `BlockLayerChangeRule.MoveDefinitionContent`(`BlockLayerChangeRule.cs:248-356`) 已用 `HashSet<BlockRecord> processed`(`:274`) 处理「同一块定义只处理一次」，并处理动态块 `ownDef.Source`(`:234,:341`) 与嵌套递归(`:337`)。`TextMaskRule` 没有复用它，而是另写 `BuildReferenceCounts`(`:694-724`) 只服务预览计数，并在 `DatabaseWalker.cs:181`、`TextMaskRule.cs:502-509` 把匿名块整体跳过——**丢掉的正是已存在的能力，代价是动态块文字零覆盖**。前序 zoom_out #11 判「复用去重逻辑没必要」只对了一半：去重部分可省，动态块部分必须复用。另外 `BoundsIndex`(`:1258-1342`，含 4096 cell 上限) 与 `EstimateCellSize`(`:945-969`) 是为一个默认关闭的分支做的网格索引，却因一个已失效开关每次执行都跑(`:100-101`)——未证明瓶颈就做的 premature optimization；加上 `Jun.Cad.Batch.json:174-212`、`samples/text-mask.json:13-27` 里 15 个开关（含 `MaskMode` 的 Auto/Wipeout 同义值、`BlockInternal` 兼容别名、退役字段告警、明知失效的 `MaskSendHatchToBack`），是「每次推翻方案只加开关不删旧」的沉积层。
- **归类:** [文档质量问题]

---

## 3. 前序三阶段未覆盖的系统性问题：验证手段本身是否可信

### 3.1 [Blocker] preflight 与真实执行路径不是同一套判据，且 preflight 根本不在执行链路上

- **影响范围:** §4(216-226)、§8(321-327，尤其行 326)、§2 根因 A(80)；`CapabilityProbe.cs:104-213`、`MaskService.cs:231-271`、`TextMaskRule.cs:1100-1129`、`Program.cs:929`、`PreflightDialog.cs:123`
- **理由:** §8 行 326 把「DWG 往返一致（4 点多边形、clipping on、无 IMAGEDEF、WCS 包围盒 100×50）」列进「验证结果」，这串文字逐字来自 `CapabilityProbe.cs:188`。但 `CapabilityProbe.RunAll()` 的消费者只有 `Program.cs:929`（CLI）与 `PreflightDialog.cs:123`（Win 界面），`TextMaskRule` 全文不引用它。两套判据完全不同：
  - **preflight:** 真 `DwgWriter.Write`→`DwgReader.Read`，固定 `MaskService.CreateWipeout(new Bounds2d(0,0,100,50))`（轴对齐矩形重载），断言 `ClippingState`、**顶点数 == 4**、`Definition == null`、`ForWipeout` 包围盒 **== (0,0)-(100,50)**。
  - **真实执行:** `MaskRuntime.CreateWipeout`(`TextMaskRule.cs:1100-1129`) 走 `CreateWipeout(IEnumerable<XY>)`（旋转四角 / 紧贴框），唯一把关是 `MaskService.IsWipeoutEncoded`(`:235-271`)——**纯内存**，只查 `ClippingState`、`Definition`、顶点 **≥3**、包围盒非退化，且 `_encodingChecked`(`:1114-1124`) 让**只有第一个 Wipeout 受检**。
  - **结论:** 一个通过的 preflight 对运行时产出的任何 Wipeout 都不构成保证；它验证的是一条产品规则从不调用的代码路径。**把 probe 加强得再好也没用，因为执行路径不经过它。**
- **归类:** [真实功能 bug]

### 3.2 [Major] 自动化测试对 TextMask 的 DWG 往返 / 宿主渲染覆盖为零

- **影响范围:** §9.4(415-418)、§9.7(481)、§5.5(262-266)、§7(286-306)、`README.md:863`
- **理由:** `TextMaskRuleTests.cs` 共 **40 个** `[Fact]/[Theory]` 方法（本次直接 `grep -c` 核实），全文无 `DwgWriter`/`DwgReader`/`accoreconsole`/`ssget`/`.png`/`Process.Start`，全是内存 `CadDocument` 断言。但更精确的缺口是：测试项目**有**真 DWG 往返用例 `DwgSampleTests.cs:59-87 写出后回读实体数不变`（`CadFileSession.Save` → 重新 `Open`），也**有**端到端跑 `BatchRunner` 并写盘的用例(`:92-125`、`:130-181`)——只是它们跑的是 `EntityLayerChange` 和 `DimTextAudit`，**没有任何一个用例把 `TextMask` 规则跑在真实 DWG 上再写盘回读**。因此「Wipeout/DIMTFILL/90=3/DSTYLE 写出后回读成什么」零回归门禁。其次，`SampleDwgFactAttribute`(`TestPaths.cs:87-111`) 在样图目录不存在时自动 `Skip`，默认目录是仓库外的 `C:\Users\lijun\Downloads\SampleDwg`；干净检出上这些用例是 skipped 而非 passed。
- **归类:** [真实功能 bug]

### 3.3 [Major] 报告把自己在诊断阶段亲手证伪的判据，在实施阶段又当验收判据用了回来

- **影响范围:** §1 实验 3(48-55)、§2 根因 A(76-80) 对 §8(323-327)、§9.4(418)、§9.6(450)、§9.7(478)
- **理由:** §1 行 52-55 的教训原文是「ACadSharp 能读出 5 个 Wipeout，AutoCAD 一个 WIPEOUT 都认不出来」「只验证能写出来、能读回来……所以探测一直通过」。但实施阶段的「验证结果」里，§8 行 323 是「ACadSharp 单体回归 round-trip 2/2 通过」、§9.4 行 418 是「输出图回读 `mtext: 347…`」（ACadSharp 侧扫描）、§9.6 行 450 是「ACadSharp 回读 flags=3」。全报告没有建立「每一版修复都必须重新过宿主 `ssget` + 出图」的门禁。**诊断阶段证明过「能写能读 ≠ 有效」，实施阶段又用「能写能读」签发「已解决」**，这是验收口径的自我否定。
- **归类:** [文档质量问题]

### 3.4 [Major] 报告与 README 对「渲染验收是否完成」给出互斥答案

- **影响范围:** §6(276-282)、§8.1(337-352)、§9.5(422,453)、§9.7(481)、§7(286-306)；`README.md:851,868-869,919-920`
- **理由:** 报告 §8.1 标题「渲染验收（2026-11-02 晚，**已完成**）」、行 347「擦除生效」；§6 用删除线把「没能确认/没解出」翻成「已确认/已解出」；§9.5 行 453「不复存在」；§9.7 行 481「测试 301/301 通过」。而 `README.md:851` 同主题行写的是「文字下横线是否实际切断**还需完成渲染对照，不能仅凭实体存在判定通过**」；`README.md:919` 写 probe "not host rendering acceptance"。同一验收在项目自身两份文档里同时是「已完成」和「未完成」。
- **归类:** [文档质量问题]

### 3.5 [Major] 「本轮实测」数字没有「预期值 → 实测值 → 判据」结构

- **影响范围:** §5.5(262-266)、§8(321-327)、§9.4(411-418)、§9.6(448-455)、§9.7(474-481)、`README.md:862-869`
- **理由:** 测试总数出现 212 / 215 / 291 / 301 四个互斥值，从不声明以哪次为准；§9.4 行 417「匹配 195 = 改动 195 + 跳过 0」，明细 175+15+5=195 却又混入「填充置后 6」变成 201；§9.7 行 478「296 处遮罩」但明细 289+2+10=301、行 481 又写「301/301」；§9.6 行 455「仍未加遮罩的 26 个」只解释 13+8=21；`MaxNestingDepth` 报告写 4，`BatchConfig.cs:303-304` 与 `README.md:318` 的默认都是 8。这些数字被放在「本轮实测 / 端到端宿主验证」标题下当作验收证据，但报告没有一处给出可重跑的断言结构，也没有把 §4 自定的 5 条「缺一条都算没修好」转成任何自动化断言。
- **归类:** [文档质量问题]

---

## 4. 反例检验：能用源码推翻的前序结论

### 4.1 [Major] zoom_out #3「约 200 行遮挡判定在默认配置下不参与实际行为 / 死代码」是事实错误

- **影响范围:** `zoom_out_report.md` 发现 3；`textmask-failure-report.md` §3.4(187-194)、§9.3(401)
- **理由:** `TextMaskRule.cs:100-101` 是 `BoundsIndex occluders = (onlyOccluded || sendHatchToBack) ? BuildOccluders(...) : null;`。默认 `sendHatchToBack=true`(`:55`，且写进 `Jun.Cad.Batch.json:205`、`samples/text-mask.json:24`)，所以 `BuildOccluders`(`:807-851`)、`EstimateCellSize`(`:945-969`)、`BoundsIndex`(`:1258-1342`)、`occluders.Query`(`:162-172`)、`PushHatchesToBack`(`:176-180,864-883`) **每次执行都跑**；被跳过的只有 `ComputeOcclusion`(`:895-943`，受 `:107` 的 `onlyOccluded` 控制)。critic #4 写「默认 true 还让 onlyOccluded=false 时仍建整空间 BoundsIndex（line 100），纯性能损耗」与源码一致。正确描述是「跑了、产出不落盘、还报成功」——比死代码更糟。zoom_out #3 的「绕过根因 C」主结论仍成立，但它的 Blocker 论证里这半截事实基础要撤掉。
- **归类:** [前序误报]

### 4.2 [Major] zoom_out #5 / comprehension #11 把 CapabilityProbe 描述成「仍是当初被列为根因的'只验能写能读'」

- **影响范围:** `zoom_out_report.md` 发现 5、`comprehension_report.md` 发现 11；`textmask-failure-report.md` §2(80)、§4(226)、§8(326)
- **理由:** 当前 `CapabilityProbe.cs:162-184` 实际断言四项几何不变量：`ClippingState`、顶点数 **==4**、`Definition == null`、`TextBounds.ForWipeout` 包围盒 **==(0,0)-(100,50)**，比「能写能读」严格得多。真正的问题不是 probe 弱，而是 (a) 不调宿主（前序已提），(b) **probe 的判据与生产执行判据完全不同、且 `TextMaskRule` 从不调用它**（见 3.1）。把 (b) 写成 (a) 会把修复引向「加强 probe 的几何检查」——加强一个没人调用的函数毫无意义。
- **归类:** [前序误报]

### 4.3 [Nit] comprehension #14「`MaskIgnoreBlockBounds` 的 XML 注释默认值写反」是过度解读

- **影响范围:** `comprehension_report.md` 发现 14；`BatchConfig.cs:707-715`
- **理由:** `BatchConfig.cs:708`「块参照的包络盒是否参与遮挡判定（默认 false = 不参与）」里的 "false" 描述的是「是否参与」这个布尔的有效值，不是配置字段的值。`TextMaskRule.cs:45` `useBlockBounds = !RuleContext.Bool(action.MaskIgnoreBlockBounds, true)` 默认算出 `false`（不参与），`BuildOccluders:836-839` 确实默认整类排除 `Insert`，与注释一致；同一属性的 `RuleField Help`(`BatchConfig.cs:713`) 明写「默认是（忽略）」，两处语义相同。最多算措辞歧义（Nit），不是「默认值写反」。
- **归类:** [前序误报]

### 4.4 [Nit] critic #2 的「没有 `DwgWriter.Write`/`DwgReader.Read`」对 `TextMaskRuleTests.cs` 成立，但外推成「测试不覆盖 DWG 往返」不成立

- **影响范围:** `critic_report.md` 发现 2；`DwgSampleTests.cs:59-87,92-125,130-181`、`TestPaths.cs:87-111`
- **理由:** 测试项目有真 DWG 往返用例，所以「没有 DWG 往返」对整套测试不成立。真实缺口更窄也更致命：**没有任何用例把 `TextMask` 规则跑在真实 DWG 上再往返回读**，且这些集成用例在无外部样图目录时自动 Skip。前序的笼统说法会把人引向「补几个 DWG 往返测试」，而实际要补的是「TextMask × 真实 DWG × 回读断言 × 入库样图」。
- **归类:** [前序误报]

> 未推翻但已核对成立的前序结论（不重复展开）：`MaskBringTextToFront=false` 对 Wipeout 路径不生效(`TextMaskRule.cs:244-247,290,332`)、`MaxMasksPerDrawing>0` 跨次不幂等(`:73,132,267,321`)、`_MASK` 非打印图层不纠正(`LayerService.cs:35-38` + `:1212-1217`)、`MeasureMTextTightWidth` 的 ±10% 误差配 1.05 阈值(`TextBounds.cs:525`、`TextMaskRule.cs:569`)、`MaxNestingDepth` 默认 8 与报告 4 不符(`BatchConfig.cs:303-304`)、`DatabaseWalker.cs:181` 跳过匿名块。

---

## 5. 编排层直接核验补记（不属于评审者结论）

评审者之间存在直接矛盾，且评审者受严格只读约束不能编译/跑测试。编排层补做以下核验，**用于最终汇总时裁定**：

| 争议点 | 核验方式 | 结果 |
|---|---|---|
| 「§7 复现材料 / bgprobe 证据全部不存在」 | 逐个 `test -e` + 统计目录文件数 | **不成立**。3 个 `.lsp`、6 个 `.png`、2 个 `.txt`、`pngscan.py`、`DimFillProbe.cs`/`DimScan.cs`、`dim-ac.png`、`m90.dwg`、`m90.png` 全部 `EXISTS`；`tmpv` 260 个文件、`.workbuddy-ai/bgprobe` 131 个文件。评审者用的是仓库 Glob（这些目录被 `.gitignore` 排除），不是文件系统不存在。 |
| 测试总数 212 / 215 / 291 / 301 哪个是真的 | `dotnet test Jun.Cad.Batch.sln --nologo -v minimal` | **当前真实值：Failed 0 / Passed 296 / Skipped 5 / Total 301**（211 ms）。301 是唯一与当前代码相符的数字；5 个 Skip 全部来自 `DwgSampleTests`（外部样图目录不存在），恰好印证 3.2 的可复现性缺口。 |
| `TextMaskRuleTests.cs` 测试方法是 36 还是 40 | `grep -c -E '\[(Fact\|Theory)\]'` | **40**。108 号评审者的 40 正确，106 号评审者的 36 有误。 |
| `SetStyleOverrideMap` 是否清空 ACAD XData | 读外部克隆 `Entities/Dimension.cs:514-526` | **成立**：`edata.Records.Clear()` 后只写回一个 DSTYLE 段；`GetStyleOverrideMap` 只取第一段控制串之间的键值对。 |
| `LayerService.EnsureLayer` 命中已存在图层是否直接返回 | 读 `Engine/LayerService.cs:24-40` | **成立**：`doc.Layers.TryGetValue(name, out existing)` → `return existing`，不触碰 `PlotFlag`。 |
| 动态块匿名块是否在遍历层被跳过 | 读 `Engine/DatabaseWalker.cs:176-186` | **成立**：`if (nested.IsAnonymous \|\| IsExternalReference(nested)) continue;`。 |
| §9.3 的 `DimensionMaskMode` 字段是否写错 | 读 `TextMaskRule.cs:51,475-478` | **成立**：加 `DIMENSION` 的条件是 `dimMode`（来自 `DimensionTextMode`），不是 `DimensionMaskMode`。 |
| §9.3 的「bit1，不是 bit0」 | 读 `MaskService.cs:138-143` | **成立（漂移）**：实际 `bit0 \| bit1`（DXF 90=3）+ `BackgroundColor = new Color(7)` + `BackgroundScale`。照 §9.3 抄会退回 §9.6 已证伪的 `90=2` 无遮罩缺陷。 |

---

## 6. 未核实

1. §1 的 DXF 组码实验、§8.1 的逐像素 PNG 扫描、§9.1 的 DIMTFILL 回读、§9.6 的 90=0/2/3/1 对照出图：PNG 与脚本虽在本机存在，但未重跑，其数值结论按「文档自述」采信。
2. 上游 ACadSharp 写出器丢失 `ACAD_SORTENTS`/块记录扩展字典的具体代码位置：外部克隆中只核实了 `Wipeout.SetMaskingArea`(`Wipeout.cs:50-86`) 与 `CadObjectCollection`(`CadObjectCollection.cs:40,97-100`)，未定位丢表点。
3. `SetStyleOverrideMap` 全清 "ACAD" XData 对真实存量图纸的影响面。
4. `Wipeout.SetMaskingArea` 用 `frameSize = max(w,h)` 构造方形像素帧，块参照非等比缩放/镜像插入时 AutoCAD 2025 是否仍正确渲染。
5. CLI 加载 `config.json` 时 `MaskPaddingRatio` 的 NaN/∞ 是否在 `RuleValidator` 之外被 editor 校验拦住。
