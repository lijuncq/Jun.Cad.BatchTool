# 文字遮罩失效：根因与修复方案

调查对象：`Jun.AutoCAD.Batch` 的 `TextMask` 规则
调查日期：2026-11-02
测试图纸：`C:\Users\lijun\Downloads\SampleDwg`（11 张，含 30250-001 等）
验证环境：AutoCAD 2025 `accoreconsole.exe`（无头，可执行 LISP、可出图 PNG）

---

> **测试基线（唯一准绳）**：默认 `317 total / 311 passed / 6 skipped / 0 failed`；设置
> `JUN_CAD_SAMPLE_DWG=tmpv/sampledwg` 后 `317 total / 317 passed / 0 skipped / 0 failed`
> （2026-11-05 实跑；修复前为 301 / 296 / 5）。正文出现的 212 / 215 / 291 / 301 / 316 均为历史轮次记录，
> 不代表当前状态；逐条命令、宿主门禁实测与签发范围见 `VERIFY.md`。

## 0. 一句话结论

**遮罩"加上了"但根本不存在。** 工具写出的 `WIPEOUT` 在 AutoCAD 里不擦除任何东西——数据里有实体、句柄、图层，视觉上完全无效。MTEXT 走的是另一条路（原生背景），它没有失效，但它把 `RectangleWidth` 写死成估算宽度，**把文字重新排版了**（一行变两行）。此外块内文字从未进入流程，标注 22 个只处理了 1 个。

所以"遮罩失败"不是一处 bug，是四个独立缺陷叠在一起。

---

## 1. 实测证据

### 实验 1：同样的图、同样的出图流程，只有 AutoCAD 自己画的 Wipeout 生效

在受控测试图上放一行文字，底下 10 条横线穿过：

| 做法 | 出图结果 |
|---|---|
| AutoCAD `_.WIPEOUT` 画框 | 横线在文字处被干净切断，留下白框 |
| 工具输出（`Wipeout` 实体已在 DWG 里） | 横线原样穿过文字，无任何变化 |

出图命令：`-PLOT` + `PublishToWeb PNG.pc3`，两张图除遮罩外完全一致。这是排除了出图管线因素后的对照。

### 实验 2：DXF 组码对比——工具写的 Wipeout 是一个 1×1 单位的方块

同一个 10×5 的遮罩区域：

| 组码 | AutoCAD 自己写 | 工具写出 |
|---|---|---|
| 10 插入点 | `(0,0)` | 文字左下角 |
| 11 U 向量 | `(10,0)` | `(1,0)` |
| 12 V 向量 | `(0,10)` | `(0,1)` |
| 13 尺寸 | `(1,1)` | `(1,1)` |
| **14 裁剪边界** | `(-0.5,0.5) (0.5,0.5) (0.5,0) (-0.5,0) (-0.5,0.5)` | **`(-0.5,-0.5) (0.5,0.5)`** |
| **71 边界类型** | `2`（多边形） | **`1`（矩形）** |
| **91 顶点数** | `5`（闭合环） | **`2`** |
| **280 边界版本** | `1` | **`0`** |

`MaskService.CreateWipeout`（`Engine/Mask/MaskService.cs:137`）给的是世界单位坐标 `(0,0)-(w,h)`，库在写出时把它压成 `±0.5`。结果就是每个遮罩都退化成文字左下角一个 **1×1 图形单位**的小方块——比文字小两个数量级，所以肉眼完全看不出。

### 实验 3：换成"正确形状"也救不回来

按 AutoCAD 的组码结构重造（闭合 5 点环、`±0.5` 归一化、U/V 给足尺寸、插入点取矩形中心、`ClassVersion=1`）后：

- ACadSharp 能读出 5 个 `Wipeout`；
- **AutoCAD 一个 `WIPEOUT` 都认不出来**（`(ssget "_X" '((0 . "WIPEOUT")))` 返回 0）。

结论：ACadSharp 3.8.35 的 Wipeout 写出器不可用。不是参数没给对，是这条路走不通。

### 实验 4：MTEXT 的 `RectangleWidth` 会把文字重新排版

工具输出里，`MTEXT OVER LINES` 被拆成了两行 `MTEXT OVER` / `LINES`。原因：`MaskService.ApplyMTextBackground`（`MaskService.cs:76`）在 `RectangleWidth<=0` 时写入估算宽度（35.77）。DXF 组码 41 是**定义宽度**，AutoCAD 会按它重新折行。

估算宽度用的是"汉字 1.0×字高、西文 0.62×字高"（`TextBounds.MeasureWidth`），真实 SHX 字体的西文系数在 0.7 上下，估算偏窄 10% 以上——偏窄就会折行。

### 实验 5：真实图纸的覆盖情况（30250-001）

| 项目 | 数量 | 实际处理 |
|---|---|---|
| 模型空间 MTEXT | 62 | 58 加背景，4 判为"无遮挡" |
| 模型空间 DIMENSION | 22 | **1** 个写入 `*D` 块，21 个判为"无遮挡" |
| 模型空间 TEXT | 0 | — |
| **块定义内文字** | **318（72 个块定义）** | **0，全部跳过** |

---

## 2. 四个根因

### 根因 A：Wipeout 写出不可用（TEXT / ATTRIB / DIMENSION 全部落空）

`MaskService.CreateWipeout` 用世界单位给裁剪边界，与库的写出格式不兼容（见实验 2、3）。凡是走"新建遮罩实体"的路径——TEXT、ATTRIB、`MaskMode=Solid` 降级、标注 `*D` 块内——产出的都是无效遮罩。

代码里唯一的把关是 `CapabilityProbe.WipeoutWriter()`（`Rules/TextMaskRule.cs:72`），它只验证"能写出来、能读回来"，不验证"AutoCAD 认不认、擦不擦得掉"。所以探测一直通过。

### 根因 B：块内文字被无条件跳过

```csharp
// Rules/TextMaskRule.cs:433
if (hit.Depth > 0)
{
    result.MarkSkipped("块内实体不处理");
    continue;
}
```

不看配置。用户把 `IncludeNestedBlocks` 打开也没用——这条判断在过滤器之后、与它无关。30250-001 一张图就有 318 个块内文字被丢掉。

### 根因 C：遮挡判定看不到"块参照"和"块内几何"

```csharp
// Rules/TextMaskRule.cs:55
bool useBlockBounds = !RuleContext.Bool(action.MaskIgnoreBlockBounds, true);  // 默认 true → useBlockBounds = false
// Rules/TextMaskRule.cs:600
if (!useBlockBounds && entity is Insert) continue;   // 块参照一律不参与判定
```

后果（30250-001 实测）：

- 标注文字实际压着 `Insert`（符号块）和别的标注，但两者都被排除，判成"遮挡 0%"→ 跳过。22 个标注只活下来 1 个；
- 文字压在块参照内的填充/线条上时，同样判成"无遮挡"；
- `ComputeOcclusion` 对 `Polyline2D`、`Hatch` 用包围盒面积比，长斜管的包围盒会横跨整张图——既可能虚高也可能漏判。

判定口径本身也值得质疑：需求是"底下有其它线条就加"，实现是"遮挡面积 ≥5%"。一条线穿过记 25%，两条线算 50%，这是拍出来的权重，不是几何事实。

### 根因 D：绘制顺序只改了集合顺序，没进排序表

`MaskService.PlaceMaskBefore`（`MaskService.cs:180`）靠"移出文字 → 追加遮罩 → 再追加文字"调整顺序。这在**没有** `SORTENTSTABLE` 的图上有效（句柄顺序也正好一致）。但真实图纸里有排序表（30250-001 有 5572 条），此时绘制顺序由排序表决定，新遮罩不在表里，只能靠句柄兜底——句柄最大 = 画在最上层。

顺带：`PushHatchesToBack` 只对"已经加了遮罩的文字"生效，被判"无遮挡"的文字不动填充；而它调用的 `MoveToTop` 语义是反的（项目自己已经记过这条）。

### 附带缺陷：旋转文字的遮罩是轴对齐大盒子

`ROT90`（3.5 高、约 6 长、旋转 90°）算出的遮罩是 6.8×14.1 的轴对齐矩形，比文字本身大一圈，会擦掉旁边的图形。`CreateWipeout` 只支持轴对齐，`Bounds2d` 存的是 AABB，旋转信息在 `MaskTarget.Rect` 里已经丢了。

---

## 3. 修复方案

按"必须先做"到"可以后做"排列。前两项不做，后面全是白工。

### 3.1 换掉 Wipeout 的构造方式（阻断级）

三条路，按推荐度排序：

**方案 1（推荐）：修 Wipeout 的几何编码，并加"渲染级"验收。**

`WIPEOUT` 的世界几何 = 插入点（框架中心）+ U/V（框架尺寸向量）+ 归一化到 `[-0.5,0.5]` 的裁剪顶点。按 AutoCAD 的实测结构写：

```
InsertPoint = 矩形中心
UVector     = (Width, 0, 0)
VVector     = (0, Height, 0)
Size        = (1, 1)
顶点        = 闭合环 5 点，坐标落在 ±0.5
ClassVersion= 1
```

但实验 3 表明 ACadSharp 连这个都写不出 AutoCAD 认的实体——**这条路要先解决库的问题**：要么换库/等库更新，要么在写出前直接改写 DWG 的实体段（代价大，不建议），要么接受方案 2。

**方案 2（可立即落地）：遮罩实体改用 2D Solid。**

`MaskService.CreateSolid` 是正确的（测试图里的 SOLID 在出图里正常渲染成实心块，能盖住文字）。Solid 是普通实体，没有编码陷阱。

代价与前置条件：

- 它是**不透明实色**，不是真"擦除"。屏幕上黑底会看到白块；打印时颜色由 CTB 决定——`monochrome.ctb` 会把白块打成黑色，遮罩变成黑块。
- 当前 `MaskLayerPlottable` 默认 false（`TextMaskRule.cs:103`），Solid 放不打印图层 = 出图时遮罩不存在。**必须改成可打印**，并且要求用"真彩色 255,255,255 + 不随层"或专门的 CTB。
- 因此 Solid 只能作为 `MaskMode=Solid` 的显式选项，不能悄悄替换默认路径。

**方案 3：MTEXT 走原生背景，TEXT/ATTRIB/DIMENSION 走"复制为 MTEXT"或暂不支持。**

对 TEXT 可以在重建时直接换成 MTEXT（保留插入点、字高、对齐、旋转），复用原生背景遮罩。代价是实体类型变了，下游按 `TEXT` 过滤的规则会漏。

我的判断：**先做方案 2 把"能用"这条线补上（明确写进配置和文档的取舍），同时把方案 1 作为库层面的待办**。理由是 Wipeout 是唯一在显示和出图两端都正确的机制，长期必须靠它。

### 3.2 MTEXT 不要动 `RectangleWidth`（阻断级）

`ApplyMTextBackground` 里补宽度的两段（`MaskService.cs:76-101`）应当去掉或改成显式开关，默认关。

依据：

- 组码 41 是"定义宽度"，写非零值会让 AutoCAD 重新折行——这是**改了用户的图**，比没加遮罩严重；
- 项目此前"41=0 就画不出背景"的结论需要重做实验：本次无头出图管线里，MTEXT 背景（90=2）无论谁写的都看不到白框，说明当时的判断可能把"出图管线看不到"误判成"AutoCAD 画不出"。这条结论在报告里不能继续当事实用。

如果确认必须靠 41 才能出背景，正确做法是：宽度取**排版后的真实宽度**（需要字形度量，库没有），或让 AutoCAD 自己算——离线工具做不到，那就退回方案 2 的 Solid。

### 3.3 块内文字：进块定义，而不是跳过（需求硬要求）

把 `TextMaskRule.cs:433` 的 `Depth > 0` 跳过改成受配置控制，并区分两种语义：

| 目标 | 做法 | 影响 |
|---|---|---|
| 块定义（默认） | 遮罩实体加进 `BlockRecord.Entities`，排在文字之前 | 所有插入共享一次，改一处全部生效 |
| 每个插入单独加 | 在模型空间按插入变换算出文字框，各自加遮罩 | 支持非等比缩放/镜像，但实体数 = 插入数 |

块内实体自带图层、不随参照图层走（项目已有这条经验），遮罩放进定义时要用 `_MASK` 图层而不是 0 层。动态块要同时处理匿名实例块与 `DynamicBlockTableRecord`，`*D`/`*T` 匿名块要跳过——这些 `BlockLayerChangeRule.MoveDefinitionContent` 已经踩过一遍，可以直接复用它的去重逻辑。

注意语义：块定义里加遮罩 = 所有插入都变。若用户只想改某几个插入，得走"每个插入单独加"。建议默认走定义级，并在预览里写明"将影响 N 个块参照"。

### 3.4 遮挡判定：改成"有几何相交"，并纳入块参照与块内几何

> **状态：本节方案未实施**（2026-11-05 复核）。当前实现仍是面积比 + 0.25 权重 + 排除 Insert；默认把 `MaskOnlyWhenOccluded` 关掉以绕过，不是修复。

1. 把 `Insert` 纳入遮挡源。两种粒度：
   - 粗：用块参照的包围盒（`MaskIgnoreBlockBounds=false`），实现简单，误判多；
   - 细：把块定义内的几何按插入变换展开到世界坐标再判定。30250-001 的 98 个 `Insert` 有 23 个包围盒取不到（库返回 NaN），粗粒度会漏，建议做细粒度但加缓存（同一块定义 + 同一变换只算一次）。
2. 判定口径从"面积比 ≥ 5%"改成**二值**：文字矩形与任一非文字几何相交即加遮罩。理由：需求原话是"底下有其它线条就加"；面积比引入了两个拍脑袋的参数（0.05 阈值、每条线 0.25 权重），且对细长图形必然出错。
3. 排除项保留：`Defpoints`、遮罩图层自身、已有遮罩、以及**其它文字**（文字压文字不需要遮罩，那是排版问题）。
4. 标注自身的 `*D` 几何不应算作遮挡源（否则每个标注都 100% 遮挡）。现在因为标注是 target 而被排除，属于"碰巧对"，要显式写清。

### 3.5 绘制顺序：进排序表，别只改集合顺序

新建遮罩后必须保证"遮罩在文字之下、在被遮的几何之上"。三个条件：

1. 遮罩实体创建在文字**之前**（现有 `PlaceMaskBefore` 的思路是对的，靠句柄序兜底）；
2. 如果空间有 `SORTENTSTABLE`，遮罩和文字都要有明确排序值——遮罩的 sort handle 取"文字 sort handle − 1"；库若做不到，就把文字从排序表里移除，让句柄序生效；
3. `MaskSendHatchToBack` 要在**所有**遮罩创建完之后统一执行，而不是每加一个遮罩就搬一次；并且要覆盖"被判无遮挡"的文字——只要文字矩形下有填充，填充就该在文字之下。

### 3.6 旋转文字：遮罩跟着转

`Bounds2d` 只存 AABB，旋转信息在 `TextBounds.RotatedRect` 之后丢了。改法：`MaskTarget` 增加"旋转矩形"（中心 + 宽高 + 旋转角），Wipeout 用 U/V 表达旋转（`U = (cosθ, sinθ) * W`，`V = (-sinθ, cosθ) * H`），Solid 用四个角点。否则 90° 竖排文字的遮罩永远是错的。

### 3.7 标注：先验证 DIMTFILL，再决定要不要进 `*D` 块

`DimensionStyle.TextBackgroundFillMode`（DIMTFILL）是标注的原生遮罩，不新增实体、跟着标注走、重生成不丢。项目记录说"库能写、AutoCAD 读不回来"——这条结论值得用本次的 accoreconsole 手段重验一次（写 DIMTFILL=2 → 存盘 → `(getvar "DIMTFILL")` 读回 → 出图看有没有白框）。

如果读得回来，这是标注的最优解，`*D` 块方案可以退役。如果读不回来，`*D` 块方案必须补两条限制说明：标注重生成即丢失、块内文字句柄会变。

---

## 4. 验收标准

修完必须能过这五条，缺一条都算没修好：

1. **受控图出图对比**：文字底下放横线 → 出图后横线在文字处断开（截图对照，不能只看"实体存在"）。
2. **块内文字**：块定义里有文字 + 插入压在线上 → 输出后该块参照处有遮罩。
3. **MTEXT 不被重排版**：修复前后 MTEXT 的 `PlainText` 分行数一致。
4. **标注**：22 个标注里压在几何上的那些都有遮罩，且出图可见。
5. **顺序**：填充压着文字时，输出图里填充在文字之下（排序表可查）。

现有的 `CapabilityProbe.WipeoutWriter` 要跟着改：只验证"能写能读"是不够的，**必须验证 AutoCAD 认不认**（例如写一个测试图，用 accoreconsole 读回 `(ssget "_X" '((0 . "WIPEOUT")))` 的计数）。

> 本节 5 条验收的可执行命令与断言见仓库根 `VERIFY.md`；其中宿主渲染项需 AutoCAD 会话，库层往返不构成验收。
> **2026-11-05 补记**：`VERIFY.md` 的第 2–5 项已在 AutoCAD 2025 Core Console 上实跑（受控图
> `tmpv/hostverify-2026-11-05/`）；`ssget` 计数 / MTEXT 与 DIMENSION 组码 / `-PLOT` 逐像素差分均通过，
> `WIPEOUTFRAME` 仍为已知未修复项。第 5 条「排序表可查」的验收口径已废弃——`SORTENTS` 写不出去
> （见 §9.7 与 `VERIFY.md` 的 C01），当前靠实体集合顺序实现文字前置。

---

## 5. 已实施的修复（2026-11-02）

> **状态：本节（§5）与 §8 为历史实施记录，已作废。** 当前唯一有效实现见 §9.2 / §9.6 / §9.7：只有原生遮罩一条路（MTEXT 背景 90=3、TEXT/ATTRIB 的 Wipeout、标注 DIMTFILL=1），没有 2D Solid 兜底，也没有 `MaskMode=Wipeout` 抛异常的行为。本节提到的 `WipeoutWriteSupported`、`MaskSolidColor`、`CreateSolid` 在当前源码中均不存在。

按"先修阻断级两项"执行，改动如下。

### 5.1 停止改写 MTEXT 的 `RectangleWidth`（`Engine/Mask/MaskService.cs`）

`ApplyMTextBackground(MText, double paddingRatio)`：不再补宽度 / 高度，只置背景标志（bit0+bit1）、`BackgroundColor`、`BackgroundScale`；**绝不碰 `RectangleWidth`**。（本节原写的单参数签名是中间态，见 §9.6。）

**修完的实测**：受控图上 `MTEXT OVER LINES` 恢复成一行（修复前被拆成两行）；出图里它背后的横线被背景干净切断——说明原生背景本来就有效，之前"必须补 41"的判断是错的。

### 5.2 Wipeout 标记为不可用，遮罩改走 2D Solid（`Rules/TextMaskRule.cs`）

- 新增 `MaskService.WipeoutWriteSupported = false`，注释里写清三条证据；
- `MaskMode=Auto` 落到 Solid，并在日志里说明原因；
- `MaskMode=Wipeout` **直接抛 `RuleValidationException`**，不静默降级；
- `MaskLayerPlottable` 默认从 false 改成 **true**——Solid 不打印就等于没有遮罩；
- 新增 `MaskService.MaskSolidColor(255)` → **真彩色白**（`RGB 255,255,255`）。ACI 255 会被 `monochrome.ctb` 打成黑块（实测：用 ACI 1 时出图是一块黑板），真彩色不受 CTB 重映射。

### 5.3 块内文字改为写进块定义（`Rules/TextMaskRule.cs`）

- 新增配置 `MaskInsideBlocks`（默认开）；
- `Depth > 0` 不再无条件跳过，改为按配置处理；匿名块枚举为 `*D`（标注缓存）、`*T`（表）、`*U`（动态块匿名实例）：动态块 `*U` **现已支持但有限制**（复用宿主块定义与去重，不展开动态参数/夹点状态），外部参照与 `*D`/`*T` 仍为显式 Non-Goal；
- 遮罩加进 `BlockRecord.Entities`，排在文字之前；`DatabaseWalker.CollectFrom` 用 `visited` 对每个块定义只遍历一次，不会按插入数重复加；
- 预览备注写明"遮罩写进块定义「X」，直接引用 N 处"；
- **块内文字跳过遮挡判定**：块定义里的文字会不会被压住，取决于每个插入插到哪、旁边画了什么，定义层面看不到。拿定义自身实体去判永远是"无遮挡"——这正是块内文字一个遮罩都加不上的原因。判不了就不判，按"宁可多加"处理，`MaxMasksPerDrawing` 兜底。

### 5.4 顺带修掉的计数 bug

汇总里的"MTEXT 原生背景 / 标注 *D 块内 / Solid"三类计数，原来按 `target.Entity is MText && !rebuildText` 判断——`rebuildText` 是配置开关不是实际路径。开了 `MaskRebuildText` 之后，走原生背景的 MTEXT 全被计成 "Solid"（30250-001 实测：报 142 个 Solid，实际 0 个）。改成由 `ApplyMask` 用 `out MaskPath` 报告真实分支。

### 5.5 验证结果

| 项 | 结果 |
|---|---|
| 单元测试 | 212 个全过（历史轮次；`TextMaskRuleTests` 当时从 7 个改写为 10 个） |
| 30250-001 | 匹配 304，改动 146（MTEXT 原生 142 / 标注 *D 4），跳过 158；输出无 Wipeout，`RectangleWidth` 全部保持 0 |
| 受控测试图出图 | MTEXT 不再折行；TEXT / 标注的白色 Solid 能切断背后的横线；块内 MTEXT 背景有效 |

### 5.6 仍然没解决的（下一步）

1. **已解出（见第 8 节）**：AutoCAD 把 WIPEOUT 的裁剪顶点当**图像像素坐标**，不是图形单位；原点在包围框最小角，像素边长取包围框长边，Y 轴翻转、顶点归一到 ±0.5。库层新增 `Wipeout.SetMaskingArea(IEnumerable<XY>)` 承担这段换算。（历史：早期曾认为 Wipeout 编码映射没解出来，于第 8 节推翻）
2. **块内 TEXT 的 Solid 位置略偏**：出图里遮罩框比文字框低约一个字高，会盖住文字的下一行。`TextBounds.ForText` 对块定义坐标系里的 `InsertPoint`/`AlignmentPoint` 取值需要复核（红/白对照实验可见）。
3. **遮挡判定仍看不到块参照**：压在块参照上的模型空间文字仍被判"无遮挡"。`MaskIgnoreBlockBounds` 需要改成"展开块定义几何"而不是简单忽略。

---

## 6. 必须承认的未验证项


1. **已确认（2026-11-02 下午，修复后复核）**：不碰 `RectangleWidth` 时，原生背景在出图里能干净切断背后的横线（受控图 `MTEXT OVER LINES` 与块内 `BLOCK MTEXT` 两处都验证到）。所以"必须补 41 才画得出背景"这条旧结论是错的，补 41 只会换来折行。（历史：早期曾认为 MTEXT 原生背景在出图里能否看到白框未确认，2026-11-02 下午修复后复核推翻）
2. **已解出**：旧模型对不上的原因是把裁剪顶点当成了图形单位。实际口径是"插入点=包围框最小角、U/V=长边、顶点=归一化像素坐标且 Y 翻转、`ClippingState=1`、image definition/reactor 句柄写 0"。实测 AutoCAD 读回 `340=0 / 360=0 / 280=1 / 71=2 / 91=5`，与原生 Wipeout 一致，出图能切断横线（8.1 节）。（历史：早期曾认为 Wipeout 的正确编码模型没有完全解出，于本节推翻）
3. 诊断阶段（本报告 1–4 节）没有改动产品代码；5 节的修复是之后按"先修阻断级两项"的决策单独实施的。所有结论基于 `SampleDwg` 的 11 张图和 1 张受控测试图。
4. **3.7 提出的"DIMTFILL 是否读得回来"已有答案：读得回来，且出图生效**（9.1 节）。`*D` 缓存块方案因此退役。（MTEXT 偏移因子"尽力而为"的旧结论已由 §9.6 推翻，见 §9.6:453。）

---

## 7. 复现材料

| 文件 | 用途 |
|---|---|
| `tmpv/masktest/mktest.lsp` | 造受控测试图（文字/线条/实心/标注/块） |
| `tmpv/masktest/render.lsp` | 无头出图 PNG（`-PLOT` + `PublishToWeb PNG.pc3`） |
| `tmpv/masktest/inspect.lsp` | 用 AutoCAD 读回 MTEXT 背景码、Wipeout 组码、排序表 |
| `tmpv/masktest/render-src.png` / `render-out.png` | 源图 / 工具输出对比（MTEXT 折行可见） |
| `tmpv/masktest/render-ctrl.png` | AutoCAD 原生 Wipeout 对照（横线被切断） |
| `tmpv/masktest/dump.txt` / `dump3.txt` | 工具写出 vs AutoCAD 写出的 Wipeout 组码 |
| `tmpv/masktest/wipeout-final.png` | **修复后的 Wipeout 出图**：横线在遮罩内部被切断（8.1 节的像素证据） |
| `tmpv/maskrun/ctrl-src.png` / `ctrl-out.png` | 受控图跑真实配置的"加遮罩前 / 后"出图对照 |
| `tmpv/diag/` | ACadSharp 诊断工具（包围盒可用性、标注遮挡来源、块内文字统计） |
| `.workbuddy-ai/bgprobe/DimFillProbe.cs` / `DimScan.cs` | 写 DIMTFILL 覆盖 / 回读统计（`*D` 块内 Wipeout 数、覆盖键总数） |
| `.workbuddy-ai/bgprobe/out/dim-ac.png` | **DIMTFILL 受控出图**：加了覆盖的标注把横线切断，对照标注连续（9.1 节）；扫描结果在同目录 `dimacscan.txt` |
| `tmpv/masktest/pngscan.py` | 无依赖 PNG 解码 + 暗像素区间扫描（本机没有 PIL，`Add-Type` 也被拦） |

accoreconsole 的两个坑（写在这里省得下次再踩）：

- `/i <dwg>` 在本机被忽略，必须脚本里 `(setvar "FILEDIA" 0)` + `(command "_.OPEN" ...)`；
- 脚本走 `/s` 参数不生效，要**管道喂 stdin**；且 LISP 里**不能有中文注释**（按 ANSI 读，会把后续代码一起吃掉）。

---

## 8. 接入 ACadSharp 修复版（2026-11-02）

> **状态：§8 的「Auto 优先 Wipeout、探测不通过降级 Solid」已作废**（见 §9.2）。§8 的 ACadSharp 库层接入与渲染实验仍有效，但产品规则的降级路径从未落地。

### 源码改动

本项目现在通过 `src/Jun.Cad.Batch.Core/Jun.Cad.Batch.Core.csproj` 项目引用使用本地克隆 `D:\Dev\CSharp\AutoCAD\ACadSharp-v3.8.35`（分支 `fix/wipeout-dxf-dwg-serialization`），不再用 NuGet 二进制。库层改动包括：

- `Wipeout.SetMaskingArea` 将 WCS 多边形转为归一化像素坐标，Y 轴翻转，按外包框长边设置图像 frame；默认 clipping 开启；
- DXF/DWG 写入器对 WIPEOUT 的 image-definition 与 reactor 句柄强制写 0；
- Core 的 `MaskService` 对矩形与旋转文字四角都调用该 API；`TextBounds.ForWipeout` 按像素坐标和 U/V 反算 WCS 外包框；
- `MaskMode=Auto` 优先走 Wipeout，探测不通过才降级 Solid；显式 `Wipeout` 不通过时明确报错。MTEXT 仍使用原生背景且不改 RectangleWidth。

### 验证结果

- ACadSharp 单体回归：AC1032 的 DXF 与 DWG `SingleWipeout` round-trip **2/2 通过**。
- AutoCAD 2025 Core Console 读取库输出：`ssget` 检出 1 个 WIPEOUT，关键组码为 `340=0 / 360=0 / 280=1 / 71=2 / 91=5`。
- 项目整套回归：**215/215 通过（历史轮次）**；`Jun.Cad.Batch.sln` Rebuild **0 errors**。上游 ACadSharp 生成 67 条既有编译 warning（XML cref、nullable context、obsolete 等），产品项目自身没有构建错误。
- `preflight` 显示 Wipeout 写出链路通过（"DWG 往返一致（4 点多边形、clipping on、无 IMAGEDEF、WCS 包围盒 100×50）"），表格检查因未给含表格样图为"未测"，标注遮罩仍标记为静态降级。
- 出图渲染：见 8.1，横线在遮罩内部被切断（像素级核对）。

> 本地克隆的 `src/ACadSharp/ACadSharp.csproj` 被改成只编 `net8.0`：本机只有 .NET 8 SDK，`net9.0/net10.0/net48/netstandard` 目标都还原不了。这一条**不属于修复内容**，提 PR 前要还原成原来的多目标列表。文件里有注释标明。

### 尚未完成的验收

**已解决（2026-11-02 晚）**：早先"线穿过遮罩"的图不是编码错误，是**绘制顺序**错误——测试图里先加 Wipeout 再加线，线后画就盖在遮罩上。把线放到遮罩之前（`Entities.Add(line)` 再 `Entities.Add(wipeout)`）重出图，横线在遮罩边界处被干净切断（见下节 8.1）。

**仍需人工确认的只剩两件事**（2026-11-05 复核后收窄）：

1. 交互式 AutoCAD 会话里的观感（屏幕上和打印预览）——本机只能跑 Core Console 无头出图，没有做界面截图。大批量处理前建议在副本上开一次图，核对 `(0 . "WIPEOUT")`、`DRAWORDER` 和肉眼效果。
2. 含 `SORTENTSTABLE` 的图纸保存后 `DRAWORDER` 是否被重置（C01）：8.3 的受控图
   `sortents= missing`，破坏性路径根本没被触发，既不能证明已修也不能证明安全。

Core Console 侧的 `ssget` / `entget` / `-PLOT` 逐像素已在 **8.3 用修复后产物重跑完毕**，
不再是「待人工确认」；`WIPEOUTFRAME` 仍为已知未修复项。

### 8.1 渲染验收（2026-11-02 晚，已完成）

受控图：一条 `(-5,2.5)-(15,2.5)` 的横线 + 一个覆盖 `(0,0)-(10,5)` 的 Wipeout，**线先入图、遮罩后入图**。用 `accoreconsole` + `PublishToWeb PNG.pc3` 出 1600×1280 的 PNG（`tmpv/masktest/wipeout-final.png`），逐像素扫描：

| 行 | 暗像素区间 | 含义 |
|---|---|---|
| y=440 | 399–1199 | 遮罩上边框（横贯） |
| y=639 | **0–399 / 1199–1598** | 横线：**在 399 处断，1199 处接上** |
| y=839 | 399–1199 | 遮罩下边框（横贯） |

横线在 x=399..1199 之间完全消失，而这段正好是遮罩内部 → **擦除生效**，不是"白块盖住"（真彩色/ACI 都不参与，遮罩本身不打印任何东西）。

这条结论同时纠正了本项目长期以来的两个误判：

1. "ACadSharp 写出的 Wipeout AutoCAD 不认" —— 错，是**几何编码**不对（像素坐标 vs 世界坐标、`ClippingState` 没开、image definition 句柄非 0）；
2. "遮罩加了但看不见" —— 多半是**顺序**：遮罩必须先于被遮对象入图（`TextMaskRule` 的 `PlaceMaskBefore` 做的正是这件事）。

### 8.2 边框问题（未修复，仅白色实体色规避）

Wipeout 出图时会带一个矩形边框（`WIPEOUTFRAME` 默认 1 = 显示并打印），受控图里 `TEXT OVER LINES` 外面就套了一圈方框。

| 做法 | 实测结果 |
|---|---|
| 往根字典 `ACAD_WIPEOUT_VARS` 写 `WipeoutVariables.DisplayImageFrame = false`（DXF 70=0） | 文件层面是对的：DXF 里 `70=0`、ACadSharp 回读 False；但 **AutoCAD 2025 重开仍报 `WIPEOUTFRAME=1`**，`dictsearch` 读到的 70 也是 1。对比 AutoCAD 自己存 `WIPEOUTFRAME=0` 的图（同样 `70=0`）说明存储位置没错，是上游 DWG 写出器的写法有出入 |
| 会话里 `(setvar "WIPEOUTFRAME" 0)` 再出图 | 边框消失、横线照样被切断 → 确认方框就是 Wipeout 边框 |
| 遮罩实体颜色改成**真彩色白** | 边框画成白色、白纸上不可见；出图与"关掉边框"那张肉眼一致，`y=767` 行由源图的连续 `340–678` 变成 `524–678`（左半段被擦掉） |

结论：**擦除已验证，边框未修复**。写入 `70=0` 在 AutoCAD 2025 重开后仍为 `WIPEOUTFRAME=1`，白色实体色只在白底出图上让边框不可见，深色绘图区仍可见白色细框。需上游 DWG 写出器修复，产品侧无解。
`WipeoutVariables` 那次写入保留——对 DXF 消费方是正确的，等上游修好后直接生效。

### 8.3 修复后宿主复验（2026-11-05，已完成）

审查 + 全量修复之后，用**当前代码的新产物**在 AutoCAD 2025 Core Console 上重跑了 `VERIFY.md` 的
第 2–5 项。工作目录 `tmpv/hostverify-2026-11-05/`：
`in/control.dwg`（受控源图，`tmpv/masktest/mask-src.dwg` 副本）→ CLI 用 `config.json` 生成
`out/control.dwg`（日志：`共为 11 处文字加了遮罩（MTEXT 原生背景 4 / 标注文字 1 / Wipeout 6）`，
`匹配 11，改动 11，跳过 0，失败 0`）。

| 项 | 实测 | 判定 |
|---|---|---|
| `ssget "_X" '((0 . "WIPEOUT"))` | 5 个，句柄 `29B 2AF 2A8 2A3 2A0`；全部 `layer=_MASK / 71=2 / 91=5 / 280=1`；`entget` 回读 `340=nil / 360=nil`（影像定义句柄为 0） | 通过 |
| 计数口径 | `tblnext "BLOCK"` + `entnext` 显示块定义 `TBLK` 内还有 1 个 WIPEOUT（`2B2`）和 1 个 MTEXT（`2B5`，`90=3 / 63=7 / 45=1.15`）。**`ssget "_X"` 不枚举块定义内部实体**，所以 CLI 的 `Wipeout 6 = 模型空间 5 + 块内 1`、`MTEXT 4 = 3 + 1`。这不是丢实体，恰好证明 `MaskInsideBlocks=true` 生效 | 澄清 |
| MTEXT `entget` | 模型空间 3 个（`2AB / 2A6 / 29E`）全部 `g90=3 / g63=7 / g45=1.15 / g41=0.0` | 通过 |
| DIMENSION `entget` | `((ACAD (1000 . DSTYLE) (1002 . {) (1070 . 140) (1040 . 3.5) (1070 . 69) (1070 . 1) (1002 . })))`：原有 `140/3.5` 保留，`69/1` 追加 → C03 的局部 DSTYLE 替换在宿主侧确认 | 通过 |
| `-PLOT` 逐像素 | 源图 / 输出图 `EXTMIN/EXTMAX` 同为 `(0,0,0)/(330,74.7062,0)`，`_.ZOOM _E` 同口径。y=746–748 源图 `0–289` → 输出 `0–20 / 255–289`；全图暗像素 `56398 → 47669`，**`erased=8729 / added=0`**（只擦除、不新增黑块/边框像素） | 通过 |
| `WIPEOUTFRAME` / `ACAD_WIPEOUT_VARS` | 重开仍为 `(WIPEOUTFRAME= 1)`、`(WIPEOUT_VARS_70= 1)`；`sortents= missing` | C11 未修；C01 未触发 |

脚本与结果：`verify.scr`/`host-result.txt`、`blocks.scr`/`block-result.txt`、
`detail.scr`/`detail-result.txt`、`plot-src.scr`/`host-plot-src.png`、`plot.scr`/`host-plot.png`、
`extents.scr`/`extents-result.txt`；逐像素用 `tmpv/masktest/pngscan.py`。

两个踩过的坑，留档：

1. **`-PLOT` 的纸张尺寸参数必须传 `""`**：本机 AutoCAD 是中文版，硬编码
   `"Sun Hi-Res (1600.00 x 1280.00 Pixels)"` 被判为无效纸张名，`-PLOT` 提前结束，
   后续 `_L/_N/_E/...` 全被当成命令（报「未知命令 L/N/E/…」），PNG 根本不生成。
2. **LISP 结果文件是 UTF-8，不是 UTF-16**：本次 `(open ... "w")` + `write-line` 写出来的是
   纯 ASCII/UTF-8；按 UTF-16 解码会得到满屏汉字乱码。PowerShell 的 stdout 仍是 UTF-16/本地化乱码，
   所以结论一律以结果文件为准。

本次仍**不签发**：C01（本图无排序表，破坏性路径未触发）、C11（`WIPEOUTFRAME=1`）、
C20、C21、以及交互式界面观感。严格签发范围见 `VERIFY.md`。

---

## 9. 标注走 DIMTFILL、Solid 退役（2026-11-02）

这一节回答 3.7 留下的那个问题，并记录本轮把"遮罩"整体收敛到原生路径的结果。

### 9.1 DIMTFILL 复验：读得回来，而且出图生效

用 8.1 那套手段（accoreconsole 读回 + `-PLOT` 出 PNG + 逐像素扫描）重验：

| 实验 | 结果 |
|---|---|
| 库侧往返 | 手写的 DSTYLE XData（`1000 "DSTYLE"` / `1002 "{"` / `1070 69` / `1070 1` / `1002 "}"`）能被 ACadSharp 写出去再读回来 |
| AutoCAD 2025 读回 | `entget` 里确实带着 `(-3 ("ACAD" (1000 . "DSTYLE") (1002 . "{") (1070 . 140) (1040 . 3.5) (1070 . 69) (1070 . 1) (1002 . "}")))` —— **不是"库能写、AutoCAD 读不回来"** |
| 受控出图 | 同一张图两条横贯线：加了 `DIMTFILL=1` 的那条在 y=358 被文字切断（暗像素 `0-793` → 文字像素 794–806 → `807-1599`）；未加覆盖的对照标注在 y=342 连续 `0-1599` |
| 真实图纸（30250-002） | 带覆盖的标注 **0 → 30**，覆盖键总数 **750 → 780**（每个标注只多一项，原有的 140 等键全部保留） |

所以 `*D` 缓存块方案退役。旧方案往 AutoCAD 自动生成的缓存块里塞 Wipeout，标注重生成就全丢，而且 TEXT 那条分支会改句柄；`DIMTFILL` 是样式覆盖，跟着标注本身走，不新增实体、重生成保留；**默认配置下 `MaskBringTextToFront=true` 会把整个标注前置，标注获得新句柄**（见 §9.7:472）。

两个实现细节，踩过坑才对：

* 当前实现：`dimension.GetStyleOverrideMap()` 取已有覆盖 → `map.DxfProperties[69] = fill` → `dimension.SetStyleOverrideMap(map)`。早期试过的 `SetDimensionOverride(clone)` 会带出多余 `340=0` 已弃用；`new DimensionStyle("*override")` 从未进入产品代码。
* 注：2026-11-05 审查发现 `SetStyleOverrideMap` 会清空段外 ACAD XData，已按 C03 修为局部替换。

### 9.2 2D Solid 移除

> **当前唯一有效实现**：`TextMaskRule.RequireNativeMaskMode`（Auto/Wipeout 同义，Solid 与未知值硬错误）+ `MaskService.IsWipeoutEncoded`（自检失败直接抛）+ `MaskService.ApplyMTextBackground`（bit0+bit1）。

5.2 那条"Wipeout 不可用就降级 Solid"的方案撤销了，代码里 `CreateSolid` / `MaskSolidColor` 一并删掉。理由：Solid 是不透明色块，只能靠"真彩色白 + 可打印图层"硬凑白底，`monochrome.ctb` 一类 CTB 会把它重映射成黑块——那不是遮罩，是另一块脏。现在只有一条主线：原生遮罩。

`MaskMode` 只剩 `Auto` / `Wipeout`（两者同义，都走原生）。写 `Solid` 或任何别的值都是**硬错误**（`RuleValidationException`），不再静默降级——静默降级比报错糟得多，因为用户拿到的图和自己以为的不是一回事。同理，Wipeout 编码自检不通过时直接抛出，不做兜底。

### 9.3 默认值改成"默认给所有文字加遮罩"

> 本节是默认值变更记录；MTEXT 背景标志的编码以 §9.6 为唯一准绳。

| 字段 | 旧默认 | 新默认 |
|---|---|---|
| `MaskOnlyWhenOccluded` | `true`（只在有遮挡时加） | **`false`**（匹配上的都加；**该开关为实验性、结果不可靠**：`Insert` 整类几何未参与遮挡判定，块参照下的文字会被判为无遮挡，建议保持 `false`） |
| `MaskInsideBlocks` | — | **`true`**（写进共享块定义，影响所有插入） |
| `MaskRebuildText` | `false` | **`true`** |
| `DimensionMaskMode` | `BlockInternal` | **`Native`**（`BlockInternal` 仍接受，同义） |
| `DimensionTextMode` | `ExplicitOnly` | **`All`**（含测量值） |
| `MaxMasksPerDrawing` | 2000 | **0 = 不限**（只有显式正数才生效） |
| `EntityTypes` 未指定时 | — | `TEXT, MTEXT, ATTDEF, ATTRIB`（`DimensionTextMode` 非 Off 时再加 `DIMENSION`；原文误写为 `DimensionMaskMode`，2026-11-05 更正） |

边界偏移改成**字高 × 比例**：`MaskPaddingRatio` 默认 `0.15`，每边张开 15% 字高（10 的字高 ⇒ ±1.5）。MTEXT 的原生偏移因子是 `1 + ratio = 1.15`；Wipeout 直接用 `height × ratio` 算几何。填色一律**图形背景色**：MTEXT 写 `UseBackgroundFillColor | UseDrawingWindowColor`（bit0+bit1，DXF 90=3，并带 63/45 成套组码；**标志编码以 §9.6 为准**，只写 bit1 的 90=2 会被 AutoCAD 判为「没有遮罩」），标注写 `DIMTFILL=1`，Wipeout 本身就是擦除到背景。

### 9.4 本轮实测

| 检查 | 结果 |
|---|---|
| 编译 | 0 warning / 0 error |
| 单元测试（`JUN_CAD_SAMPLE_DWG` 指向真实海工图） | **291 passed / 0 skipped / 0 failed（历史轮次，需外部 `JUN_CAD_SAMPLE_DWG`）** |
| 端到端（30250-002） | 匹配 195 = 改动 195 + 跳过 0，失败 0；明细 = MTEXT 原生背景 175 / 标注文字 15 / Wipeout 5 / 填充置后 6（历史轮次 / 无效指标：该路径只计数、不改变输出文件，见 §9.7，不再作为成功证据） |
| 输出图回读 | `mtext: 347 total, 175 with drawing-window mask, 0 with RectangleWidth>0`；`wipeout: 5, solid: 0`；`bad wipeout: 0`；`*D` 缓存块内 Wipeout 0 |

### 9.5 还剩的限制

* **该限制已消除（见 §9.6:453）**：因子丢失的真因是只写 bit1（90=2）导致 45/63 不落盘，改 bit0+bit1 后 AutoCAD 回读 `(45 . 1.15)`。（历史：早期曾认为 MTEXT 偏移因子在宿主侧只能"尽力而为"，于 §9.6 推翻。）
* **画在标注之上的几何仍会盖住文字背景**：`DIMTFILL` 只管标注自己那层，排序表在它之上的实体照样压过去。`MaskSendHatchToBack` 处理填充，别的几何目前**没有可靠手段**（SORTENTS 排序表经 ACadSharp 写出后 AutoCAD 读不回，见 §9.7:470）；`MaskSendHatchToBack` 已于 2026-11-05 停用，见 C02。
* 交互式会话的观感已在 §9.6:428 那次用户复查中部分确认（正是那次查出了 bit0 缺陷）；完整界面截图仍未做，列为 `VERIFY.md` 宿主门禁项。

### 9.6 补充：MTEXT 背景标志只写 bit1，AutoCAD 当"没有遮罩"（2026-11-02 晚）

用户在交互式 AutoCAD 里复查输出图，属性面板"背景遮罩"显示**否**。排查结论：

**根因**：`ApplyMTextBackground` 只置了 bit1（`UseDrawingWindowColor`），写出 `(90 . 2)`。而 DXF/ODA 规范要求 **45、90、63 成套出现**；库的写出器只在 bit0（`UseBackgroundFillColor`）置位时才落盘 45/63。于是文件里是"一套不完整的背景填充定义"，AutoCAD 直接当没有遮罩——属性面板显示"否"，渲染也不画。

**取值对照实验**（`.workbuddy-ai/bgprobe/out/m90.dwg` → `m90.png`，4 个 MTEXT 压在同一条横线上，逐像素扫描）：

| 写法 | 出图结果 |
|---|---|
| `90=0`（无遮罩，对照） | 横线从文字底下连续穿过 |
| `90=2`（只 bit1，旧实现） | **无填充，横线穿过**——与用户所见一致 |
| `90=3`（bit0+bit1，带 63/45） | **窗口底色填充，横线在文字处擦断** ✓ |
| `90=1`（显式 ACI 7 色，带 63/45） | 整块黑盒——ACI 7 被 `monochrome.ctb` 打黑，正是"显式填充色"不能用的原因 |

`90=3` 的填充不是 63 的颜色（否则也会被打黑），AutoCAD 在 bit1 置位时**忽略 63、用窗口底色**——这正是"填充颜色默认使用图像背景颜色"的语义。

**修复**（`MaskService.ApplyMTextBackground`）：

* 标志写成 `UseBackgroundFillColor | UseDrawingWindowColor`（bit0+bit1），并给 `BackgroundColor` 一个 ACI 7（bit1 下被忽略，只为让 45/90/63 成套）；
* 判重只看 bit0——旧版只写过 bit1 的文件，重跑一次就能补齐，不会被"已有遮罩"跳过。

**修复后实测**（用户给的 30250-005 源图重跑）：

* ACadSharp 回读：291 个 MTEXT `flags=3, scale=1.15`（因子第一次真正落盘）；标注 10/10 带 `DIMTFILL=1`；
* AutoCAD 2025 entget 回读：`(90 . 3) (63 . 7) (45 . 1.15)`，标注 XData 末尾 `(1070 . 69) (1070 . 1)`、原有键全部保留。

**连带修正**：9.5 里"MTEXT 偏移因子宿主侧尽力而为"的限制**不复存在**——因子丢失就是缺 bit0 的同一个 bug。（历史：早期曾认为该因子在宿主侧只能"尽力而为"，于 §9.6 推翻。）

（历史统计：26 个未加遮罩，其中 13 个空文本、8 个嵌套超深，其余 5 个当时未归类）具体为 `PICT_OWNER` 块里的空文本 `{}`（无可见内容），以及嵌套超过 `MaxNestingDepth: 8（项目默认；当时配置写 4）` 的符号块（过滤配置所致，调大深度即可覆盖）。


### 9.7 补充：绘图次序前置 + 遮罩框紧贴文字（2026-11-02 深）

用户反馈两点：① 线条画在遮罩上面（要"所有文字前置"）；② 遮罩框一侧紧贴文字、另一侧空一大截（要贴合文字主体）。

**框宽问题（受控出图实测，AutoCAD 2025）**

- AutoCAD 的 MTEXT 原生背景填充按**文字框宽度**（DXF 41）画：框 60、文字只要 ~6，横线擦除断口正好 60 个单位，字形像素孤零零嵌在里面——这就是截图里"一侧空白一大截"的来源；不设框宽（41=0）时本来就贴文字。
- 修复：`MaskTextBox=Auto`（默认）——框宽 ≤ 实测文字宽 × 1.05 时保持原生；明显松时改用**紧贴 Wipeout**（估测文字宽度 + 15% 字高留白，`RectangleWidth` 绝不动，零折行风险）。`Native`/`Tight` 可强制。实测：框 100/文字 ~14，断口 ≈15.5 紧贴文字。Auto 的分支依据是估算文字宽度（误差约 ±10%），框宽接近阈值时结果可能不稳定；需要确定性行为请显式指定 `Native` 或 `Tight`。
- 旧版处理过的文件重跑时按「框宽 × MaskTextBox × 已有标志」分派，并非一律换成紧贴遮罩：

| 框宽 vs 实测文字宽 | 已有 bit0 | Auto（默认） | Native | Tight |
|---|---|---|---|---|
| 41=0 或 ≤ 宽×1.05 | 无 | 补 bit0+bit1，保持原生背景 | 同 Auto | 清原生背景，换紧贴 Wipeout |
| 41 > 宽×1.05 | 无 | 清原生背景，换紧贴 Wipeout | 补 bit0+bit1，保持原生背景（框宽留白） | 清原生背景，换紧贴 Wipeout |
| 任意 | 有 | 判重跳过 | 判重跳过 | 判重跳过（`HasOverlappingWipeout` 命中亦跳过） |

`RectangleWidth` 全程不动，零折行风险。

**绘图次序问题（三个连锁发现）**

1. **SORTENTS 排序表路线彻底不可用**：ACadSharp 写出的块记录扩展字典 AutoCAD 读不到（`dictsearch` 返回 nil；LAYER 的字典能活、BLOCK_RECORD 的丢），而 ACAD_SORTENTS 恰恰挂在块记录扩展字典下。连 AutoCAD 自己写的排序表经过库的读写往返也会消失。**这意味着此前"填充置后"（MaskSendHatchToBack）从未真正生效过**——只计数、文件里没写。也意味着**存量图纸里用户自己排的绘制顺序在每次保存时都会被重置**（上游数据丢失 bug，已记入 ACadSharp 写出器源码注释）。字典条目引用类型也按规范从 SoftOwnership 改成了 HardOwnership（本地克隆），但条目仍会丢，说明还有第二处缺陷，未继续深挖（上游 issue #897 在跟）。
2. **实体集合是 HashSet**：`CadObjectCollection` 底层是 HashSet，`Remove+Add` 会复用刚释放的槽位——"移出再加回"根本不会把实体挪到末尾。旧 Wipeout 路径能工作纯属幸运（遮罩抢了文字的旧槽、文字拿了新末尾槽，但遮罩仍留在文字原来的深度，线照样穿过遮罩区）。
3. **修复：占位实体技巧**——`Remove(文字) → Add(占位 Point) → Add(遮罩) → Add(文字) → Remove(占位)`。占位吃掉释放的槽位，逼遮罩和文字都落到新末尾槽 = 绘制最上层，遮罩在文字之下。`MaskBringTextToFront`（默认开）对 MTEXT 原生路径和标注同样生效（标注整个前置）。被移动的实体会拿**新句柄**。

**端到端宿主验证（产品规则本体 + AutoCAD 出图逐像素）**

- "文字在先、线在后"的测试图：规则跑完集合顺序变成 `[LINE, MTEXT]`，出图横线在文字区被背景填充干净擦断（之前连续不断）。
- 宽框测试图：擦除断口 ≈15.5 单位紧贴文字（框 100）。
- 真实图纸 30250-005：（历史轮次记录：MTEXT 289 原生 flags=3 + 2 紧贴 Wipeout + 标注 10/10 DIMTFILL=1 + 13 跳过；原文字中的「296 处」与分项 301 不符，属当时统计口径差异，不可复算，保留原样仅作过程记录），AutoCAD `entget` 实读 `(90 . 3) (63 . 7) (45 . 1.15)` 成套，标注 DSTYLE 原有键全保留、`(1070 . 69) (1070 . 1)` 追加成功。
- 幂等：同位置重复运行不再叠加 Wipeout（TEXT 与紧贴 MTEXT 都有判重）。

**遗留**：排序表/块记录扩展字典写出不修的话，存量绘制顺序仍会在保存时重置（与遮罩无关的通病）；测试基线（2026-11-07 实跑 `dotnet test tests/Jun.Cad.Batch.Tests`）：**327 total / 321 passed / 6 skipped / 0 failed**；6 个 Skip 全部来自缺外部样图的 `DwgSampleTests`。历史基线：2026-11-05 为 317 / 311 / 6，§10 新增 9 条几何回归，§10.1 再增 2 条旋转判重回归。宿主侧 `ssget` / `entget` / `-PLOT` 逐像素已于 2026-11-05 用修复后产物重跑，见 §8.3；`WIPEOUTFRAME=1` 与 C01 / C20 / C21 仍未解决。

---

## 10. 遮罩过大截断行线（2026-11-06）

**现象**：用户截图（30250-003 的修订栏：B0/A0/REV. · DESCRIPTION · DATE）里，文字背后的背景块比字高出一倍，把表格的行分隔线擦断了。

**根因（两处，都是尺寸口径问题，不是"留白配大了"）**

1. **原生 MTEXT 背景填的不是字形，是行框**。AutoCAD 用 `DXF 41 × DXF 46` 当填充矩形：
   41 是定义宽度，**46 是行框高度**。30250-003 实测 `h=2.04 / rectH=3.77 / BackgroundScale 1.15`
   → 遮罩高 `3.77 × 1.15 = 4.34 = 2.13 × 字高`，而字形只有 `1.0`。多出来的 1.13 个字高
   全部落在文字下方，正好压到下一行的分隔线上（该行 `195.165`，遮罩底 `194.79`）。
   旧 `Auto` 只看**宽度**（`RectangleWidth > 实测宽 × 1.05`）决定要不要换紧贴 Wipeout，
   所以"框宽贴文字"的单行 MTEXT 全部留在原生路径——遮罩必然过大。
2. **Top/Bottom 对齐方向写反**。`AttachmentPoint` 是"插入点落在文字块的哪个角"：
   Top* 文字在插入点**下方**（`dy = −h`）、Bottom* 在**上方**（`dy = 0`）。
   旧实现在 MTEXT 分支里 Top 给 `0`、Bottom 给 `−h`，整整错开一个字高
   （`TextBounds.ForText` 是对的，只有 MTEXT 抄反）。走紧贴 Wipeout 时会直接把框画到上一行。

**修复**

| 位置 | 改动 |
|---|---|
| `TextBounds.MTextAnchorOffset` | 新增，统一 MTEXT 的 `dx/dy`；修正 Top/Bottom 方向 |
| `TextBounds.TextMaskVertices(tight)` | 高度改为**字形带** = 字高 + 下伸部（有 `g j p q y , ; ( ) [ ]` 才加 `0.25 × 字高`）；宽度用实测文字宽并以 DXF 41 为上限（折行文字不会算宽） |
| `TextMaskRule` Auto 分支 | 单行且不折行的 MTEXT → 紧贴 Wipeout；多行/折行仍走原生（宿主排版的行数我们算不出来，硬造框会漏字） |
| `TextMaskRule` 紧贴分支 | 已有**指定色**背景遮罩（bit0 开 / bit1 关）不转换，保留用户设置 |

遮罩高度：`2.13 × 字高` → `1.30 × 字高`（无下伸）/ `1.55 × 字高`（有下伸）。

**验收（AutoCAD 2025 `-PLOT` 逐像素，材料在 `tmpv/maskfix/`）**

- 同一张 30250-003，同一份配置，只换产物：
  | 出图横线 | 修复前断口 | 修复后断口 |
  |---|---|---|
  | y=513px | 63 px | 0 |
  | y=619px | 67 px | 0 |
  | y=724px | 110 / 300 / 129 px | 0 |
- 几何回读（`probe2 mdump`）：`Issued for Review` 遮罩 `(922.767,213.492)-(944.868,216.142)`，
  高 2.65 = 1.30 × 2.04，包住文字框 `(923.073,213.798)-(944.562,215.837)` 且四边留白 0.3；
  `Issued for Inter-Disciplinary Check` 高比 1.55（多留 0.51 给 `p/y` 的下伸部）。
- 端到端：30250-003 共 237 处（Wipeout 217 / 标注 20），匹配 238 = 改动 237 + 跳过 1，失败 0。
- 单元测试：`325 total / 319 passed / 6 skipped / 0 failed`，新增 9 条几何回归
  （1.3 倍字高、下伸留白、TopLeft 在插入点下方、BottomLeft 反之、多行/折行退回原生、显式 Native…）。

**已知代价**：单行 MTEXT 现在会**新增 Wipeout 实体并把文字重排到最上层（句柄变化）**，
和 TEXT 路径一样；`MaskTextBox=Native` 可退回"零新增实体"的旧行为（遮罩仍然偏大）。

### 10.1 幂等补丁：旋转文字的遮罩重复运行会叠加（2026-11-07）

§10 修复上线后发现：对已处理产物**重跑一次**，Wipeout 217 → 229（+12），
`mdups` 全部重合对都是竖排文字（`E 99020mm` 这类 rot=90° 的 MTEXT）。

**根因**：判重的旋转角取自 `Atan2(UVector.Y, UVector.X)`，但 Wipeout 的
U/V 向量是**带尺寸的变换向量，不是方向**——实测竖排遮罩 `U=(20.58,0)`、
`V=(0,20.58)`，旋转信息全在 clip 边界多边形里。于是旧实现对所有遮罩
`RotationOf` 恒返回 0，而 target.Rotation = π/2，`SameBox` 的旋转比对
永远不相等 → 旋转文字全部漏判，每跑一次叠一层。

**修复**：`TextBounds.WipeoutCorners`（新增）把裁剪顶点变换回 WCS 真实角点；
`RotationOf` 改取**第 0→1 条角点边的方向**（与 `CollectTargets` 给
`target.Rotation` 的算法同源），UVector 仅作兜底。180° 同矩形由既有
"模 π 比对"兜住。

**验收**：
- 幂等：对 `out-after` 重跑 → 改动 0 / 跳过 238 / 失败 0，Wipeout 保持 217。
- 单元测试新增 2 条（327 total / 321 passed / 6 skipped / 0 failed）：
  `旋转文字的遮罩重复运行不叠加`、`旋转角相差180度的遮罩判为同一个`。
