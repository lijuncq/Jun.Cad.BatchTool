# Jun.AutoCAD.BatchTool

配置驱动的 AutoCAD / 中望CAD 批量处理插件。用一份 JSON 描述"选什么、改什么"，插件按规则顺序在模型空间、全部布局空间以及块定义内部执行批量修改，并输出逐条统计与日志。

- 命名空间：`Jun.AutoCAD`（子命名空间 `Jun.AutoCAD.Config` / `Jun.AutoCAD.Core` / `Jun.AutoCAD.Rules` / `Jun.AutoCAD.Commands`）
- 目标宿主：AutoCAD 2021 ~ 2025（.NET Framework 4.8 / .NET 8 双栈）、中望CAD ZWCAD 2020（.NET Framework 4.8）
- 命令：`BATCHUI`（图形界面）/ `BATCHPROCESS`（命令行）
- 序列化：`Newtonsoft.Json`（AutoCAD 版引用宿主自带版本并随插件拷贝；ZWCAD 版走 NuGet 包并拷贝到输出目录）

> 同一份源码支持两个宿主：`Autodesk.AutoCAD.*` 与 `ZwSoft.ZwCAD.*` 两套托管 API 形状几乎一致，
> 差异点用 `#if ZWCAD` 收口（命名空间 using 与少数 API 形状），见第 2.3 节。

---

## 1. 交付物

| 文件 | 说明 |
|---|---|
| `Jun.AutoCAD.BatchTool.csproj` | 工程文件，按 `AcadVersion` 条件切换目标框架、引用路径与输出目录 |
| `Jun.AutoCAD.PluginEntry.cs` | `IExtensionApplication` 入口，注册内置规则 |
| `Commands/Jun.AutoCAD.BatchProcessCommand.cs` | `BATCHPROCESS` 命令、`/config` 参数解析、日志路径解析 |
| `Config/Jun.AutoCAD.BatchConfig.cs` | 配置模型：`BatchConfig` / `RuleConfig` / `RuleFilter` / `RuleAction` |
| `Config/Jun.AutoCAD.ConfigLoader.cs` | 默认路径解析、反序列化、结构校验、未知字段检测 |
| `Core/*.cs` | 框架层：日志、规则工厂、DXF 过滤、数据库遍历、执行引擎、各类符号表服务、宿主差异收口 |
| `Rules/*.cs` | 11 个 `IRuleHandler` 实现 |
| `config.json` | 示例配置（覆盖全部 11 种规则类型） |

代码目录结构：

```
Jun.AutoCAD.BatchTool/
├─ Jun.AutoCAD.BatchTool.csproj
├─ Jun.AutoCAD.BatchTool.sln
├─ Jun.AutoCAD.PluginEntry.cs
├─ config.json
├─ README.md
├─ Commands/
│   └─ Jun.AutoCAD.BatchProcessCommand.cs
├─ Config/
│   ├─ Jun.AutoCAD.BatchConfig.cs
│   ├─ Jun.AutoCAD.RuleFieldAttribute.cs ← 字段元数据（标签/控件/可见类型/单位…）
│   └─ Jun.AutoCAD.ConfigLoader.cs
├─ Core/
│   ├─ Jun.AutoCAD.BatchLogger.cs
│   ├─ Jun.AutoCAD.BatchRunner.cs
│   ├─ Jun.AutoCAD.BlockNameUtil.cs
│   ├─ Jun.AutoCAD.DatabaseWalker.cs
│   ├─ Jun.AutoCAD.ConfigSaver.cs
│   ├─ Jun.AutoCAD.ConfigValues.cs        ← 配置默认值取值（无宿主依赖，可单测）
│   ├─ Jun.AutoCAD.DiagnosticLog.cs
│   ├─ Jun.AutoCAD.DimStyleService.cs
│   ├─ Jun.AutoCAD.DxfFilter.cs
│   ├─ Jun.AutoCAD.EntityScanner.cs
│   ├─ Jun.AutoCAD.FieldCatalog.cs       ← 按类型裁剪字段 + 字段联动（禁用不隐藏）
│   ├─ Jun.AutoCAD.FieldIssue.cs         ← 字段级校验问题（路径 + 红/黄级别）
│   ├─ Jun.AutoCAD.HostCompat.cs      ← AutoCAD / ZWCAD 差异收口
│   ├─ Jun.AutoCAD.HostNameLookup.cs     ← 唯一读符号表做名称校验的地方
│   ├─ Jun.AutoCAD.IRuleHandler.cs
│   ├─ Jun.AutoCAD.LastRunStore.cs
│   ├─ Jun.AutoCAD.INameLookup.cs        ← 名称查询接口（UI 只认它，不碰宿主类型）
│   ├─ Jun.AutoCAD.LayerService.cs
│   ├─ Jun.AutoCAD.NameValidator.cs      ← 保存时的名称校验 + 最近匹配
│   ├─ Jun.AutoCAD.ObjectUtil.cs
│   ├─ Jun.AutoCAD.PluginInfo.cs
│   ├─ Jun.AutoCAD.RuleExecutionResult.cs
│   ├─ Jun.AutoCAD.RuleFactory.cs
│   ├─ Jun.AutoCAD.RuleJson.cs        ← 规则 JSON 精简序列化
│   ├─ Jun.AutoCAD.RuleSummary.cs
│   ├─ Jun.AutoCAD.RuleValidator.cs      ← 唯一的类型级校验入口（红/黄分级）
│   ├─ Jun.AutoCAD.TextEntityHelper.cs
│   ├─ Jun.AutoCAD.UiRunBridge.cs
│   └─ Jun.AutoCAD.TextStyleService.cs
├─ UI/
│   ├─ Jun.AutoCAD.BatchToolForm.cs
│   ├─ Jun.AutoCAD.FieldRow.cs           ← 一行字段控件 + 控件工厂
│   └─ Jun.AutoCAD.RuleEditorDrawer.cs   ← 规则编辑抽屉（表单）
└─ Rules/
    ├─ Jun.AutoCAD.BlockLayerChangeRule.cs   ← 块批量归层（新增）
    ├─ Jun.AutoCAD.BlockReplaceRule.cs
    ├─ Jun.AutoCAD.DimensionStyleChangeRule.cs
    ├─ Jun.AutoCAD.EntityLayerChangeRule.cs
    ├─ Jun.AutoCAD.EntityLinetypeChangeRule.cs
    ├─ Jun.AutoCAD.EntityLineweightChangeRule.cs
    ├─ Jun.AutoCAD.HatchPatternChangeRule.cs
    ├─ Jun.AutoCAD.PurgeUnusedRule.cs
    ├─ Jun.AutoCAD.SetByLayerRule.cs
    ├─ Jun.AutoCAD.TextFontChangeRule.cs
    └─ Jun.AutoCAD.TextHeightChangeRule.cs
```

---

## 2. 编译

```bat
:: 本机 AutoCAD 2025（默认）
dotnet build

:: 编译 2021 ~ 2024（需本机装有对应版本）
dotnet build -p:AcadVersion=2024
dotnet build -p:AcadVersion=2024 -p:AcadDir="D:\Autodesk\AutoCAD 2024"

:: 编译中望CAD 2020（默认安装路径 E:\ZWCAD 2020）
dotnet build -p:AcadVersion=ZWCAD2020
dotnet build -p:AcadVersion=ZWCAD2020 -p:AcadDir="D:\ZWSOFT\ZWCAD 2020"
```

工程按版本自动切换：

| AcadVersion | TargetFramework | 托管程序集 | 输出目录 |
|---|---|---|---|
| 2025 | `net8.0-windows` | `acdbmgd` / `acmgd` / `accoremgd` | `bin\Debug\` |
| 2021 ~ 2024 | `net48` | 同上 | `bin\Debug\` |
| `ZWCAD2020` | `net48` | `ZwDatabaseMgd` / `ZwManaged` | `bin\Debug\ZWCAD2020\` |

界面用 **WinForms**（不引 XAML）：
2025 目标打开 `UseWindowsForms`，`runtimeconfig.json` 里会多声明 `Microsoft.WindowsDesktop.App 8.0` ——
这与 AutoCAD 自身 `acdbmgd.runtimeconfig.json` 的声明完全一致，所以不会多出额外的部署依赖。

输出目录 `bin\Debug\` 中**这四个文件必须放在一起**（NETLOAD 只指向 .dll，其余三个由运行时按同目录解析）：

| 文件 | 作用 |
|---|---|
| `Jun.AutoCAD.BatchTool.dll` | 插件本体（NETLOAD 选这个） |
| `Jun.AutoCAD.BatchTool.runtimeconfig.json` | 声明目标框架 `Microsoft.NETCore.App 8.0` + `Microsoft.WindowsDesktop.App 8.0`（`EnableDynamicLoading` 生成） |
| `Jun.AutoCAD.BatchTool.deps.json` | 依赖清单，宿主据此解析 `Newtonsoft.Json` |
| `Newtonsoft.Json.dll` | JSON 库，随插件拷贝，版本与 AutoCAD 自带的一致 |

> 这三个伴随文件由 `<EnableDynamicLoading>true</EnableDynamicLoading>` 触发生成。**不要只拷 dll 出去**——
> .NET 8 版 AutoCAD 的 NETLOAD 依赖它们做依赖解析，缺了往往只报一句「无法加载程序集」。

代码基线刻意停留在 AutoCAD 2021 级别的托管 API，未使用任何高版本专有方法，因此同一份源码可在两套运行时下编译。

### 2.1 中望CAD 2020 版（`-p:AcadVersion=ZWCAD2020`）

中望CAD 2020 的托管 API 是 Autodesk 那套的同构实现，只是命名空间从 `Autodesk.AutoCAD.*` 换成 `ZwSoft.ZwCAD.*`，
程序集从 `acdbmgd/acmgd/accoremgd` 换成 `ZwDatabaseMgd.dll` + `ZwManaged.dll`：

| AutoCAD | 中望CAD 2020 | 程序集 |
|---|---|---|
| `Autodesk.AutoCAD.DatabaseServices` | `ZwSoft.ZwCAD.DatabaseServices` | `ZwDatabaseMgd.dll` |
| `Autodesk.AutoCAD.Colors` / `.Geometry` / `.GraphicsInterface` | `ZwSoft.ZwCAD.*` 同名 | `ZwDatabaseMgd.dll` |
| `Autodesk.AutoCAD.ApplicationServices` | `ZwSoft.ZwCAD.ApplicationServices` | `ZwManaged.dll` |
| `Autodesk.AutoCAD.EditorInput` | `ZwSoft.ZwCAD.EditorInput` | `ZwManaged.dll` |
| `Autodesk.AutoCAD.Runtime` | `ZwSoft.ZwCAD.Runtime` | `ZwManaged.dll` |

输出目录 `bin\Debug\ZWCAD2020\` 里只需要**两个文件**（.NET Framework 宿主不需要 runtimeconfig/deps）：

| 文件 | 作用 |
|---|---|
| `Jun.AutoCAD.BatchTool.dll` | 插件本体（ZWCAD 里 `NETLOAD` 选这个） |
| `Newtonsoft.Json.dll` | JSON 库（ZWCAD 安装目录不自带，由 NuGet 包拷贝到输出目录） |

在 ZWCAD 里加载：

```
NETLOAD  →  D:\Dev\CSharp\AutoCAD\Jun.AutoCAD.BatchTool\bin\Debug\ZWCAD2020\Jun.AutoCAD.BatchTool.dll
BATCHUI            （或 BATCHPROCESS / BATCHDIAG）
```

> **两个宿主的 DLL 不能混用**：ZWCAD 版引用的是 `ZwSoft.ZwCAD.*`，装到 AutoCAD 里会因找不到程序集而加载失败，反之亦然。
> 因此两套构建产物放在不同目录（`bin\Debug\` 与 `bin\Debug\ZWCAD2020\`），文件名相同但内容不同。
>
> **两套构建可以任意交替**，互不清除对方产物。中间目录已按宿主分开
> （`obj\net8.0-windows\Debug\`、`obj\net48\Debug\`、`obj\ZWCAD2020\Debug\`）。
> 2026-10-29 之前两者共用 `obj\Debug\`，`IncrementalClean` 会按上一份文件清单把对方的产物删掉——
> 表现是"先建 ZWCAD 版、再建 AutoCAD 版，`bin\Debug\ZWCAD2020\` 空了，而且增量构建不会补回来"。

### 2.2 双宿主是怎么做到的

1. **引用按 `AcadVersion` 条件切换**（csproj 里 `IsZwCad` 开关），输出目录也分开；
2. **每个文件的条件 using**：
   ```csharp
   #if ZWCAD
   using ZwSoft.ZwCAD.DatabaseServices;
   #else
   using Autodesk.AutoCAD.DatabaseServices;
   #endif
   ```
   业务代码本身一行不改——两个命名空间的类型名、成员名完全一致；
3. **少量形状差异用别名/条件块收口**：
   | 差异 | 处理 |
   |---|---|
   | 宿主异常类型 | `using HostException = ZwSoft.ZwCAD.Runtime.Exception;`（AutoCAD 分支指向 `Autodesk.AutoCAD.Runtime.Exception`） |
   | `Hatch.GradientShift` 在 ZWCAD 里是 `float` | 赋值前统一 `(float)` 转换，两个宿主都能用 |
   | `Application.DocumentManager` / `Version` / `ShowModelessDialog` | 走 `Core/Jun.AutoCAD.HostCompat.cs` 统一入口 |
   | `Cell.TextStyleId` 等版本差异属性 | 已有的反射兜底逻辑照常生效 |

`HostCompat` 还提供 `ProductName`（"AutoCAD" / "中望CAD"）与 `Describe`，命令行横幅与 `BATCHDIAG` 自检信息会自动显示正确的宿主名。

---

## 3. 加载与运行

```bat
:: AutoCAD 命令行
NETLOAD
D:\Dev\CSharp\AutoCAD\Jun.AutoCAD.BatchTool\bin\Debug\Jun.AutoCAD.BatchTool.dll

:: 中望CAD 命令行（注意是 ZWCAD2020 子目录里的那个）
NETLOAD
D:\Dev\CSharp\AutoCAD\Jun.AutoCAD.BatchTool\bin\Debug\ZWCAD2020\Jun.AutoCAD.BatchTool.dll

BATCHPROCESS
输入配置文件路径 [回车=默认] 或 /config <路径>:
```

三个命令：

| 命令 | 作用 |
|---|---|
| `BATCHUI` | **图形界面（非模式）**：打开即加载默认配置，可勾选规则、调执行设置、执行；点「关闭」才关窗 |
| `BATCHUIRUN` | 内部命令：由界面「执行」按钮排队触发，请勿手工调用 |
| `BATCHPROCESS` | 命令行交互：提示输入配置路径（回车=默认），支持 `/config <路径>` |
| `BATCHAGAIN` | **重复上次执行**：不提示路径，直接重新读取上次用过的配置文件再跑一遍 |
| `BATCHPROCESSDEF` | 免提示：直接用默认配置路径执行。**排查"提示符没出来"时用它** |
| `BATCHDIAG` | 环境自检：AutoCAD 版本、当前图形、伴随文件是否齐全、默认路径候选、规则注册、诊断日志尾部 |

另有独立于业务日志的**诊断日志**：`%TEMP%\Jun.AutoCAD.BatchTool.diag.log`，记录每次命令进入/中止/异常的落点。
AutoCAD 的命令方法抛异常时命令行有时什么都不打印，这个文件就是"到底有没有进命令、卡在哪一步"的证据。

三种指定配置的方式：

1. 直接回车 → 使用默认配置文件。默认路径按优先级查找：
   1) 插件 DLL 同目录 `config.json`
   2) 当前图形所在目录 `config.json`
   3) `我的文档\Jun.AutoCAD.BatchTool\config.json`
2. 输入 `/config D:\work\config.json`
3. 直接把 `config.json` 拖进命令行（等价于输入完整路径）

执行过程会在命令行打印：配置路径 → 每条规则的 DXF 过滤组码 → 命中数 → 逐条明细 → 规则统计 → 汇总表 → 日志文件路径；结束后自动 `REGEN`。

日志文件：默认写在配置文件同目录 `Logs\batch-yyyyMMdd-HHmmss.log`；配置里 `"LogFile": "none"` 可关闭，或填相对/绝对路径。

### 3.1 图形界面（BATCHUI）

输入 `BATCHUI` 弹出"执行 + 设置"窗口（**非模式**，不阻塞命令行）。**窗口一打开就已经加载了默认配置文件**，不需要先选路径，勾选规则后点「执行」即可；窗口不会自动关闭，点「关闭」才退出。

窗口顶部有两个页签：**执行**（跑批处理）和 **规则编辑**（改配置文件、增删改规则、调顺序）。
配置文件相关内容（路径输入框、浏览、重新加载、状态行、「打开配置文件」按钮）都在**规则编辑**页签，执行页只留"跑批处理"直接相关的设置。

界面结构（浅灰底 + 白色圆角卡片，规则用卡片行而不是原生表格）：

```
┌──────────────────────────────────────────────────────────┐
│ ▎批量处理                                                │
│   Jun.AutoCAD.BatchTool · v1.0.0.0 (构建 2026-10-23 15:17) │
├──────────────────────────────────────────────────────────┤
│ [执行]  规则编辑                                        │
├──────────────────────────────────────────────────────────┤
│ ╭ 执行设置 ────────────────────────────────────────────╮ │
│ │ 执行模式 [Sequential 单事务 ▾]  ●单条失败后继续  ●命令行输出明细 │
│ ╰────────────────────────────────────────────────────────╯ │
│  ┌────────────────────────────────────────────────────┐  │
│  │ ▎(●)  替换图框块   ⓑBlockReplace                    │  │
│  │       块 A1_TITLE → 块 A1_TITLE_NEW                 │  │
│  └────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────┐  │
│  │  ( )  设备图层归并  ⓑEntityLayerChange               │  │
│  │       LINE,LWPOLYLINE,CIRCLE [色1] → 图层 EQUIPMENT  │  │
│  └────────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────┤
│ 勾选要执行的规则，点「执行」运行；点「关闭」退出窗口            [全选][全不选][执行][关闭] │
└──────────────────────────────────────────────────────────┘
```

视觉层的几个约定：

- **规则行是自绘卡片**：左侧开关（自绘，替代系统 CheckBox）、启用时左侧亮起主题色竖条、名称后跟一个类型徽标、第二行是「过滤 → 操作」摘要；
- **按钮分三级**：`执行` 实心蓝（主操作）、`保存规则到配置` 描边（次操作）、其余无边框（辅助）；
- **圆角卡片 + 1px 描边**，全部 GDI+ 自绘，不引入任何第三方 UI 库，插件仍是单个 DLL；
- 窗口默认宽度约为原设计的 3/4（更紧凑），支持高 DPI（`AutoScaleMode.Dpi`），缩放后布局按比例放大。

界面能做的事：

| 操作 | 说明 |
|---|---|
| 单击规则行（或左侧开关） | 切换启用/停用；状态直接对应配置里的 `Enabled`，只执行启用的规则 |
| 执行模式 / 出错继续 / 详细输出 | 对应 `ExecutionMode` / `ContinueOnError` / `Verbose`（日志路径不再由界面维护，直接改 config.json） |
| 双击某行 | **打开规则编辑抽屉**（表单，见 3.2.2）。只读的 JSON 已移到抽屉里，可点「显示 JSON」查看 |
| 执行 | **窗口保持打开**，把执行排队回命令上下文跑批处理（命令行有完整输出），可反复调整后再执行 |
| 关闭 | 仅关闭窗口，不执行任何操作 |
| **规则编辑**页签 | 见下表 |

#### 3.2 规则编辑页签

第二个页签用来改配置文件与规则本身，不用手编辑 config.json：

```
┌──────────────────────────────────────────────────────────┐
│ ▎批量处理                                                │
│   Jun.AutoCAD.BatchTool · v1.0.0.0 (构建 2026-10-23 18:17) │
├──────────────────────────────────────────────────────────┤
│ 执行    [规则编辑]                                       │
├──────────────────────────────────────────────────────────┤
│ ╭ 配置文件 ────────────────────────────────────────────╮ │
│ │ [D:/.../bin/Debug/config.json      ] [浏览...][重新加载]  │ │
│ │ 共 12 条规则，已勾选 4 条。单击行切换启用，双击查看规则详情。│ │
│ ╰────────────────────────────────────────────────────────╯ │
│ [BlockLayerChange ▾][添加规则][删除规则][上移][下移][打开配置文件] │
│ ╭ 规则列表 ────────────────────────────────────────────╮ │
│ │ ▎替换图框块    BlockReplace      启用                  │ │
│ │ ▎设备图层归并  EntityLayerChange 启用  ← 选中（高亮）  │ │
│ │ ▎注释文字体系统 TextFontChange    启用                  │ │
│ ╰────────────────────────────────────────────────────────╯ │
│ ╭ 规则 JSON ───────────────────────────────────────────╮ │
│ │ {                                                    │ │
│ │   "Name": "设备图层归并",                            │ │
│ │   "Type": "EntityLayerChange",                       │ │
│ │   ...                                                │ │
│ │ }                                                    │ │
│ ╰────────────────────────────────────────────────────────╯ │
├──────────────────────────────────────────────────────────┤
│ 共 12 条规则                       [保存规则到配置]       │
└──────────────────────────────────────────────────────────┘
```

| 操作 | 说明 |
|---|---|
| 配置文件 | 路径输入框 + 「浏览...」「重新加载」+ 状态行（共几条/已勾选几条/配置警告数）。**打开即自动加载默认配置**，不需要先选路径 |
| 打开配置文件 | 用系统默认程序打开 config.json，做深度编辑 |
| 规则列表 | 点击选中某条规则，选中的行会高亮并在左侧亮出色条；每行显示名称、类型、启用状态 |
| 添加规则 | 左侧下拉框选规则类型（清单来自 `RuleFactory`，新增类型自动出现），点「添加规则」追加一条该类型的模板规则；`BlockLayerChange` / `BlockReplace` / 其余类型各有一套预填模板，改完 JSON 再保存 |
| 删除规则 | 删除当前选中的规则，有二次确认 |
| 上移 / 下移 | 调整规则执行顺序（规则按数组顺序执行，顺序会影响结果） |
| JSON 预览 | 当前选中规则的**精简版** JSON（只保留真正生效的字段，见 3.2.1）。**只读**，用于核对与复制；编辑请双击行打开抽屉 |
| 保存规则到配置 | 把整段 `Rules` 数组写回 config.json |

两条与"能不能看懂/会不会写错"直接相关的行为（2026-10-29 新增）：

1. **摘要覆盖全部条件**。规则行上的一行摘要现在会把 `Filter` 里每一个"已设置"的字段都体现出来——
   包括 `LayerNames`、`SourcePatternNames`、`TrueColor`、`DimensionStyleName`、非默认的 `Scope`、
   关掉的 `SkipLockedLayers` / `IncludeNestedBlocks` 等。此前漏掉这些字段，`LayerNames=[A,B] → 图层 X`
   会被渲染成 `(全部) → 图层 X`，同事读到"改全图"，实际只改两层。
   万一将来新增了字段却忘了写渲染，它会自动变成末尾的 `+N 项条件`，不会悄悄消失。
2. **保存时校验名称**。点「保存规则到配置」时会对照**当前图纸**的符号表检查图层名 / 块名 / 文字样式名 /
   标注样式名 / 线型名，写错的字段给**黄色**提示并给出最接近的候选（例如
   `图层「TK-FRAM」在当前图纸（A-101.dwg）中不存在，最接近「TK-FRAME」`）。
   黄色**不阻断保存**：批处理的目标是目录里的一批图纸，当前打开的这张不是判据。
   `CreateIfMissing` 生效时的 `TargetLayer`、以及 `TargetTextStyle` / `TargetDimensionStyle` 不参与校验，
   因为 handler 会自动创建它们。

两个关于保存的实现细节：

1. **只替换 `Rules` 数组那一段文本**。`ConfigSaver.SaveRules` 先用带行号的 JSON 解析定位 `"Rules": [` 的起止（括号配对扫描会跳过字符串和注释），
   再用序列化后的数组整段替换，其余内容——文件头的说明注释、`ExecutionMode` 旁的注释、字段顺序、自定义字段——**全部原样保留**。
   写盘前自动备份成 `config.json.bak`。只有括号配对失败时才退化为整体重写，并在状态栏明确标注"（排版特殊，已整体重写）"。
2. **JSON 改错不会丢数据**。切换选中、关闭页签前会先把编辑区内容解析回内存中的规则对象；
   解析失败只在状态栏提示"JSON 格式错误"，磁盘上的文件不动，已加载的规则也保持原样。

#### 3.2.1 规则 JSON 为什么这么短（精简序列化）

`RuleConfig` 里绝大多数字段是可空的（`int?` / `bool?` / `string` / `List<string>`），一条规则实际只用到其中 3~6 个。
早期直接序列化会把**所有没用到的字段**都写成 `null`，一条规则动辄 80~120 行，真正要改的字段被埋在噪声里。
现在统一走 `Core/Jun.AutoCAD.RuleJson.cs`，规则 JSON 只输出"有值"的部分：

| 规则 | 精简前 | 精简后 |
|---|---|---|
| 一条 `BlockReplace` | 约 100 行（几十个 `"xxx": null`） | 约 18 行 |

去掉的三类内容，都等价于"没配置"：

1. **值为 `null` 的条目**——`NullValueHandling.Ignore`，整条不输出；
2. **空数组 / 空集合**——`"EntityTypes": []` 与不写完全等价，一并省略；
3. **空壳对象**——`"Filter": {}` / `"Action": {}` 这类所有成员都没配置的对象整体省略。

保留的内容：

- `"Name"` / `"Type"` / `"Enabled"` 始终输出（`Enabled` 是开关，必须可见）；
- `0` / `false` 这类**真实取值**照常输出（不当成"没配置"丢掉，否则 `"Enabled": false` 会消失、规则被误启用）；
- `Filter` / `Action` 里只要有一项有值，整个对象照常展开。

语义等价性由"序列化 → 反序列化"验证：被省略的字段反序列化后仍是 `null`，
`Filter` / `Action` 由属性初始化器补回空实例，规则行为不变（见第 8 节验证表）。

同一套设置也用于「保存规则到配置」，所以写回的 `config.json` 与界面上看到的内容完全一致。

#### 3.2.2 规则编辑抽屉（表单）

双击规则列表里的任意一行，打开模态抽屉（约 900×600，按屏幕可用区域夹取）。抽屉按**规则类型**生成表单，
只显示这个类型用得上的字段：

- **字段来源**：`RuleFilter` / `RuleAction` 属性上的 `[RuleField]` 标注（标签、控件、单位、候选值、帮助）。
  新增字段只需加一个属性 + 一行标注，面板自动出现；
- **控件按类型自动选**：文本 / 多行文本 / 整数 / 小数 / 三态开关（不设置·是·否）/ 单选下拉 /
  多选列表 / 逗号分隔的多值输入；
- **分组固定三块**：「过滤条件 —— 选中什么」「操作 —— 改什么」「高级 —— 扫描范围与遍历行为」，空组不显示；
- **联动是禁用不是隐藏**：打开「强制随层」后，线型/线宽输入框会变灰并说明原因；指定了目标渐变后，
  图案相关字段全部变灰。隐藏会留下"看不见但仍在生效"的字段，所以一律禁用 + 说明；
- **切类型会清残留**：把规则从 A 类型改成 B 类型时，A 独有且已有值的字段会被列出来，确认后清空；
- **校验分两级**：红色阻断保存（必填缺失、数值越界、类型前置条件不满足），黄色只提醒（名称在当前图纸里找不到）。
  提示直接标在对应字段下方；
- **编辑的是副本**：抽屉里改的是深拷贝，点「保存」才写回内存并落盘；点「取消」原规则一点不动——
  所以红级校验阻断时不会留下被改脏的配置；
- **JSON 只读**：抽屉底部「显示 JSON」可展开当前规则的精简 JSON，用于核对或复制，不可编辑。
  要手改整份配置，用「打开配置文件」按钮。
- **从图上拾取**：块名 / 图层名这类字段右侧有「拾取」按钮。点它抽屉会**关掉**（编辑到一半的内容不丢），
  在图上点一个对象，抽屉随即带着原内容重新打开，并自动填入它的块名或所在图层名（多值字段是追加，不覆盖）。
  按 Esc 取消拾取，抽屉同样会回来，只提示"已取消拾取"。抽屉关着的那段时间，主窗状态栏会显示
  「请在图中点选块参照（按 Esc 取消）」，避免用户不知道下一步该干什么。
  为什么必须关掉而不是隐藏：模态窗口 `Hide()` 会让 `ShowDialog` 立即返回，调用方随即 Dispose 抽屉，
  窗口会凭空消失、拾取结果也无处回填（2026-10-29 实测确认）。所以改成"关掉 → 主窗口排队 `BATCHPICK`
  → 命令取名字 → 用同一份副本重开抽屉"；命令若 10 秒内没跑起来（没有活动文档等），看门狗会取消并带提示重开抽屉。

抽屉保存后立即写回 `config.json`（与「保存规则到配置」同一条写盘路径，同样保留数组外的注释、
数组内含注释时备份 `config.json.rules.bak`），并刷新名称提醒徽标。


> 注：`SavePatched` 只改 `ExecutionMode`/`ContinueOnError`/`Verbose` 和每条规则的 `Enabled`。
> 「执行」页签原来的「保存设置」按钮已移除，执行设置（执行模式/出错继续/详细输出）目前**只对当前会话生效**，不写回 config.json；
> 规则编辑页签改的是整条规则（含增删和排序），走的是另一条 `SaveRules` 路径（只替换 `Rules` 数组，不动顶层标量）。

三个刻意的设计：

1. **非模式窗口 + 执行排队回命令上下文**。窗口用 `Application.ShowModelessDialog` 打开，挂到 AutoCAD 主窗口下，不阻塞命令行，可以一边看图一边点执行。
   点「执行」时并不在命令上下文里，直接开事务会让撤销记录错乱（AutoCAD 的 undo 按命令组织），
   所以走 `UiRunBridge`：把界面上的配置暂存，再用 `SendStringToExecute` 排队一个内部命令 `BATCHUIRUN`，
   命令真正执行时就有正常的文档上下文与撤销边界。窗口本身不关，可以反复调整再执行。
2. **保存设置只改"界面能编辑的那几个值"**（`ExecutionMode`/`ContinueOnError`/`Verbose` + 每条规则的 `Enabled`）。实现方式是按行号精准打补丁，**`//` 注释、字段顺序、自定义字段全部保留**；写盘前自动备份成 `config.json.bak`。只有遇到"整个对象挤在一行"这类极端排版时才会退化为整体重写，并在提示里明确告知。日志路径（`LogFile`）不再由界面维护，需要时直接编辑 config.json。

### 3.2 多轮替换：BATCHAGAIN 的用法

`BATCHAGAIN` 解决的是"同一套流程反复试跑"的问题：**不提示路径，直接复用上一次的配置文件**。

关键点：它每次都会**重新从磁盘读取** config.json，所以改配置 → 再执行，不需要重新 NETLOAD，也不需要重新指定路径：

```
BATCHPROCESS            ← 第一次：指定 D://work//config.json
（跑完，看到"匹配 12 / 成功 10"）
（编辑 config.json，把规则换成下一批要处理的块名/图层）
BATCHAGAIN              ← 第二轮：自动用同一个 config.json 的新内容再跑
（再改配置）
BATCHAGAIN              ← 第三轮……
```

执行时命令行会先回放上次的记录，再开始新的执行：

```
上次执行：2026-10-23 14:43:47 执行于「A-101.dwg」：匹配 12 / 成功 10 / 跳过 2 / 失败 0
本次复用配置：D://work//config.json
（配置文件会重新读取，改完 config.json 直接再跑即可生效）
```

几个细节：

| 情况 | 行为 |
|---|---|
| 没有历史记录 | 退化为默认配置路径，并提示"首次请先用 BATCHPROCESS" |
| 上次的配置文件被删/改名 | 走正常的"配置文件不存在"提示，列出默认路径候选 |
| 换了图形再执行 | 额外打印一行提示：上次是在哪张图上跑的、当前是哪张，避免误操作 |
| 历史记录位置 | `%APPDATA%\Jun.AutoCAD.BatchTool\last-run.txt`（纯文本，可直接删掉重置） |

> 因为是"改配置 → 再跑一遍"的循环，建议每轮只改 `Rules`，并保持 `"LogFile"` 为空（每轮自动生成带时间戳的独立日志），这样多轮结果互不覆盖。

### 3.3 NETLOAD 报「无法加载 … 程序集」怎么排查

.NET 8 版 AutoCAD（2025）的 NETLOAD 失败时**只打印一句话，不给异常细节**，按下面顺序逐条排除：

| # | 检查项 | 做法 |
|---|---|---|
| 1 | **伴随文件是否齐全** | `bin\Debug\` 下应有 `dll` + `runtimeconfig.json` + `deps.json` + `Newtonsoft.Json.dll` 四个文件；只拷 dll 出去必然失败 |
| 2 | **AutoCAD 版本是否匹配** | 命令行输入 `ACADVER`：`25.0s` = 2025（用默认构建）；`24.x` 及更低必须用 `dotnet build -p:AcadVersion=2024` 重新编译成 net48 版本，net8 的 dll 在 2024 里加载不了 |
| 3 | **受信任路径 / SECURELOAD** | 命令行执行 `(setvar "SECURELOAD" 0)` 后再 NETLOAD；长期方案：选项 → 文件 → 受信任的位置，加入 `bin\Debug` 目录 |
| 4 | **文件是否被 Windows 阻止** | 右键 dll → 属性 → 常规，若底部有「解除锁定」勾选框就勾上并确定（来自网络/压缩包的文件会被打上标记） |
| 5 | **同一会话重复加载** | 同一次 AutoCAD 会话里重新编译后再 NETLOAD 同名程序集，可能因程序集标识已存在而失败 → 重启 AutoCAD |
| 6 | **是否 64 位** | AutoCAD 只有 x64 版本，工程已固定 `PlatformTarget=x64`，若手工改成 AnyCPU/32 位会加载失败 |
| 7 | **命令能加载但执行无输出** | 先看 `%TEMP%\Jun.AutoCAD.BatchTool.diag.log` 里有没有「BATCHPROCESS 命令进入」：有→命令已进，看后续行；没有→命令没被触发（多半是命令名打错/在别的图纸上下文）。再执行 `BATCHDIAG` 看自检信息 |
| 8 | **跑了但一个对象都没改** | 命令行会打印「本次没有任何对象被修改」+ 6 条排查提示；最常见原因是配置里的块名/图层名/图案名是示例值，图纸里并不存在 |

排查完成后，`NETLOAD` 成功会在命令行看到：

```
[Jun.AutoCAD.BatchTool] 已加载。输入 BATCHPROCESS 开始配置驱动的批量处理。
[Jun.AutoCAD.BatchTool] 已注册规则：BlockLayerChange, BlockReplace, ..., HatchPatternChange
```

> **中望CAD 2020 的排查差异**：
> 1. 只能加载 `bin\Debug\ZWCAD2020\` 里的 dll（那个是用 `ZwSoft.ZwCAD.*` 编译的），
>    `bin\Debug\` 里的 AutoCAD 版在 ZWCAD 里会报"找不到 Autodesk.AutoCAD.xxx"；
> 2. 只需要 dll + `Newtonsoft.Json.dll` 两个文件，**不要**去找 `runtimeconfig.json` / `deps.json`（.NET Framework 宿主不需要）；
> 3. `BATCHDIAG` 里的"伴随文件"清单会自动只列这两项，"宿主版本"显示为 `中望CAD <版本>`。

---

## 4. 配置参考

顶层（`BatchConfig`）：

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Version` | int | 1 | 配置版本 |
| `ExecutionMode` | string | `Sequential` | `Sequential` / `Isolated` / `Parallel`(降级为 Isolated) |
| `ContinueOnError` | bool | `true` | 单条规则异常后是否继续执行后续规则 |
| `LogFile` | string | 空 | 空=默认路径；`none`=不写文件 |
| `Verbose` | bool | `true` | 命令行是否打印逐条明细（明细始终写日志文件） |
| `MaxEditorDetailLines` | int | 100 | 单条规则最多向命令行打印多少条明细 |
| `Rules` | array | — | 规则数组，按顺序执行 |

规则（`RuleConfig`）：

| 字段 | 说明 |
|---|---|
| `Name` | 规则名，用于统计与日志 |
| `Type` | 规则类型（见第 5 节） |
| `Enabled` | 是否启用，默认 `true` |
| `ContinueOnError` | 覆盖全局开关（可选） |
| `Filter` | 选中什么（见下表） |
| `Action` | 改什么（见下表） |

`Filter` 字段分两级，配置时要注意：

- **引擎级**：`EntityTypes`、`ColorIndex(es)`、`TrueColor`、`LayerName(s)`、`SourceBlockName(s)`、
  `Scope`、`MaxNestingDepth`、`SkipLockedLayers`、`IncludeNestedBlocks`——由 `DxfFilter` / `DatabaseWalker`
  统一读取，**对所有规则类型生效**；
- **handler 级**：`TextStyleName`、`DimensionStyleName`、`SourcePatternName(s)`——只有对应规则类型会读。

`Filter`（所有字段可选，按"与"叠加）：

| 字段 | 说明 |
|---|---|
| `SourceBlockName` | 源块名（BlockReplace 必填）。动态块按**有效块名**匹配 |
| `SourceBlockNames` | 多个源块名（与 `SourceBlockName` 取并集，内部翻译成 `(2 . "<OR") … "OR>"`）。BlockLayerChange 用它一次归并好几种块 |
| `Scope` | `ModelAndLayouts`(默认) / `ModelOnly` / `LayoutsOnly` / `Selection` |
| `EntityTypes` | DXF 实体类型名数组：`LINE`、`LWPOLYLINE`、`CIRCLE`、`TEXT`、`MTEXT`、`ATTRIB`、`ATTDEF`、`DIMENSION`、`ACAD_TABLE`、`LEADER`、`MULTILEADER`、`TOLERANCE`… |
| `ColorIndex` | 单个颜色索引（1=红 2=黄 3=绿 4=青 5=蓝 6=洋红 7=白/黑 256=随层 0=随块） |
| `ColorIndexes` | 多个颜色索引（与 `ColorIndex` 取并集，内部翻译成 `(-4 . "<OR") … (-4 . "OR>")`） |
| `TrueColor` | 真彩色 `#RRGGBB`。**注意**：DXF 组码 62 只存 ACI 索引，真彩色无法用 SelectionFilter 过滤，插件在遍历后比较 `Entity.Color.ColorValue` |
| `LayerName` / `LayerNames` | 按实体自身图层过滤（不做随层解析） |
| `TextStyleName` | 按当前文字样式名过滤（标注取其标注样式的文字样式） |
| `DimensionStyleName` | 按当前标注样式名过滤 |
| `SourcePatternName` / `SourcePatternNames` | 按**源填充图案名**过滤（如 `AR-CONC`）；渐变填充按渐变名匹配 |
| `IncludeNestedBlocks` | 是否递归块定义内部，默认 `true`。**2026-10-29 起真正生效**：此前该字段被声明、文档化、示例配置也在用，但没有任何代码读它，设成 `false` 没有效果 |
| `MaxNestingDepth` | 最大递归深度，默认 8 |
| `SkipLockedLayers` | 是否跳过锁定图层上的实体，默认 `true` |

`Action`（按规则类型只使用相关字段）：

| 分组 | 字段 |
|---|---|
| 块替换 | `TargetBlockName`、`AllowDynamicBlock`、`MigrateAttributes` |
| 块归层 | `TargetLayer`、`CreateIfMissing`、`LayerColorIndex`、`LayerLinetype`、`LayerLineWeight`、`LayerPlottable`、`AlsoMoveBlockContent`、`AlsoMoveAttributes` |
| 图层 | `TargetLayer`、`CreateIfMissing`、`LayerColorIndex`、`LayerLinetype`、`LayerLineWeight`、`LayerPlottable` |
| 文字 | `TargetTextStyle`、`FontFileName`、`BigFont`、`WidthFactor`、`ObliqueAngle`(度)、`TextStyleHeight`、`HandleMTextInlineFont`、`AlsoUpdateDimensionStyle` |
| 线型/线宽 | `LinetypeName`、`LineWeight`(0.01mm)、`ForceByLayer` |
| 标注样式 | `TargetDimensionStyle`、`ArrowSize`、`TextHeight` |
| 文字高度 | `Height`、`LineSpacingFactor` |
| 填充图案 | `TargetPatternName`、`TargetGradientName`、`PatternType`、`PatternScale`、`PatternAngle`(度)、`PatternSpace`、`PatternDouble`、`HatchStyle`、`GradientType`、`GradientAngle`(度)、`GradientShift`、`GradientOneColorMode` |
| 随层 | `ForceColorByLayer`、`ForceLinetypeByLayer`、`ForceLineWeightByLayer`、`ForcePlotStyleByLayer` |
| 清理 | `PurgeCategories`、`PurgePasses` |

> JSON 允许写 `//` 注释（Newtonsoft 的宽松解析），字段名大小写不敏感；未知字段会在加载时给出"配置警告"，用于发现拼写错误。

---

## 5. 规则清单

| Type | 功能 | 关键行为 |
|---|---|---|
| `BlockReplace` | 批量替换块参照 | 保留插入点/旋转/缩放/图层/颜色；属性按 Tag 迁移；递归块定义内部；动态块受 `AllowDynamicBlock` 控制 |
| `BlockLayerChange` | **按块名批量归层** | 指定一个或多个块名 → 块参照与**块内对象**一起挪到目标图层（`AlsoMoveBlockContent`，默认开）；动态块按有效块名匹配；可选连同属性一起归层；目标图层可自动创建 |
| `EntityLayerChange` | 按类型+颜色+图层改图层 | 目标图层不存在时按配置自动创建（颜色/线型/线宽） |
| `TextFontChange` | 统一文字字体 | 修改 `TextStyleTableRecord`（`FileName`/`BigFontFileName`/`XScale`/`ObliquingAngle`/`TextSize`）后重映射实体；标注改标注样式；MText `\F` 内联字体可解析 |
| `PurgeUnused` | 清理未使用对象 | 块/图层/线型/文字样式/标注样式/注册应用/组/多重引线样式，多轮迭代 |
| `EntityLinetypeChange` | 改线型 | 线型缺失时尝试从 `acad.lin` / `acadiso.lin` 加载 |
| `EntityLineweightChange` | 改线宽 | 校验合法线宽值（-3~211），支持强制随层 |
| `DimensionStyleChange` | 改标注样式 | 目标样式缺失时以 `Standard` 克隆创建，可同步 `Dimasz` / `Dimtxt` |
| `SetByLayer` | 特性强制随层 | 颜色/线型/线宽/打印样式，逐项容错 |
| `TextHeightChange` | 统一字高 | 覆盖 TEXT/MTEXT/ATTDEF/ATTRIB/表格单元格/MLEADER；标注改 `Dimtxt` |
| `HatchPatternChange` | 批量替换填充图案 | 换图案保留填充边界与关联性；可同时统一比例/角度/间距/双线/填充样式；可转渐变填充 |

### 5.1 嵌套块遍历

`BlockReference` 本身也可能出现在**块定义内部**，只扫顶层空间会漏掉"图框块里嵌的标题块"。`Core/Jun.AutoCAD.DatabaseWalker.cs` 用 BFS 递归：

- 种子：模型空间 + 所有 `IsLayout` 的 BlockTableRecord（按 `Scope` 取舍）；
- 每处理一个 BTR，先**快照**子对象 `ObjectId` 列表再遍历——规则可能在遍历中新增/删除实体，直接 `foreach` 已打开的表记录会抛 `eWasOpenForRead`；
- 遇到 `BlockReference` 就把它的块定义入队，同一块定义只进入一次（`visited` 去重，避免重复处理与死循环）；
- **不进入**：外部参照（`IsFromExternalReference`/`IsFromOverlayReference`）与匿名块（`IsAnonymous`，典型是动态块的 `*U` 实例块）。

在块定义内部完成的替换/修改，会同时作用于所有引用该定义的插入——这正是批量治理想要的效果。

### 5.2 属性迁移

`new BlockReference(pos, targetBtrId)` 创建的新参照**不会自动带上属性**，必须从目标块定义的 `AttributeDefinition` 逐个生成 `AttributeReference`：

```
遍历目标块定义 → 跳过 Constant 属性 → new AttributeReference()
  → SetAttributeFromBlock(attDef, newRef.BlockTransform)   // 位置/字高/对齐方式
  → 若源块存在同名 Tag：TextString = 源值（迁移成功计数）
  → 否则保留目标默认值，并写日志（Tag 不匹配不迁移）
  → newRef.AttributeCollection.AppendAttribute + AddNewlyCreatedDBObject
```

属性迁移必须在 `AppendEntity` 之后执行，因为 `BlockTransform` 依赖已插入的位置。

### 5.3 块批量归层（BlockLayerChange）

**要解决的问题**：图框块、标题块、设备块常常被插入到五花八门的图层（`0`、当前层、别人图纸的残留层），
出图时颜色/线宽/打印样式就跟着乱。`EntityLayerChange` 按"类型+颜色+图层"筛选，表达不了"只要是这个块就归层"，
所以单独加了按**块名**筛选的规则。

```json
{
  "Name": "图框块统一归层",
  "Type": "BlockLayerChange",
  "Enabled": true,
  "Filter": {
    "SourceBlockNames": [ "A1_TITLE", "A2_TITLE" ],
    "LayerName": "0"
  },
  "Action": {
    "TargetLayer": "TK-FRAME",
    "CreateIfMissing": true,
    "LayerColorIndex": 7,
    "LayerLinetype": "Continuous",
    "LayerLineWeight": 25,
    // 新图层是否参与打印（默认 true；辅助线/定位点图层可以设 false）
    "LayerPlottable": true,
    // 块定义内部的对象（含嵌套块、属性定义 ATTDEF）是否一起归层，默认 true
    "AlsoMoveBlockContent": true
    // 属性（ATTRIB）不写时跟随上面这一项；想让已有属性留在原图层，补 "AlsoMoveAttributes": false
  }
}
```

行为约定：

| 场景 | 处理 |
|---|---|
| 一个块 / 多个块 | `SourceBlockName` 写一个，`SourceBlockNames` 写一组，两者可叠加（内部生成 `(2 . "<OR") (2 . "A1") (2 . "B") "OR>"`） |
| 一个块名都没写 | **主动中止**并提示，避免"空过滤 = 全图块都归层"的误操作 |
| 动态块 | 按**有效块名**匹配（`BlockNameUtil.GetEffectiveName`），不受匿名块名 `*U##` 影响 |
| 块里的内容 | `AlsoMoveBlockContent`（默认 `true`）把**块定义内部的对象**也改到目标图层（见下方"为什么必须改块内对象"） |
| 已在目标图层 | 跳过，不计入"成功"，也不产生无谓的修改记录；但**块内对象/属性仍会被检查**，只有它们也都在目标图层时才算"跳过" |
| 源图层被锁定 | 按 `SkipLockedLayers`（默认 `true`）跳过并告警（块参照、属性、块内对象同一口径） |
| 目标图层不存在 | `CreateIfMissing=true`（默认）时按 `LayerColorIndex` / `LayerLinetype` / `LayerLineWeight` / `LayerPlottable` 自动创建；为 `false` 时规则中止 |
| 新图层是否打印 | `LayerPlottable`（默认 `true`，与 AutoCAD 新建图层一致）；设 `false` 时新图层上的对象不打印。**只作用于自动创建的新图层**——图层已存在时保持原有打印设置 |
| 只改某一层上的这些块 | 叠加 `LayerName` / `LayerNames`（匹配块参照**自身**图层） |
| 块参照带属性 | `AlsoMoveAttributes` **不写时跟随 `AlsoMoveBlockContent`**（即一起归层）——`ATTDEF` 都改了、已有 `ATTRIB` 不改等于只修一半；显式写 `false` 才让属性留在原图层 |
| 关掉块定义遍历 | `Filter.IncludeNestedBlocks=false` 时**不进入块定义**，`AlsoMoveBlockContent` 自动失效（规则会打一条警告），只挪块参照本身；属性（ATTRIB）是块参照的兄弟实体、不在块定义里，仍按 `AlsoMoveAttributes` 归层 |
| `Scope=Selection` | 选中的块参照会改到它的**块定义**，该块在其它位置的所有引用都会一起归层；规则会打警告提醒 |
| 块定义内部的嵌套插入 | 由 `DatabaseWalker` 递归进入块定义，块中块里的引用同样归层（改动会影响所有引用该定义的插入） |
| 扫描范围 | 沿用 `Scope`（`ModelAndLayouts` 默认 / `ModelOnly` / `LayoutsOnly` / `Selection`） |

**为什么必须改块内对象**（2026-10-30 修复的功能缺口）：

块内实体自带图层，**不随块参照的图层走**（只有画在 `0` 层上的块内对象才跟随参照图层显示）。
旧版只把 `BlockReference.Layer` 改成目标图层，结果命令行显示"成功 N 个"，图上却看不出变化——
块里的线、文字、填充仍然在原来的图层上。`AlsoMoveBlockContent`（默认开）会逐个改块定义里的实体：

- 处理对象：块定义里的所有 `Entity`（线、文字、填充、`ATTDEF` 属性定义……）以及**嵌套块参照**背后的定义；
- 每个块定义在一次规则执行里只处理一次（`HashSet<ObjectId>` 去重），多个 INSERT 引用同一定义不会重复改、统计也不会翻倍；
- 动态块同时改**匿名实例块**（`*U##`，实际显示的几何在里面）与**原始动态块定义**（`DynamicBlockTableRecord`），
  否则参数一变或重生成后，宿主会用原定义把匿名块重建回去；
- **不进入**：外部参照块（`IsFromExternalReference` / `IsFromOverlayReference` / `IsDependent`）、
  匿名派生块（`*D` 标注块、`*T` 表格块这类由宿主按需重建的块，改了没意义还可能干扰重建）；
- 锁定图层上的块内对象按 `SkipLockedLayers` 跳过，按块定义汇总告警（逐个实体告警会刷满日志）；
- 递归深度沿用 `Filter.MaxNestingDepth`（默认 8）。

> 注意：改的是**块定义**，所以引用同一定义的**所有**插入会一起变。这是批量治理想要的效果；
> 需要"只动块参照本身"时把 `AlsoMoveBlockContent` 设为 `false`。

统计口径与其他规则一致：命中数 = 通过块名判定的 INSERT 数，成功数 = 实际改了图层（或块内对象/属性）的 INSERT 数，
已在目标图层/锁定图层计为"跳过"，单个对象异常计为"失败"并带上句柄。
块内对象的修改数量写在明细里（`块内对象 12 个（「A1_TITLE」）`），不单独计入"匹配/成功"，
保证 `匹配 = 成功 + 跳过 + 失败` 的口径不被打乱；但若某个 INSERT 别的都没改成、只有块内对象改失败，
它会记成"失败"而不是"跳过"，免得汇总行显示一切正常。

### 5.4 动态块边界（明确说明）

- **默认不支持**动态块替换：`"AllowDynamicBlock": false` 时，遇到 `BlockReference.IsDynamicBlock == true` 直接跳过，命令行与日志记录警告（含有效块名）。
  原因：动态块实例在块表里是匿名块 `*U##`，跨定义替换会丢失参数约束与可见性状态，属于"看起来成功、实际坏图"的高风险操作。
- **显式开启后支持**：`"AllowDynamicBlock": true` 时，按参数名逐个迁移：
  `br.DynamicBlockReferencePropertyCollection` → 收集 `PropertyName`/`Value` → 在目标参照上按同名参数赋值；只读参数（`ReadOnly == true`）跳过并记日志；值沿用源参数原生类型，避免类型转换破坏约束。
- 由于遍历阶段不进入匿名块，**动态块内部的嵌套块不会被扫描**——这是刻意的边界，避免误改某个动态块实例的私有几何。

### 5.5 过滤器：DXF 组码

`Core/Jun.AutoCAD.DxfFilter.cs` 把 `Filter` 翻译成一份 `TypedValue[]`，两条通道共用这份数据，避免语义漂移：

| 组码 | 含义 | 用法 |
|---|---|---|
| `0` `DxfCode.Start` | 实体类型 | 多类型用逗号连接：`"LINE,LWPOLYLINE,CIRCLE"` |
| `62` `DxfCode.Color` | 颜色索引 | 多颜色用 `(-4 . "<OR") (62 . 1) (62 . 3) (-4 . "OR>")` |
| `8` `DxfCode.LayerName` | 图层名 | 多图层同样用 OR 组 |
| `2` `DxfCode.BlockName` | 块名 | 仅 `INSERT` 有效 |
| `-4` `DxfCode.Operator` | 逻辑运算 | `<OR` / `OR>` |

1. `Scope = Selection` → `Editor.SelectImplied()` / `Editor.SelectAll(SelectionFilter)`，过滤完全交给 AutoCAD 内核；
2. 其它 Scope → `DatabaseWalker` 遍历 + `DxfFilter.Matches` 对**同一份组码**做托管解释。

两个已知差异（已在代码注释中标注）：

- 随层实体在 DXF 里不写 `62` 组码，`SelectionFilter` 用 `62 = 256` 可能命中不到；托管判定直接用 `Entity.Color.ColorIndex`（随层返回 256），更可靠。
- 动态块实例的块名是匿名名 `*U##`，`62`/`2` 组码过滤会漏；托管判定用 `BlockNameUtil.GetEffectiveName`（动态块取 `DynamicBlockTableRecord` 的名字）。

### 5.6 文字样式重映射

不是"只改实体上的 `TextStyleId`"，而是**直接改样式记录本身**：`FileName`（SHX/TTF）、`BigFontFileName`、`XScale`（宽度因子）、`ObliquingAngle`（弧度，配置里写度）、`TextSize`（固定字高）；TTF 字体额外写 `FontDescriptor`。样式不存在时先创建再写入，最后把命中实体的 `TextStyleId` 指向它。

各类型的挂载点不同：

| 实体 | 文字样式挂载点 |
|---|---|
| TEXT / MTEXT / ATTDEF / ATTRIB | 实体 `TextStyleId` |
| DIMENSION | **标注样式** `DimStyleTableRecord.Dimtxsty`（标注实体上的样式是覆盖值，改样式表才影响全部同类标注） |
| ACAD_TABLE | 每个 `Cell.TextStyleId`（`Nullable<ObjectId>`） |
| LEADER | 其注释对象（MText / 形位公差） |
| MULTILEADER / TOLERANCE | 反射兜底（跨版本属性差异） |

MText 内联字体：`HandleMTextInlineFont = true` 时用正则 `\\(?<cmd>[Ff])(?<name>[^;|\\{}]*)(?<opts>\|[^;\\{}]*)?;` 解析并替换字体名，保留 `|b0|i0|c134` 后缀：

```
输入  {\Fsimplex.shx|b0|c134;多行文字}
输出  {\Fgbenor.shx|b0|c134;多行文字}
```

### 5.7 填充图案替换（HatchPatternChange）

`Hatch.PatternName` / `Hatch.PatternType` 是**只读**属性，换图案只能调用：

```csharp
hatch.UpgradeOpen();
hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");   // 重建图案定义，保留填充边界 Loops
hatch.PatternScale = 1.0;                                       // 比例
hatch.PatternAngle = 45 * Math.PI / 180.0;                      // 角度（API 用弧度，配置用度）
hatch.PatternSpace = 3.0;                                       // 仅 UserDefined：线间距
hatch.PatternDouble = false;                                    // 仅 UserDefined：单/双线
hatch.HatchStyle = HatchStyle.Normal;                           // Normal / Outer / Ignore
```

行为约定：

| 场景 | 处理 |
|---|---|
| 关联填充（`Associative = true`） | 只换图案，不动边界，关联关系保持 |
| 已经是目标图案且比例/角度都一致 | 跳过（`IsAlreadyTarget` 判定，避免无谓地重建图案定义） |
| 目标图案名不在 `acad.pat` / `acadiso.pat` 中 | `SetHatchPattern` 抛 `eInvalidInput`，**第一次失败就中止整条规则**并提示图案名，避免几百个填充刷错误日志 |
| `TargetPatternName = "SOLID"` | 转成纯色填充（`Hatch.IsSolidFill == true`） |
| `TargetGradientName` 有值 | 走 `SetGradient` 转渐变填充，优先于 `TargetPatternName`；可再设 `GradientAngle` / `GradientShift`(0~1) / `GradientOneColorMode` |
| 参数对当前图案不适用（如给预定义图案设 `PatternDouble`） | 单项 `try/catch`，只告警不放弃整次替换 |
| 比例/间距 ≤ 0 | 忽略该项并告警（配置加载阶段也会给出警告） |

`SourcePatternName` 用于"只改某一种填充"，是最常用的安全阀：

```json
{
  "Name": "混凝土填充统一为 ANSI31",
  "Type": "HatchPatternChange",
  "Filter": { "EntityTypes": [ "HATCH" ], "SourcePatternName": "AR-CONC" },
  "Action": { "TargetPatternName": "ANSI31", "PatternScale": 1.0, "PatternAngle": 45 }
}
```

不写 `SourcePatternName` 时，规则会作用于所有命中过滤条件的 HATCH——第一次使用建议先带上源图案名，确认效果后再放开。

### 5.8 事务边界

| 位置 | 约定 |
|---|---|
| `BatchRunner` | Sequential：整个批处理一个 `StartTransaction`，结束统一 `Commit`；Isolated：每条规则一个事务，成功 `Commit`，异常时不提交、`Dispose` 自动回滚 |
| `IRuleHandler` | **不得**自行 `Commit`/`Dispose` 环境事务，所有修改都在 `context.Transaction` 内完成 |
| 新增对象 | 必须 `AppendEntity`/`Add` 后调用 `tr.AddNewlyCreatedDBObject(obj, true)` |
| 已读对象改属性 | 先 `UpgradeOpen()`（或直接 `OpenMode.ForWrite` 打开） |
| `PurgeUnused` | 候选收集放在**独立短事务**里完成并立即提交，保证交给 `Database.Purge` 的对象处于关闭状态；随后在环境事务内 `Erase` |
| 文档锁 | 命令层 `doc.LockDocument()` 包住整个执行过程，避免用户切换布局导致 `eLockViolation` |

`ExecutionMode` 的真实差别：`Sequential` 里某条规则中途失败无法只回滚它自己（修改与其他规则混在同一事务）；需要"单条规则失败即回滚该条"时用 `Isolated`。`Parallel` 不受支持（AutoCAD 图形数据库不支持多线程并发写入），会被降级为 `Isolated` 并给出警告。

### 5.9 错误处理与统计

- 单条规则异常不会中断整体（`ContinueOnError` 控制），每条规则独立统计：
  `规则名 | 匹配 N | 成功 N | 跳过 N | 失败 N | 耗时 ms | 状态`
- 失败明细带**句柄**（`ObjectUtil.Describe` → `LINE(句柄:2A3F) 图层:0`），可直接在图纸里 `(handent "2A3F")` 定位；
- 命令行与 `.log` 双通道输出，明细过多时命令行只打印前 `MaxEditorDetailLines` 条。

---

## 6. 扩展新规则（不改主流程）

```csharp
public sealed class MyRule : Jun.AutoCAD.Core.IRuleHandler
{
    public string RuleType => "MyRule";

    public void Execute(Jun.AutoCAD.Core.RuleExecutionContext ctx, Jun.AutoCAD.Core.RuleExecutionResult result)
    {
        var filter = Jun.AutoCAD.Core.DxfFilter.Build(ctx.Filter, null, ctx.Transaction);
        foreach (var hit in Jun.AutoCAD.Core.EntityScanner.Scan(ctx, filter))
        {
            var ent = (Autodesk.AutoCAD.DatabaseServices.Entity)ctx.Transaction.GetObject(hit.Id, OpenMode.ForRead);
            result.Hit();
            // ... 改 ent（记得 UpgradeOpen）...
            result.MarkChanged("...");
        }
    }
}

// 在任意 Initialize 中注册（RuleFactory.RegisterBuiltIns 是幂等的）
Jun.AutoCAD.Core.RuleFactory.Register("MyRule", () => new MyRule());
```

配置里写 `"Type": "MyRule"` 即可生效——`BatchRunner`、日志、事务、统计全部复用。

---

## 7. 已知限制

1. **不支持并发写**：`ExecutionMode: Parallel` 会被降级；多图纸批量请用 `AcCoreConsole` 脚本逐张调用 `BATCHPROCESS`。
2. **不进入外部参照**：xref 内容需先 `XREF` 绑定（`XBIND`）后再处理。
3. **扫描阶段不进入动态块匿名实例块**（见 5.4），因此动态块内部的嵌套块不会被扫描到；
   `BlockLayerChange` 的块内对象归层是例外：它从命中的参照出发直接进定义，会同时处理匿名实例块与原始动态块定义（见 5.3）。
4. **块替换要求目标块已存在**于当前图纸块表；插件不会跨图纸导入块定义（可用 `INSERT` + `PURGE` 或设计中心先引入）。
5. **真彩色过滤**只能走遍历判定，`Scope = Selection` 时不生效。
6. 形位公差（TOLERANCE）的**字高**无法直接修改（该实体没有 `TextHeight` 属性），规则会记"跳过"；文字样式仍可改。
7. `PurgeUnused` 的多轮迭代依赖已提交/已擦除状态，极端链式依赖下可能需要再执行一次命令。
8. **填充图案必须已定义**：`TargetPatternName` 只能引用 `acad.pat` / `acadiso.pat` 里已有的图案；插件不解析 .pat 文件，也不会自动加载自定义图案库（可先用 `HATCH` 命令手工选一次目标图案，使其进入当前图形）。
9. **填充比例是绝对值**：`PatternScale` 不做「相对原比例缩放」，需要按图纸单位换算（例如原来 0.5、想放大一倍就写 1.0）。
10. 关联填充换图案后若边界被外部编辑过，AutoCAD 可能提示「填充边界无效」——这属于图纸本身问题，规则只负责换图案。
11. **中望CAD 2020 版只保证"编译期对齐"**：所有 `ZwSoft.ZwCAD.*` API 都是按 ZWCAD 2020 安装目录里的
    `ZwDatabaseMgd.dll` / `ZwManaged.dll` 实际签名编译通过的，但本机没有可自动化的 ZWCAD 无界面入口，
    **未做运行时实跑**。首次在 ZWCAD 里 NETLOAD 后建议先跑 `BATCHDIAG` 自检，再用一张备份图纸试跑。
12. **ZWCAD 2020 的 API 基线偏 AutoCAD 2015 左右**：`Cell.TextStyleId` 之类的高版本属性在 ZWCAD 里可能不存在，
    相关逻辑已有反射兜底（失败只记"跳过"）；填充渐变的 `GradientShift` 在 ZWCAD 里是 `float`，代码统一做 `(float)` 转换。
13. **两个宿主的构建产物不能混用**，见 2.1。

---

## 8. 验证情况

| 项目 | 方式 | 结果 |
|---|---|---|
| 编译（AutoCAD 2025） | `dotnet build` 针对 AutoCAD 2025（25.0.58.0）托管程序集 | 通过，0 错误 0 警告 |
| 编译（中望CAD 2020） | `dotnet build -p:AcadVersion=ZWCAD2020` 针对 `E:\ZWCAD 2020` 的 `ZwDatabaseMgd.dll` + `ZwManaged.dll` | 通过，0 错误 0 警告；产物 `bin\Debug\ZWCAD2020\` 仅引用 `ZwManaged` / `ZwDatabaseMgd`，无任何 `Autodesk.*` 引用 |
| 单元测试 | `dotnet test tests/Jun.AutoCAD.BatchTool.Tests`（xunit，88 个用例） | 通过 88 / 失败 0。覆盖 `RuleSummary`（含 `LayerNames` 不再渲染成"全部"、`LayerPlottable=false` 才写"新图层不打印"、块归层的"含块内对象"标记）、`RuleValidator`（含原本零校验的 3 种类型）、`NameValidator`（三态与豁免）、`NameMatcher`、`RuleJson`（`Extra` 往返）、`FieldCatalog`（按类型裁剪、残留字段、联动禁用、schema 自检）、`PickBridge`（请求号、旧请求取消不误伤新请求、命令已开始与未开始的区分、回调只触发一次） |
| 配置回归 | 用 `ConfigLoader.Load` 读仓库根 `config.json` | 12 条规则、0 警告，摘要逐条核对通过 |
| API 正确性 | 反射核对 `TextStyleTableRecord.ObliquingAngle`、`DimStyleTableRecord.Dimtxsty/Dimtxt`、`BlockReference.DynamicBlockReferencePropertyCollection`、`Cell.TextStyleId(Nullable<ObjectId>)`、`Hatch.PatternName/PatternType`（只读）+ `SetHatchPattern` 等成员签名 | 全部对齐 |
| 配置解析 | 用独立控制台程序调用 `ConfigLoader.Load("config.json")` | 12 条规则全部解析，0 警告，11 个规则类型注册成功 |
| 规则 JSON 精简 | 独立宿主里 `RuleJson.Serialize(rule)` + 回读比对 | `BlockReplace` 由 100 行降到 18 行；`null` / 空集合 / 空 `Filter` 全部不输出；`Enabled=false`、`0`、`""` 保留；序列化→反序列化后字段值逐项一致 |
| 块归层规则 | `config.json` 里新增的 `BlockLayerChange` 示例 | 摘要渲染为「块 A1_TITLE/A2_TITLE [层0] → 图层 TK-FRAME（含块内对象、属性）」，配置 0 警告 |
| 块内对象一并归层 | 代码审查 + `RuleSummary` / `FieldCatalog` 单测（本机 `accoreconsole` 无法执行脚本，未做实机跑图） | 摘要默认写「含块内对象、属性」、显式关掉属性写「含块内对象」、两个都关则都不写；抽屉里出现「块内对象一并归层」且只对 `BlockLayerChange` 可见；`config.json` 示例回读 0 警告 |
| 新建图层打印开关 | 离屏 WinForms 宿主渲染 `BlockLayerChange` 抽屉 + `ConfigLoader.Load` 回读 | 抽屉出现「新建图层可打印」字段；`LayerPlottable` 缺省按可打印创建，`false` 时日志写「不打印」 |
| 拾取链路（模态抽屉） | 离屏 WinForms 宿主：点「拾取」→ 检查抽屉状态与续接上下文 → 带结果重开抽屉 | 点拾取后抽屉**关闭**且 `PickRequested=true`、续接上下文带原副本（`TargetLayer` 未丢）；重开后名字填入 `Filter.SourceBlockName`，多值字段为追加；保存仍返回 OK |
| 拾取链路（命令侧） | `BATCHPICK` 源码核对 + `PickBridge` 单测 | 命令进入即 `MarkStarted`（界面停掉看门狗），Esc/异常/无文档三条路径都会回传，回传带请求号、迟到结果被忽略 |
| 逻辑单测 | `ReplaceMTextInlineFont` / `HasForeignInlineFont` / `DxfFilter.Build().Describe()` / `HatchPatternChangeRule.ParsePatternType` | `\F` 与 `\f` 各替换 1 处且保留后缀；OR 组码生成正确；图案类型字符串解析正确 |
| 界面冒烟测试 | 独立宿主里实例化 `BatchToolForm`（不显示），检查控件树与规则列表；`Dispose` 后读属性 | 构造无异常，规则渲染出「过滤 → 操作」摘要，Dispose 后读属性不抛异常 |
| 非模式行为 | `Show()` 后点「关闭」；`UiRunBridge` 的 HasPending/TryTake/Clear | 关闭后 `IsDisposed=true`；空队列 `TryTake=false`，`Clear` 正常 |
| 保存设置（API） | 在 config.json 副本上改 `ExecutionMode`/`Verbose`/`Enabled` 后 `ConfigSaver.SavePatched` | `//` 注释与字段顺序保留、`UsedFullRewrite=false`、`.bak` 生成、回读值正确（界面入口已随「保存设置」按钮移除，方法保留） |
| 保存规则（整段替换） | 改规则名后 `ConfigSaver.SaveRules` | 新名写入、旧名移除、文件头与 `ExecutionMode` 旁注释保留、JSON 合法、`.bak` 生成、执行面板同步 |
| 双页签界面 | `DrawToBitmap` 渲染 tab1/tab2/选中态；反射驱动 添加→下移→上移→删除 | tab1 只剩执行设置、配置文件卡在 tab2 顶部；切换页签后布局完好 |
| 实机运行（AutoCAD） | `accoreconsole.exe /i <dwg> /s <scr>` | **未完成**：本机 `accoreconsole` 能启动（V.58.0.0），但 `/s` 脚本不执行——一直停在「命令:」提示，连只含 `QUIT` 的脚本也不响应（疑似授权/初始化未完成）。请在 AutoCAD 图形界面下 `NETLOAD` 后用示例配置试跑 |
| 实机运行（ZWCAD） | `NETLOAD` + `BATCHDIAG` | **未完成**：需要人工在 ZWCAD 2020 图形界面里加载 `bin\Debug\ZWCAD2020\Jun.AutoCAD.BatchTool.dll` 验证 |

建议首次试跑步骤：备份图纸 → 用 `config.json` 里已启用的 4 条规则（块替换 / 图层归并 / 字体统一 / 填充替换）→ 把 `SourceBlockName` / `TargetBlockName` / `TargetLayer` / `SourcePatternName` 改成图纸里真实存在的名字 → `BATCHPROCESS` → 查看命令行统计与 `Logs\*.log`。
