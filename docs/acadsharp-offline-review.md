# 离线批处理：规则扩展与技术可行性评审（ACadSharp 3.8.35 / 无 AutoCAD 宿主）

评审对象：`Jun.Cad.Batch`（`src/`，11 条规则、WinForms UI、CLI）。
评审日期：2026-10-31。库版本：ACadSharp 3.8.35（本机 NuGet 缓存）。
评审方式：读源码 + 读 ACadSharp 3.8.35 源码 + **在真实图纸上跑能力探测**（探测程序见 `.workbuddy-ai/probe/`）。

> **本篇是 2026-10-31 的可行性评审，已被后续实现取代，不要照着它改代码。** 其中"探测失败降级 2D Solid"的设计**已废弃**（Solid 会被 CTB 重映射成黑块，实测过）；Wipeout 的写出问题后来定位为几何编码错误并已修复；文字遮罩现在是"默认给所有文字加、边界 = 字高 × 15%、填充 = 图形背景色"；标注走 `DIMTFILL=1` 样式覆盖而不是 `*D` 缓存块。现状以 `README.md` 第 5/8 节和 `textmask-failure-report.md` 第 8、9 节为准。

---

## 0. 结论性判断

**四项必加功能全部可以在纯 ACadSharp 层落地，没有一项需要 AutoCAD 进程参与。**
但有三个硬约束必须在设计阶段就承认，否则方案会在实现时崩掉：

1. **文字包围盒不可用。** `TextEntity.GetBoundingBox()` / `MText.GetBoundingBox()` 在 3.8.35 上返回**退化矩形**（`min == max`），对从 DWG 读入的实体和内存里新建的实体都一样。所以"文字遮罩多大""文字有没有被压住""文字中心点是否在区域内"这三件事，**全部要自建文字矩形估算器**，不能调用库的包围盒。
2. **标注包围盒也不可用。** `DimensionLinear.GetBoundingBox()` 返回零宽矩形（实测 X 恒定）。标注的真实范围只能取 `Dimension.Block`（`*D###` 匿名块）内实体的并集。
3. **表格没有包围盒。** `TableEntity.GetBoundingBox()` 源码直接 `return BoundingBox.Null`。表格不能参与任何基于包围盒的区域过滤。

另外两条与"看起来能用"有关的判断：

* Wipeout 的**写出路径在本机实测可用**（构造 → 写盘 → 回读，实体、裁剪边界、包围盒都对得上），但回读用的是 ACadSharp 自己的解析器，**不等于 AutoCAD 认可**；且 `DefinitionReactor` 是 `internal`，构造不出完整的图像定义反应器。所以 Wipeout 定为"纯 ACadSharp 层但需 capability probe"，探测失败降级 2D Solid（Solid 实测写出与包围盒都精确）。
* MTEXT 原生背景遮罩可以写（`BackgroundFillFlags` 保真），但 `BackgroundScale` 回读**恒为 1.5**（写 1.0 / 1.1 / 1.2 / 1.15 / 2.0 全部读回 1.5），不能用于精确控制。文字遮罩仍以 Wipeout/Solid 为主路径。

**当前 UI 的三个真实缺口**（第 3 节展开）：没有差异预览、没有前置检查可视化、没有"本次运行"级回滚。现在点「执行」是直接写盘的，只在 in-place 时留 `.bak`。

---

## 1. 实测基线（所有后续判断的事实依据）

探测程序：`.workbuddy-ai/probe/`（`dotnet run -- <dwg> <输出目录>`）。
样本：
`samples/out-sample/test1.dwg`（2568KB，AC1027，11419 实体）、
`KLD-ZHJT-PIP-LYT-2000ZZ-30220-001_S1.dwg`（442 实体 / 90 MTEXT / 84 标注，AC1027）、
`楼梯和平台.dwg`（1658 实体，AC1021）。

| # | 探测项 | 方法 | 实测结果 |
|---|---|---|---|
| 1 | 文字包围盒 | `TextEntity` / `MText`.`GetBoundingBox()`，读入实体 + 新建实体各一组 | **退化**：`(4570.9,13456.3)-(4570.9,13456.3)`；新建 `TEXT h=2.5 @ (10,20)` → `(10,20)-(10,20)` |
| 2 | 标注包围盒 | `DimensionLinear.GetBoundingBox()` | **退化**：`(8312.5,11376.4)-(8312.5,16266.3)`，零宽 |
| 3 | 标注内部构成 | `Dimension.Block` 实体清单 | `*D139`：2×Line + 2×Insert(箭头) + **1×MText** + 3×Point(Defpoints)，共 8 个 |
| 4 | 标注文字 | `Dimension.Text` / `Block` 内 MText | `Text="4890"`（显式覆盖），块内 MText = `\A1;4890`，`InsertPoint=(3453.65,13821.32)`，`Height=187.5` |
| 5 | 显式文字占比 | 168 个标注（含重复计数，实为 84） | 84/84 全部 `Text != "<>"` |
| 6 | 表格包围盒 | 源码 `TableEntity.GetBoundingBox()` | `return BoundingBox.Null` |
| 7 | 表格单元格 API | `TableEntity.GetCell(r,c)` → `Cell.Content.CadValue` | API 存在（`Contents` / `MergedValue` / `MergedCellRanges` / `CellStyleOverride`）；**三张样本图均无 ACAD_TABLE，未实测** |
| 8 | Wipeout 写出 | 构造 + `DwgWriter.Write` + 回读 | **成功**：回读 1 个，`clipVerts=2`，`bbox=0,0-100,50`；带/不带 `ImageDefinition` 都能写出 |
| 9 | Wipeout 旋转包围盒 | `UVector=+Y, VVector=-X`，裁剪 `(0,0)-(10,4)` | 返回 `(0,0)-(10,4)`；真实范围应为 `X:-4..0, Y:0..10` → **误差已确认** |
| 10 | Solid 降级 | 4 角点构造 + 写出 + 回读 | **成功**，`bbox=0,0-100,50` 精确 |
| 11 | MTEXT 背景遮罩 | `BackgroundFillFlags` + `BackgroundScale` 往返 | flags 保真（`None → UseDrawingWindowColor`）；**scale 恒回读 1.5** |
| 12 | MTEXT 明文 | `MText.PlainText` | `{\l\fISOCPEUR\|b0\|i0;\T1;CONNECT TO\P...}` → `lT1;CONNECT TO`，**残留格式码碎片，不可作匹配源** |
| 13 | 块参照包围盒 | `Insert.GetBoundingBox()`（`VIEW1`，scale=75） | **可用**：`(31233,34649)-(73290,65715)`，含块内 9 个实体 |
| 14 | 布局枚举 | `BlockRecords.Where(b => b.Layout != null)` | `*Model_Space` 的 `Layout.Name == "Model"`，与 `doc.ModelSpace` 是**同一个块** → 按名字过滤必须按引用去重 |
| 15 | 属性/表格分布 | 块定义扫描 | 样本图 `ATTDEF=0 ATTRIB=0 TABLE=0`（标题栏为纯文字）→ 属性递归与表格写回**未实测** |

---

## 2. A. 工程常用批处理功能审查

除四项必加功能与现有 11 条规则之外，按海工结构详图的制图习惯，建议补 10 条规则。
轨道只允许三类：**①纯 ACadSharp 层**；**②纯 ACadSharp 层但需 capability probe / 回归验证**；**③当前版本需验证 / 暂不支持 / 仅能降级近似**。

### A-1 图框与标题栏属性规范化 `TitleBlockNormalize`

* **输入/输出**：DWG 目录 → 同结构输出；图框块参照的 ATTRIB 值被规范化。
* **过滤条件**：`Container.BlockNamePattern`（如 `A1_*`、`TK-*`）+ 图框属性值条件（如 `DWG_NO` 前缀）+ 空间范围（通常仅布局）。
* **数据模型**：`{ BlockPattern, AttributeMap: [{Tag, Source: Literal|Regex|FileMeta, Value}], RequiredTags[], MaxWidth/MaxHeight }`。
* **执行流程**：定位图框块参照 → 遍历 `Insert.Attributes` → 按 Tag 匹配 → 校验/写入 → 未匹配的必填 Tag 记 `Blocked`。
* **ACadSharp 要点**：`Insert.Attributes`（`AttributeEntity.Value/Tag/IsInvisible`）；图幅校验用 `Insert.GetBoundingBox()`（实测可用）。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：属性定义在块内但实例未带该 ATTRIB（`Insert.Attributes` 少项）；不可见属性；同一图框被多次参照（改一处等于改全部引用同一块定义的实例——**但 ATTRIB 是实例级，不共享**）；属性值为空串 vs 缺失。
* **风险点**：`Source: FileMeta`（从文件名推图号）一旦正则写错会批量写错号；建议默认只做"校验+报告"，写入需显式开关。
* **轨道**：**①**

### A-2 图层标准映射与合并 `LayerMap`

* **输入/输出**：图层名映射表（旧名 → 标准名，支持正则与通配符）→ 实体与图层表被合并。
* **过滤条件**：源图层名模式；`SkipLockedLayers`；`SkipCurrentLayer`（当前层不删，避免留下空当前层）。
* **数据模型**：`{ Mappings: [{Pattern, Target, CreateIfMissing, Color, Linetype, LineWeight}], DryRunOnly }`。
* **执行流程**：遍历图层表 → 匹配映射 → 改所有引用实体的 `Layer`（含块定义内实体）→ 合并后删除空源层。
* **ACadSharp 要点**：`Layer` 表读写；块内实体归层必须改**块定义内部**实体（项目已有 `BlockLayerChangeRule.MoveDefinitionContent` 的成熟做法，直接复用）；`*Model_Space` / `Defpoints` / `0` 层列入黑名单。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：目标层已存在但属性不同（颜色/线型冲突）；图层被外部参照引用；图层名大小写差异（DWG 内不区分，配置要统一 `OrdinalIgnoreCase`）。
* **风险点**：删层是不可逆操作，必须"先并后删 + 删前二次扫描引用"（项目 `Purge/ReferenceScanner` 可复用）。
* **轨道**：**①**

### A-3 块内实体归 0 层 `BlockContentToLayer0`

* **输入/输出**：块名模式 → 块定义内实体图层改为 `0`，使"随层"真正生效。
* **过滤条件**：块名模式；排除匿名块 `*D`/`*T`/`*U` 与外参；`KeepLayers[]` 白名单（如 `Defpoints`）。
* **数据模型**：`{ BlockPatterns[], KeepLayers[], AlsoMoveNested }`。
* **执行流程**：按块定义 `ObjectId` 去重 → 遍历定义内实体 → 改 `Layer=0` → 属性定义（ATTDEF）同步。
* **ACadSharp 要点**：块定义内实体自带图层，不随参照图层走（README 6.2 已记录）；动态块要同时处理匿名实例块与 `DynamicBlockTableRecord`。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：实体本来就在 `Defpoints`（不能挪，挪了会参与打印）；块内嵌套块参照的图层。
* **风险点**：把块内实体全归 0 后，如果参照图层是"辅助线"层，整块会跟着不可见/不打印——必须给"预演受影响块数量"。
* **轨道**：**①**

### A-4 焊接符号与引线注释规范化 `WeldSymbolNormalize`

* **输入/输出**：焊接符号块（属性块）/ 引线注释 → 字高、箭头大小、符号比例统一。
* **过滤条件**：块名模式（`WELD*`、`*_WELD`）+ 所在图层 + 空间范围。
* **数据模型**：`{ BlockPatterns[], TargetHeight, TargetArrowSize, ScaleMode: Uniform|ByViewScale, AttrTagMap }`。
* **执行流程**：定位符号块参照 → 统一 `XScale/YScale`（或块内文字字高）→ 属性值大小写/单位规范。
* **ACadSharp 要点**：块属性可写（①）；`MultiLeader` 的文字**不可写**（README 6.2 已确认，库未建模可写入口）→ 用 MLEADER 画的焊缝标注只能降级为"跳过 + 报告"。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：符号块被不同视口比例参照（统一 scale 会破坏"随视口比例"的意图）；引线是 `Leader` + 独立 MTEXT 的组合（要连带改 MTEXT）。
* **风险点**：焊接符号在 ISO 与 AWS 体系里箭头侧/尾部符号语义不同，规则只能改"形式"不能改"语义"，必须在文档里写死这条边界。
* **轨道**：块属性部分 **①**；MLEADER 部分 **③（暂不支持，仅报告）**

### A-5 标高与坐标标注规范化 `ElevationTagNormalize`

* **输入/输出**：标高符号（块+属性或 TEXT）/ 坐标标注 → 小数位、单位、±0.00 基准统一。
* **过滤条件**：块名模式 + 图层 + 文字内容正则（如 `^[+-]?\d+\.\d{3}$`）。
* **数据模型**：`{ BlockPatterns[], ValueRegex, Decimals, Unit, DatumOffset, NegativeZeroRule }`。
* **执行流程**：取值 → 解析数值 → 按基准换算 → 按小数位格式化 → 写回（块属性或 TEXT.Value）。
* **ACadSharp 要点**：`AttributeEntity.Value` / `TextEntity.Value` 直接可写（①）；坐标标注若是 `DimensionOrdinate`，其显示文字来自 `Dimension.Block` 缓存，改 `Dimension.Text` 只影响下次重生成 → 见 B-4。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：`±0.00` 这类非数值文本；带前缀（`EL.+12.500`）；单位是 m 而图是 mm（差 1000 倍，必须显式配置而不是猜）。
* **风险点**：数值格式化不可逆（四舍五入后再读回会丢精度），默认必须"只格式化、不改数值语义"，且差异预览要显示原值。
* **轨道**：**①**（块/TEXT）；`DimensionOrdinate` 走 **③**

### A-6 布局命名规范 `LayoutRename`

* **输入/输出**：`Layout.Name` → 规范名（`V-01`、`S-02`、`A1-平面`），可选同步图框属性里的图名。
* **过滤条件**：现有布局名模式；`ExcludeModelSpace`（默认 true）。
* **数据模型**：`{ RenameMap: [{Pattern, NewName}], SyncTitleBlockAttr, DryRunOnly }`。
* **执行流程**：枚举 `BlockRecords.Where(b => b.Layout != null)` → **按引用去重并排除模型空间块** → 改名 → 冲突检测 → 写盘。
* **ACadSharp 要点**：`Layout.Name` 可写，但布局块记录名（`*Paper_Space0`）与 `Layout.Name` 是两套名字；改名是否被 `DwgWriter` 正确写回**需 probe**（最小验证：建两个布局 → 改名 → 写 → 回读比对）。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：重名（必须拒绝而不是覆盖）；非法字符（`<>/\":;?*|,=`）；`*Model_Space` 的 `Layout.Name == "Model"`（实测，见基线 #14）——按名字过滤时它会冒充一个布局。
* **风险点**：布局名被图纸集/外部索引引用时，改名会断链；默认 dry-run。
* **轨道**：**②**

### A-7 材料表 / 明细栏一致性核对 `BomSync`

* **输入/输出**：图内材料表（ACAD_TABLE 或"块+属性"）→ 导出 CSV；与外部清单比对 → 差异报告；可选回写单元格。
* **过滤条件**：表格所在空间/图框容器；表头关键字（`序号`、`名称`、`规格`、`材质`、`数量`）。
* **数据模型**：`{ Source: Table|BlockAttrs, HeaderKeys[], ExportPath, CompareWith, WriteBack: false }`。
* **执行流程**：定位表格 → 逐行读单元格 → 归一化（去空格、全半角）→ 与外部清单 join → 输出 `新增/缺失/数量不符` → 可选回写。
* **ACadSharp 要点**：`TableEntity.GetCell(r,c).Contents[i].CadValue.Value`（读，①）；写回 `CadValue.SetValue(...)`（**需回归**，见 B-3）。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：合并单元格（只有左上角有内容，其余单元格读出来是空）；单元格内含 `\P` 多行；表格被拆分成多个 `TableBreakData` 段；表头行不在第 0 行。
* **风险点**：**写回后表格的匿名缓存块（`*T`）不会同步**，查看端可能仍显示旧值（纯文件模式固有限制，不提供宿主侧重生成）；因此默认 `WriteBack=false`，先做"核对 + 报告"。
* **轨道**：导出 **①**；写回 **②**（未实测，样本图无表格）

### A-8 标注文字与测量值一致性审计 `DimTextAudit`

* **输入/输出**：标注清单 → 报告（`Text` 与 `Measurement` 偏差超过阈值、或 `Text` 为空/为 `<>`）。
* **过滤条件**：标注类型（线性/角度/半径/坐标）；偏差阈值；空间范围。
* **数据模型**：`{ Types[], Tolerance, ToleranceUnit: Absolute|Percent, IncludeEmptyText }`。
* **执行流程**：遍历 `Dimension` → 取 `Text` 与 `Measurement` → 数值化比对 → 出报告（**不改图**）。
* **ACadSharp 要点**：`Dimension.Text` / `Dimension.Measurement` 直接可读（①）。实测样本：84/84 标注都是显式文字覆盖（海工图里手工改文字极常见），这条审计的现实价值很高。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：`Text` 带前后缀（`%%c50`、`R100`、`2×45°`）；公差文本；`Text` 是 `<>`（跟随测量值）。
* **风险点**：数值解析失败要记 `Skipped` 而不是当成 0 偏差。
* **轨道**：**①**

### A-9 零长线 / 空文字 / 重复图元清理 `GeometryCleanup`

* **输入/输出**：删除零长度 `LINE`、空 `TEXT/MTEXT`、完全重合的重复实体。
* **过滤条件**：实体类型白名单；长度/面积阈值；图层白名单；`DryRunOnly` 默认 true。
* **数据模型**：`{ ZeroLengthTolerance, EmptyTextKinds[], DuplicateMode: None|SameLayer|AnyLayer, DuplicateTolerance }`。
* **执行流程**：按 bbox 分桶 → 桶内精确比较（`Line` 端点、`LwPolyline` 顶点序列、`Insert` 块名+矩阵）→ 保留第一个，其余删除。
* **ACadSharp 要点**：`BlockRecord.Entities.Remove(entity)`（①）；几何比较要自建（库没有 `Equals` 语义的几何比较）。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：`Polyline2D` 顶点顺序不同但几何相同；块参照旋转角不同但视觉相同；`Hatch` 边界相同但图案不同（不能算重复）。
* **风险点**：误删是最贵的错误——必须默认 dry-run，且删除清单逐条给句柄。
* **轨道**：**②**（几何比较需回归标定）

### A-10 必填属性校验 `AttributeAudit`

* **输入/输出**：图框/材料表块的必填属性为空 → 报告（可选按默认值填充）。
* **过滤条件**：块名模式 + `RequiredTags[]` + 空间范围。
* **数据模型**：`{ BlockPatterns[], RequiredTags[], FillDefaults: false, Defaults: {Tag: Value} }`。
* **执行流程**：定位块参照 → `Insert.Attributes` 按 Tag 查 → 空值/缺失 → 报告或填充。
* **ACadSharp 要点**：同 A-1（①）。
* **是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。
* **边界情况**：属性存在但值为空格；属性被设为不可见（`IsInvisible`）但仍需校验；块定义里有 ATTDEF 但实例没带 ATTRIB。
* **风险点**：填充默认值会把"没填"变成"填错"，默认只报告。
* **轨道**：**①**

### 不建议做的两条（写在这里是为了避免以后被反复提）

* **自动布局重排 / 视口自动摆放**：纯文件模式需要重算视口比例与图纸空间变换，且没有任何视觉反馈可校验，一次跑错就是整批图废掉。
* **自动生成焊接符号或材料表内容**：属于设计决策，不是批处理。

---

## 3. B. 技术可行性分轨审查

### B-1 Wipeout 实体

| 分轨 | 内容 |
|---|---|
| 纯 ACadSharp 可完成 | 构造 `Wipeout`：`ClipBoundaryVertices`（2 点=矩形，>2 点=多边形，`ClipType` 由顶点数自动推导）、`InsertPoint`、`Size=(1,1)`、`UVector/VVector`、`Flags`（构造函数已置 `ShowImage|ShowNotAlignedImage|UseClippingBoundary`）；写入 `BlockRecord.Entities`；`WipeoutVariables.DisplayImageFrame` 控制边框显示 |
| 需 probe / 回归 | **整条写出链路**：构造 → `DwgWriter.Write` → 重新读入 → 断言"实体存在 + 裁剪顶点一致 + 包围盒一致"。本机已在两张真实图上跑通（基线 #8），但回读是 ACadSharp 自己的解析器 |
| 暂不支持 / 降级 | `DefinitionReactor` 是 `internal`，无法构造完整的图像定义反应器；`GetBoundingBox()` 忽略 `UVector/VVector` 旋转（基线 #9） |

**capability probe 方法（建议固化为 `Preflight.WipeoutWriterProbe`）**

1. 取一个临时 `CadDocument`（不要用待处理图纸，避免污染）；
2. 构造最小 Wipeout：`InsertPoint=(0,0,0)`、`ClipBoundaryVertices=[(0,0),(100,50)]`、`Size=(1,1)`；
3. `DwgWriter.Write(temp.dwg, doc, null, onNotify)`，捕获 Error/Warning 通知；
4. `DwgReader.Read(temp.dwg)`，断言：`OfType<Wipeout>().Count()==1`、`ClipBoundaryVertices.Count==2`、`GetBoundingBox()` 与写入值一致（容差 1e-6）；
5. 三项全过 → `Passed`；任一项失败或抛异常 → `Failed`，**本次运行内所有遮罩规则自动降级 Solid**，并在差异预览里逐条标注。

> 注意：probe 通过只证明"ACadSharp 自洽"，不证明 AutoCAD 认可。本项目不做宿主验证，这个不确定性必须写进文档和 UI 提示，不能藏起来。

**旋转 / 非轴对齐 Wipeout 的已知误差**

`CadWipeoutBase.GetBoundingBox()` 的实现是"裁剪顶点 min/max + `InsertPoint`"，**完全不经过 `UVector/VVector`**：

```
旋转 90° 的 Wipeout（U=+Y, V=-X），裁剪 (0,0)-(10,4)
库返回： (0,0)-(10,4)
真实范围：X:-4..0, Y:0..10
```

后果：旋转遮罩在遮挡判定、区域过滤、差异预览里都会被当成"横着的矩形"。**处理方式**：自建 `WipeoutBounds`（用 U/V 向量做仿射变换后再取 AABB），库的 `GetBoundingBox()` 只作为交叉校验。

**探测失败时的降级：2D Solid**

* `new Solid(c1, c2, c3, c4)`（实测写出 + 回读 + 包围盒都精确，基线 #10）。
* 注意 DWG 的角点顺序是 1-2-**4**-3（回读时 `ThirdCorner`/`FourthCorner` 会对调），构造时按 `(左下, 右下, 左上, 右上)` 传，视觉上才是正确的矩形。
* 颜色：Solid 是不透明实心，必须放在**不可打印图层**（`Action.LayerPlottable=false` 已支持），否则出图会出现白块/黑块。
* 打印样式（`PlotStyleName`）库未建模，不能靠"255 号色 + 打印样式表"解决，只能靠图层不打印。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### B-2 TEXT / MTEXT / AttributeReference

| 分轨 | 内容 |
|---|---|
| 纯 ACadSharp 可完成 | `TextEntity.Value/Height/Rotation/WidthFactor/ObliqueAngle/AlignmentPoint`；`MText.Value/Height/Rotation/AttachmentPoint/RectangleWidth/LineSpacing`；`Insert.Attributes` 遍历、`AttributeEntity.Value/Tag/IsInvisible` |
| 需 probe / 回归 | `MText.Value` 含格式码时的**写回保真**（读保真已确认）；含 `\f...;` 内联字体的字符串经 `TextFontChange` 改写后的结构合法性 |
| 暂不支持 / 降级 | `MText.PlainText` **不可用**（实测残留 `lT1;` 碎片）；库没有 MText 格式码解析器/生成器；`MultiLeader`/`Tolerance` 文字不可写 |

**格式码现状（实测样本里出现的真实内容）**

```
{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P\l\fISOCPEUR|b0|i0;\T1;P30 MODULE}
```

涉及：`{}` 分组、`\l` 下划线、`\f<font>|b0|i0;` 内联字体、`\T1;` 字距、`\P` 段落、`\~` 不换行空格；标注块里还有 `\A1;`（对齐）。

**结论**：`Value` 是"带格式码的原始串"，只能当**不透明字符串**处理。任何"查找/替换/统计字数"都必须自建 tokenizer（见 D-2），不能依赖 `PlainText`，也不能用 `string.Replace` 直接上。

**AttributeReference 的递归陷阱**

现有 `DatabaseWalker` 遍历的是 `BlockRecord.Entities`，而 **ATTRIB 挂在 `Insert.Attributes` 上，不在块定义里**。也就是说"递归进入块定义"并不等于"能拿到块参照上的属性"。要覆盖 ATTRIB，必须在遍历到 `Insert` 时**额外遍历 `ins.Attributes`**，并给这些属性单独标注 `Owner = 该 Insert`（否则日志里只会显示块定义名，定位不到是哪个图框）。

**动态块可见性对属性列表的影响**

* 动态块实例指向匿名块（`*U###`），现有策略**不进入匿名块**（README 6.2），这条要继续保持；
* 属性列表以 `Insert.Attributes` 为准，可见性状态切换后**不在当前可见性里的属性仍可能留在列表里**，靠 `AttributeEntity.IsInvisible` 判定；
* 规则选项：`IncludeInvisibleAttributes`（默认 false）——不可见属性是否参与替换。默认不参与，因为改它用户看不见，出了问题也发现不了。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### B-3 ACAD_TABLE 单元格文字

| 分轨 | 内容 |
|---|---|
| 纯 ACadSharp 可完成 | 读：`table.Rows[i].Cells[j]` / `table.GetCell(r,c)` → `Cell.Contents`（`List<CellContent>`）→ `CellContent.CadValue.Value`；`Cell.MergedValue`、`table.MergedCellRanges`、`table.CellStyleOverride` 均暴露 |
| 需 probe / 回归 | **单元格文字写回**：`CellContent.CadValue.SetValue("...")` → 写盘 → 回读比对；**合并单元格**；**多行文字单元格** |
| 暂不支持 / 降级 | `TableEntity.GetBoundingBox()` 返回 `BoundingBox.Null`（源码）→ 表格不能参与区域过滤；表格缓存块（`*T`）不随单元格数据同步 → 显示可能滞后 |

**近期重构带来的变化（与"旧印象"的差别）**

3.8.x 已把单元格结构重构为 `Cell.Contents` 列表 + `CellContent{ ContentType, Format, CadValue }`，并暴露 `CellStyleOverride`。所以**不能再按"表格单元格文字不可写"来定级**（README 6.2 那句已过时），应改为"支持但需回归"。

**必须覆盖的边界**

1. **合并单元格**：`Cell.MergedValue` 标记合并角色，只有左上角单元格持有内容，其余 `Contents` 为空。写错位置 = 静默无效。规则必须先 `GetCell` 再判 `MergedValue`，只处理主单元格。
2. **多内容单元格**：`Cell.Content` 在 `HasMultipleContent` 时**返回 null**（源码），必须遍历 `Contents`，否则整格被当成空。
3. **多行文字单元格**：`CadValue` 是字符串，内部可能含 `\P`；替换时要走同一套 MText tokenizer，不能当单行处理。
4. **缓存块滞后**：DWG 里表格的可见几何来自它的匿名缓存块。改单元格数据不更新缓存 → 查看端可能显示旧值。这是纯文件模式的固有限制，方案里只做**显式提示**，不提供宿主侧重生成。
5. **拆分段**：`TableBreakData` / `BreakRowRanges` 存在时，`Rows` 是逻辑行，视觉上是多段。

**验证方法**：造一张含 3 种表格（普通 / 含合并 / 含多行）的测试图 → 改单元格 → 写盘 → 回读比对 → **再用独立解析器核对**（不能用 ACadSharp 自证）。本机三张样本图均无 `ACAD_TABLE`（基线 #7/#15），**这一项必须在实施前先补测试图**。

**降级策略**：写回失败 → 只读导出（CSV）+ 报告；合并单元格无法定位主格 → 记 `Skipped (merged cell)`。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### B-4 DIMENSION 文字位置推算与"遮罩联动"边界

**可读到的信息（实测）**

```
DimensionLinear  Text="4890"  Measurement=4889.8776  IsTextUserDefinedLocation=True
Block = *D139（8 个实体）
   Line ×2 (MISC_PIP_DIM)
   Insert ×2（箭头）
   MText  \A1;4890  InsertPoint=(3453.65,13821.32)  Height=187.5
   Point ×3 (Defpoints)  ← 定义点
```

| 分轨 | 内容 |
|---|---|
| 纯 ACadSharp 可完成 | 读 `Text/Measurement/InsertionPoint/TextMiddlePoint/IsTextUserDefinedLocation`；读 `Block` 内 MText 的位置与字高；改 `Dimension.Text`（显式覆盖） |
| 需 probe / 回归 | 改 `Text` 后**缓存块是否同步**（大概率不同步，需实测确认）；`Dimension.UpdateBlock()` 存在但语义与宿主重生成不等价，**不作为方案前提** |
| 暂不支持 / 降级 | **文字遮罩随标注联动**：纯文件模式无反应器，不可能实现；`Dimension.GetBoundingBox()` 退化（基线 #2）；`MText.GetBoundingBox()` 退化（基线 #1） |

**文字位置推算的两条路**

1. **首选：读 `Dimension.Block` 里的 MText**（实测可用）。位置 = `mtext.InsertPoint`，字高 = `mtext.Height`，文字 = `mtext.Value`（含 `\A1;` 对齐码，需剥）。
   * 优点：就是显示位置，不需要推算；
   * 缺点：依赖缓存块存在（未重生成的标注可能没有 `*D` 块或块内无文字）；块内可能是 `TEXT` 而不是 `MTEXT`（两种都要处理）。
2. **兜底：`TextMiddlePoint` + `IsTextUserDefinedLocation`**。用户手动拖过文字时该点为真值；否则要用 `InsertionPoint`、`DefinitionPoint`、`TextRotation` 自建推算（各标注子类几何不同，工作量按子类分裂）。

**文字矩形**：`MText.GetBoundingBox()` 退化，必须自建：
`width ≈ Σ(字符宽) × height × widthFactor`，中文按 1.0×height、西文按 0.6~0.7×height（可配置），多行按 `\P` 切行取最大行宽，再按 `Rotation` 旋转成 AABB。**这套估算器是 D-1/D-2/D-3 的公共基础件**，必须先做、先标定。

**"尺寸文字遮罩随标注联动"的边界（必须写进 UI 和报告）**

* 遮罩放在**模型空间**：标注被拉伸/改文字后，遮罩不动 → **静态**，会露出旧位置的空洞或盖住新文字。
* 遮罩放进 `Dimension.Block`（`*D###`）：能随标注整体变换移动，但**任何一次标注重生成都会重建这个块，手工塞进去的图元会被清掉**——比静态更脆。
* 因此：**默认放模型空间，且明确标注"静态遮罩，不随标注联动"**；不放进 `*D` 块。
* 明确排除的方案（按项目约束，不采用）：标注样式级背景填充（`DIMTFILL`）与任何宿主反应器。

**静态遮罩的适用条件（写进规则说明）**

1. 标注文字位置已定稿（`IsTextUserDefinedLocation=true`，或 `*D` 块已生成且与显示一致）；
2. 本批处理是**出图/归档**用途，之后不再编辑标注；
3. 接受"改了标注就要重跑一次遮罩规则"。

**显式降级标注**：差异预览里该行状态为 `Will change`，并追加标记 `mask=Static (not linked)`；报告汇总里单列"静态遮罩 N 处"。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### B-5 ModelSpace / Layout 遍历与三种过滤

| 分轨 | 内容 |
|---|---|
| 纯 ACadSharp 可完成 | `doc.ModelSpace`；`doc.BlockRecords.Where(b => b.Layout != null)`；`Insert.GetBoundingBox()`（实测可用）；矩形/多边形谓词自建 |
| 需 probe / 回归 | 布局名通配符匹配后的**重复命中**（模型空间块 `Layout.Name=="Model"`，基线 #14）；旋转块参照的包围盒保守度 |
| 暂不支持 / 降级 | `TableEntity` 无 bbox；`Text/MText` 无 bbox；`Wipeout` 旋转 bbox 有误差；嵌套实体没有累计变换 |

**空间级**

```csharp
// 现状（DatabaseWalker.GetSpaces）已经按引用去重，可复用
doc.ModelSpace                                   // 模型空间
doc.BlockRecords.Where(b => b.Layout != null)    // 布局（含 *Model_Space，靠 HashSet<BlockRecord> 去重）
```

**关键坑**：`*Model_Space` 这个块记录的 `Layout.Name` 就是 `"Model"`。按名字过滤布局时，`"Model"` 会同时命中模型空间和一个"布局"，导致**同一批实体被处理两遍**。实测证据：探测程序不去重时，442 个实体被枚举成 884，84 个标注变成 168。**布局名过滤必须先按 `BlockRecord` 引用去重，再把 `"Model"` 显式映射到模型空间或直接排除。**

**布局名通配符**：自建 matcher（`*` → `.*`，`?` → `.`，其余 `Regex.Escape`），不要直接 `new Regex(pattern)`——布局名里带 `(`、`+`、`-` 的不少（`A1-平面(1)`）。

**容器级（推荐主路径）：图框块参照**

* 用 `Insert.GetBoundingBox()` 取子区域（实测含块内实体、含缩放）；
* 块名匹配走 `EntityFilter.EffectiveBlockName()`（动态块回溯 `BlockRecord.Source`，项目已有）；
* 可再叠加图框属性值条件（`Insert.Attributes` 里某个 Tag 的值匹配）；
* **注意**：图框包围盒是轴对齐包络，图框旋转 90° 时 bbox 仍然正确（因为是变换后的点），但"图框内"的判定要按 bbox 而不是按旋转矩形，边界上会有少量误收。

**几何区域级**

| 谓词 | 实现 | 适用 |
|---|---|---|
| 中心点在区域内 | 取实体 bbox 中心 → 点在多边形内（ray casting） | 默认，最稳 |
| 完全包含 | 实体 bbox 四角全在区域内 | 严格，会漏掉跨界实体 |
| 部分相交 | bbox 与区域相交（矩形）或多边形-多边形相交（SAT / 边相交） | 最宽松，误收最多 |

**误差来源（必须写进差异预览的备注）**

1. **旋转块参照**：bbox 是包络，实际形状可能只占一角 → "部分相交"会误收；
2. **长曲线**（`Spline`/`Ellipse`/`Arc`）：bbox 由库的离散近似算出，大半径弧的包络可能明显大于视觉范围；
3. **Wipeout**：旋转时 bbox 错（B-1 实测）；
4. **文字**：bbox 退化，必须换成自建矩形，否则所有文字都会被判成"在 (x,y) 这一个点上"；
5. **表格**：bbox 是 `Null`，直接判 `Skipped (no bbox)`；
6. **嵌套实体**：`ScanHit` 目前只有 `Owner`/`Depth`，**没有累计变换矩阵**。块定义内实体的坐标是块坐标，直接拿去和 WCS 区域比是错的。要么给 `ScanHit` 加 `OwnerChain` + 累计 `Transform`，要么明确"区域过滤只作用于顶层实体"（默认后者，简单且不会错）。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

## 4. C. UI 设计审查与修改方案

### 4.1 现状（读码结论）

`Jun.Cad.Batch.Win` 是**独立 WinForms 桌面程序**（`Jun.Cad.Batch.exe`），不是 AutoCAD 内面板——这一点已经符合要求，继续保持。

| 能力 | 现状 | 结论 |
|---|---|---|
| 新增规则 | `OnAddRule` → `CreateRuleTemplate(type)`，只给 3 种类型写了模板，其余落到"改层"模板 | ⚠ 新类型会拿到错误模板 |
| 编辑规则 | `RuleEditorDrawer`（模态窗体，深拷贝、类型切换清理残留字段、两级校验） | ✅ 达标 |
| 复制规则 | **无** | ❌ 缺 |
| 启用/禁用 | 规则行左侧开关 + 抽屉里的 Switch | ✅ 达标 |
| 调整顺序 | `OnMoveUp` / `OnMoveDown` | ✅ 达标（无拖拽） |
| 导入/导出 | 只有整份 `Jun.Cad.Batch.json` 保存（带 `.bak`），单条规则不能导出 | ❌ 缺 |
| 差异预览 | **无**，`OnRun` 选完文件直接 `OfflineRunner.Run` 写盘 | ❌ 缺（最大缺口） |
| 前置检查可视化 | 只有名称校验黄点（`NameValidator`） | ❌ 缺 |
| 执行报告 | `RunResultDialog`（日志原文 + 汇总表） | ✅ 达标 |
| 回滚 | 仅 in-place 时的文件级 `.bak`；没有"本次运行"清单 | ⚠ 只到文件级 |

### 4.2 修改方案

**① 差异预览（新页签，执行前必经）**

* 新增 `PreviewRunner`：与 `OfflineRunner` 共用规则执行路径，但**不写盘**，改为收集 `ChangeRecord`；
* 实现方式：在 `RuleContext` 上挂 `IDiffSink`，规则把"要改什么"写进 sink 而不是直接改实体；或者更省事——**在内存文档上真改**，改之前记录旧值（`CadFileSession` 本来就是"失败不写盘"，天然支持预演）；
* 列：`文件 | 空间/布局 | 句柄 | 类型 | 摘要 | 操作 | 旧值 → 新值 | 状态 | 降级标记`；
* 状态三态：`Will change` / `No change` / `Blocked`（附原因，如"目标图层不存在且未开自动创建"）；
* 降级标记**必须显式**：`mask=Wipeout→Solid（writer probe 失败）`、`mask=Static（不随标注联动）`、`table cell=Skipped (merged)`；
* 万级行必须虚拟化：`DataGridView` 的 `VirtualMode`（WinForms 足够，不需要换 WPF）。

**② 前置检查面板（与规则列表同屏可见）**

| 检查项 | 来源 | 影响 |
|---|---|---|
| Wipeout writer probe | 临时文档构造→写出→回读（B-1） | 决定遮罩走 Wipeout 还是 Solid |
| Table cell 回归 | 内置测试图跑一遍单元格读写 | 决定表格规则是"可写"还是"只读导出" |
| DIMENSION 遮罩模式 | 固定为 `Static` | 提示"不随标注联动" |
| 输出格式可写性 | `CadFileSession.IsSafeWriteVersion` | 提示版本会被提升 |
| 图案/线型库可用性 | `ResourceLocator` | 决定 `HatchPatternChange` 能否跑 |

状态三态：`通过` / `失败` / `未测`，直接显示在**规则行角标**（现有黄点体系扩展为黄/红/灰）和检查面板里，**不能只写日志**。

**③ 复制规则**

`OnDuplicateRule`：深拷贝（复用 `RuleEditorDrawer.Clone` 的 JSON 往返）→ 名称加 `_副本` → 插入到原规则之后 → 选中新行。

**④ 规则导入/导出**

* 单条导出：`{ schemaVersion, rule }` → `.rule.json`；
* 整组导出：`{ schemaVersion, rules[] }` → `.ruleset.json`；
* 导入时**必须做类型检查**：`RuleFactory.IsRegistered(type)` 为假 → 拒绝并提示"本机没有该规则类型"，不能静默丢弃；
* 未知字段靠 `JsonExtensionData`（`RuleConfig.Extra`）原样保留——现有设计已经支持，直接用。

**⑤ 回滚（按"本次运行"）**

```
runs/20261031-095512/
  manifest.json      # 文件清单、输出路径、备份路径、每条规则的改动条数、降级项
  backups/xxx.dwg.bak
```

* 每次运行生成一个目录；`manifest.json` 记录"哪个输出文件来自哪个源文件、备份在哪"；
* 「回滚本次运行」= 按 manifest 把备份还原回输出路径（不是源路径），并校验哈希；
* 注意：默认模式（写 `output/`）不覆盖源文件，回滚的价值主要在 `--in-place`；UI 里要区分两种模式给不同文案。

**⑥ WPF vs WinForms 取舍**

| 维度 | WinForms（现状） | WPF |
|---|---|---|
| 现有资产 | 2732 行自绘窗体 + 离屏截图自检（`--screenshot*`） | 需要重写 |
| 大列表虚拟化 | `DataGridView.VirtualMode` 够用 | `DataGrid` 原生更好 |
| 高 DPI | 已经踩过坑并修好（`AutoScaleMode.Dpi`） | 原生更好 |
| 样式/主题 | 全靠自绘 | 样式化容易 |
| 迁移成本 | 0 | 高（自绘控件、截图自检全要重做） |

**结论：保留 WinForms**，差异预览用 `DataGridView` 虚拟模式；不迁 WPF。
**明确不做**：AutoCAD 内嵌面板/窗口、`ShowModalDialog`/`ShowModellessDialog` 之类的宿主内调用——本项目是独立桌面程序。

---

## 5. D. 四项必加功能详细方案

### D-1 批量添加文字遮罩

**数据模型**

```jsonc
{
  "Type": "TextMask",
  "Filter": { "Scope": "ModelAndLayouts", "LayoutPatterns": ["A1-*"], "Container": { "BlockPattern": "A1_*" } },
  "Action": {
    "TargetKinds": ["TEXT", "MTEXT", "ATTRIB", "DIMENSION"],
    "MaskMode": "Auto",              // Auto | Wipeout | Solid
    "PaddingRatio": 0.15,            // 相对字高的留白
    "PaddingAbsolute": 0.0,
    "OnlyWhenOccluded": true,
    "MinOccludedRatio": 0.05,        // 被遮挡面积占比阈值
    "IgnoreBlockReferenceBounds": true,
    "IncludeInvisibleAttributes": false,
    "DimensionTextMode": "ExplicitOnly", // ExplicitOnly | All（All 会告警）
    "MaskLayer": "_MASK",
    "SolidColorIndex": 255,
    "SolidLayerPlottable": false
  }
}
```

**过滤条件**：空间（模型/布局/通配符）→ 容器（图框块参照 bbox）→ 区域（矩形/多边形）→ 实体谓词（类型/图层/颜色）→ 本规则专属条件（字高范围、文字内容正则）。

**执行流程**

1. 建空间索引：对目标空间内所有实体取 bbox（文字类用自建矩形），按网格分桶；
2. 遍历候选文字 → 自建矩形 `R_text`（含 padding）；
3. 遮挡判定：查桶内实体 → 排除自身与"配套图元" → 逐个判 bbox 相交 → 累计相交面积 / `R_text` 面积 ≥ 阈值；
4. 需要遮罩 → 按 `MaskMode` 生成 Wipeout（probe 通过）或 Solid（降级）；
5. 遮罩实体插入**与文字相同的空间**（模型/布局块），图层 = `MaskLayer`，并置于文字之前（`Entities.Insert(index, mask)`，保证在文字下面）；
6. 记录 `ChangeRecord`（旧值=无，新值=`Wipeout 100×50 @ (x,y)`）。

**遮挡判断的包围盒策略**

* 文字矩形：自建（B-4），`height` 取 `TextEntity.Height` / `MText.Height`，宽度按字符宽估算，多行按 `\P` 切；
* 遮挡物：**只用 bbox 相交**，不做精确几何求交（成本与收益不成比例）；
* **排除"文字自身所属"的图元**——这是误判的主要来源：
  * `DIMENSION`：排除该标注的 `Dimension.Block` 内全部实体（实测 `*D` 块里有 2 线 + 2 箭头 + 1 文字 + 3 定义点），否则标注自己的尺寸线一定会"压住"自己的文字；
  * `ATTRIB`：排除同一个 `Insert` 的其它 ATTRIB 与该 `Insert` 引用的块内实体；
  * `BlockReference` 的块内配套图元：排除同一 `Owner` 下的实体；
  * `Defpoints` 图层上的点（实测标注块里有 3 个）——永远不参与遮挡判定；
  * 遮罩层自身（`_MASK`）——避免重复叠加。
* **复杂块参照 bbox 过大的误判风险**：块参照的 bbox 是包络，一个"设备外形"块可能只画了一根管，包络却覆盖半张图。开关 `IgnoreBlockReferenceBounds`（**默认 true**）：
  * true → 块参照整体不参与遮挡判定，只按块内实体判定（保守，少加遮罩）；
  * false → 用块参照 bbox 判定（激进，容易给整片文字加遮罩）。
  差异预览里对"因块参照包络而判定为遮挡"的行单独标记 `reason=block-bbox`，方便事后追。

**ACadSharp 实现要点**

* Wipeout 构造与 probe：见 B-1；
* Solid 降级：4 角点，注意 1-2-4-3 顺序；放不可打印图层；
* 插入顺序：`BlockRecord.Entities` 是 `List<Entity>`，`Insert(index, entity)` 可控制绘制顺序（后画的在上）；
* `WipeoutVariables.DisplayImageFrame` 可关闭边框（可选，默认关边框）。

**边界情况**

| 情况 | 处理 |
|---|---|
| 文字旋转 / 倾斜 | 矩形按 `Rotation` 旋转后取 AABB；`ObliqueAngle` 忽略（误差 < 5%） |
| 文字镜像 | `TextEntity.Mirror` 为真时宽度估算不变，位置按 `AlignmentPoint` |
| 多行 MTEXT | 按 `\P` 切行，取最大行宽；`RectangleWidth > 0` 时优先用它 |
| MTEXT 分栏 | `ColumnData` 存在时按单栏估算并标记 `approx` |
| 不可见属性 | 默认跳过（`IncludeInvisibleAttributes=false`） |
| 文字在块定义内 | 遮罩要插进**同一个块定义**（否则位置对不上）；但块定义被多处参照 → 遮罩会被复制到所有实例，需二次确认 |
| 已存在遮罩 | 按"文字句柄 + 遮罩类型"去重，避免重复叠加 |
| 遮罩层被锁定 | 记 `Blocked (mask layer locked)` |

**风险点**

1. **遮罩数量爆炸**：一张图 1000+ 文字，全加 Wipeout → 文件变大、打开变慢；建议默认 `OnlyWhenOccluded=true` + 上限 `MaxMasksPerDrawing`（默认 2000，超出记 `Blocked`）；
2. **误判**：宁可少加（保守），也不要给整张图加满；
3. **打印差异**：Wipeout 在打印时是"白底"，Solid 是实色——两者在彩色打印下视觉不同，必须在报告里分开计数；
4. **旋转 Wipeout 的 bbox 误差**：只用自建 AABB 计算，不信任库返回值。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### D-2 批量替换文字内容

**数据模型**

```jsonc
{
  "Type": "TextReplace",
  "Action": {
    "TargetKinds": ["TEXT", "MTEXT", "ATTRIB", "ACAD_TABLE"],
    "MatchMode": "Contains",        // Exact | Contains | WholeWord | Regex
    "Pattern": "P30",
    "Replacement": "P40",
    "CaseSensitive": false,
    "IncludeInvisibleAttributes": false,
    "RecurseNestedBlocks": true,
    "MaxNestingDepth": 8,
    "AllowEmptyResult": false,      // 替换后为空串是否允许（默认拒绝）
    "DimensionTextMode": "Off"      // Off | ExplicitOnly（单独语义，默认关）
  }
}
```

**匹配模式**

| 模式 | 语义 | 注意 |
|---|---|---|
| Exact | 整个 token 完全相等 | 对 MTEXT 是"整个纯文本 token"，不是整段 |
| Contains | 子串 | 默认 |
| WholeWord | 词边界 | 中文无空格分词 → 对 CJK 退化为 Contains 并**告警** |
| Regex | 正则 | 支持 `$1` 捕获组；超时 200ms 防回溯爆炸 |

`CaseSensitive` 独立开关，默认 false（图纸里大小写混用极常见）。

**MTEXT tokenizer（核心件）**

```
输入： {\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P...}
token：GroupStart "{" | Format "\l" | Format "\fISOCPEUR|b0|i0;" | Format "\T1;"
       | Text "CONNECT TO" | Format "\P" | ...
```

* 规则：`\` 后跟字母/符号 → 读到一个"格式码结束符"（`;` 或单个字符）为止，整体算 `Format` token；
* `{` / `}` 算 `GroupStart` / `GroupEnd`；
* 其余连续字符算 `Text` token；
* **只在 `Text` token 上做匹配**；
* 匹配跨越 token 边界（例如 pattern 里含 `\P` 或跨越 `{\f...;`）→ 记 `Skipped (format boundary)`，**不猜、不拼**；
* 替换后按原顺序重组，格式码原样保留；
* `\~`（不换行空格）在匹配时视为空格还是保留？→ 选项 `TildeAsSpace`，默认 false（保留原字符，匹配按字面）。

**为什么不能用 `PlainText`**：实测 `{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P...}` 的 `PlainText` 是 `lT1;CONNECT TO`——`\l` 的 `l` 和 `\T1;` 的 `T1;` 都留在结果里。拿它做匹配源会替换到错误位置，且写回时无处安放。

**AttributeReference 递归**

* 遍历到 `Insert` 时**额外遍历 `ins.Attributes`**（见 B-2 的陷阱）；
* 递归进入 `ins.Block.Entities`（现有 `DatabaseWalker` 已做，注意匿名块/外参跳过策略保持不变）；
* 同一块定义被 N 处参照 → 块定义内的 TEXT 只改一次（改的是定义），但**每处参照的 ATTRIB 要各改一次**（实例级）。差异预览要能区分这两类。

**动态块可见性**

* 匿名实例块（`*U###`）不进入（沿用现有策略）；
* 属性列表以 `Insert.Attributes` 为准；
* 不在当前可见性状态里的属性：`IncludeInvisibleAttributes=false` 时跳过，并在报告里计数（"另有 N 个不可见属性未处理"）。

**ACAD_TABLE 单元格**

* 遍历 `Rows[i].Cells[j]` → `Contents`（不是 `Content`，后者在多内容时为 null）；
* 合并单元格：只处理 `MergedValue` 为主格的那个；
* 多行单元格：走同一套 tokenizer；
* 写回 `CadValue.SetValue(newText)`；
* **回归测试要求**（实施前必须补测试图）：普通表格 / 含合并 / 含多行 / 含公式（`ContentType=Field` 的单元格**不替换**，否则会破坏字段）。

**不覆盖 DIMENSION 测量值**

* 默认 `DimensionTextMode=Off`；
* `ExplicitOnly` 时只处理 `Text` 非空且 ≠ `<>` 的标注，替换语义单独定义（整段替换，不 token 化，因为标注文字里 `\A1;` 是对齐码不是内容）；
* **绝不改 `Measurement`**（那是几何算出来的，改了也不显示）。

**边界情况**

| 情况 | 处理 |
|---|---|
| 替换后长度变化 | MTEXT 固定 `RectangleWidth` 时会重排，位置可能移动 → 差异预览标注 `reflow` |
| 替换后为空串 | 默认拒绝（`AllowEmptyResult=false`），避免批量删文字 |
| 正则回溯爆炸 | 200ms 超时，超时记 `Blocked (regex timeout)` |
| 同一文字被多条规则命中 | 按规则顺序执行，后一条看到前一条的结果（现状语义），预览里按"最终态"显示 |
| 属性 Tag 本身 | **不替换**（Tag 是块结构的一部分，改了等于改定义） |
| 表格字段单元格 | 跳过（`Field` 内容由公式生成） |

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### D-3 过滤条件：ModelSpace / Layout / 区域

**数据模型（`RuleFilter` 扩展）**

```jsonc
{
  "Spatial": {
    "Mode": "ModelAndLayouts",           // ModelOnly | LayoutsOnly | ModelAndLayouts
    "LayoutPatterns": ["A1-*", "V-0?"],  // 通配符 * ?
    "ExcludeModelSpaceBlock": true
  },
  "Container": {
    "BlockPattern": "A1_*",              // 图框块参照（有效块名，动态块回溯 Source）
    "AttributeMatch": [{ "Tag": "DWG_NO", "Pattern": "KLD-*" }],
    "NestedOnly": false                  // true = 只处理图框内部的实体
  },
  "Region": {
    "Mode": "None",                      // None | Rect | Polygon
    "Points": [[0,0],[1000,0],[1000,500],[0,500]],
    "Predicate": "CenterInside"          // CenterInside | FullyInside | Intersects
  }
}
```

**执行流程（顺序固定，先粗后细）**

```
空间过滤 → 容器过滤（图框 bbox）→ 区域过滤（几何谓词）→ 实体谓词（类型/图层/颜色）→ 规则专属条件
```

**实现要点**

| 环节 | 实现 |
|---|---|
| 空间 | `DatabaseWalker.GetSpaces`（已按引用去重）；`"Model"` 名字要显式映射，不能当成普通布局 |
| 布局通配符 | 自建 matcher（`*`/`?` + `Regex.Escape`），区分大小写不敏感 |
| 图框容器 | `Insert.GetBoundingBox()`（实测可用）；`EffectiveBlockName()` 匹配动态块；属性条件读 `ins.Attributes` |
| 矩形区域 | 两对角点，标准化成 `min/max` |
| 多边形 | 顶点序任意，先算有向面积定方向；自交多边形**拒绝**（报配置错误） |
| 中心点在区域内 | bbox 中心 + ray casting |
| 完全包含 | bbox 四角全在 |
| 部分相交 | 矩形：bbox 相交；多边形：边相交 + 一个顶点在内 |
| 文字实体 | **用自建矩形**，不用 `GetBoundingBox()` |
| 表格 | bbox 为 `Null` → `Skipped (no bbox)` |
| 嵌套实体 | 默认不参与区域过滤（没有累计变换）；要参与必须先给 `ScanHit` 加 `OwnerChain` + `Transform` |

**误差说明（差异预览里逐条显示 `approx` 标记）**

* 旋转块参照：bbox 是包络 → "部分相交"偏宽松；
* 长曲线（`Spline`/大半径 `Arc`）：离散近似 → 包络偏大；
* `Wipeout`：旋转时 bbox 错（B-1 实测）；
* 文字：自建矩形本身有 ±10% 宽度误差；
* 嵌套实体：坐标是块坐标，直接比会错（默认排除）。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

### D-4 所有批处理规则在 UI 中可新增及修改

**规则数据模型（UI 视角）**

```csharp
sealed class RuleDescriptor
{
    string Type;                 // 唯一标识
    string DisplayName;          // "文字遮罩"
    string Category;             // "文字" / "图层" / "块" / "填充"
    RuleParameterSchema Schema;  // 参数集（见下）
    Func<RuleConfig> Template;   // 新增时的默认值（按类型给，不是统一"改层"模板）
    Func<RuleConfig, List<FieldIssue>> Validate;
    string[] PreflightKeys;      // 依赖哪些前置检查（wipeout-writer / table-cell / ...）
    string ExecutionChannel;     // "pure-acadsharp" | "probe-required" | "degraded"
}
```

**规则类型如何扩展（这是关键设计点）**

现状：参数全塞在 `RuleAction` 这个共享袋里，用 `[RuleField(AppliesTo="TextFontChange,...")]` 标注，`FieldCatalog` 反射生成面板。新增一个类型要改 **5 处**（属性 + 标注 + AppliesTo + 模板 + 校验 + 摘要），漏一处就出现"界面看不见但执行时生效"的隐藏字段。

两条路线：

| 路线 | 做法 | 评价 |
|---|---|---|
| A. 枚举式（现状改良） | 继续加属性，但把 5 处改动收敛成一个 `RuleTypeDefinition` 注册点 | 改动小，仍要改核心模型 |
| B. 插件式（推荐） | `IRuleHandler` 增加 `Describe()` 返回 schema；参数存 `RuleConfig.Params`（复用现有 `JsonExtensionData`）；UI 完全按 schema 渲染 | 新增类型 = 加一个类 + 注册一行，核心模型不动 |

推荐 **B，但保留 A 的存量**：现有 11 条规则继续用强类型字段（不动，避免回归），新规则（含四项必加功能）用 `Params` + schema。读写时两者并存，`RuleJson` 按"强类型优先、Params 兜底"序列化。

**交互流程**

| 操作 | 交互 |
|---|---|
| 新增 | 工具栏「添加」→ 类型选择器（按 `Category` 分组，显示 `ExecutionChannel` 角标）→ 用该类型的 `Template` 生成 → 直接打开抽屉 |
| 编辑 | 双击行 / 抽屉；字段按 schema 渲染，`AppliesTo` 之外的字段置灰并给原因（现有 `FieldDescriptor.DisabledReason` 机制可复用） |
| 复制 | 工具栏「复制」→ 深拷贝 + 名称 `_副本` → 插到原规则后 |
| 启用/禁用 | 行左侧开关（已有） |
| 顺序 | 上下移（已有）+ 拖拽（可选） |
| 导入/导出 | 单条 `.rule.json` / 整组 `.ruleset.json`，带 `schemaVersion`；导入时校验类型是否注册 |
| 前置状态 | 行右侧角标：灰=未测、黄=有警告、红=会阻断；悬停显示原因（现有黄点机制扩展） |

**避免"硬编码参数面板"的三条硬要求**

1. 面板**不许**按类型写 `if (type == "TextMask")` 分支——所有字段来自 schema；
2. 新增类型**不许**改 `BatchToolForm`——只改注册表；
3. 每个类型必须自带 `Template`（否则新类型会拿到错误的默认值，现状 `CreateRuleTemplate` 就是这样）。

**是否需要宿主参与**：否；本项目不依赖 AutoCAD 宿主。

---

## 6. 实施优先级

| 优先级 | 事项 | 理由 |
|---|---|---|
| **P0** | ① 文字矩形估算器（公共件）；② 空间/布局/图框过滤（D-3 前两级）；③ Wipeout writer probe；④ 差异预览骨架 + 降级标记 | 后面所有功能都依赖这四件；没有 probe 和预览就上线遮罩规则＝盲改 |
| **P1** | ⑤ 文字替换（TEXT/MTEXT/ATTRIB）；⑥ 遮罩（TEXT/MTEXT）；⑦ UI 复制 + 导入导出；⑧ 前置检查面板 | 主体价值，风险可控 |
| **P2** | ⑨ ACAD_TABLE 单元格（先补测试图 + 回归）；⑩ DIMENSION 静态遮罩；⑪ 区域几何谓词（多边形/部分相交）；⑫ 本次运行级回滚 | 依赖未实测能力或边界复杂 |
| **P3** | ⑬ A 节的 10 条新规则（图框属性、图层映射、块内归 0 层…）；⑭ 拖拽排序 | 价值高但可后置 |

---

## 7. 主要技术风险

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| 1 | **文字包围盒退化** | 遮罩尺寸、遮挡判定、区域过滤全部依赖自建估算，误差不可忽略 | 估算器先用真实图人工标定（取 50 个文字量实际宽高），把误差写进差异预览的 `approx` 标记 |
| 2 | **Wipeout 自洽 ≠ AutoCAD 认可** | probe 通过仍可能在 CAD 里显示异常 | 降级路径（Solid）必须始终可用；报告里标注"未做宿主验证" |
| 3 | **ACAD_TABLE 缓存块不同步** | 单元格改了但显示旧值 | 默认只读导出；写回需显式开关 + 报告提示 |
| 4 | **DIMENSION 遮罩静态** | 标注一改就错位 | 默认放模型空间 + 显式标注"不随标注联动"；不放进 `*D` 块 |
| 5 | **嵌套实体无累计变换** | 区域过滤对块内实体误判 | 默认"区域过滤只作用于顶层实体" |
| 6 | **遮罩数量爆炸 / 文件膨胀** | 大图跑完打不开 | `OnlyWhenOccluded` + `MaxMasksPerDrawing` + 运行前预估条数 |
| 7 | **布局名重复命中**（`"Model"`） | 同一批实体被处理两遍 | 按 `BlockRecord` 引用去重（实测证据见基线 #14） |
| 8 | **新规则类型模板错误** | 新类型拿到"改层"模板，静默改错 | 每个类型必须自带 `Template`，注册表强制校验 |

---

## 8. 实施后的修正（实测推翻了第 5 节的一个假设）

写代码时发现一条**评审阶段没想到**的硬约束，它改变了 D-1 的形态，必须记在这里：

**新建的 Wipeout / Solid 一定画在文字上面，因为实体集合只能追加。**

* `BlockRecord.Entities` 的类型是 `CadObjectCollection<Entity>`，公开成员只有
  `Add` / `AddRange` / `Remove` / `Clear`，**没有按索引插入**；
* 新实体的句柄按文档种子递增，所以它天然排在最后 → 绘制顺序最后 → Wipeout 会把文字自己擦掉；
* 要让它排在文字之前，只能"移出文字 → 加遮罩 → 再把文字加回去"。实测这会给文字
  **重新分配句柄**（`48 → 4A`），而 `CadObject.Handle` 是**只读属性**，无法恢复；
* 整表 `Clear()` + `AddRange()` 更糟：所有实体都会拿到新句柄，差异预览里会显示成"全图新增"。

于是 D-1 改成按类型分流：

| 类型 | 做法 | 代价 |
|---|---|---|
| MTEXT | **原生背景遮罩**（`BackgroundFillFlags`），不新增实体 | 无；但 `BackgroundScale` 回读恒为 1.5 |
| TEXT / ATTRIB | Wipeout/Solid + **重建文字实体**，`MaskRebuildText` 显式开关（默认关） | 文字句柄改变 |
| DIMENSION | **不支持**，记 Blocked 并写明原因 | —— |

顺带修正另外两条：

* **遮挡判定不能只按包围盒面积算**。实测第一版把所有文字都判成"100% 遮挡"——
  一条斜穿整图的对角线，其包围盒能盖住全图。改成：线状图元（`LINE`、`CIRCLE` 环）
  只判"是否真的穿过文字矩形"（穿过一次记 0.25），面状图元（`HATCH`/`SOLID`）才按面积累加。
  改完分布变成 25% / 50% / 75% / 100% 多档，符合直觉；
* **表格单元格写回**仍然"需回归"：本机三张样本图都没有 `ACAD_TABLE`，
  前置检查里明确显示"未测"，不假装通过。

## 9. 附：本次探测可复用的东西

* `.workbuddy-ai/probe/`：能力探测程序（读图 → 统计 → 构造 Wipeout/Solid → 写出 → 回读 → 打印包围盒/单元格/标注内部构成）。建议把其中的 `Wipeout` 与 `TableCell` 两段搬成正式的 `Preflight` 检查，作为 UI 前置检查面板的数据源。
* 关键实测数字（供写测试断言用）：`Wipeout bbox=0,0-100,50`、旋转 Wipeout 误差 `10,4 vs -4..0/0..10`、`MText bgScale` 恒 1.5、`*D` 块 8 个实体、`Dimension.Text` 显式覆盖 84/84。
