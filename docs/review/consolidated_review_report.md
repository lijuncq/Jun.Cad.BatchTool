# 汇总审查报告 · Jun.Cad.Batch

- 审查对象：`README.md`（设计文档，全文 676 行）+ `src/`（实现，20 条规则 / 3 个工程）
- 执行模式：Consolidated（Stage 5 → 9 全跑，不停顿）
- 审查者：egm-zoom-out ×2、egm-comprehension、egm-design、egm-structural、egm-style ×2、egm-implementation-readiness、egm-codebase-details
- 生成时间：2026-10-31

---

## 一、结论

**不通过。** 文档在库行为与几何换算上有可复核的证据（组码、反例、换算推导），但作为"产品设计文档"缺三样硬性内容（问题陈述 / 非目标 / 备选方案），并且**四条安全承诺里有三条在代码里不成立**。

按杀伤力排序，最该先处理的三件事：

1. **写盘不看"规则是否中止"** → 规则崩了照样把半成品写出去，而文档三处承诺"失败不写盘"。
2. **预览对"改样式表 / 删表项"完全失明，还会反向报告"均已是目标状态"** → 比不显示更坏。
3. **配置是这个系统唯一的输入，但它没有校验边界** → 同一个拼写错误在 7 条规则上 fail-closed（安全）、在 13 条规则上 fail-open（整图被改）。

这三条都不是"文档写得不好"，是**承诺与实现不一致**，必须改代码或改承诺，不能改措辞糊过去。

---

## 二、严重度统计（跨 5 阶段去重后）

| 级别 | 原始条数 | 去重后 | 说明 |
|---|---|---|---|
| 🔴 Blocker | 14 | **12** | 2 条合并进根因（R1/R4） |
| 🟡 Major | 58 | **14 组** | 按主题合并，组内条目在追溯矩阵可查 |
| 🟢 Nit | 37 | **9 组** | 批量处理，不单独排期 |

原始 109 条 → 35 个行动项。**没有收敛的审查报告是失败的审查**：下面第三节给根因，第四节给行动项。

---

## 三、四个根因（比 109 条发现更有用）

### R1 · 配置没有独立契约层，schema 就是 C# 属性 + 反射

`RuleAction` 是 128 个可空属性的扁平并集，112 个 `RuleField` 标注，schema 由反射生成。于是**五个消费者各读一份反射面**：文档（手抄）、界面（`FieldCatalog`）、校验（`RuleValidator`）、摘要（`RuleSummary`）、legacy 兼容（`JsonExtensionData`）。

五份副本必然漂移。以下 13 条发现全部是它的投影，不是 13 次疏忽：

- 默认值写三遍（`RuleAction` 可空 + 规则里 `Bool(x,true)` + 摘要硬编码），`RuleSummary.cs:260` 的注释自认"要跟规则里一致，否则摘要会说反话"
- §4 只覆盖 9/20 条规则，其余 11 条甩给源码
- `DimensionTextMode` / `DimensionMaskMode` 两个字段名，文档只写了一个
- `MatchSpacing` 是唯一改变输出几何的默认行为，却不在字段清单里
- `Version` 字段从不被读取
- legacy 与本项目的配置互通被描述为"结构一致"，实质是"字段能往返、语义会丢"

**修一个根因，消 13 条。**

### R2 · "能力"只活在 README 的表格里，代码里没有这个对象

`CapabilityProbe` 全项目只有一个消费者（`TextMaskRule` 的 Wipeout 分支）。"表格单元格能不能写"在四处有四种说法（§5"可用" / §6.2"支持但需回归" / §3"未测" / §10.6 暗示前置检查约束执行），因为四张表都是手抄的。

能力应当是**数据**：`capability → supported/degraded/unsupported + reason + consumer`，被规则、preflight、预览、界面共同查询。这也解释了为什么探测进了运行路径——规则没法问"这个能力现在什么状态"，只能现场探一次，于是同一份配置在不同机器上产出不同实体类型。

### R3 · 没有分层边界，职责按"当时谁方便"分配

`Core/Editing/` 3288 行全是界面支撑物，却住在"与宿主无关的核心库"里；`Engine/TextColumns.cs`（控制台等宽列对齐）也在 Core。边界不存在 → legacy 迁移只能靠复制、测试工具塞进产品二进制、命令参考切成两半。这些是同一个缺失的三个投影。

### R4 · 文档体裁错位：这是"移植实验报告"，被当作"产品设计"使用

因为项目没有设计文档，README 被迫同时承担论证职责，于是**实验证据被当作设计理由**："与插件一致"当理由、"AUDIT 0 错误"当成功判据、"恰好 6 行不同"当通过证据。

实验报告的合法结构不产生"非目标""备选方案""成功判据"——不是作者漏写，是这个体裁里没有这些格子。所以补一节没用，得把证据整体降级为附录，正文按"约束 → 效果族 → 执行语义 → 能力矩阵"重写。

---

## 四、行动项

### 🔴 P0 · 必须修（数据损坏或承诺失效）

| ID | 问题 | 证据（已由 Elephant 复核） | 最小修法 |
|---|---|---|---|
| **B1** | 规则中止后仍写盘，写的是半成品 | `grep Aborted` 在 CLI/UI 写盘路径零命中；`BatchRunner.cs:297` catch 只设标记 | 加 `OnRuleFailure: WritePartial \| DiscardFile`，默认 `DiscardFile`；写出前检查 `Results.Any(r => r.Aborted)` |
| **B2** | `--in-place --no-backup` 先删源文件再改名 | `CadFileSession.cs:295-296` `File.Delete` + `File.Move` | 该组合直接拒绝（备份是原地模式的唯一退路）；回退路径失败时从 backup 拷回 |
| **B3** | 预览对改样式表/删表项失明，还打印"均已是目标状态" | `EntitySnapshot` 只抓实体属性；`BatchRunner.cs:219` 在 `diff==0` 时写 `No change` | 快照加表对象（`TextStyle`/`DimStyle`/`Layer`/`BlockRecord`/`Layout`）的指纹；或给 handler 加 `ReportsOwnChanges`，为 true 时不写 `No change` |
| **B4** | 预览句柄去重跨规则吞行 | `ChangeRecord.cs:130` 的 `_handles` 是 sink 级单集合，`Clear()` 无调用者 | 键改 `(RuleIndex, Handle)`，或每规则执行前 `Clear()` |
| **B5** | `GeometryCleanup` 删了对象但 `Changed=0`，`--skip-unchanged` 静默丢弃删除 | `GeometryCleanupRule.cs:157` 只 `removed++`，从不 `MarkChanged` | 删除成功分支改 `MarkChanged`；预演分支保留 `MarkSkipped` |
| **B6** | 配置校验静默，同一错字后果相反 | `ConfigLoader` 用 `MissingMemberHandling.Ignore` + `JsonExtensionData`；`EntityFilter.HasCondition` 只看 5 个字段 | 未知键 → Warning（列出键名）；`HasCondition` 计入空间/区域条件 |
| **B7** | 输入/输出集合相交，递归会自我喂食 | `Program.cs:815` `AllDirectories` 不排除 `output\`、`.jun-runs\` | 枚举时排除输出根与 `.jun-runs`；`--out` 落在输入树内时告警 |
| **B8** | 配置契约层缺失（R1） | 见第三节 | 一份显式 schema（代码生成或 JSON Schema），文档/界面/校验/摘要都从它读 |
| **B9** | 文档缺问题陈述 / 非目标 / 备选方案 / 成功判据（R4） | 全文结构 | 按"约束 → 效果族 → 执行语义 → 能力矩阵"重写正文，证据降为附录 |
| **B10** | `TitleBlockNormalize` 没写 `SourceBlockName` 时写进**所有**带该 Tag 的块 | `TitleBlockNormalizeRule.cs:67` + `Wildcard.IsMatch(x, null)` 恒真 | 与 `BlockLayerChange` 同构的护栏：块名与容器都空则中止 |
| **B11** | `PurgeUnused` 未知清理类别 → 兜底清**全部**类别 | `PurgeUnusedRule.cs:347` `set.Count == 0 → AllCategories` | 全部未识别时抛 `RuleValidationException`，列出可用类别 |
| **B12** | CLI 的 `--out` 指向输入目录会原地覆盖源图 | `Program.cs:404` 无 same-file 护栏（UI 侧有） | 把 UI 的 `ResolveOutputPath` 提到 Core，两侧共用 |

### 🟡 P1 · 应该修（可用性 / 可维护性）

| 组 | 内容 | 涉及条目 |
|---|---|---|
| **M1 批处理语义** | manifest 只在整批结束后写（中断=无清单，`--latest` 静默回滚上一次）；`--recursive` 同名文件互相覆盖；零改动仍重写并升版本；`BomSync` 在 `--dry-run` 下写 CSV | I6、I7、I8、I10、D7、B1(Stage9) |
| **M2 校验双轨** | `ConfigLoader.ValidateByType` 与 `RuleValidator` 两份，覆盖不一致（非法正则只有 UI 拦得住） | 1.3、I15、J-未注册类型措辞 |
| **M3 计数与术语** | "命中" vs "匹配"是两个数；"成功"列实为 `Changed`（审计规则永远成功 0）；同一事件三个词（跳过/Blocked/被挡住）；`MarkSkipped(detail)` 丢参数导致"跳过"无原因 | J1、J2、J3、J4、I14、C1 |
| **M4 Filter 语义** | `HasCondition` 不含空间条件；区域过滤对无 bbox 对象 fail-open；三条"按块写属性"规则丢掉用户 Filter 的层/色条件；颜色索引过滤不排除真彩色 | D6、I19、I16、3.4 |
| **M5 规则契约** | 无不变式校验；无"我改了什么"的必填上报；`ChangeStatus` 三态不足（表项增删无处报）；`RuleContext.Filter` 可写导致规则改写用户配置 | D-契约、I14、2.5 |
| **M6 重复实现** | 输出路径解析 ×2、文件循环 ×2、`"旧=新"` 解析 ×4、`IsWide` ×2（区间还不同） | D1–D5(codebase) |
| **M7 可复现** | 图案库用了哪份 `.pat` 从不记录；`--pat` 路径错时静默回退到本机 AutoCAD；数值格式化未固定 `InvariantCulture` | D14、I9、3.7 |
| **M8 规模** | 预览 O(规则数 × 全库遍历) + 常驻全量快照；实测上限 1.1 万实体，10 万级无数据；未讨论文件级并行 | D13、I12 |
| **M9 文档结构** | §4 只覆盖 9/20；§7（证据 140 行）插在规范叙事中间；§3/§4 子节不编号导致交叉引用只能覆盖一半；无 TOC；无"目标/非目标" | S1–S8 |
| **M10 状态词表** | 6.2 状态列 8 种说法（不可用/降级/不支持/不处理/默认不支持/不可控/支持但需回归/无）；"遮罩"未定义（属性 vs 新增实体） | Y1、Y14、F1 |
| **M11 事实性错误** | "组码逐字节相同"（实际 41 由 1.0 改为 0.384615）；"两边配置完全一致"（9 种新规则在插件里中止）；§7.1"160 个填充"vs 78+144=222；§7.5"匹配 90"vs 71+228=299 | Y3、D10、C10、D11 |
| **M12 安全章节名不副实** | §9 五条全是数据保全；没有输入信任、路径越界（`BomExportPath` 可指向任意路径）、正则 ReDoS | M1(Stage8)、M6 |
| **M13 测试** | `src` 无测试项目；`legacy/tests/` 1161 行移植时整套丢掉 | E-测试 |
| **M14 死代码** | `RuleJson.cs:355` 判断 `StartsWith("Jun.AutoCAD.Config")` 永远不成立（已复核）；`LastRunStore` 整文件零调用；`MarkChanged(detail)` 参数被忽略 | 4.1、4.2、4.3 |

### 🟢 P2 · 有空再修

- 术语统一（降级/退化、块内实体两种写法、`2D Solid`/`SOLID`/`Solid`）
- 用户可见文案（"插件不会跨图导入"、"命令行输出明细"、状态列中英混排）
- 注释与代码矛盾（`ExecutionMode` 还在讲事务、`TextMaskRule` 说标注遮罩"不支持"、`LayerMap` 说"当前层永不动"）
- 帮助文本说 `acad.pat` 是唯一来源（实际查 `Resources\`）
- `--version` 可接受值用"等"兜底；退出码表未覆盖 `preflight`/`rollback`
- 路径风格（`D://图纸//output`）、比例精度（0.384615/0.3846）、`2568KB` 单位
- `MaskIgnoreBlockBounds` 标签与默认值方向相反
- 死代码清理（`NameMatcher`、`IsFrozen`、`ScaleForSpacing`、`DescribeColor/LineWeight`、`ContainerCount/RegionCount`）

---

## 五、我不同意的三条

审查不是投票。以下三条我认为审查者的判断越界了，理由如下。

**1. "回滚体系为不存在的风险而建"（Stage 5 · Z6）——部分反对。**
`--in-place` 模式下回滚是真需求，`output/` 模式下用户也确实会覆盖上一次产物。但这条批评有一半成立：manifest 记的是文件级事实，事后无法回答"是哪条规则改的"。所以结论应是**扩展 manifest 的内容**（加变更级摘要），而不是删掉回滚。

**2. "4 条规则与宿主能力重复（PURGE/OVERKILL/DATAEXTRACTION）"（Stage 5 · Z9）——反对。**
项目前提就是"没有 AutoCAD 授权的服务器/CI"。在这个前提下，重复不是冗余，是**唯一路径**。这条批评暴露的真问题是另一件事：文档没有"非目标"段，导致读者不知道这些规则为什么存在。

**3. "未建模对象会在写出时静默丢弃"（Stage 7 · D3）——降级为未验证风险。**
逻辑上成立（部分实现 + 全量重写 + 读入告警只打日志），但**没有实测证据**：我无法在无宿主条件下确认 ACadSharp 的 writer 是否透传 `UnknownNonGraphicalObject`。写成"已知会丢"是过度断言；应写成"未验证，需一张带 Civil3D/天正对象的图做对照实验"。

另有一条我接受但改了归因：**"§7.1 恰好 6 行不同是把缺陷当证据"（D12）**。未被引用的块定义不处理是既定策略（与 `PurgeUnused` 配合），不一定是缺陷；但"与插件一致"不能当设计理由，这点成立。

---

## 六、修复顺序

**第一批（本周，防数据损坏）**：B1、B2、B5、B10、B11、B12
六条都是"会让文件变错或变没"的问题，改动量都不大（每条 10–40 行）。

**第二批（结构，防漂移）**：B3、B4、B6、B7 + M2、M5、M14
预览可信 + 校验统一 + 规则契约收紧。做完这批，M3（计数与术语）会自然收敛。

**第三批（文档重写）**：B8、B9 + M9、M10、M11
先定 schema（B8），文档才能从 schema 生成，手抄的 13 条漂移才不会复发。

**第四批（工程化）**：M13（建测试项目，把 legacy 的 1161 行搬回来）、M1、M6、M7、M8。

---

## 七、追溯矩阵

| 汇总 ID | 来源 |
|---|---|
| B1 | S7-D2、S9-A1 |
| B2 | S7-D8、S9-A2 |
| B3 | S9-A3 |
| B4 | S9-A4、S9-1.1 |
| B5 | S9-A5、S9-2.1 |
| B6 | S8-M1、S6-C1 |
| B7 | S8-M2 |
| B8 | S8-R1（归组 Z1/Z3/Z4/C1/C2/C3/C5/C6/Y2/Y4/S2/D6/D10） |
| B9 | S8-R4（归组 Z11/S1/S3/S6/D1/D4/D12/D18/Y19） |
| B10 | S9-2.2、S9-C4 |
| B11 | S9-J-未知类别 |
| B12 | S9-1.2 |
| M1 | S9-I6/I7/I8/I10、S7-D7 |
| M2 | S9-1.3、S9-I15 |
| M3 | S9-J1/J2/J3/J4、S9-I14、S6-C10 |
| M4 | S7-D6、S9-I19、S9-I16、S9-3.4 |
| M5 | S9-D-契约、S9-2.5 |
| M6 | S9-D1–D5 |
| M7 | S7-D14、S9-I9、S9-3.7 |
| M8 | S7-D13、S9-I12 |
| M9 | S7-S1–S8 |
| M10 | S7-Y1/Y14 |
| M11 | S7-Y3、S7-D10、S6-C10、S7-D11 |
| M12 | S8-M1/M6 |
| M13 | S9-E-测试 |
| M14 | S9-4.1–4.3 |

---

## 八、附：阶段报告

| 阶段 | 文件 | 原始条数 |
|---|---|---|
| 5 · Initial Zoom Out | [zoom_out_report.md](zoom_out_report.md) | 1B / 10M / 3N |
| 6 · Comprehension | [comprehension_report.md](comprehension_report.md) | 1B / 3M / 12N |
| 7 · Critic | [critic_report.md](critic_report.md) | 7B / 31M / 14N |
| 8 · Intermediate Zoom Out | [intermediate_zoom_out_report.md](intermediate_zoom_out_report.md) | 5B / 8M / 1N |
| 9 · Implementation Readiness | [implementation_readiness_report.md](implementation_readiness_report.md) | 5B / 12M / 7N |

合计 109 条原始发现 → 35 个行动项（12 Blocker / 14 Major 组 / 9 Nit 组）。
