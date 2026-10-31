using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Jun.Cad.Batch.Engine.Runs
{
    /// <summary>本次运行里一个文件的处理记录。</summary>
    public sealed class RunManifestEntry
    {
        public string SourcePath { get; set; }

        public string OutputPath { get; set; }

        /// <summary>备份文件路径；为空表示这个文件没写过盘（或没开备份）。</summary>
        public string BackupPath { get; set; }

        /// <summary>写出前目标文件是否存在。false = 本次新建，回滚时应删除。</summary>
        public bool Existed { get; set; }

        public bool Succeeded { get; set; }

        public int Changed { get; set; }

        public string Error { get; set; }
    }

    /// <summary>一条规则在本批运行里的统计（回滚时用来解释"这次到底改了什么"）。</summary>
    public sealed class RunRuleStat
    {
        public string Name { get; set; }

        public string Type { get; set; }

        public int Matched { get; set; }

        public int Changed { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public bool Aborted { get; set; }

        public string AbortReason { get; set; }
    }

    /// <summary>
    /// 一次运行的清单（manifest）。
    ///
    /// 为什么要有它：
    ///   * 现有回滚只到"单个文件"（失败不写盘），批量跑完发现改错了**没有整体回退手段**；
    ///   * `.bak` 散落在各输出目录旁边，时间一长就分不清哪个 .bak 属于哪一次运行；
    ///   * 回滚需要知道"这个输出文件之前是否存在"——新建的要删掉，覆盖的要还原，
    ///     只靠 .bak 判断不了（新建的文件也有 .bak 吗？没有）。
    ///
    /// 清单落在 <c>&lt;输出目录&gt;\.jun-runs\&lt;时间戳&gt;\manifest.json</c>，
    /// 备份落在同目录 <c>backups\</c>。回滚按清单逐条还原，不猜。
    /// </summary>
    public sealed class RunManifest
    {
        public string RunId { get; set; }

        /// <summary>清单所在目录（不写进 JSON，运行时才有）。</summary>
        [JsonIgnore]
        public string RunDirectory { get; set; }

        public DateTime Timestamp { get; set; }

        public string ConfigPath { get; set; }

        /// <summary>输出模式：output / in-place。</summary>
        public string Mode { get; set; }

        public string OutputRoot { get; set; }

        public List<RunManifestEntry> Entries { get; set; }

        public List<RunRuleStat> Rules { get; set; }

        public int TotalChanged { get; set; }

        public RunManifest()
        {
            Entries = new List<RunManifestEntry>();
            Rules = new List<RunRuleStat>();
        }

        public string Describe()
        {
            return string.Format("{0} · {1} 个文件 · 改动 {2} 处",
                Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Entries.Count, TotalChanged);
        }
    }

    /// <summary>清单的读写与回滚。</summary>
    public static class RunManifestStore
    {
        public const string RunFolderName = ".jun-runs";

        private const string ManifestFileName = "manifest.json";

        /// <summary>为一次运行建目录（<c>&lt;root&gt;\.jun-runs\&lt;id&gt;</c>）。</summary>
        public static string CreateRunDirectory(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                outputRoot = ".";
            }

            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(outputRoot, RunFolderName, id);

            int suffix = 1;
            while (Directory.Exists(dir))
            {
                dir = Path.Combine(outputRoot, RunFolderName, id + "-" + suffix);
                suffix++;
            }

            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string BackupDirectoryOf(string runDirectory)
        {
            return Path.Combine(runDirectory, "backups");
        }

        public static void Save(RunManifest manifest, string runDirectory)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(runDirectory))
            {
                return;
            }

            manifest.RunId = Path.GetFileName(runDirectory);

            string path = Path.Combine(runDirectory, ManifestFileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest, Formatting.Indented),
                new UTF8Encoding(true));
        }

        public static RunManifest Load(string runDirectory)
        {
            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                return null;
            }

            string path = Path.Combine(runDirectory, ManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<RunManifest>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>找输出目录下最近一次运行。</summary>
        public static string FindLatest(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                return null;
            }

            string root = Path.Combine(outputRoot, RunFolderName);
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, ManifestFileName)))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>
        /// 按清单回滚。返回逐条动作说明（成功/跳过/失败都会写一行）。
        /// </summary>
        public static List<string> Rollback(RunManifest manifest, bool dryRun)
        {
            var actions = new List<string>();
            if (manifest == null || manifest.Entries == null)
            {
                actions.Add("清单为空，没有可回滚的内容。");
                return actions;
            }

            foreach (RunManifestEntry entry in manifest.Entries)
            {
                if (!entry.Succeeded)
                {
                    actions.Add("跳过（本次未写出）：" + entry.OutputPath);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.OutputPath))
                {
                    continue;
                }

                try
                {
                    if (!entry.Existed)
                    {
                        if (File.Exists(entry.OutputPath))
                        {
                            if (!dryRun)
                            {
                                File.Delete(entry.OutputPath);
                            }

                            actions.Add("删除（本次新建）：" + entry.OutputPath);
                        }
                        else
                        {
                            actions.Add("跳过（文件已不存在）：" + entry.OutputPath);
                        }

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(entry.BackupPath) || !File.Exists(entry.BackupPath))
                    {
                        actions.Add("无法回滚（备份缺失）：" + entry.OutputPath);
                        continue;
                    }

                    if (!dryRun)
                    {
                        File.Copy(entry.BackupPath, entry.OutputPath, true);
                    }

                    actions.Add("还原：" + entry.OutputPath);
                }
                catch (Exception ex)
                {
                    actions.Add(string.Format("失败：{0}（{1}）", entry.OutputPath, ex.Message));
                }
            }

            return actions;
        }
    }
}
