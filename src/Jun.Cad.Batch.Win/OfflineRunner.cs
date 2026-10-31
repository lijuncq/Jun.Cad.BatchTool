using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Engine.Preview;
using Jun.Cad.Batch.Engine.Runs;

namespace Jun.Cad.Batch.Win
{
    /// <summary>
    /// 离线执行：按配置处理一批 dwg 文件，写到输出目录。
    ///
    /// 与 AutoCAD 版的根本差别：那边把配置排回命令上下文、改的是**当前打开文档**；
    /// 这边自己开文件、改内存、写盘，全程不需要 AutoCAD。
    ///
    /// 回滚边界是文件（见 <see cref="CadFileSession"/>）：单个文件处理途中抛异常就不写出，
    /// 源文件保持原样；已处理完的文件不受影响。
    /// </summary>
    internal static class OfflineRunner
    {
        /// <summary>回报一次进度（进度回调为空时什么都不做）。</summary>
        private static void Report(IProgress<BatchProgress> progress, int index, int total,
            string fileName, string phase, bool finished = false)
        {
            if (progress == null)
            {
                return;
            }

            progress.Report(new BatchProgress
            {
                Index = index,
                Total = total,
                FileName = fileName,
                Phase = phase,
                Finished = finished
            });
        }

        /// <summary>最近一次界面运行的清单目录（供「回滚上次」使用）。</summary>
        public static string LastRunDirectory { get; private set; }

        /// <summary>执行并返回可读的执行报告（日志原文 + 汇总）。</summary>
        public static string Run(BatchConfig config, string configPath, IList<string> files, string outputDir)
        {
            return Run(config, configPath, files, outputDir, null);
        }

        /// <summary>
        /// 执行并逐文件回报进度。
        ///
        /// 进度回调在**工作线程**上调用（界面用 `Task.Run` 起后台线程，靠 `Progress&lt;T&gt;` 封送回 UI），
        /// 所以这里只做"报告第几个文件"，不碰任何界面对象。
        /// </summary>
        public static string Run(BatchConfig config, string configPath, IList<string> files, string outputDir,
            IProgress<BatchProgress> progress)
        {
            var report = new StringBuilder();

            int ok = 0;
            int failed = 0;
            int changed = 0;

            var captured = new StringWriter();
            var resources = new ResourceLocator();

            // 运行清单：与命令行同一套（.jun-runs\<时间戳>），界面跑完也能用 rollback 回滚
            RunManifest manifest = null;
            string runDirectory = null;
            try
            {
                runDirectory = RunManifestStore.CreateRunDirectory(outputDir);
                manifest = new RunManifest
                {
                    RunDirectory = runDirectory,
                    Timestamp = DateTime.Now,
                    ConfigPath = configPath,
                    Mode = "output",
                    OutputRoot = Path.GetFullPath(outputDir)
                };
            }
            catch
            {
                manifest = null;
            }

            int index = 0;

            using (var log = new BatchLogger(captured, null, config.Verbose))
            {
                foreach (string file in files)
                {
                    index++;
                    Report(progress, index, files.Count, Path.GetFileName(file), "执行");

                    captured.WriteLine();
                    captured.WriteLine("======== " + Path.GetFileName(file) + " ========");

                    try
                    {
                        using (CadFileSession session = CadFileSession.Open(file, null))
                        {
                            var runner = new BatchRunner(config, configPath, log, resources);
                            BatchRunSummary summary = runner.Run(session.Document);
                            runner.WriteSummary(summary);

                            // 与命令行同一套语义：规则中止默认丢弃该文件（OnRuleFailure=WritePartial 才写出部分改动）
                            bool writePartial = config != null
                                && string.Equals(config.OnRuleFailure, "WritePartial", StringComparison.OrdinalIgnoreCase);

                            if (!writePartial && summary.Results.Any(r => r.Aborted))
                            {
                                failed++;
                                captured.WriteLine("丢弃（规则中止，未写盘）：" + Path.GetFileName(file)
                                    + " —— " + string.Join("；", summary.Results
                                        .Where(r => r.Aborted)
                                        .Select(r => r.RuleName + "：" + (r.AbortReason ?? "中止"))
                                        .ToArray()));
                                continue;
                            }

                            string target = ResolveOutputPath(file, outputDir);
                            bool existed = File.Exists(target);
                            string backupDir = manifest == null
                                ? null
                                : RunManifestStore.BackupDirectoryOf(manifest.RunDirectory);

                            session.Save(target, true, backupDir);
                            captured.WriteLine("已写出：" + target);

                            if (manifest != null)
                            {
                                manifest.Entries.Add(new RunManifestEntry
                                {
                                    SourcePath = file,
                                    OutputPath = target,
                                    BackupPath = existed
                                        ? Path.Combine(backupDir, Path.GetFileName(target) + ".bak")
                                        : null,
                                    Existed = existed,
                                    Succeeded = true,
                                    Changed = summary.TotalChanged
                                });

                                manifest.TotalChanged += summary.TotalChanged;
                            }

                            changed += summary.TotalChanged;
                            ok++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        captured.WriteLine("失败：" + ex.Message);
                    }
                }
            }

            if (manifest != null && runDirectory != null)
            {
                try
                {
                    RunManifestStore.Save(manifest, runDirectory);
                    LastRunDirectory = runDirectory;
                }
                catch
                {
                    LastRunDirectory = null;
                }
            }

            Report(progress, files.Count, files.Count, string.Empty, "执行", true);

            report.AppendLine(string.Format("共 {0} 个文件：成功 {1} 个，失败 {2} 个，累计改动 {3} 处。",
                files.Count, ok, failed, changed));
            report.AppendLine("输出目录：" + outputDir);
            if (!string.IsNullOrEmpty(LastRunDirectory))
            {
                report.AppendLine("运行清单：" + Path.Combine(LastRunDirectory, "manifest.json"));
            }

            report.AppendLine();
            report.Append(captured.ToString());

            return report.ToString();
        }

        /// <summary>
        /// 差异预览：跑完整流程但**不写盘**，把每条规则要改的东西收集回来。
        ///
        /// 与 <see cref="Run"/> 的唯一区别是"不 Save"——规则照跑、内存照改，
        /// 改完的文档直接丢掉。所以预览和实际执行看到的是同一段代码的结果，
        /// 不存在"预览说会改 A、执行改了 B"这种漂移。
        /// </summary>
        public static List<ChangeRecord> Preview(
            BatchConfig config, string configPath, IList<string> files, out string logText)
        {
            return Preview(config, configPath, files, out logText, null);
        }

        /// <summary>预览并逐文件回报进度（`Phase = "预览"`）。</summary>
        public static List<ChangeRecord> Preview(
            BatchConfig config, string configPath, IList<string> files, out string logText,
            IProgress<BatchProgress> progress)
        {
            var records = new List<ChangeRecord>();
            var captured = new StringWriter();
            var resources = new ResourceLocator();

            int index = 0;

            using (var log = new BatchLogger(captured, null, config.Verbose))
            {
                foreach (string file in files)
                {
                    index++;
                    Report(progress, index, files.Count, Path.GetFileName(file), "执行");

                    captured.WriteLine();
                    captured.WriteLine("======== " + Path.GetFileName(file) + " ========");

                    try
                    {
                        using (CadFileSession session = CadFileSession.Open(file, null))
                        {
                            var sink = new PreviewSink();
                            var runner = new BatchRunner(config, configPath, log, resources, sink)
                            {
                                FileLabel = Path.GetFileName(file)
                            };

                            BatchRunSummary summary = runner.Run(session.Document);
                            runner.WriteSummary(summary);
                            records.AddRange(sink.Records);
                        }
                    }
                    catch (Exception ex)
                    {
                        captured.WriteLine("失败：" + ex.Message);
                    }
                }
            }

            Report(progress, files.Count, files.Count, string.Empty, "预览", true);

            logText = captured.ToString();
            return records;
        }

        /// <summary>
        /// 输出路径：默认"输出目录\原文件名"。若与源文件是同一个文件（输出目录就是源目录），
        /// 改名为 <c>原名_out.dwg</c>，避免覆盖源文件——离线工具默认不碰原图。
        /// </summary>
        private static string ResolveOutputPath(string sourceFile, string outputDir)
        {
            string name = Path.GetFileName(sourceFile);
            string candidate = Path.Combine(outputDir, name);

            bool sameFile;
            try
            {
                sameFile = string.Equals(
                    Path.GetFullPath(candidate),
                    Path.GetFullPath(sourceFile),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                sameFile = false;
            }

            if (!sameFile)
            {
                return candidate;
            }

            return Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourceFile) + "_out.dwg");
        }
    }
}
