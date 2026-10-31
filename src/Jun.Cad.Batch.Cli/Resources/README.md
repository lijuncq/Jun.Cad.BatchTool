# Resources 目录

离线工具没有 AutoCAD 内核，**图案与线型必须自带**。把下面两个文件放到这里（构建时会自动复制到输出目录）：

- `acadiso.pat` —— 填充图案库（`HatchPatternChange` 需要）
- `acad.pat` —— 英制填充图案库。**只用来判断"某个图案名在英制图纸里认不认得"**，
  决定要不要把图形的 `$MEASUREMENT` 改成公制（见 README 第 5 节 `MeasurementUnits`）。
  不参与"按名字取图案定义"（那份始终是 `acadiso.pat`）
- `acadiso.lin` —— 线型库（`EntityLinetypeChange`、新建图层指定线型时需要）

来源（从本机 AutoCAD 安装目录复制）：

```
C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\zh-cn\Support\acadiso.pat
C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\zh-cn\Support\acad.pat
C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\zh-cn\Support\acadiso.lin
```

公制图纸用 `acadiso.*`；英制图纸用 `acad.*`。两个都放进来也可以，查找顺序是
`acadiso` 优先。AutoCAD 2025 的 `acadiso.pat` 是 `acad.pat` 的超集，多出来的
只有 JIS 系列（`JIS_LC_8A` 等 11 个），所以两份都带不会有歧义。

也可以用命令行临时指定，不放到这里：

```
Jun.Cad.Batch.Cli run --config Jun.Cad.Batch.json --input 图纸 --pat D:\pat\acadiso.pat
```

不提供图案库时：`HatchPatternChange` 会在执行前中止并说明"目标图案在图案库中不存在"，
其它规则不受影响。
