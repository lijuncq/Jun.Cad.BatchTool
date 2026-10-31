using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Engine.Preflight;
using Jun.Cad.Batch.Engine.Runs;
using Jun.Cad.Batch.Engine.Preview;

namespace Jun.Cad.Batch.Cli
{
    /// <summary>
    /// 命令行入口：不打开 AutoCAD，直接读写 DWG 文件。
    ///
    /// 用法：
    ///   Jun.Cad.Batch.Cli run    --config config.json --input 图纸目录 [--pattern *.dwg]
    ///   Jun.Cad.Batch.Cli inspect 图纸.dwg
    ///   Jun.Cad.Batch.Cli patterns [--pat acadiso.pat]
    ///
    /// 设计取舍：
    ///   * **默认不改源文件**：输出写到 <c>--out</c> 目录（默认 &lt;输入目录&gt;\output）。
    ///     要覆盖源文件必须显式 <c>--in-place</c>，且会自动生成 .bak 备份。
    ///     批量改图是不可逆操作，默认值必须是安全的那个。
    ///   * <c>--dry-run</c> 跑完整流程但不写盘，用来看"会改多少个对象"。
    ///   * 退出码：0 全部成功；1 有文件失败；2 参数/配置错误。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            try
            {
                switch (args[0].Trim().ToLowerInvariant())
                {
                    case "run":
                        return RunCommand(args.Skip(1).ToArray());
                    case "inspect":
                        return InspectCommand(args.Skip(1).ToArray());
                    case "patterns":
                        return PatternsCommand(args.Skip(1).ToArray());
                    case "preflight":
                        return PreflightCommand(args.Skip(1).ToArray());
                    case "rollback":
                        return RollbackCommand(args.Skip(1).ToArray());
                    case "schema":
                        return SchemaCommand(args.Skip(1).ToArray());
                    case "types":
                        return TypesCommand();
                    default:
                        Console.Error.WriteLine("未知命令：" + args[0]);
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("执行失败：" + ex.Message);
                return 2;
            }
        }

        // ---------------------------------------------------------------- run

        private static int RunCommand(string[] args)
        {
            var options = RunOptions.Parse(args);
            if (options == null)
            {
                PrintRunUsage();
                return 2;
            }

            List<string> configWarnings;
            BatchConfig config;
            try
            {
                config = ConfigLoader.Load(options.ConfigPath, out configWarnings);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("读取配置失败：" + ex.Message);
                return 2;
            }

            var files = CollectFiles(options);
            if (files.Count == 0)
            {
                Console.Error.WriteLine("没有找到待处理的 DWG 文件。输入：" + options.InputPath);
                return 2;
            }

            // 日志：命令行显式指定 > 配置 LogFile > 默认写到输出目录
            string logPath = ResolveLogPath(options, config, files.Count);

            using (var log = new BatchLogger(Console.Out, logPath, options.Verbose))
            {
                log.Section("Jun.Cad.Batch.Cli（离线批量改图）");
                log.Info(string.Format("配置文件：{0}", options.ConfigPath));
                log.Info(string.Format("待处理文件：{0} 个", files.Count));
                log.Info(string.Format("输出方式：{0}", DescribeOutputMode(options)));

                if (!string.IsNullOrEmpty(options.VersionText))
                {
                    log.Info(string.Format("指定输出版本：{0}", options.VersionText));
                }

                foreach (string w in configWarnings)
                {
                    log.Warn(w);
                }

                var resources = new ResourceLocator
                {
                    PatternFile = options.PatternFile,
                    LinetypeFile = options.LinetypeFile
                };

                // 把"实际用了哪份 .pat / .lin"打出来：不同 AutoCAD 版本的 acadiso.pat 内容不同，
                // 不写清楚的话，同一份配置在开发机与服务器上可以产出不同的图案几何。
                if (resources.HasPatterns)
                {
                    log.Info("图案库：" + string.Join("；", resources.LoadedPatternFiles.ToArray()));
                }
                else
                {
                    log.Warn("未加载到图案库（.pat）：HatchPatternChange 会用不了。");
                }

                var fileResults = new List<FileRunResult>();
                var previewRecords = new List<ChangeRecord>();
                var totalWatch = Stopwatch.StartNew();

                // 运行清单：记录"这次写了哪些文件、备份在哪"，用于按本次运行回滚
                string outputRoot = ResolveOutputRoot(options, files);
                string runDirectory = null;
                RunManifest manifest = null;

                if (!options.NoManifest && !options.Preview && !options.DryRun && !string.IsNullOrEmpty(outputRoot))
                {
                    try
                    {
                        runDirectory = RunManifestStore.CreateRunDirectory(outputRoot);
                        manifest = new RunManifest
                        {
                            RunDirectory = runDirectory,
                            Timestamp = DateTime.Now,
                            ConfigPath = options.ConfigPath,
                            Mode = options.InPlace ? "in-place" : "output",
                            OutputRoot = outputRoot
                        };
                    }
                    catch (Exception ex)
                    {
                        log.Warn("无法创建运行清单目录（不影响本次执行）：" + ex.Message);
                        manifest = null;
                    }
                }

                // 递归目录里同名文件（A\1.dwg 与 B\1.dwg）会互相覆盖：这类名字改走相对路径
                var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (options.Recursive)
                {
                    foreach (var group in files.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                    {
                        if (group.Count() > 1)
                        {
                            ambiguousNames.Add(group.Key);
                        }
                    }

                    if (ambiguousNames.Count > 0)
                    {
                        log.Warn(string.Format(
                            "有 {0} 个文件名在多个子目录中重复（{1}），这些文件会按子目录结构输出，避免互相覆盖。",
                            ambiguousNames.Count, string.Join("/", ambiguousNames.ToArray())));
                    }
                }

                foreach (string file in files)
                {
                    FileRunResult fileResult = ProcessOneFile(file, options, config, resources, log, previewRecords,
                        manifest, ambiguousNames);
                    fileResults.Add(fileResult);
                }

                totalWatch.Stop();

                WriteFileSummary(log, fileResults, totalWatch.Elapsed, options);

                if (manifest != null && runDirectory != null)
                {
                    CollectRuleStats(manifest, fileResults);
                    try
                    {
                        RunManifestStore.Save(manifest, runDirectory);
                        log.Info("运行清单：" + Path.Combine(runDirectory, "manifest.json")
                                 + "（回滚用：Jun.Cad.Batch.Cli rollback " + runDirectory + "）");
                    }
                    catch (Exception ex)
                    {
                        log.Warn("运行清单写出失败：" + ex.Message);
                    }
                }

                if (options.Preview)
                {
                    WritePreview(log, previewRecords, options, files);
                }

                bool anyFailed = fileResults.Any(r => !r.Succeeded);
                return anyFailed ? 1 : 0;
            }
        }

        private static FileRunResult ProcessOneFile(
            string file, RunOptions options, BatchConfig config, ResourceLocator resources, BatchLogger log,
            List<ChangeRecord> previewRecords, RunManifest manifest, HashSet<string> ambiguousNames)
        {
            var fileResult = new FileRunResult { FilePath = file };

            log.Section("文件：" + Path.GetFileName(file));

            ACadVersion? requested = null;
            if (!string.IsNullOrEmpty(options.VersionText))
            {
                ACadVersion parsed;
                if (!CadFileSession.TryParseVersion(options.VersionText, out parsed))
                {
                    log.Error("无法识别的版本字符串：" + options.VersionText);
                    fileResult.Error = "版本字符串无法识别";
                    return fileResult;
                }

                requested = parsed;
            }

            CadFileSession session;
            try
            {
                session = CadFileSession.Open(file, requested);
            }
            catch (Exception ex)
            {
                log.Error("读取失败：" + ex.Message);
                fileResult.Error = "读取失败：" + ex.Message;
                return fileResult;
            }

            using (session)
            {
                log.Info(string.Format("版本：{0} → 输出 {1}{2}",
                    CadFileSession.DescribeVersion(session.SourceVersion),
                    CadFileSession.DescribeVersion(session.OutputVersion),
                    session.VersionUpgraded ? "（已提升，源版本不可安全写出）" : string.Empty));

                if (session.ReadNotices.Count > 0)
                {
                    log.Warn(string.Format("读入告警 {0} 条：{1}", session.ReadNotices.Count, session.DescribeReadNotices(3)));
                }

                PreviewSink sink = options.Preview ? new PreviewSink() : null;
                var runner = new BatchRunner(config, options.ConfigPath, log, resources, sink)
                {
                    FileLabel = Path.GetFileName(file)
                };

                BatchRunSummary summary = runner.Run(session.Document);
                runner.WriteSummary(summary);

                fileResult.Summary = summary;

                if (sink != null)
                {
                    previewRecords.AddRange(sink.Records);
                    log.Info(string.Format(
                        "预览：将改动 {0} 处，无需改动 {1} 处，被挡住 {2} 处（其中降级 {3} 处）。",
                        sink.CountOf(ChangeStatus.WillChange),
                        sink.CountOf(ChangeStatus.NoChange),
                        sink.CountOf(ChangeStatus.Blocked),
                        sink.DegradedCount));
                    fileResult.Succeeded = true;
                    fileResult.Skipped = true;
                    return fileResult;
                }

                // 规则中止 → 默认整份文件不写盘（与 README "失败不写盘" 一致）。
                // 关闭方式：配置里写 "OnRuleFailure": "WritePartial"。
                if (!IsWritePartial(config) && summary.Results.Any(r => r.Aborted))
                {
                    string reason = string.Join("；", summary.Results
                        .Where(r => r.Aborted)
                        .Select(r => r.RuleName + "：" + (r.AbortReason ?? "中止"))
                        .ToArray());

                    log.Error("有规则中止，按 OnRuleFailure=DiscardFile 丢弃本文件（不写盘）：" + reason);
                    fileResult.Error = "规则中止，未写盘：" + reason;
                    return fileResult;
                }

                if (options.DryRun)
                {
                    log.Info("--dry-run：不写出文件。");
                    fileResult.Succeeded = true;
                    fileResult.Skipped = true;
                    return fileResult;
                }

                if (summary.TotalChanged == 0 && options.SkipUnchanged)
                {
                    log.Info("没有任何改动，跳过写出（--skip-unchanged）。");
                    fileResult.Succeeded = true;
                    fileResult.Skipped = true;
                    return fileResult;
                }

                string outputPath = ResolveOutputPath(file, options, ambiguousNames);
                bool existed = File.Exists(outputPath);

                try
                {
                    session.Save(outputPath, options.CreateBackup, manifest == null ? null : BackupDirOf(manifest));
                    log.Info("已写出：" + outputPath);

                    foreach (string notice in session.WriteNotices.Take(5))
                    {
                        log.Warn("写出告警：" + notice);
                    }

                    fileResult.OutputPath = outputPath;
                    fileResult.Succeeded = true;

                    if (manifest != null)
                    {
                        manifest.Entries.Add(new RunManifestEntry
                        {
                            SourcePath = file,
                            OutputPath = outputPath,
                            BackupPath = existed && options.CreateBackup ? BackupPathOf(manifest, outputPath) : null,
                            Existed = existed,
                            Succeeded = true,
                            Changed = summary == null ? 0 : summary.TotalChanged
                        });

                        manifest.TotalChanged += summary == null ? 0 : summary.TotalChanged;

                        // 每处理完一个文件就落一次清单：整批结束才写的话，
                        // 中途 Ctrl-C / 崩溃会留下"有备份、没清单"的残局，rollback 反而回滚到上一次运行。
                        TrySaveManifest(manifest, log);
                    }
                }
                catch (Exception ex)
                {
                    log.Error("写出失败：" + ex.Message);
                    fileResult.Error = "写出失败：" + ex.Message;

                    if (manifest != null)
                    {
                        manifest.Entries.Add(new RunManifestEntry
                        {
                            SourcePath = file,
                            OutputPath = outputPath,
                            Existed = existed,
                            Succeeded = false,
                            Error = ex.Message
                        });

                        TrySaveManifest(manifest, log);
                    }
                }
            }

            return fileResult;
        }

        /// <summary>增量保存运行清单（失败只告警，不影响本次执行）。</summary>
        private static void TrySaveManifest(RunManifest manifest, BatchLogger log)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.RunDirectory))
            {
                return;
            }

            try
            {
                RunManifestStore.Save(manifest, manifest.RunDirectory);
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log.Warn("运行清单增量保存失败（不影响本次执行）：" + ex.Message);
                }
            }
        }

        /// <summary>配置要求"中止也写出部分改动"吗（默认不是）。</summary>
        private static bool IsWritePartial(BatchConfig config)
        {
            return config != null
                   && string.Equals(config.OnRuleFailure, "WritePartial", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本次运行的备份目录（没有清单时返回 null）。</summary>
        private static string BackupDirOf(RunManifest manifest)
        {
            return manifest == null || string.IsNullOrEmpty(manifest.RunDirectory)
                ? null
                : RunManifestStore.BackupDirectoryOf(manifest.RunDirectory);
        }

        /// <summary>某个输出文件在本次运行里的备份路径（与 CadFileSession 的命名保持一致）。</summary>
        private static string BackupPathOf(RunManifest manifest, string outputPath)
        {
            string dir = BackupDirOf(manifest);
            return dir == null ? null : Path.Combine(dir, Path.GetFileName(outputPath) + ".bak");
        }

        /// <summary>本次运行的输出根目录（清单与备份都放在这里）。</summary>
        private static string ResolveOutputRoot(RunOptions options, List<string> files)
        {
            if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                return Path.GetFullPath(options.OutputDirectory);
            }

            string first = files.Count > 0 ? files[0] : options.InputPath;
            if (File.Exists(first))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(first));
                return options.InPlace ? dir : Path.Combine(dir ?? ".", "output");
            }

            return options.InPlace
                ? Path.GetFullPath(first)
                : Path.Combine(Path.GetFullPath(first), "output");
        }

        /// <summary>把每个文件的规则统计汇总进清单（去重合并同名规则）。</summary>
        private static void CollectRuleStats(RunManifest manifest, List<FileRunResult> results)
        {
            foreach (FileRunResult fileResult in results)
            {
                if (fileResult.Summary == null)
                {
                    continue;
                }

                foreach (RuleExecutionResult rule in fileResult.Summary.Results)
                {
                    RunRuleStat stat = manifest.Rules.FirstOrDefault(
                        r => string.Equals(r.Name, rule.RuleName, StringComparison.Ordinal)
                             && string.Equals(r.Type, rule.RuleType, StringComparison.OrdinalIgnoreCase));

                    if (stat == null)
                    {
                        stat = new RunRuleStat { Name = rule.RuleName, Type = rule.RuleType };
                        manifest.Rules.Add(stat);
                    }

                    stat.Matched += rule.Matched;
                    stat.Changed += rule.Changed;
                    stat.Skipped += rule.Skipped;
                    stat.Failed += rule.Failed;
                    stat.Aborted |= rule.Aborted;

                    if (rule.Aborted && string.IsNullOrEmpty(stat.AbortReason))
                    {
                        stat.AbortReason = rule.AbortReason;
                    }
                }
            }
        }

        private static List<string> CollectFiles(RunOptions options)
        {
            var list = new List<string>();

            if (File.Exists(options.InputPath))
            {
                list.Add(Path.GetFullPath(options.InputPath));
                return list;
            }

            if (!Directory.Exists(options.InputPath))
            {
                return list;
            }

            // 排除规则放在 DrawingFolder 里，与界面的"输入图纸文件夹"共用一份：
            // 输出根、.jun-runs、以及"以前跑过"的旧输出根都不当输入。
            string skipRoot = options.InPlace
                ? null
                : (string.IsNullOrEmpty(options.OutputDirectory)
                    ? DrawingFolder.DefaultOutputRoot(options.InputPath)
                    : Path.GetFullPath(options.OutputDirectory));

            list.AddRange(DrawingFolder.Enumerate(options.InputPath, options.Patterns, options.Recursive, skipRoot));
            return list;
        }

        private static string ResolveOutputPath(string inputFile, RunOptions options, HashSet<string> ambiguousNames = null)
        {
            if (options.InPlace)
            {
                return inputFile;
            }

            string name = Path.GetFileName(inputFile);
            string outDir = options.OutputDirectory;

            if (string.IsNullOrEmpty(outDir))
            {
                // 输出根按**输入目录**算，不按每个文件自己所在的目录算。
                //
                // 按文件目录算的话，递归处理 `图纸\sub\b.dwg` 会写到 `图纸\sub\output\b.dwg`，
                // 而排除规则只认 `图纸\output`——下一次运行就把 `sub\output` 里的产物当输入，
                // 一层层套下去（实测第三次运行待处理文件从 2 变成 5）。
                // 统一到 `<输入目录>\output` 之后，输出根与运行清单根也终于对上了。
                outDir = Directory.Exists(options.InputPath)
                    ? DrawingFolder.DefaultOutputRoot(options.InputPath)
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputFile)) ?? ".", "output");
            }

            string candidate = Path.Combine(outDir, name);

            // 同名冲突：保留相对输入目录的子路径，别让 A\1.dwg 与 B\1.dwg 抢同一个输出名
            if (ambiguousNames != null && ambiguousNames.Contains(name) && Directory.Exists(options.InputPath))
            {
                string relativeDir = Path.GetDirectoryName(Path.GetRelativePath(options.InputPath, inputFile));
                if (!string.IsNullOrEmpty(relativeDir) && relativeDir != ".")
                {
                    candidate = Path.Combine(outDir, relativeDir, name);
                }
            }

            // --out 指到输入目录时，候选路径就是源文件本身：改名而不是覆盖源图
            try
            {
                if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(inputFile),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(outDir, Path.GetFileNameWithoutExtension(inputFile) + "_out.dwg");
                }
            }
            catch
            {
                // 路径解析失败时按原样返回，由写出阶段报错
            }

            return candidate;
        }

        /// <summary>
        /// 文件汇总的一行。列宽按显示列宽算（汉字两列），否则中文文件名会把数字列顶歪，
        /// 与 <see cref="BatchRunner.WriteSummary"/> 保持同一套算法（见 TextColumns）。
        /// </summary>
        private static string FormatFileRow(string file, string matched, string changed,
            string skipped, string status)
        {
            return TextColumns.Pad(file, 34, false)
                 + TextColumns.Pad(matched, 10, true)
                 + TextColumns.Pad(changed, 8, true)
                 + TextColumns.Pad(skipped, 8, true)
                 + "  "                                   // 数字列右对齐后紧贴状态，留两格气口
                 + TextColumns.Pad(status, 10, false);
        }

        private static void WriteFileSummary(
            BatchLogger log, List<FileRunResult> results, TimeSpan elapsed, RunOptions options)
        {
            log.Section("文件汇总");
            log.Info(FormatFileRow("文件", "匹配", "改动", "跳过", "状态"));

            // 同名文件（A\1.dwg / B\1.dwg）在汇总里只显示 1.dwg 就分不清是哪张，重复时带上父目录
            var duplicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in results.GroupBy(r => Path.GetFileName(r.FilePath), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1)
                {
                    duplicateNames.Add(group.Key);
                }
            }

            foreach (FileRunResult r in results)
            {
                int matched = r.Summary == null ? 0 : r.Summary.TotalMatched;
                int changed = r.Summary == null ? 0 : r.Summary.TotalChanged;
                int skipped = r.Summary == null ? 0 : r.Summary.TotalSkipped;

                string status = !r.Succeeded ? "失败" : (r.Skipped ? "跳过" : "已写出");

                string name = Path.GetFileName(r.FilePath);
                if (duplicateNames.Contains(name))
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(r.FilePath));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        name = parent + "/" + name;
                    }
                }

                log.Info(FormatFileRow(name, matched.ToString(), changed.ToString(), skipped.ToString(), status));
            }

            log.Info(new string('-', 70));

            int totalChanged = results.Where(r => r.Summary != null).Sum(r => r.Summary.TotalChanged);
            int failed = results.Count(r => !r.Succeeded);

            log.Info(string.Format("共 {0} 个文件，改动 {1} 处，失败 {2} 个，耗时 {3:0.0}s",
                results.Count, totalChanged, failed, elapsed.TotalSeconds));

            if (failed > 0)
            {
                log.Warn("失败文件：");
                foreach (FileRunResult r in results.Where(x => !x.Succeeded))
                {
                    log.Warn(string.Format("  · {0}：{1}", r.FilePath, r.Error ?? "未知原因"));
                }
            }

            if (options.DryRun)
            {
                log.Warn("本次是 --dry-run，没有写出任何文件。");
            }
        }

        // ---------------------------------------------------------------- inspect

        private static int InspectCommand(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("用法：Jun.Cad.Batch.Cli inspect <文件.dwg>");
                return 2;
            }

            string path = args[0];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("文件不存在：" + path);
                return 2;
            }

            var watch = Stopwatch.StartNew();
            using (CadFileSession session = CadFileSession.Open(path, null))
            {
                watch.Stop();
                CadDocument doc = session.Document;

                Console.WriteLine("文件      : " + Path.GetFileName(path) + "  (" + new FileInfo(path).Length / 1024 + " KB)");
                Console.WriteLine("版本      : " + CadFileSession.DescribeVersion(session.SourceVersion));
                Console.WriteLine("读入耗时  : " + watch.ElapsedMilliseconds + " ms");
                Console.WriteLine("读入告警  : " + session.ReadNotices.Count);
                foreach (string n in session.ReadNotices.Take(8))
                {
                    Console.WriteLine("   ! " + n);
                }

                Console.WriteLine("图层      : " + doc.Layers.Count);
                Console.WriteLine("块定义    : " + doc.BlockRecords.Count);
                Console.WriteLine("文字样式  : " + doc.TextStyles.Count);
                Console.WriteLine("标注样式  : " + doc.DimensionStyles.Count);
                Console.WriteLine("线型      : " + doc.LineTypes.Count);

                var hits = DatabaseWalker.Collect(doc, ScanScope.ModelAndLayouts, 8, true, null);
                Console.WriteLine("实体      : " + hits.Count + "（模型 + 布局 + 块定义内部）");

                foreach (var g in hits.GroupBy(h => EntityFilter.DxfNameOf(h.Entity)).OrderByDescending(g => g.Count()).Take(15))
                {
                    Console.WriteLine("   " + g.Key.PadRight(16) + g.Count());
                }

                var hatches = hits.Select(h => h.Entity).OfType<ACadSharp.Entities.Hatch>().ToList();
                if (hatches.Count > 0)
                {
                    Console.WriteLine("填充图案（线距 = 图案线的实际垂直间距，比例与角度已烘进图案线）：");
                    foreach (var g in hatches.GroupBy(h => h.Pattern == null ? "(null)" : h.Pattern.Name).OrderByDescending(g => g.Count()).Take(10))
                    {
                        var list = g.ToList();
                        string scales = string.Join("/", list
                            .Select(h => h.PatternScale)
                            .Distinct()
                            .OrderBy(x => x)
                            .Take(4)
                            .Select(x => x.ToString("0.####")));

                        double? reference = HatchPatternMetrics.ReferenceSpacing(list[0].Pattern);
                        string spacing = reference.HasValue ? reference.Value.ToString("0.####") : "-";

                        // 颜色与图层一起打出来：换图案后"看起来颜色变了"时，
                        // 第一件要确认的就是实体颜色索引有没有被改动过
                        string colors = string.Join("/", list
                            .Select(h => h.Color.IsTrueColor
                                ? "#" + h.Color.R.ToString("X2") + h.Color.G.ToString("X2") + h.Color.B.ToString("X2")
                                : h.Color.Index.ToString())
                            .Distinct()
                            .OrderBy(x => x)
                            .Take(4));

                        string layers = string.Join("/", list
                            .Select(h => h.Layer == null ? "?" : h.Layer.Name)
                            .Distinct()
                            .Take(3));

                        Console.WriteLine("   " + (g.Key ?? "(null)").PadRight(26)
                            + g.Count().ToString().PadRight(6)
                            + "比例 " + scales.PadRight(16)
                            + "线距 " + spacing.PadRight(10)
                            + "色 " + colors.PadRight(12)
                            + "层 " + layers);
                    }
                }

                var inserts = hits.Select(h => h.Entity).OfType<ACadSharp.Entities.Insert>().ToList();
                if (inserts.Count > 0)
                {
                    Console.WriteLine("块参照：");
                    foreach (var g in inserts.GroupBy(i => EntityFilter.EffectiveBlockName(i.Block) ?? "(null)").OrderByDescending(g => g.Count()).Take(10))
                    {
                        Console.WriteLine("   " + g.Key.PadRight(24) + g.Count());
                    }
                }
            }

            return 0;
        }

        // ---------------------------------------------------------------- patterns

        private static int PatternsCommand(string[] args)
        {
            string pat = args.Length > 0 ? args[0] : null;
            var resources = new ResourceLocator { PatternFile = pat };

            if (!resources.HasPatterns)
            {
                Console.Error.WriteLine("未找到图案库（.pat）。请用参数指定路径，或把 acadiso.pat 放到程序目录的 Resources 下。");
                return 2;
            }

            // 先说清楚用的是哪份文件：不同 AutoCAD 版本的 acadiso.pat 内容不同，
            // 只列图案名不列路径，用户无法判断"为什么这台机器上的图案和那台不一样"。
            Console.WriteLine("图案库文件：");
            foreach (string file in resources.LoadedPatternFiles)
            {
                Console.WriteLine("   " + file);
            }

            var names = resources.PatternNames.ToList();
            Console.WriteLine("可用图案 " + names.Count + " 个：");
            foreach (string name in names)
            {
                Console.WriteLine("   " + name);
            }

            return 0;
        }

        private static int TypesCommand()
        {
            RuleFactory.RegisterBuiltIns();
            Console.WriteLine("已注册规则类型：");
            foreach (string t in RuleFactory.GetRegisteredTypes())
            {
                Console.WriteLine("   " + t);
            }

            return 0;
        }

        // ---------------------------------------------------------------- schema

        /// <summary>
        /// 导出配置契约：默认打印 JSON schema；`--markdown` 输出 README 用的字段表。
        ///
        /// 存在的理由：配置的五个消费者（文档/界面/校验/摘要/legacy 兼容）以前各抄一份，
        /// 必然漂移。schema 从属性标注导出，是它们的单一真源。
        /// </summary>
        private static int SchemaCommand(string[] args)
        {
            string outPath = null;
            bool markdown = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                }
                else if (string.Equals(args[i], "--markdown", StringComparison.OrdinalIgnoreCase))
                {
                    markdown = true;
                }
            }

            string content = markdown ? SchemaExport.ToMarkdown() : SchemaExport.ToJson();

            if (string.IsNullOrEmpty(outPath))
            {
                Console.WriteLine(content);
                return 0;
            }

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(outPath, content, new UTF8Encoding(true));
                Console.WriteLine("已写出：" + Path.GetFullPath(outPath));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("写出失败：" + ex.Message);
                return 2;
            }
        }

        // ---------------------------------------------------------------- rollback

        /// <summary>
        /// 按"本次运行"回滚：读 manifest，把输出文件还原成运行前状态。
        ///
        /// 只回滚**输出文件**，不碰源图。默认模式（写 output/）下源图本来就没动过，
        /// 回滚的意义在于把 output/ 里那批半成品清掉；--in-place 时才是真正的还原。
        /// </summary>
        private static int RollbackCommand(string[] args)
        {
            bool dryRun = false;
            string runDir = null;
            string outputRoot = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                }
                else if (string.Equals(a, "--latest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outputRoot = args[++i];
                }
                else if (!a.StartsWith("-", StringComparison.Ordinal))
                {
                    runDir = a;
                }
            }

            if (string.IsNullOrEmpty(runDir) && !string.IsNullOrEmpty(outputRoot))
            {
                runDir = RunManifestStore.FindLatest(outputRoot);
                if (runDir == null)
                {
                    Console.Error.WriteLine("在 " + outputRoot + " 下找不到运行清单（.jun-runs）。");
                    return 2;
                }
            }

            if (string.IsNullOrEmpty(runDir))
            {
                Console.Error.WriteLine("用法：Jun.Cad.Batch.Cli rollback <运行目录> | --latest <输出目录> [--dry-run]");
                return 2;
            }

            RunManifest manifest = RunManifestStore.Load(runDir);
            if (manifest == null)
            {
                Console.Error.WriteLine("读不到清单：" + Path.Combine(runDir, "manifest.json"));
                return 2;
            }

            Console.WriteLine("回滚运行：" + manifest.Describe());
            Console.WriteLine("模式：" + manifest.Mode + "，输出根：" + manifest.OutputRoot);
            Console.WriteLine();

            List<string> actions = RunManifestStore.Rollback(manifest, dryRun);
            foreach (string action in actions)
            {
                Console.WriteLine("  " + action);
            }

            int failed = actions.FindAll(x => x.StartsWith("失败", StringComparison.Ordinal)).Count;
            int skipped = actions.FindAll(x => x.StartsWith("跳过", StringComparison.Ordinal)).Count;
            int missing = actions.FindAll(x => x.StartsWith("无法回滚", StringComparison.Ordinal)).Count;

            Console.WriteLine();
            Console.WriteLine(string.Format("{0}：还原/删除 {1} 项，跳过 {2} 项，无法回滚 {3} 项，失败 {4} 项。",
                dryRun ? "预演（未改动任何文件）" : "完成",
                actions.Count - skipped - missing - failed, skipped, missing, failed));

            return failed > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- preflight

        private static int PreflightCommand(string[] args)
        {
            string tableSample = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--table-sample", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    tableSample = args[++i];
                }
            }

            Console.WriteLine("前置检查（能力探测）—— 结果只代表 ACadSharp 自洽，不代表 AutoCAD 认可");
            Console.WriteLine();

            List<ProbeResult> results = CapabilityProbe.RunAll(tableSample);
            foreach (ProbeResult r in results)
            {
                Console.WriteLine(string.Format("  [{0,-4}] {1}", r.StatusText, r.Title));
                Console.WriteLine("         " + r.Detail);
                if (!string.IsNullOrEmpty(r.Affects))
                {
                    Console.WriteLine("         影响：" + r.Affects + (r.ElapsedMs > 0 ? string.Format("（{0} ms）", r.ElapsedMs) : string.Empty));
                }
            }

            bool anyFailed = results.Exists(r => r.Status == ProbeStatus.Failed);
            Console.WriteLine();
            Console.WriteLine(anyFailed
                ? "有检查未通过：相关规则会自动降级，降级原因会写进差异预览。"
                : "没有失败项。");

            return anyFailed ? 1 : 0;
        }

        // ---------------------------------------------------------------- preview

        private static void WritePreview(BatchLogger log, List<ChangeRecord> records, RunOptions options, List<string> files)
        {
            log.Section("差异预览");
            log.Info(string.Format("共 {0} 行：将改动 {1}，无需改动 {2}，被挡住 {3}，其中降级 {4}。",
                records.Count,
                records.FindAll(r => r.Status == ChangeStatus.WillChange).Count,
                records.FindAll(r => r.Status == ChangeStatus.NoChange).Count,
                records.FindAll(r => r.Status == ChangeStatus.Blocked).Count,
                records.FindAll(r => r.Degraded).Count));

            const int consoleLimit = 40;
            int shown = 0;
            foreach (ChangeRecord r in records)
            {
                if (shown >= consoleLimit)
                {
                    log.Info(string.Format("  … 另有 {0} 行，见预览文件。", records.Count - shown));
                    break;
                }

                string change = string.IsNullOrEmpty(r.OldValue)
                    ? (r.NewValue ?? string.Empty)
                    : r.OldValue + " → " + (r.NewValue ?? string.Empty);

                string note = r.Degraded
                    ? "  [降级] " + r.Note
                    : (string.IsNullOrEmpty(r.Note) ? string.Empty : "  " + r.Note);

                log.Info(string.Format("  {0,-11} {1}  {2}{3}", r.StatusText, r.Describe(), change, note));
                shown++;
            }

            string path = ResolvePreviewPath(options, files);
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# 差异预览");
                sb.AppendLine();
                sb.AppendLine(string.Format("生成时间：{0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
                sb.AppendLine(string.Format("配置文件：{0}", options.ConfigPath));
                sb.AppendLine(string.Format("文件数：{0}", files.Count));
                sb.AppendLine();
                sb.AppendLine(string.Format("- 将改动：{0}", records.FindAll(r => r.Status == ChangeStatus.WillChange).Count));
                sb.AppendLine(string.Format("- 无需改动：{0}", records.FindAll(r => r.Status == ChangeStatus.NoChange).Count));
                sb.AppendLine(string.Format("- 被挡住：{0}", records.FindAll(r => r.Status == ChangeStatus.Blocked).Count));
                sb.AppendLine(string.Format("- 降级：{0}（降级行会在备注里写明原因）", records.FindAll(r => r.Degraded).Count));
                sb.AppendLine();
                sb.Append(EntitySnapshot.ToMarkdownTable(records, 0));

                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                log.Info("预览文件：" + path);
            }
            catch (Exception ex)
            {
                log.Warn("预览文件写出失败：" + ex.Message);
            }

            log.Warn("本次是 --preview：没有任何文件被写出。");
        }

        private static string ResolvePreviewPath(RunOptions options, List<string> files)
        {
            if (!string.IsNullOrWhiteSpace(options.PreviewOut))
            {
                return options.PreviewOut;
            }

            string dir = options.OutputDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                string first = files.Count > 0 ? files[0] : options.InputPath;
                string baseDir = File.Exists(first)
                    ? Path.GetDirectoryName(Path.GetFullPath(first))
                    : Path.GetFullPath(first);
                dir = Path.Combine(baseDir ?? ".", "output");
            }

            return Path.Combine(dir, string.Format("preview-{0:yyyyMMdd-HHmmss}.md", DateTime.Now));
        }

        // ---------------------------------------------------------------- 文件收集

        private static string ResolveLogPath(RunOptions options, BatchConfig config, int fileCount)
        {
            if (options.NoLog)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(options.LogPath))
            {
                return options.LogPath;
            }

            string configured = config.LogFile;
            if (!string.IsNullOrWhiteSpace(configured)
                && !string.Equals(configured.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                // 相对路径按配置文件所在目录解析，避免"日志跑到启动目录去了"
                if (!Path.IsPathRooted(configured))
                {
                    string configDir = Path.GetDirectoryName(Path.GetFullPath(options.ConfigPath));
                    return Path.Combine(configDir ?? ".", configured);
                }

                return configured;
            }

            if (string.Equals(configured == null ? null : configured.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string baseDir = options.InPlace
                ? (File.Exists(options.InputPath)
                    ? Path.GetDirectoryName(Path.GetFullPath(options.InputPath))
                    : Path.GetFullPath(options.InputPath))
                : (string.IsNullOrEmpty(options.OutputDirectory)
                    ? (File.Exists(options.InputPath)
                        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.InputPath)), "output")
                        : Path.Combine(Path.GetFullPath(options.InputPath), "output"))
                    : Path.GetFullPath(options.OutputDirectory));

            return Path.Combine(baseDir ?? ".", string.Format("Logs/batch-{0:yyyyMMdd-HHmmss}.log", DateTime.Now));
        }

        private static string DescribeOutputMode(RunOptions options)
        {
            if (options.DryRun)
            {
                return "试运行（不写盘）";
            }

            if (options.InPlace)
            {
                return "覆盖源文件（" + (options.CreateBackup ? "自动备份 .bak" : "不备份") + "）";
            }

            return "写入目录 " + (string.IsNullOrEmpty(options.OutputDirectory)
                ? "(输入目录)\\output"
                : options.OutputDirectory);
        }

        // ---------------------------------------------------------------- 帮助

        private static bool IsHelp(string arg)
        {
            return arg == "-h" || arg == "--help" || arg == "/?";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Jun.Cad.Batch.Cli —— 不打开 AutoCAD，直接批量修改 DWG 文件");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  Jun.Cad.Batch.Cli run      --config <config.json> --input <文件或目录> [选项]");
            Console.WriteLine("  Jun.Cad.Batch.Cli inspect  <文件.dwg>");
            Console.WriteLine("  Jun.Cad.Batch.Cli patterns [<acadiso.pat>]");
            Console.WriteLine("  Jun.Cad.Batch.Cli preflight [--table-sample <含表格的.dwg>]");
            Console.WriteLine("  Jun.Cad.Batch.Cli rollback <运行目录>|--latest <输出目录> [--dry-run]");
            Console.WriteLine("  Jun.Cad.Batch.Cli schema   [--out <schema.json>] [--markdown]");
            Console.WriteLine("  Jun.Cad.Batch.Cli types");
            Console.WriteLine();
            Console.WriteLine("输入 \"Jun.Cad.Batch.Cli run --help\" 查看 run 的完整选项。");
        }

        private static void PrintRunUsage()
        {
            Console.WriteLine("用法：Jun.Cad.Batch.Cli run --config <config.json> --input <文件或目录> [选项]");
            Console.WriteLine();
            Console.WriteLine("必填：");
            Console.WriteLine("  --config <path>       配置文件路径");
            Console.WriteLine("  --input  <path>       DWG 文件或所在目录");
            Console.WriteLine();
            Console.WriteLine("输出：");
            Console.WriteLine("  --out <dir>           输出目录（默认 <输入目录>\\output）");
            Console.WriteLine("  --in-place            覆盖源文件（默认不覆盖）");
            Console.WriteLine("  --no-backup           --in-place 时不生成 .bak（默认生成）");
            Console.WriteLine("  --dry-run             跑完整流程但不写盘");
            Console.WriteLine("  --preview             差异预览：逐条列出 文件/空间/句柄/旧值→新值/状态，不写盘");
            Console.WriteLine("  --preview-out <path>  预览报告输出路径（默认 <输出目录>\\preview-时间.md）");
            Console.WriteLine("  --no-manifest         不写运行清单（默认写，用于 rollback）");
            Console.WriteLine("  --skip-unchanged      没有改动时不写出文件");
            Console.WriteLine("  --version <ver>       指定输出版本（AC1032 / 2018 / ...；默认沿用源版本，不安全时提升到 AC1032）");
            Console.WriteLine();
            Console.WriteLine("输入筛选：");
            Console.WriteLine("  --pattern <glob>      文件匹配（默认 *.dwg，可重复）");
            Console.WriteLine("  --recursive           递归子目录");
            Console.WriteLine();
            Console.WriteLine("资源与日志：");
            Console.WriteLine("  --pat <path>          指定填充图案库 .pat");
            Console.WriteLine("  --lin <path>          指定线型库 .lin");
            Console.WriteLine("  --log <path>          日志文件路径");
            Console.WriteLine("  --no-log              不写日志文件");
            Console.WriteLine("  --verbose             把每条明细都打印到控制台");
        }

        // ---------------------------------------------------------------- 选项

        private sealed class RunOptions
        {
            public string ConfigPath { get; private set; }

            public string InputPath { get; private set; }

            public string OutputDirectory { get; private set; }

            public bool InPlace { get; private set; }

            public bool CreateBackup { get; private set; }

            public bool DryRun { get; private set; }

            public bool Preview { get; private set; }

            public bool NoManifest { get; private set; }

            public string PreviewOut { get; private set; }

            public bool SkipUnchanged { get; private set; }

            public bool Recursive { get; private set; }

            public bool Verbose { get; private set; }

            public bool NoLog { get; private set; }

            public string LogPath { get; private set; }

            public string VersionText { get; private set; }

            public string PatternFile { get; private set; }

            public string LinetypeFile { get; private set; }

            public List<string> Patterns { get; private set; }

            public static RunOptions Parse(string[] args)
            {
                var o = new RunOptions
                {
                    CreateBackup = true,
                    Verbose = true,
                    Patterns = new List<string>()
                };

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (IsHelp(a))
                    {
                        return null;
                    }

                    switch (a.Trim().ToLowerInvariant())
                    {
                        case "--config":
                            o.ConfigPath = Next(args, ref i, a);
                            break;
                        case "--input":
                        case "--in":
                            o.InputPath = Next(args, ref i, a);
                            break;
                        case "--out":
                        case "--output":
                            o.OutputDirectory = Next(args, ref i, a);
                            break;
                        case "--in-place":
                            o.InPlace = true;
                            break;
                        case "--no-backup":
                            o.CreateBackup = false;
                            break;
                        case "--backup":
                            o.CreateBackup = true;
                            break;
                        case "--dry-run":
                            o.DryRun = true;
                            break;
                        case "--preview":
                            o.Preview = true;
                            break;
                        case "--preview-out":
                            o.PreviewOut = Next(args, ref i, a);
                            break;
                        case "--no-manifest":
                            o.NoManifest = true;
                            break;
                        case "--skip-unchanged":
                            o.SkipUnchanged = true;
                            break;
                        case "--pattern":
                            string p = Next(args, ref i, a);
                            if (!string.IsNullOrEmpty(p))
                            {
                                o.Patterns.Add(p);
                            }

                            break;
                        case "--recursive":
                        case "-r":
                            o.Recursive = true;
                            break;
                        case "--version":
                        case "--dwg-version":
                            o.VersionText = Next(args, ref i, a);
                            break;
                        case "--pat":
                            o.PatternFile = Next(args, ref i, a);
                            break;
                        case "--lin":
                            o.LinetypeFile = Next(args, ref i, a);
                            break;
                        case "--log":
                            o.LogPath = Next(args, ref i, a);
                            break;
                        case "--no-log":
                            o.NoLog = true;
                            break;
                        case "--verbose":
                        case "-v":
                            o.Verbose = true;
                            break;
                        case "--quiet":
                        case "-q":
                            o.Verbose = false;
                            break;
                        default:
                            if (a.StartsWith("-", StringComparison.Ordinal))
                            {
                                Console.Error.WriteLine("未知选项：" + a);
                                return null;
                            }

                            // 位置参数：第一个当 --input
                            if (string.IsNullOrEmpty(o.InputPath))
                            {
                                o.InputPath = a;
                            }
                            else
                            {
                                Console.Error.WriteLine("多余的参数：" + a);
                                return null;
                            }

                            break;
                    }
                }

                if (string.IsNullOrWhiteSpace(o.ConfigPath))
                {
                    bool created;
                    string error;
                    string found = ConfigLoader.EnsureDefaultConfig(out created, out error);

                    if (created)
                    {
                        Console.WriteLine(string.Format(
                            "未指定 --config，已在程序目录生成默认配置：{0}（所有规则默认关闭）", found));
                    }
                    else if (!string.IsNullOrEmpty(error))
                    {
                        Console.Error.WriteLine(string.Format(
                            "缺少 --config，且无法在程序目录生成 {0}：{1}", ConfigLoader.DefaultFileName, error));
                        return null;
                    }
                    else if (!File.Exists(found))
                    {
                        Console.Error.WriteLine(string.Format(
                            "缺少 --config，且程序目录下没有 {0}。", ConfigLoader.DefaultFileName));
                        return null;
                    }

                    o.ConfigPath = found;
                }

                if (string.IsNullOrWhiteSpace(o.InputPath))
                {
                    Console.Error.WriteLine("缺少 --input（DWG 文件或目录）。");
                    return null;
                }

                if (o.InPlace && !string.IsNullOrEmpty(o.OutputDirectory))
                {
                    Console.Error.WriteLine("--in-place 与 --out 不能同时使用。");
                    return null;
                }

                // 原地覆盖 + 不备份 = 唯一一条"失败即丢图"的路径：写盘要删旧文件再改名，
                // 中间任何一步失败源图就没了，而且没有备份可恢复。
                if (o.InPlace && !o.CreateBackup)
                {
                    Console.Error.WriteLine("--in-place 必须保留备份：去掉 --no-backup。"
                                            + "（原地覆盖时备份是唯一退路）");
                    return null;
                }

                return o;
            }

            private static string Next(string[] args, ref int i, string option)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("选项 " + option + " 缺少参数值。");
                    return null;
                }

                i++;
                return args[i];
            }
        }
    }
}
