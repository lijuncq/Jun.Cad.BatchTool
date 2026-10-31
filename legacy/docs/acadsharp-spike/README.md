# ACadSharp 可行性验证工程（一次性）

用于回答一个问题：**不启动 AutoCAD，能不能直接用 ACadSharp 读写 dwg 并按 config.json 做批量修改。**

配套结论见 `../acadsharp-port-feasibility.md`。这里是可复现的证据，不是产品代码。

## 用法

```bash
cd docs/acadsharp-spike
dotnet restore

# 读入并统计内容（图层/块/样式/实体直方图/填充图案）
dotnet run -- inspect "D:/work/A-101.dwg"

# 往返：读入 → 写出 → 回读，检查实体数守恒
dotnet run -- roundtrip "D:/work/A-101.dwg" "D:/work/A-101.rt.dwg"

# 强制版本后往返（AC1021/AC1015 必须走这条）
dotnet run -- roundtrip "D:/work/A-101.dwg" "D:/work/A-101.rt.dwg" AC1032

# 六类修改一次性验证（改图层/字体/填充/标注样式/线型/块归层）
dotnet run -- modify "D:/work/A-101.dwg" "D:/work/A-101.mod.dwg"

# 块替换 + 属性迁移
dotnet run -- blockreplace "D:/work/A-101.dwg" "D:/work/A-101.br.dwg"

# 填充专项：8 个用例（换图案/渐变/纯色/用户自定义/双线/比例角度/错误枚举）
dotnet run -- hatch "D:/work/hatch.dwg" "D:/work/out"

# 看某个填充图案的定义行与"有效间距"（偏移模长 × 比例）
dotnet run -- hatchinfo "D:/work/A-101.dwg" "2_SECONDARY_ESCAPE_ROUTE_-_TEST"

# 换图案（模拟 config.json 的 HatchPatternChange）
dotnet run -- setpattern "D:/work/A-101.dwg" "D:/work/A-101.new.dwg" "源图案名" "JIS_LC_8A"

# 枚举实际数值（确认 HatchPatternType 的命名陷阱）
dotnet run -- enum
```

## 用 AutoCAD 内核验收（可选，但这是唯一可信的"AutoCAD 认不认"标准）

```powershell
$ac = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe"
& $ac /i "D:\work\A-101.rt.dwg" /s "D:\work\audit2.scr"
```

判据：输出 `共发现 0 个错误，已修复 0 个` + `已删除 0 个对象`。

三个坑（都踩过了）：

1. **脚本文件必须是 CRLF 换行**，否则 `/s` 脚本不执行，accoreconsole 停在「命令:」提示符；
2. **LISP 加载需要先 `SECURELOAD 0`**，否则报「加载失败」（AutoCAD 的可执行内容安全策略）；
3. **accoreconsole 的输出是 UTF-16LE**，用 `[System.Text.Encoding]::Unicode` 或 Python `decode('utf-16-le')` 读，否则全是乱码。

## 附带的脚本

| 文件 | 作用 |
|---|---|
| `audit2.scr` | `AUDIT` + `Y` + `QUIT`，用于验收 |
| `dump.lsp` / `dump2.scr` | 把图纸内容导出成纯文本清单（实体/图层/块/块内实体/文字样式/标注样式/线型/填充图案），写到 dwg 同目录的 `dump.txt` |
| `attcheck.lsp` / `att.scr` | 顺着 INSERT 的 SEQEND 走一遍，导出属性（ATTRIB）的 Tag 和值——`ssget` 看不到属性，必须这样取 |
| `hatchdump.lsp` | 导出填充的关键组码（名称/比例/角度/样式/图案类型/双线/图案线数/边界数/渐变） |
| `hatchraw.lsp` | 导出填充的**原始组码序列**（含图案线 43/44/45/46/53、渐变 450~470），排查几何问题用 |
| `blockhatch.lsp` | 遍历块定义内部的填充（`ssget` 不返回块内实体，验块内填充必须走这条） |

> 清单比对用法：对原图和输出图各跑一次 `dump.lsp`（放在不同目录，避免 `dump.txt` 互相覆盖），然后 `diff <(sort a.txt) <(sort b.txt)`。零差异 = 内容完全保真。

> `dump2.scr` / `att.scr` 里写死了绝对路径，换机器要改。

## 注意

- 这是**一次性验证工程**，没有错误处理、没有单元测试，不要直接拿去生产用。
- `Program.cs` 里的 `FindPat()` 写死了本机 AutoCAD 的 `acadiso.pat` 路径。
- 工程引用 `ACadSharp 3.8.35`（MIT）。上游迭代很快，正式采用时请锁定版本。
