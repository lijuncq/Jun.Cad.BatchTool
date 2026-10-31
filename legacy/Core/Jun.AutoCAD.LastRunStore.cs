using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 上一次批处理的记录，供 BATCHAGAIN（再次执行）使用。
    ///
    /// 为什么单独存文件而不是只放内存：AutoCAD 重启后仍然要能"再来一次"，
    /// 否则这个命令只能在同一次会话里用，价值减半。
    ///
    /// 存储位置：%APPDATA%\Jun.AutoCAD.BatchTool\last-run.txt
    /// 格式用最简单的 key=value 行文本（不引 JSON，避免多一层依赖与解析失败风险）。
    /// </summary>
    public sealed class LastRunInfo
    {
        public string ConfigPath { get; set; }

        public DateTime Timestamp { get; set; }

        public string Drawing { get; set; }

        public int Matched { get; set; }

        public int Changed { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public bool IsValid
        {
            get { return !string.IsNullOrWhiteSpace(ConfigPath); }
        }

        public string Describe()
        {
            return string.Format("{0:yyyy-MM-dd HH:mm:ss} 执行于「{1}」：匹配 {2} / 成功 {3} / 跳过 {4} / 失败 {5}",
                Timestamp,
                string.IsNullOrEmpty(Drawing) ? "(未知图形)" : Drawing,
                Matched, Changed, Skipped, Failed);
        }
    }

    /// <summary>上一次批处理记录的读写。</summary>
    public static class LastRunStore
    {
        private static readonly object SyncRoot = new object();

        /// <summary>记录文件路径；%APPDATA% 不可用时回退到 %TEMP%。</summary>
        public static string FilePath
        {
            get
            {
                try
                {
                    string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (string.IsNullOrEmpty(baseDir))
                    {
                        baseDir = Path.GetTempPath();
                    }

                    return Path.Combine(baseDir, "Jun.AutoCAD.BatchTool", "last-run.txt");
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>保存本次执行结果（任何异常都不影响主流程）。</summary>
        public static void Save(string configPath, BatchRunSummary summary, string drawingName)
        {
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(configPath))
                {
                    return;
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder();
                sb.AppendLine("ConfigPath=" + configPath);
                sb.AppendLine("Timestamp=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Drawing=" + (drawingName ?? string.Empty));
                sb.AppendLine("Matched=" + (summary == null ? 0 : summary.TotalMatched));
                sb.AppendLine("Changed=" + (summary == null ? 0 : summary.TotalChanged));
                sb.AppendLine("Skipped=" + (summary == null ? 0 : summary.TotalSkipped));
                sb.AppendLine("Failed=" + (summary == null ? 0 : summary.TotalFailed));

                lock (SyncRoot)
                {
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                }
            }
            catch
            {
                // 记录失败不影响批处理本身
            }
        }

        /// <summary>读取上次记录；不存在或损坏时返回 null。</summary>
        public static LastRunInfo Load()
        {
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    map[key] = value;
                }

                var info = new LastRunInfo();
                info.ConfigPath = Get(map, "ConfigPath");
                info.Drawing = Get(map, "Drawing");
                info.Matched = GetInt(map, "Matched");
                info.Changed = GetInt(map, "Changed");
                info.Skipped = GetInt(map, "Skipped");
                info.Failed = GetInt(map, "Failed");

                DateTime ts;
                info.Timestamp = DateTime.TryParse(Get(map, "Timestamp"), out ts) ? ts : DateTime.MinValue;

                return info.IsValid ? info : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string value;
            return map.TryGetValue(key, out value) ? value : null;
        }

        private static int GetInt(Dictionary<string, string> map, string key)
        {
            int n;
            return int.TryParse(Get(map, key), out n) ? n : 0;
        }
    }
}
