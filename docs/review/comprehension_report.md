# Stage 6 · Comprehension Test

- 目标：`README.md` 全文（FeatureRange = 1..end；DetailedRange = N/A）
- 角色：egm-comprehension（Naive Reader）

## 本文档提出的内容

文档提出并描述一个名为 `Jun.Cad.Batch` 的"离线批量改图"工具：不启动 AutoCAD，用 ACadSharp 3.8.35 直接读写 `.dwg`，按 `config.json` 里顺序排列的规则批量修改图纸，可跑在服务器/CI/批处理里。它有两个入口——命令行 `jun-cad-batch.exe` 和从旧插件平移来的 WinForms 界面 `jun-cad-batch-ui.exe`——并与 `legacy/` 里保留的原 AutoCAD 插件共用同一套 `config.json` 结构。文档主体是"移植到离线模式后哪些能力降级、为什么降级、以及逐条实测证据"。

## 按本文档所述，现有系统如何工作

一次 `run` 把 DWG 读入 ACadSharp 内存模型（源版本不在可安全写出集合里时提升到 AC1032），从模型空间和布局出发递归遍历块定义，用每条规则的 `Filter` 判定对象是否命中，再由对应的 `Action` 改内存里的实体，全部规则跑完后原子写出到 `<输入目录>\output`（或 `--in-place` 覆盖并留 `.bak`），中途失败则该文件不写盘。规则顺序执行、无事务，回滚边界是"文件"而不是"事务"，因此 `ExecutionMode=Isolated`、`Scope=Selection`、事务回滚都只能降级。文字与遮罩类操作受 ACadSharp 建模限制：只能往实体列表末尾追加，要插到下面就得重建实体，而重建会重新分配句柄。每次运行写 `.jun-runs\<时间戳>\manifest.json`，`rollback` 按清单"本次新建的删掉、本次覆盖的从备份还原"。

**复述在哪一段失去依据**：第 4 节。文档把规则 `Action` 字段委托给源码，只对 20 条规则里的 9 条列了字段，全文没有一个完整 JSON 示例——`Filter` / `Action` 是不是两个嵌套对象，是靠推断补上的。

## 发现

### C1 [Blocker] 配置参考不成其为参考
- 影响：§4，L167、L194–220
- 只给了平铺字段名，没说 `Filter`/`Action` 是子对象；全文无完整 `config.json`；「Action」只列 9 条规则，其余 11 条（含文档化最用力的 `HatchPatternChange`）整体交给源码。
- 新读者据本文档写不出任何一条非列表规则的配置，也无法确认键名层级。

### C2 [Major] 被当作核心行为的字段不在字段清单里
- 影响：§6.4，L341、L351
- `Action.MatchSpacing` 是全文唯一改变输出几何的默认行为，但 §4 Action 清单里没有；`PatternScale` 只在正文出现一次。默认值、类型、所在层级都查不到。

### C3 [Major] 两个相似字段名，只解释了一个
- 影响：§6.2 L281、§7.6 L441 vs §4 L204
- 6.2/7.6 用 `DimensionMaskMode=BlockInternal`，§4 的 TextMask 清单里只有 `DimensionTextMode`。照着字段清单配置，永远打不开 7.6 描述的能力。

### C4 [Major] `rollback <运行目录>` 的"运行目录"未定义
- 影响：§3 L133–143、§7.8 L474–481
- manifest 在 `<输出目录>\.jun-runs\<时间戳>\`，那么"运行目录"是时间戳目录、`.jun-runs`、还是输出目录？`--latest <输出目录>` 又明确要输出目录，两种形式参数类型不同却无对比说明。

### C5 [Major] 字段引用记法三种并存
- `Action.MatchSpacing`（属性路径）、`MaskRebuildText`（裸名）、`DimensionMaskMode=BlockInternal`（赋值）、`MatchMode`（只给候选值）。读者分不清哪些是 JSON 键、哪些是源码属性名。

### C6 [Major] 一批只有名字、零说明的字段
- `LayoutRenameSyncTag`、`LayerMapDeleteEmpty`、`MaskLayerPlottable`、`AuditTolerancePercent`（与绝对容差并用时以谁为准）。

### C7 [Major] 前置条件缺失
- 影响：§2.1 L37–41、§10 L568
- 从未说明需要 .NET 8 SDK（唯一线索是产物路径里的 `net8.0`）；也没说 CLI 与 UI 是两个工程、一次构建是否都产出。

### C8 [Nit] "共用 `Resources\`"字面不成立
- CLI 产物在 `net8.0\`，UI 在 `net8.0-windows\`，是两个目录；照 2.2 做会出现"CLI 能用图案、UI 说找不到"。

### C9 [Nit] 2.2 的命令环境与 AutoCAD 版本写死
- 该代码块是 PowerShell，其余是 bash；路径写死 `AutoCAD 2025\UserDataCache\zh-cn\Support\`。

### C10 [Nit] 证据区数字自相矛盾
- §7.1 开头说"160 个填充"，紧接的清单是 78 + 144 = 222；§7.5 "匹配 90，成功 71，跳过 228"，71 + 228 = 299 ≠ 90。

### C11 [Nit] `MaxEditorDetailLines` 的 "Editor" 在离线版指什么没交代
- 且 `Verbose=true` 与 `--quiet` 冲突时谁优先未说明。

### C12 [Nit] `--dry-run` 与 `--preview` 的区别未说明
- 两者都"不写盘"，差别只在产物；`--preview-out` 默认值依赖 `<输出目录>`，而 `--dry-run`/`--in-place` 场景下"输出目录"未定义。

### C13 [Nit] `--version` 取值格式不完整
- 混用 `AC1032`/`2018`/`R2013` 三种记法又用"等"封闭集合。

### C14 [Nit] `patterns` 与 `preflight` 的边角语义
- `patterns` 省略参数时是否走 2.2 的查找顺序未说明；`preflight` 示例出现 `[未测]`，而 10.6 的表格只列"通过/失败/降级"三态。

### C15 [Nit] "为什么规则 0 命中"可读成"规则编号 0"
- 且 `test1.dwg`、`P3-B1.dwg`、`楼梯和平台.dwg` 都不在仓库里也无来源说明，实测结论无法复现。

### C16 [Nit] 同一项目三个名字
- 标题 `Jun.Cad.Batch`、解决方案 `Jun.Cad.Batch.sln`、仓库目录 `Jun.AutoCAD.Batch`、旧插件 `Jun.AutoCAD.BatchTool`，未说明哪个是现行名称。

## 复核意见（Elephant）

C1–C4 全部成立。C10 的 7.5 数字不是笔误：`TextMaskRule` 在 `result.Hit()` 之前就 `MarkSkipped`（块内实体、锁定图层），导致"跳过"大于"匹配"——这是实现缺陷，不只是文档缺陷。C15 的图纸不在仓库是事实，属可复现性缺口。
