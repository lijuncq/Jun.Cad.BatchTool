# Stage 5 · Initial Zoom Out Review

- 目标：`README.md`（全文）
- 角色：egm-zoom-out（Skeptical Architect）
- 标准：`resources/document_standards/zoom-out-standard.md`
- 结论：**1 Blocker / 10 Major / 3 Nit**

## Blocker

### Z1 配置互操作承诺与代码不符
- 影响：§1「和 legacy 的关系」L25–26
- 文档称"两边 `config.json` 结构完全一致，同一份配置既能给插件用也能给命令行用"。
- 事实：legacy 配置模型 77 个字段，`src` 128 个，51 个字段只存在于离线版（`LayoutPatterns`、`ContainerBlockPattern`、`Region*`、全部 `Mask*`、`MatchSpacing`、9 条新规则参数）。
- legacy 用 `JsonExtensionData`（`legacy/Config/Jun.AutoCAD.BatchConfig.cs:85`）静默吞掉未知字段；它的"至少一个过滤条件"护栏只认 `EntityTypes`/`ColorIndex`/`LayerName`（`legacy/Rules/Jun.AutoCAD.EntityLayerChangeRule.cs:49`）。
- 后果：一份 `EntityTypes=CIRCLE` + `RegionPoints` 的配置在插件里跑，区域条件被丢掉，整图的圆被改层——正是 §9 声称要防的事故。
- 修法：把配置模型抽成共用库并加 parity 测试；或把"结构完全一致"改成"离线版是超集，插件会忽略未知字段"。

## Major

### Z2 `src/` 是 legacy 的整仓 fork，不是文档说的"平移"
- 影响：§1、§8、§10
- 一一对应的文件约 38 个，UI 三个文件从 4163 行复制成 4485 行；两边没有 `ProjectReference`、没有链接编译，字段已漂移（128 vs 77）。
- 架构死胡同：每加一个字段、修一个界面 bug 都要改两处，且无法判断哪边是"对的那份"。
- 修法：抽象宿主能力接口让插件引用同一份 Core；或明确写"legacy 冻结，不再演进"并删掉"同一套窗体"这类表述。

### Z3 扁平 `RuleAction` 联合体 + 反射元数据层
- 影响：§4 / `Config/BatchConfig.cs`
- 128 个可空属性、112 个 `RuleField` 标注装 20 条规则的全部参数；为了挑出"某类型用得上"的字段又长出 `FieldCatalog`/`RuleValidator`/`RuleSummary`（623 行）等 3288 行 `Editing/` 元数据机制。
- 按类型拆成独立 Action 类型（多态 JSON）可整块删掉反射过滤与摘要分支。
- 文档侧：§4 用"见 `BatchConfig.cs`"把配置契约甩给代码，读者无法判断字段归属、默认值与互斥关系。

### Z4 同一默认值写三遍
- 影响：§4 / 全仓
- `RuleAction` 可空 + 规则里 `RuleContext.Bool(x, true)` + `RuleSummary` 再硬编码。规则侧 42 处、`Editing/` 侧 34 处字面量；`RuleSummary.cs:260` 注释自认"默认值也要跟规则里一致，否则摘要会说反话"。

### Z5 预览有两条数据来源
- 影响：§3 `--dry-run`/`--preview`、§10.6
- `BatchRunner` 每条规则前后各抓全文档快照做 diff（`BatchRunner.cs:118`、`:202`）+ 规则自报（30 处 `RecordChange`）+ `HasRecordFor` 去重。
- 20 条规则 = 40 次全图遍历；两条来源对"重建实体换句柄"的语义不一致。规则自报本就是统计与 `--skip-unchanged` 的必需品，快照路径可删。

### Z6 回滚体系为不存在的风险而建
- 影响：§3 `rollback`、§8 `Runs/`、§10.6
- 文档自认：默认写 `output/` 时源图没动过，回滚就是清目录；`--in-place` 本来就有 `.bak`。
- 为一个"删目录"的动作建了 manifest + `backups/` + 时间戳目录 + 子命令 + 界面按钮 + 259 行实现。

### Z7 能力探测进了产品运行路径
- 影响：§3 `preflight`、`TextMaskRule.cs:63`
- `CapabilityProbe` 静态缓存 + 遮罩规则每次调用，探测失败自动从 Wipeout 降级 Solid；而文档自认"探测通过不代表 AutoCAD 认可"。
- 后果：同一份配置在不同机器上产出不同实体类型——批处理工具最不该有的不确定性。

### Z8 界面自检入口编译进发行版
- 影响：§10.8
- 4 个隐藏开关（`--screenshot*`、`--run`）连同 `DrawToBitmap` 一起进 `jun-cad-batch-ui.exe`（`Program.cs:30-190`）；解决方案没有测试项目，于是测试工具被塞进产品二进制。

### Z9 20 条规则里至少 4 条与宿主能力重复
- 影响：§5、§6.2
- `PurgeUnused`≈PURGE、`GeometryCleanup`≈OVERKILL、`DimTextAudit`≈标注审计、`BomSync`≈DATAEXTRACTION；在"有 AutoCAD 就用插件"的分工下只在"服务器/CI 且必须清理"时成立，文档没给这个前提。
- 全文没有"非目标"段，也没说哪些规则真在图纸上用过（§7 只覆盖 4 条链路）。

### Z10 `SpatialFilter` 662 行 = 自建空间查询语言
- 影响：§4「空间/图框/区域」、§7.7
- 多边形自交检测、三种谓词、`RegionIncludesNested`、图框包围盒 + 属性匹配、布局通配符，一次加 10 个配置字段。
- 对"批量改一个目录里的图"这个根问题，先做"布局 + 矩形"就够。

### Z11 README 40% 是测试报告与踩坑笔记
- 影响：§6.3、§7、§10.8
- 库坑代码片段、公式推导、逐图实测日志占了主体；真正该有的设计决策（为什么配置驱动、规则边界、错误处理契约、永久不做的能力）反而缺席。

## Nit

- Z12 图层规则 4 条、属性写入 3 条，可合并成"一条规则 + 模式"。
- Z13 读得出写不出的版本静默升到 AC1032，交付物格式被悄悄改掉；应默认拒绝。
- Z14 6 个子命令 + 25 个选项 + 大量别名，还有一个文档没写的 `--no-manifest`。

## 复核意见（Elephant）

Z1、Z2、Z10 成立且严重。Z6/Z7 是价值判断，不是缺陷——回滚与探测的取舍可以在文档里论证，但不能说"不该存在"。Z9 的"与宿主重复"忽略了本项目的核心前提（服务器/CI 无授权环境），属于对问题陈述的误读；但"没有非目标段"这一点成立。
