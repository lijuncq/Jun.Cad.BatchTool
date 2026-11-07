# 验收门禁（VERIFY）

本文件是 `textmask-failure-report.md` 各节「已确认 / 已验收」结论的**唯一可执行落点**：逐条给出
「命令 + 输入 + 预期断言」。每条标注两类之一：

- **可自动断言**：本机 / CI 直接跑，不依赖 AutoCAD 宿主；
- **宿主门禁（需 AutoCAD）**：必须在 AutoCAD 2025 `accoreconsole.exe` 或交互式会话里执行，
  库层「能写能读」的往返不构成验收。

> **测试基线（唯一准绳，2026-11-05 实跑）**
> - 默认（未设置 `JUN_CAD_SAMPLE_DWG`）：**317 total / 311 passed / 6 skipped / 0 failed**
> - 设置 `JUN_CAD_SAMPLE_DWG=tmpv/sampledwg`：**317 total / 317 passed / 0 skipped / 0 failed**
>
> 修复前基线为 301 / 296 / 5；正文出现的 212 / 215 / 291 / 301 / 316 均为历史轮次记录，不代表当前状态。
> 6 个 Skip 全部来自 `DwgSampleTests`（外部样图目录缺失时在发现阶段跳过）。

> **宿主门禁已于 2026-11-05 在 AutoCAD 2025 Core Console 上实跑**，逐项结果见下方各节与
> 「宿主门禁执行结果」汇总。所有产物在 `tmpv/hostverify-2026-11-05/`。

---

## 1. 单元测试基线 — 可自动断言

- 命令：`dotnet test Jun.Cad.Batch.sln --nologo -v minimal`
- 输入：仓库根 `Jun.Cad.Batch.sln`（无需外部样图）
- 预期断言：`Failed 0 / Passed 311 / Skipped 6 / Total 317`。
- 带真实样图的命令：
  ```sh
  JUN_CAD_SAMPLE_DWG="D:/Dev/CSharp/AutoCAD/Jun.AutoCAD.Batch/tmpv/sampledwg" \
    dotnet test Jun.Cad.Batch.sln --nologo -v minimal
  ```
  预期断言：`Failed 0 / Passed 317 / Skipped 0 / Total 317`。
- `TextMaskRuleTests` 当前 50 个测试方法（其中 15 条新增用例是 2026-11-05 全量修复的回归：
  C02 停用告警、C03 ACAD XData 保留与 DIMTFILL 用户值保留、C05 单图上限含存量遮罩、C06 已有
  非打印 `_MASK` 图层纠正、C07 动态块 `*U`、C09 两个枚举非法值、C15 空 MTEXT、C27 NaN/∞）。
- `DwgSampleTests` 新增 `TextMask在真实图上写盘回读后遮罩仍在()`：真实 DWG 上跑 `TextMask` →
  `CadFileSession.Save` → 重新 `Open`，断言遮罩指示数增加且 MTEXT 的 bit0+bit1 仍在。
  该测试只证明**库层往返**，不替代第 2–5 项宿主门禁。

## 2. WIPEOUT 实体计数 — 宿主门禁（需 AutoCAD）· **已实跑**

- 命令：`accoreconsole.exe /i <dwg> /s <scr> /l en-US`
  （本机 `/i` 被忽略，脚本里需 `(setvar "FILEDIA" 0)` + `(command "_.OPEN" ...)`；
  `/s` 走 PowerShell 调用；LISP 内不能有中文注释——见报告 §7）
- 输入：`tmpv/hostverify-2026-11-05/out/control.dwg`（CLI 用 `config.json` 处理 `in/control.dwg` 的产物），
  脚本 `verify.scr` / `blocks.scr` / `detail.scr`，结果 `host-result.txt` / `block-result.txt` / `detail-result.txt`
- 预期断言：计数 = 该图按 `MaskTextBox` / 判重规则应产生的 WIPEOUT 数；
  组码 `71=2 / 91=5 / 280=1 / 340=0 / 360=0`；`ssget` 返回 0 即判不通过。
- **实测结果（通过）**：
  - CLI 日志 `out/Logs/batch-20261105-152942.log`：
    `共为 11 处文字加了遮罩（MTEXT 原生背景 4 / 标注文字 1 / Wipeout 6 / 重排到最上层 11）`，
    `匹配 11，改动 11，跳过 0，失败 0`。
  - `ssget "_X" '((0 . "WIPEOUT"))` → **5** 个，句柄 `29B 2AF 2A8 2A3 2A0`，全部 `layer=_MASK`、
    `71=2 / 91=5 / 280=1`。`detail.scr` 经 `entget` 回读 `340=nil / 360=nil`（即影像定义句柄为 0），
    与 `MaskService.IsWipeoutEncoded` 要求的 `Definition == null` 一致。
  - **计数口径澄清（不是缺实体）**：`ssget "_X"` **不返回块定义内部的实体**。用
    `tblnext "BLOCK"` + `entnext` 枚举后，块定义 `TBLK` 内还有 1 个 WIPEOUT（句柄 `2B2`，
    `layer=_MASK / 71=2 / 91=5 / 280=1`）和 1 个 MTEXT（句柄 `2B5`，`90=3 / 63=7 / 45=1.15`）。
    因此 **CLI 的 Wipeout 6 = 模型空间 5 + 块定义 1**，**MTEXT 原生 4 = 模型空间 3 + 块定义 1**。
    `MaskInsideBlocks=true` 的块内遮罩确实写进了 DWG 且宿主可读。
    标注缓存块 `*D2` 内另有一个 MTEXT，属 `*D` 匿名块，按规则不计入目标。

## 3. 出图逐像素断言 — 宿主门禁（需 AutoCAD）· **已实跑（新证据）**

- 命令：`accoreconsole.exe /s plot-src.scr` / `/s plot.scr`，`-PLOT` + `PublishToWeb PNG.pc3`
  + `monochrome.ctb` 出 1600×1280 PNG；用 `tmpv/masktest/pngscan.py` 与差分脚本扫描。
- 输入：`in/control.dwg`（源图）与 `out/control.dwg`（加遮罩后）。`extents.scr` 实测两者
  `EXTMIN=(0,0,0)` / `EXTMAX=(330,74.7062,0)` 完全一致，故 `_.ZOOM _E` 是严格同口径对照。
- 产物：`host-plot-src.png`（13024 B）、`host-plot.png`（14121 B）。
- **实测结果（通过）**：

  | 行 | 源图暗像素区间 | 加遮罩后暗像素区间 | 含义 |
  |---|---|---|---|
  | y=746 | `0–289` | `0–20` / `255–289` | 横贯线在遮罩内 x=21..254 被擦除，边界处断开/接上 |
  | y=747 | `0–289` | `0–20` / `255–289` | 同上 |
  | y=748 | `0–289` / `1212–1599` | `0–20` / `255–289` / `1212–1599` | 遮罩外的线保持原样 |
  | y=766..768 | `0–289` | `0–20` / `255–289` | 同上；同行的 `339–678` 段在两图均存在 |

  全图差分：**源图暗像素 56398 → 输出 47669（-8729）；`erased=8729 / added=0`**。
  即遮罩只擦除、不新增任何暗像素（没有白块/黑框假象）；最长连续擦除区 234 px（x=21–254）。
- 历史证据：`textmask-failure-report.md` §8.1 的 y=639 `0–399 / 1199–1598` 是 2026-11-02 另一张
  自建受控图的结果，口径相同，仍有效；本节是**修复后代码产物的新出图证据**。
- ⚠️ `-PLOT` 纸张尺寸参数必须传 `""` 取默认值：本机 AutoCAD 为中文版，硬编码
  `"Sun Hi-Res (1600.00 x 1280.00 Pixels)"` 会被判为无效纸张名，导致 `-PLOT` 提前结束、
  后续参数被当成命令（实测报「未知命令 L/N/E/…」）。

## 4. 关键组码 `entget` 回读 — 宿主门禁（需 AutoCAD）· **已实跑**

- 命令：同第 2 项的 `accoreconsole.exe`；`verify.scr` 内 `(entget ...)` 打印组码 / XData。
- 输入：工具输出 DWG 中的 MTEXT 与 DIMENSION，结果 `host-result.txt` / `detail-result.txt`。
- 预期断言：MTEXT `(90 . 3) (63 . 7) (45 . 1.15)` 成套出现；DIMENSION 的 ACAD DSTYLE XData
  含 `(1070 . 69) (1070 . 1)` 且原有键（如 `1070 . 140`）全部保留。
- **实测结果（通过）**：
  - 模型空间 3 个 MTEXT（`2AB / 2A6 / 29E`，文字分别 `MTEXT OVER HATCH / LINES / SOLID`）
    全部 `g90=3 g63=7 g45=1.15 g41=0.0`；块 `TBLK` 内 MTEXT `2B5` 也是 `90=3 / 63=7 / 45=1.15`。
  - 唯一 DIMENSION 的 XData：
    `((ACAD (1000 . DSTYLE) (1002 . {) (1070 . 140) (1040 . 3.5) (1070 . 69) (1070 . 1) (1002 . })))`。
    **原有 `140 / 3.5` 完整保留、`69 / 1` 追加在后** —— C03 的局部 DSTYLE 段替换在宿主侧确认生效，
    不再是 `SetStyleOverrideMap` 清空整段 ACAD XData 的旧行为。

## 5. `WIPEOUTFRAME` 重开值 — 宿主门禁（需 AutoCAD）· **已实跑，仍为已知未修复项**

- 命令：`verify.scr` 内 `(getvar "WIPEOUTFRAME")` 与 `(dictsearch (namedobjdict) "ACAD_WIPEOUT_VARS")`。
- 输入：`out/control.dwg`。
- **实测结果**：

  ```text
  (WIPEOUTFRAME= 1)
  (WIPEOUT_VARS_70= 1)
  ```

  与预期「写入 `70=0` 后 AutoCAD 2025 重开仍报 `1`」一致 → **C11 仍未修复**。
  此项按「未修复」记录，**不作为通过项**；需上游 DWG 写出器修复后才可改为断言 0（见报告 §8.2）。
  白色实体色只在白底出图上让边框不可见，深色绘图区仍可见白色细框。
  注：第 3 项出图里 `added=0`，说明 `monochrome.ctb` 出图时该边框不产生暗像素，但不等于边框消失。

---

## 宿主门禁执行结果（2026-11-05 汇总）

| 门禁 | 状态 | 证据 |
|---|---|---|
| §1 单元测试 | ✅ 可自动断言通过 | 默认 317/311/6/0；带真实样图 317/317/0/0 |
| §2 WIPEOUT 计数 / 组码 | ✅ 通过 | `host-result.txt`、`block-result.txt`、`detail-result.txt`；块内计数口径已澄清 |
| §3 `-PLOT` 逐像素 | ✅ 通过（新证据） | `host-plot-src.png` vs `host-plot.png`；`erased=8729 / added=0` |
| §4 MTEXT / DIMENSION `entget` | ✅ 通过 | `g90=3 / g63=7 / g45=1.15`；DSTYLE 保留 `140/3.5` 并追加 `69/1` |
| §5 `WIPEOUTFRAME` | ❌ 已知未修复（C11） | 重开仍为 `1`，字典 `70=1` |

**签发范围（严格限定）**：以上只证明「本仓库当前代码在 `tmpv/hostverify-2026-11-05/in/control.dwg`
这张受控图上，经 AutoCAD 2025 Core Console 无头会话回读与 `-PLOT` 出图，遮罩数据可解析、擦除生效、
关键组码与 XData 正确」。**不签发**以下内容：

- **C01 / 绘制顺序数据丢失**：本图 `sortents= missing`（无 SORTENTSTABLE），破坏性路径未被触发，
  既不能证明已修、也不能证明在含排序表的图上安全。**仍为未解决**，含 `SORTENTSTABLE` 的图纸
  请勿原地覆盖，先 `--out` 到副本并核对 `DRAWORDER`（见 README §8）。
- **C11 / WIPEOUTFRAME 边框**：实测仍为 `1`，未修复。
- **C21 / 块内 TEXT 遮罩偏移**：需交互式 AutoCAD 2025 出图复核，无头 `-PLOT` 未覆盖该判定。
- **交互式会话观感**（屏幕显示 / 打印预览 / 深色绘图区里的白框）：本机只能跑 Core Console 无头出图，
  未做界面截图，仍需人工复核。

## 未决 ESCALATION（保持未解决，不得写成已修复）

- **C01**：`SORTENTS` / 块记录扩展字典写不出去，保存时重置用户 `DRAWORDER`（绘制顺序数据丢失）。
  **未修复**。本次宿主回读 `sortents= missing` 只说明受控图没有排序表，不构成对该缺陷的验证。
- **C20**：私有 ACadSharp 克隆的构建链依赖（`Jun.Cad.Batch.Core.csproj` 引用仓库外路径），
  干净检出编不过。**未修复**。
- **C21**：块内 TEXT 遮罩比文字框低约一个字高，是否仍复现需 AutoCAD 2025 出图复核。**未修复**。

以上三条需要用户的领域知识 / 架构决策，且证据部分落在仓库外或需要宿主环境；本轮只记录，不擅自关闭。
