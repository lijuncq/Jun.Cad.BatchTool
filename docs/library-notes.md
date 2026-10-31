# ACadSharp 3.8.35 行为笔记

这些是移植过程中实测出来的库行为，**不是猜测**。放在这里而不是 README 里，因为它们是
"踩坑记录"，不是产品契约；README 只留结论。

每条都写明：现象、原因、正确写法、以及为什么不能用直觉写。

---

## 1. `HatchPatternType` 枚举名是反的

```csharp
PatternFill = 0   // 其实是"用户自定义"
SolidFill   = 1   // 才是"预定义"
Custom      = 2   // 自定义文件
```

按名字直觉写，会把预定义图案写成用户自定义，**AutoCAD 里图案直接消失**。

正确写法：统一走 `HatchPatternChangeRule.ToLibraryPatternType()`，不要在别处直接写枚举名。

---

## 2. `Arc` 继承自 `Circle`

`ent is Circle` 会把所有圆弧一起捞进来。

实测：一张 2568 KB 的图里 `CIRCLE` 被误判成 1450 个
（= 33 个圆 + 1417 个圆弧）。

正确写法：`EntityFilter.MatchesType` 里用 `ent is Circle && !(ent is Arc)`。

同类陷阱：**`Wipeout` 继承自 `RasterImage`**，按类型排查时顺序反了会把遮挡框报成 `IMAGE`；
`TableEntity` 继承自 `Insert`，不排除会把它当成普通块参照。

---

## 3. `HatchPattern.Line.Offset` 存的是世界坐标

`.pat` 里写 `45,0,0,0,7.8`，进内存变成 `(-5.5154, 5.5154)`——
解析时按线角度旋转过了（`HatchPattern.LoadFrom` 里那句 `dir.X*cos - dir.Y*sin`）。

所以算**垂直间距**必须用叉积 `|dx·sinθ − dy·cosθ|`，
用 `|Offset.Y|` 会差一个 `cosθ`：`JIS_LC_8A` 会算成 5.5154 而不是 7.8。

---

## 4. `Hatch.PatternScale` 是带副作用的属性

```csharp
set { this._patternScale = value; this.Pattern?.Update(XY.Zero, 0, this._patternScale); }
```

赋值会把比例**乘进图案线**，而且每赋一次就乘一次。`PatternAngle` 同理。

这不是库的 bug——DWG 里存的图案线本来就是"最终几何"：实测比例 75 的填充，
图案线偏移是基准值的 75 倍，组码 `41` 只是个记录值。

正确姿势：**赋原始定义 → 只设一次比例**。重复赋值会把图案越缩越小。

推论：算实际线距时**不要再乘** `PatternScale`（图案线里已经含了）。
`HatchPatternMetrics` 就是按这个前提写的。

---

## 5. 换图案要重算比例（否则疏密会变）

图案的视觉疏密 = 图案线的垂直间距。各图案在 `.pat` 里的基准线距差得很远：

| 图案 | 基准线距 |
|---|---|
| `ANSI31` | 3.175 |
| `JIS_LC_8A` | 7.8 |
| `JIS_RC_20` | 20 |

沿用比例 1 直接改图案名，源图案线距 3、目标 7.8 时会**稀疏 2.6 倍**。

离线版默认开启 `Action.MatchSpacing`，按 `新比例 = 替换前的实际线距 ÷ 新图案基准线距` 反算。

实测：`2_SECONDARY_ESCAPE_ROUTE_-_TEST`（线距 3）→ `JIS_LC_8A`（基准 7.8），
自动得到比例 `0.384615`；输出文件里图案线的垂直间距仍是 3，与源文件组码 `45/46` 逐字节相同
（`45=-2.12132 46=2.12132`），只有 `41` 由 `1.0` 变成 `0.384615`。

关掉 `MatchSpacing` 时：**沿用源比例**——必须把原比例显式写回 `PatternScale`，
让 setter 把 `.pat` 的图案线按这个比例缩放；显式写了 `PatternScale` 时取配置值（优先级更高）。

> 这里踩过一次坑：早期版本只是"不碰 `PatternScale`"，以为这就是沿用。
> 实际效果是**图案线保持未缩放、记录值还是老比例**——组码 `41` 说 75、图案线却是 `7.8`。
> AutoCAD 按图案线绘制，实测线距从 225 变成 7.8（密了 29 倍），属性面板却显示 75，
> 看哪个数都对不上。修法见 `HatchPatternChangeRule.ApplySpacingMatch`。
>
> 关掉之后线距变化是**静默**的，用户只会看到"图上变了"却查不到原因，
> 所以现在会打一条 `Warn`：`图案线距会从 225 变成 585（2.6 倍）`。

**源填充是纯色/渐变时没有图案线可比**，这时**不能**沿用它的 `PatternScale`——
纯色填充的 `PatternScale` 是 **0**，沿用下去新图案会以 0 比例绘制：图案线间距被压成 0，
AutoCAD 里看起来就是**一整块实色**（颜色索引没变，但"面"代替了"线"，观感完全不同）。
正确做法是取 `1.0`（新图案在 .pat 里的原始疏密），并加一道兜底：比例 <= 0 一律改 1.0。

### 换图案后"看起来不一样"的两件事（都不是比例问题）

实测同一张图里：

| 源图案 | 线族 | 主方向 | 线距 | 换成 ANSI31 后 |
|---|---|---|---|---|
| `3_MAINTENANCE_ROUTE` | 1 | 45° | 225 | 线距 225，方向 45° — **观感一致** |
| `8001_SYSTEMSTYLE_1` | 1 | 30° | 300 | 线距 300，方向 **45°** — 方向变了 |
| `6_PRIMARY_ESCAPE_ROUTE_2X` | 2（45°/315° 交叉） | 45° | 450 | 线距 450，**线族 2 → 1** — 线少一半，看起来变淡 |

* **方向**：每个 `.pat` 自带线角度（ANSI31 = 45°、`8001_SYSTEMSTYLE_1` = 30°）。换图案就会换成新图案的方向。

  **但如果目标图案只是原图案的一个旋转**（线族数相同、各族相对角度吻合、虚线结构一致），
  规则会自动叠加一个旋转把方向转回原样——做到"换图案名、观感不变"。
  实测：`8001_SYSTEMSTYLE_1`（30°，1 族，线距 300）→ ANSI31，输出是 **ANSI31 @ 30°、线距 300**，
  图元上记的 `PatternAngle = -15°`（在 ANSI31 自带 45° 上叠加）。

  **结构不同时退一步只对齐"主方向"**：交叉图案（2 线族）换单线族图案，线族数必然对不上，
  但主方向仍要对齐，否则换完连方向都变了（实测 `1_PRIMARY_ESCAPE_ROUTE` 45°/135° 换 `LINE` 会变成 0°）。
  主方向取"垂距最大的那族"，垂距相同时取**角度较小**的那族（45°/135° → 45°，结果确定）。
  剩下的差异（少一条线族）由"观感会变"提示说明。

  什么时候不动：配置里写了 `PatternAngle` 或 `PatternScale`——显式配置永远优先。
* **线族数**：交叉/网格图案换成单线族图案，线数减半，同一颜色下"墨量"减半，看起来颜色也变了——颜色索引其实没动。

`HatchPatternChangeRule.WarnAppearanceChange` 会在出现这几类变化时打一行提示，避免被误当成比例算错。

### 双线图案的"缝宽"是独立于线距的第三个量

`JIS_LC_8A`、`2_SECONDARY_ESCAPE_ROUTE` 这类"双线"图案有两条同向线族：

| 图案 | 线距 | 缝宽 | 缝/距 |
|---|---|---|---|
| `2_SECONDARY_ESCAPE_ROUTE` | 3 | 0.5 | 1:6 |
| `JIS_LC_8A` | 7.8 | 0.7071 | 1:11 |

线距对齐到 3 后（比例 0.384615），缝宽只能跟着比例一起缩到 `0.7071 × 0.3846 = 0.272`。
**一个统一比例无法同时对齐两个比值**——这是图案自身的几何比例，不是换算错误。
`HatchPatternMetrics.PairGap()` 负责算这个值（只在所有线族同向时返回，交叉图案返回 null），
规则会把它单独提示一次：`双线缝宽 0.5 → 0.272`。

---

## 5.5 `$MEASUREMENT` 决定 AutoCAD 按哪份图案库解析名字

这是"填充画得出来，但属性面板的预定义图案列表里找不到这个名字"的根因。

AutoCAD 按图形的 `$MEASUREMENT` 选图案库：

| `$MEASUREMENT` | 图案库 | 图案数（AutoCAD 2025） |
|---|---|---|
| 0（英制） | `acad.pat` | 72 |
| 1（公制） | `acadiso.pat` | 83 |

两份文件的差集**只有 JIS 系列**（`JIS_LC_20`、`JIS_LC_20A`、`JIS_LC_8`、`JIS_LC_8A`、
`JIS_RC_10`、`JIS_RC_15`、`JIS_RC_18`、`JIS_RC_30`、`JIS_STN_1E`、`JIS_STN_2.5`、`JIS_WOOD`），
`acadiso.pat ⊃ acad.pat`（没有只存在于 `acad.pat` 的图案）。

实测（AutoCAD 2025 `accoreconsole`，同一份输出图）：

```
$MEASUREMENT=0  → (setvar "HPNAME" "JIS_LC_8A") → 无效的图案名称
$MEASUREMENT=0  → (setvar "HPNAME" "ANSI31")    → OK
$MEASUREMENT=1  → (setvar "HPNAME" "JIS_LC_8A") → OK
```

同时用 `entget` 确认**实体本身是完好的**，两种情况下组码一致：

```
(2 . "JIS_LC_8A") (41 . 0.384615) (78 . 2)
(53 . 0.785398) (43 . 0.0)      (44 . 0.0) (45 . -2.12132) (46 . 2.12132)
(53 . 0.785398) (43 . 0.384615) (44 . 0.0) (45 . -2.12132) (46 . 2.12132)
```

即：偏移向量 `(-2.12132, 2.12132)` 的模是 3.0，`41` 是 0.384615——**几何是对的**，
丢的只是"名字能不能被解析"。所以修正点是 `$MEASUREMENT`，不是图案定义。

`ACadSharp` 用 `CadHeader.MeasurementUnits`（枚举 `English = 0` / `Metric = 1`）暴露它。
注意：读一份 `$MEASUREMENT=0` 的 DWG 会得到 **256** 而不是 0（库的读码与 AutoCAD 的解释不一致），
但**写回 `Metric` 之后 AutoCAD 读到的是 1**，所以按"== Metric 才算公制"判断即可，
不要写 `== 0` 之类的相等比较。

---

## 6. 文字与标注的包围盒不可用

`TextEntity.GetBoundingBox()` / `MText.GetBoundingBox()` 返回**退化矩形**（`min == max`），
对从 DWG 读入的实体和内存里新建的实体都一样。

`Dimension.GetBoundingBox()` 返回零宽矩形（X 恒定）。
`TableEntity.GetBoundingBox()` 源码直接 `return BoundingBox.Null`。

所以 `Engine/TextBounds.cs` 是文字/标注/遮罩包围盒的唯一入口：
文字按"字高 × 字符数"估算（中文 1.0、西文 0.62），标注取 `*D` 块内实体并集，
Wipeout 自己按 `UVector/VVector` 变换裁剪顶点（库的实现忽略旋转）。

---

## 7. 新建实体只能追加，`Handle` 只读

`BlockRecord.Entities` 是 `CadObjectCollection<Entity>`，公开成员只有
`Add` / `AddRange` / `Remove` / `Clear`，**没有按索引插入**。

新实体的句柄按文档种子递增，所以它天然排在最后 → 绘制顺序最后。
对 Wipeout 来说这意味着"遮罩画在文字上面，会把文字自己擦掉"。

要放到文字之前只能"移出文字 → 加遮罩 → 再加回文字"，而这会给文字**重新分配句柄**
（实测 48 → 4A），且 `CadObject.Handle` 是只读属性，无法恢复。

整表 `Clear()` + `AddRange()` 更糟：所有实体都会拿到新句柄，差异预览里会显示成"全图新增"。

---

## 8. MTEXT 的格式码只能当不透明字符串

`MText.PlainText` 不能用作"查找替换"的匹配源：实测
`{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P...}` 的 `PlainText` 是 `lT1;CONNECT TO`——
`\l` 的 `l` 和 `\T1;` 的 `T1;` 都留在结果里。

库没有格式码解析器/生成器。`Engine/Text/MTextTokenizer.cs` 是自建的：切成
`Text` / `Format` / `GroupStart` / `GroupEnd` 四类 token，替换只落在 `Text` 上。

---

## 9. 表格单元格

`TableEntity.GetCell(r, c)` 返回 `Cell`，内容在 `Cell.Contents`（列表）里，
`Cell.Content` 在"多内容"时返回 `null`——必须遍历 `Contents`。

单元格文字是 `CellContent.CadValue.Value`（`string` 时才是文字；字段/块内容是别的类型）。

已知限制：表格的可见几何来自它的匿名缓存块（`*T`），改单元格数据不会更新缓存，
查看端可能仍显示旧值。
