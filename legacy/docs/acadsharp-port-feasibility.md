# 移植可行性调查：用 ACadSharp 替代 AutoCAD 宿主

> 调查对象：`Jun.AutoCAD.BatchTool`（配置驱动的 CAD 批量处理插件，11 条规则 / 约 1.4 万有效行）
> 目标形态：**不启动 AutoCAD**，直接读写 `.dwg` 文件，按 `config.json` 执行同样的批量修改
> 调查日期：2026-10-30 · ACadSharp 3.8.35（NuGet 最新）· 本机 AutoCAD 2025（V.58.0.0）作为验收内核

---

## 0. 结论速览

**可行，但不是"换个引用"级别的移植，而是"换掉宿主"级别的重写。** 建议按"新建 `Jun.Cad.BatchTool` 独立工具、复用配置层与规则语义"的方式推进，而不是在原插件里加条件编译。

| 维度 | 判断 | 依据 |
|---|---|---|
| 读 DWG | ✅ 完全可行 | AC1032 / AC1021 / AC1015 三种格式实测全部读通，最大 4.4 万实体、1.1 秒 |
| 写 DWG | ⚠️ 可行，有 2 个硬约束 | **AC1021（2007）不能写出**；**AC1015（2000）写出后 AutoCAD 打不开**（实测）；改用 AC1024/AC1032 写出则审计 0 错误 |
| 内容保真 | ✅ 优秀 | 4 份图纸往返后，AutoCAD 侧内容清单**逐行零差异**（最多 99,711 行） |
| 11 条规则 | 🟢 8 条 / 🟡 2 条 / 🔴 1 条 | 6 条已实跑验证，见第 4 节 |
| 框架层 | 🔴 必须重写 | 事务、ObjectId、选择集、Editor 全部不存在，约 6,000 行 Core 层不能直接复用 |
| 配置层 / 规则语义 | ✅ 可直接复用 | `Config/` 834 行无任何宿主依赖；`Rules` 的业务语义可原样保留 |
| 净收益 | ✅ 显著 | 免 AutoCAD 授权、可上服务器、可跨文件并行、快（4.4 万实体写盘 315ms） |
| 主要代价 | ⚠️ | 丢失"从图上拾取"、`Scope=Selection`、动态块参数、打印样式随层；`PurgeUnused` 需从零实现 |

**一句话**：能做成，且做出来的东西比现在更好用（无授权、可并行、可服务化）；代价是要重写 1/3 的代码，并接受 4 个功能降级。

---

## 1. 调查方法

不靠读文档下结论，全部结论都有可复现的实测支撑：

1. **API 面测绘**：反射导出 ACadSharp 全部 606 个公共类型的成员清单，逐条对照现有规则用到的 AutoCAD API；
2. **真实图纸实测**：取 4 张真实 DWG（含 2 张外来图纸）做 读取 → 写出 → 修改 → 写回；
3. **AutoCAD 内核验收**：用 `accoreconsole.exe` 跑 `AUDIT` 判定文件合法性（这是唯一可信的"AutoCAD 认不认"标准）；
4. **内容清单比对**：用 LISP 把图纸的实体/图层/块/样式/填充/线型全量导出成纯文本清单，对原图与输出图**逐行 diff**——这比"AUDIT 0 错误"更严格，能抓出"合法但内容变了"的情况。

> 复现脚本：`docs/acadsharp-spike/`（一次性验证工程，含读取/往返/六类修改/块替换四种模式）。
> 验收脚本：`accoreconsole /i <dwg> /s audit.scr`（脚本必须 CRLF；LISP 加载需先 `SECURELOAD 0`；输出是 UTF-16，用 `encoding='utf-16-le'` 解码）。

### 测试样本

| 样本 | 格式 | 规模 | 特点 |
|---|---|---|---|
| P3-B1.dwg | AC1032 | 82 实体 / 12 块 | 图框属性块、标注、填充、视口 |
| HATCHING.dwg | AC1032 | 19 实体 | 5 种填充图案、TTF 文字 |
| 楼梯和平台.dwg | **AC1021** | 11,713 实体 / 183 块 / 168 填充 | 真实外来图纸，17 条历史错误 |
| Gatehouse.dwg | **AC1015** | 44,114 实体 / 612 块 / 1,224 块参照 | 大图，718 个多面网格 |

---

## 2. 实测结果

### 2.1 读取

| 样本 | 耗时 | 结果 |
|---|---|---|
| P3-B1 (35 KB) | 153 ms | 通过，5 条非致命告警 |
| 楼梯和平台 (1.05 MB) | 696 ms | 通过，4 条告警 |
| Gatehouse (5.0 MB) | 1,125 ms | 通过，**0 条告警** |

告警都是"不认识的扩展对象按 UnknownNonGraphicalObject 保留"（`ACDBDETAILVIEWSTYLE`、`SUN`、`CELLSTYLEMAP` 等），不影响主流程。**没有任何一张图读取失败。**

### 2.2 写出与往返（核心结论）

| 输入 → 输出 | ACadSharp | AutoCAD `AUDIT` | 内容清单 diff |
|---|---|---|---|
| AC1032 → AC1032（P3-B1） | 成功 50ms | 0 错误 / 0 删除 | **0 差异**（117 行） |
| AC1032 → AC1032（HATCHING） | 成功 50ms | 0 错误 / 0 删除 | **0 差异**（46 行） |
| AC1015 → AC1015（Gatehouse） | "成功" 315ms | ❌ **打不开，ErrorStatus=53** | 无法比对 |
| AC1015 → **AC1024** | 成功 353ms | 0 错误 / 0 删除 | 未比对（仅验审计） |
| AC1015 → **AC1032** | 成功 383ms | 0 错误 / 0 删除 | **0 差异**（99,711 行） |
| AC1021 → AC1021（楼梯） | ❌ `CadNotSupportedException: File version not supported: AC1021` | — | — |
| AC1021 → **AC1032** | 成功 225ms | 12 错误 / 12 已修复（**原图本身就有 17 条历史错误**，输出后减少到 12 条） | **0 差异**（34,598 行） |

三条硬结论：

1. **保真度没有问题**。4 张图、最多 99,711 行内容清单、逐行零差异——ACadSharp 不是"能打开就行"的半吊子实现，往返是真的稳。
2. **AC1021（AutoCAD 2007）根本写不出来**。写之前必须把 `doc.Header.Version` 改成 AC1024/AC1032，等于**把图纸格式升级**。这是行为变更，要在文档里写清楚。
3. **AC1015（AutoCAD 2000）的写出器有 bug**。同一份 Gatehouse.dwg，写成 AC1015 后 AutoCAD 连打开都失败，改成 AC1024/AC1032 写出则审计 0 错误、内容零差异。**对策：一律以 AC1032（或 AC1024）写出，不做"保持原版本"**。这条必须在实现里写死，否则会静默产出打不开的文件。

### 2.3 修改能力（实跑，非纸面推演）

在 P3-B1.dwg 上一次跑通 6 类操作，写回后 AutoCAD 审计 **0 错误**，内容清单显示改动精确落在预期位置：

| 操作 | 结果 | AutoCAD 侧看到的 |
|---|---|---|
| 按类型+颜色改图层 | 命中 2 / 移动 2 | 2 条线移到新建的 `EQUIPMENT`（ACI=3） |
| 统一文字字体 | 18 个文字重映射 | 全部指向新建 `GB_STANDARD`（gbenor.shx + gbcbig.shx） |
| 填充图案替换 | 1 个 ANSI31 → ANSI37 | 图案名/比例 2.0/角度 30° 全部正确 |
| 标注样式替换 | 3 个标注 | 指向新建 `GB_DIM`（Dimtxt=3.5 / Dimasz=2.5） |
| 新建线型并应用 | 5 个实体 | `DASHED` 线型被 AutoCAD 正常识别 |
| 块参照+块内对象归层 | 2 个参照 / 9 个块内实体 | 参照与块定义内 LINE/ATTDEF 全部到 `TK-FRAME` |
| 块替换+属性迁移 | 替换 2 个 / 迁移 2 个属性 | 新块 2 个、旧块 0 个、属性值 `A-101`/`B-201` 正确迁移 |

块替换这一项要特别注意一个实现细节：**不能手工 `new AttributeEntity` 塞进 `Insert.Attributes`**，否则会与库自动按 ATTDEF 补的 ATTRIB 撞车，产生同 Tag 两条属性。正确姿势是：

```csharp
var ni = new Insert(targetBlock) { InsertPoint = ..., Rotation = ..., Layer = ... };
ni.UpdateAttributes();                       // 让库按 ATTDEF 建 ATTRIB
foreach (var a in ni.Attributes)             // 再按 Tag 赋迁移值
    if (srcAttrs.TryGetValue(a.Tag, out var v)) a.Value = v;
owner.Entities.Remove(oldInsert);
owner.Entities.Add(ni);
```

---

## 3. 必须接受的 4 个功能降级

| 功能 | 现状 | 移植后 | 影响 |
|---|---|---|---|
| `Filter.Scope = Selection` | 用当前选择集 | ❌ 不可能（无 Editor / 无选择集） | 改为"整图"或"按图层/块名筛选"；批处理场景本来就不该依赖交互选择 |
| `BATCHUI` 的「从图上拾取」 | 点图取块名/图层名 | ❌ 不可能（要内置 DWG 查看器） | 改为"从文件名列表/配置历史里选"，或提供 `--list-blocks <dwg>` 辅助命令 |
| 动态块参数迁移（`AllowDynamicBlock`） | `DynamicBlockReferencePropertyCollection` 逐个迁移 | ❌ 无对应 API（只有 `HasDynamicSubclass` 标志） | 动态块替换继续"跳过并告警"，与现在默认行为一致 |
| `SetByLayer` 的打印样式随层 | `ent.PlotStyleName = "ByLayer"` | ❌ 实体层无 `PlotStyleName` 属性 | 其余三项（颜色/线型/线宽）正常；该子项降级为"跳过并告警" |

另有 2 个"要自己补"的能力，不算降级但工作量不小：

- **`PurgeUnused`**：AutoCAD 的 `Database.Purge()` 是内核级引用扫描，ACadSharp **完全没有**（API 里搜不到任何 purge 相关成员）。必须自己实现"遍历全部符号表 + 块定义 + 字典 → 统计引用 → 无引用者删除"，还要处理多轮迭代的链式依赖。这是移植里最大的一块新增工作。
- **`.lin` / `.pat` 文件解析**：`HatchPattern.LoadFrom(path)` 有（可读 `acadiso.pat`，实测读到 83 个图案，ANSI37 定义完整）；但**线型没有对应的加载器**，`DASHED` 这类要自己解析 `acadiso.lin` 或用 `LineType.AddSegment` 手工拼（实测可行，AutoCAD 认）。建议把常用图案/线型定义内嵌进程序，避免依赖本机 AutoCAD 安装目录。

---

## 4. 11 条规则逐条可行性

图例：🟢 直接可移植（改类型名即可）· 🟡 需改造 · 🔴 需重写/降级 · ✅ 已实跑验证

| # | 规则 | 判定 | 关键差异 / 做法 |
|---|---|---|---|
| 1 | `BlockReplace` | 🟢✅ | `Insert.Block` 直接指向 `BlockRecord`（比 ObjectId 更简单）；属性迁移用 `UpdateAttributes()` + 按 Tag 赋值（已实跑）。动态块边界保留现状 |
| 2 | `BlockLayerChange` | 🟢✅ | 块定义就是 `BlockRecord.Entities`，遍历更直观；匿名块 `IsAnonymous`、外部参照 `Flags & BlockTypeFlags.XRef` 都能判（已实跑） |
| 3 | `EntityLayerChange` | 🟢✅ | `entity.Layer = layer`；建层 `new Layer(name){Color=new Color(aci), LineWeight=..., PlotFlag=...}`（已实跑） |
| 4 | `TextFontChange` | 🟡✅ | TEXT/MTEXT/ATTDEF/ATTRIB 走 `entity.Style`（更简单，无 Id 重映射）；**标注改 `DimensionStyle.Style`**；⚠️ `ACAD_TABLE` 的 `Cell.Style` 是表格单元样式不是文字样式，`MULTILEADER` 无 `TextStyle` 属性——这两个分支要放弃或绕道 |
| 5 | `PurgeUnused` | 🔴 | 见第 3 节，需从零实现引用扫描（3~5 人日） |
| 6 | `EntityLinetypeChange` | 🟢✅ | `entity.LineType = lt`；线型缺失时自己造（`LineType.AddSegment` 已实跑验证 AutoCAD 认可） |
| 7 | `EntityLineweightChange` | 🟢 | `LineWeightType` 枚举（`W0`…`W211`/`ByLayer`/`ByBlock`），配置里的 0.01mm 整数映射成枚举即可 |
| 8 | `DimensionStyleChange` | 🟢✅ | `DimensionStyle.Clone()` 克隆再改 `Name/TextHeight/ArrowSize/Style`（已实跑，AutoCAD 侧 Dimtxt/Dimasz 正确） |
| 9 | `SetByLayer` | 🟡 | 颜色 `Color.ByLayer`、线型 `LineType.ByLayer`、线宽 `LineWeightType.ByLayer` 都可以；打印样式做不到 |
| 10 | `TextHeightChange` | 🟡 | `TextEntity.Height` / `MText.Height` / `AttributeBase.Height` 直接改；表格单元格文字高度需经 `CellStyle`，绕道或跳过 |
| 11 | `HatchPatternChange` | 🟡✅ | `Hatch.Pattern` 是**可写对象**（不像 AutoCAD 是只读+`SetHatchPattern`），换图案 = 赋一个新 `HatchPattern`；**必须从 `.pat` 加载完整定义**（含 `Lines`），只改 `Name` 会留下错误的图案线。渐变走 `HatchGradientPattern`（`Angle/Shift/IsSingleColorGradient/Colors`） |

**净结论：8 条能顺利过，2 条有局部降级，1 条要重写。**

---

## 4.1 `HatchPatternChange` 专项验证（8 个用例，全部经 AutoCAD 组码核对）

**结论：能实现，而且比现有实现更直接。**

AutoCAD 的 `SetHatchPattern(type, name)` 是"把名字丢给内核，让内核自己去 `.pat` 里查定义"；
ACadSharp 是"你给一个完整的 `HatchPattern` 对象（含图案线定义）"。后者更可控，但**必须自己把图案定义喂进去**——
只改 `Pattern.Name` 会留下错误的图案线，图上看起来就是一堆糊在一起或间距错误的线。

### 实测矩阵

源图 `HATCHING.dwg`（5 个预定义图案填充，各 1 条边界路径），逐个用例改完写盘后用 AutoCAD 读组码：

| 用例 | 配置意图 | AutoCAD 侧实际结果 | 判定 |
|---|---|---|---|
| c1 | `ANSI37` → `ANSI31`，比例 2.0，角度 30°，样式 Outer | `2=ANSI31` `41=2` `52=0.5236`(30°) `75=1`(Outer) `76=1`(预定义) `78=1`(1 条图案线) `53=1.309`(75°=45°+30°) 偏移模长 6.35 = 源 2.5401×2.5 | ✅ 完全正确 |
| c2 | `GRATE` → 渐变 `LINEAR`，45°，位移 0.1，单色 | `450=1` `470=LINEAR` `460=0.7854`(45°) `461=0.1` `462=0.5` `70=1`(纯色) | ✅ 完全正确 |
| c3 | `ANSI38` → 纯色填充 | `70=1`，图案线清空，`91=1` 边界保留 | ✅ 完全正确 |
| c4 | `JIS_LC_8A` → 用户自定义，间距 5 | `2=U` `76=0`(用户自定义) `45=0` `46=5`(间距精确 5) | ✅ 完全正确 |
| c5 | `ANSI31` → `ANSI37` 且开双线 | `77=1`，`78=2` | ✅ 完全正确 |
| c6 | 只改比例 3.0 / 角度 15° | `41=3` `52=0.2618`(15°) | ✅ 完全正确 |
| c7 | **故意用错枚举**（`PatternFill`=0） | `76=0` → 变成"用户自定义"，图案名形同虚设 | ❌ 反例，见下 |
| c8 | 渐变 `CYLINDER`，0.5rad，位移 0.25，双色，染色 0.75 | `470=CYLINDER` `460=0.5` `461=0.25` `462=0.75` `463=0,1`(双色) | ✅ 完全正确 |

8 个用例的 `AUDIT` 全部 **0 错误 / 0 删除**；边界路径数（组码 91）全部保持 1，改图案不动边界。

### 三个必须记住的坑

**坑 1：`HatchPatternType` 的枚举名是反的。**

| 枚举成员 | 实际值 | DXF 组码 76 的真实含义 |
|---|---|---|
| `HatchPatternType.PatternFill` | **0** | **用户自定义**（UserDefined） |
| `HatchPatternType.SolidFill` | **1** | **预定义**（PreDefined） |
| `HatchPatternType.Custom` | 2 | 自定义文件（CustomDefined） |

所以"把图案换成预定义 ANSI31"要写 `PatternType = HatchPatternType.SolidFill`——名字看着像"纯色"，其实是"预定义"。
写成 `PatternFill` 不会报错、`AUDIT` 也过，但图纸里那个填充的语义已经变成"用户自定义"（c7 实证），
AutoCAD 会按用户自定义规则重新解释图案线，图案名不再起作用。**这是移植时最容易静默写错的地方。**

**坑 2：ACadSharp 没有 `PatternSpace`。**
现有规则的 `Action.PatternSpace`（用户自定义间距）在 ACadSharp 里没有对应属性，
必须转成图案线的偏移向量：`new HatchPattern.Line { Angle = 0, Offset = new XY(0, 间距) }`（c4 已验证等效）。

**坑 3：`Hatch.GradientColor` 不能置 `null`。**
纯色填充要把渐变关掉时，写 `h.GradientColor = null` 会让写出器抛 `NullReferenceException`（实测）。
正确做法是保留对象、只把 `Enabled` 置 false：`if (h.GradientColor != null) h.GradientColor.Enabled = false;`

### 仍然要处理的差异

| 项 | 说明 |
|---|---|
| `PatternType = CustomDefined` | 枚举存在（值 2），但需要用户自己带 `.pat` 图案库，本次未构造用例 |
| 渐变单/双色标志 | `IsSingleColorGradient` 没有反映到组码 451（c2/c8 都是 451=0），双色靠 463 颜色对表达。AutoCAD 能正常打开，但**双色渐变的视觉效果未做像素级验证** |
| 关联填充（Associative） | 源样本无关联填充，未验证。ACadSharp 有 `IsAssociative` 属性但没有"边界变化后重算"能力——改图案不动边界，理论上无影响 |
| `.pat` 图案库 | 必须自带。`.pat` 是纯文本，可以直接内嵌解析（实测读到 83 个图案，`AR-CONC`、`ANSI31` 都在），**不需要依赖本机装 AutoCAD** |
| 目标图案不存在 | 现有实现靠 `SetHatchPattern` 抛 `eInvalidInput` 才发现；移植后应在规则开始前就查一遍图案表，直接 `RuleValidationException` 中止，比现在更早失败、更清楚 |

### 真实图纸验证（`test1.dwg`，2026-10-30）

拿一张真实业务图纸跑完整流程，验证结论不是"实验室数据"：

- **源**：`C:\Users\lijun\Downloads\test1.dwg`，AC1027，2.6 MB，27,346 实体，222 个填充，10 个图层，64 个块
- **任务**：把 12 个 `2_SECONDARY_ESCAPE_ROUTE_-_TEST` 填充改为 `JIS_LC_8A`
- **结果**：12/12 命中并替换 —— 6 个在模型空间（比例 1），6 个在块定义 `VIEW1` 内部（比例 75，详图块按 1:75 出图）

| 验收项 | 结果 |
|---|---|
| AutoCAD `AUDIT` | **0 错误 / 0 删除**（原图本身有 4 条历史错误，改后反而干净） |
| 图案定义行 | `53=0.7854`(45°)、两条线基点 0 与 1、偏移模长 **7.8** —— 与 `acadiso.pat` 的 `45,0,0,0,7.8` 逐字一致 |
| 图案类型 | `76` 从 2(CustomDefined) → **1(PreDefined)** |
| 比例 / 角度 | 原样保留（1 / 75、0°） |
| 边界与关联性 | 边界路径数 `91=1` 保留，关联标志 `71=1` 保留 |
| 整体内容保真 | 79,771 行内容清单，**差异仅 12 行**（6 条旧图案名 + 6 条新图案名），其余 27,334 个实体、图层、块、样式、文字、标注全部未动 |
| 耗时 | 读 1.9 s + 写 0.49 s |

**一个必须提醒用户的行为**：只换图案、不动比例，间距会跟着目标图案走。
本例源图案间距 3，`JIS_LC_8A` 间距 7.8，替换后所有填充**变稀 2.6 倍**。
如果要求"视觉密度不变"，必须同时把 `PatternScale` 乘上 `3 / 7.8 ≈ 0.385`。
这不是 ACadSharp 的问题，AutoCAD 的 `SetHatchPattern` 行为完全相同——但批量跑之前必须先跟使用方确认。

---

## 5. 框架层：不能复用，但逻辑可以照搬

Core 层 5,957 行里有 16 个文件引用 `Autodesk.AutoCAD.*`（全项目共 29 个文件）。差异不是"改个命名空间"能解决的，是**对象模型不同**：

| 现有机制 | AutoCAD | ACadSharp | 对策 |
|---|---|---|---|
| 事务 | `Transaction` + `Commit/Abort` | **不存在**（对象直接改内存模型） | 用"文件级"回滚：保留原始字节，规则失败就重新 `Read` 一份重来 |
| 对象标识 | `ObjectId`（句柄+数据库） | 直接对象引用，`Handle` 只读 | `ScanHit.Id` → `ScanHit.Entity`；去重从 `HashSet<ObjectId>` 换成 `HashSet<CadObject>`（引用比较） |
| 打开模式 | `OpenMode.ForRead/ForWrite` + `UpgradeOpen()` | 无 | 全部删除，直接赋值 |
| 选择集 | `Editor.SelectAll/SelectImplied` | 无 | `Scope=Selection` 直接不支持 |
| DXF 组码过滤 | `TypedValue[]` + `SelectionFilter` | 无 | 只保留"托管判定"这一条通道（现在的 `DxfFilter.Matches` 逻辑照搬，删掉 `ToSelectionFilter`） |
| 表记录遍历 | `tr.GetObject(db.BlockTableId)` + `foreach` | `doc.BlockRecords` / `doc.Layers` 等强类型集合，直接 `foreach` | 大幅简化 |
| 日志 | `Editor.WriteMessage` | 无 | `BatchLogger` 把 `Editor` 换成 `Action<string>`/`TextWriter`，改动约 20 行 |
| `REGEN` / 文档锁 | 需要 | 不需要 | 整段删除 |
| 新增对象登记 | `AppendEntity` + `AddNewlyCreatedDBObject` | `collection.Add(obj)` 即可 | 删除 |

**可以原样复用的部分**：`Config/`（834 行，零宿主依赖）、`RuleExecutionResult`、`RuleFactory`、`RuleSummary`、`RuleJson`、`ConfigSaver`、`NameValidator`、`FieldCatalog`（这些是纯逻辑/字符串处理）。**合计约 3,000 行可直接搬。**

`ExecutionMode` 的语义要重写，而且**能比现在更强**：

| 模式 | 现在 | 移植后 |
|---|---|---|
| Sequential | 单事务，最后统一提交 | 单次读入 → 依次执行 → 一次写出（语义不变，更简单） |
| Isolated | 每条规则独立事务，失败回滚该条 | 每条规则前存一份内存快照（或原始字节），失败则丢弃重来 |
| Parallel | ❌ 不支持（宿主不支持并发写） | ✅ **可以真并行**——按文件并行，多线程各处理各的文件 |

---

## 6. 建议的目标架构

```
Jun.Cad.BatchTool.sln
├─ Jun.Cad.BatchTool.Core/          ← 与宿主无关的批处理引擎（net8.0，可跨平台）
│   ├─ Config/                      ← 从现有 Config/ 原样搬（834 行）
│   ├─ Rules/                       ← 11 条规则（ACadSharp 版）
│   ├─ Engine/
│   │   ├─ CadDocumentSession.cs    读入/写出/版本策略/原子替换
│   │   ├─ DatabaseWalker.cs        BFS 遍历 BlockRecord（去重：HashSet<BlockRecord>）
│   │   ├─ EntityFilter.cs          原 DxfFilter.Matches 的托管判定
│   │   ├─ BatchRunner.cs           文件级循环 + 规则调度 + 统计
│   │   └─ Purge/                   自实现引用扫描
│   └─ Cli/                         batchtool run --config x.json --input dir --pattern *.dwg
├─ Jun.Cad.BatchTool.UI/            ← WinForms（net8.0-windows），去掉"拾取"、加文件/目录队列
└─ Jun.Cad.BatchTool.Tests/         ← xunit，复用现有 88 个用例 + 新增 golden-file 比对
```

**执行流程（单文件）**：

```
读入 dwg (DwgReader)
   ↓ 失败 → 记录并跳过（不影响其它文件）
规范化版本 → AC1032（AC1021/AC1015 一律升级）
   ↓
逐条规则：Filter 命中 → 改对象 → 统计
   ↓ 单条异常 → 按 ContinueOnError 决定是否继续；Isolated 模式回滚到本规则前的快照
写出到 <file>.tmp.dwg (DwgWriter)
   ↓
自检：重新读入 + 实体数/关键属性指纹比对（可选：调用 accoreconsole 跑 AUDIT）
   ↓ 通过 → 原子替换原文件（先备份为 .bak）
```

**三个必须写进实现的工程约定**：

1. **永远以 AC1032 写出**，并在日志里注明"文件格式已从 AC1021 升级为 AC1032"；
2. **写出前先写临时文件，自检通过再替换**，绝不在原文件上原地写；
3. **默认先备份**（`.bak` 或带时间戳副本），批处理是破坏性操作。

---

## 7. 工作量估算

按"新建独立工具、复用配置层与规则语义"的路线（单人，熟悉现有代码的前提下）：

| 工作项 | 规模 | 估算 |
|---|---|---|
| 引擎骨架（Session / Walker / Filter / Runner / CLI） | 约 1,500 行 | 5 ~ 7 人日 |
| 8 条 🟢 规则移植 | 约 1,800 行 | 8 ~ 10 人日 |
| `PurgeUnused` 从零实现 | 约 500 行 | 3 ~ 5 人日 |
| 2 条 🟡 规则的降级处理（表格/MLeader/打印样式） | 约 300 行 | 2 ~ 3 人日 |
| `.lin` / `.pat` 内置解析与内嵌资源 | 约 300 行 | 2 ~ 3 人日 |
| UI 改造（队列、进度、去掉拾取） | 改 3,595 行中的约 800 行 | 4 ~ 6 人日 |
| 验证体系（golden-file 比对 + 真实图纸回归集） | — | 3 ~ 5 人日 |
| **合计** | **约 4,500 ~ 5,000 行新代码** | **27 ~ 39 人日（约 6 ~ 8 周）** |

对比：如果只在现有插件里"加一个 ACadSharp 分支"，会因为事务/选择集/Editor 的语义冲突，成本更高且两套逻辑互相污染——**不建议**。

---

## 8. 风险清单

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| 1 | AC1015 写出器产出的文件 AutoCAD 打不开 | 🔴 高 | 强制以 AC1032 写出（实测可行）；写出后做自检 |
| 2 | AC1021 直接抛异常 | 🟡 中 | 读入后立即改写 `Header.Version`，日志明确告知格式升级 |
| 3 | 未知/代理对象（`ACDBDETAILVIEWSTYLE`、`SUN` 等）以 Unknown 形式保留，写出后行为未知 | 🟡 中 | 已实测 4 张图内容零差异；对含第三方扩展对象的图纸建议先小批量验证 |
| 4 | ACIS 实体（3DSOLID / REGION / BODY）的 ACIS 数据能否原样写出 | ⚠️ 未验证 | 本次样本无实心体。**建议上线前专门找含 3D 实体的图纸跑一轮 AUDIT + 视觉比对** |
| 5 | `PurgeUnused` 自实现误删仍被引用的对象 | 🔴 高 | 引用扫描覆盖：块定义、图层、线型、文字/标注/表格/多重引线样式、注册应用、字典、扩展字典、组；先"只报告不删除"模式跑一轮 |
| 6 | 动态块替换会破坏参数约束 | 🟡 中 | 保持现状（`AllowDynamicBlock=false` 跳过并告警） |
| 7 | 目标版本升级影响下游系统（比如归档要求 2007 格式） | 🟡 中 | 提供 `--dwg-version` 参数，仅允许 AC1024/AC1032，并在日志/报告里标注 |
| 8 | 上游库迭代快（3.8.35 → 3.8.36 仅隔数日） | 🟢 低 | 锁定版本号；MIT 协议，必要时可 fork |

---

## 9. 建议路线

**阶段 0（1~2 天）——先验证你自己的图纸**
拿 5~10 张真实业务图纸（最好包含：3D 实体、动态块、外部参照、大图框），跑 `docs/acadsharp-spike/` 的 `roundtrip` 模式 + `accoreconsole AUDIT`。**这一步能提前暴露 80% 的风险**，成本极低。

**阶段 1（1~2 周）——引擎 + 3 条主力规则**
`BlockReplace` / `EntityLayerChange` / `TextFontChange`。这三条覆盖了 config.json 里最常用的场景，跑通即可让业务方看到效果。

**阶段 2（2~3 周）——补齐其余规则 + PurgeUnused**

**阶段 3（1 周）——UI 与批量队列**（目录扫描、并发、失败重试、报告导出）

**阶段 4（1 周）——回归验收**
用现有 88 个单元测试（纯逻辑部分可直接复用）+ golden-file 比对（同一批图纸，ACadSharp 结果 vs 现插件结果，逐项比对统计数字）。

---

## 10. 附：验证脚本速查

```bash
# 1) 读入并统计（看能不能读、有什么内容）
dotnet run -- inspect "D:/work/A-101.dwg"

# 2) 往返（读入→写出→回读），检查实体数守恒
dotnet run -- roundtrip "D:/work/A-101.dwg" "D:/work/A-101.rt.dwg"

# 3) 强制升级版本后往返（AC1021 / AC1015 必须走这条）
dotnet run -- roundtrip "D:/work/A-101.dwg" "D:/work/A-101.rt.dwg" AC1032

# 4) AutoCAD 内核验收（脚本文件必须 CRLF 换行）
accoreconsole.exe /i "D:/work/A-101.rt.dwg" /s audit.scr
#   audit.scr 内容：AUDIT / Y / QUIT
#   判据：输出 "共发现 0 个错误，已修复 0 个" + "已删除 0 个对象"
```

---

## 11. 最终建议

**做。** 理由：

- 技术上没有拦路虎——保真度实测零差异，六类核心修改全部跑通，AutoCAD 审计干净；
- 收益是结构性的：免 AutoCAD 授权、可上服务器/容器、可跨文件并行、单文件 300ms 级处理速度；
- 代价集中在"重写引擎层"和"重写 PurgeUnused"，这两块都是**可控的工程量**，不是未知风险；
- 现有代码里 3,000 行（配置层 + 规则语义 + 摘要/校验/JSON）可以原样复用，等于白捡。

**前提条件**（不满足就别做）：

1. 业务上能接受**图纸格式统一升级到 2018 版（AC1032）**；
2. 业务上能放弃**图上拾取**和**按选择集处理**这两个交互功能；
3. 上线前必须完成**阶段 0** 的真实图纸验证，特别是含 3D 实体的图纸。
