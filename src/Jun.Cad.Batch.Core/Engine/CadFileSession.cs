using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ACadSharp;
using ACadSharp.IO;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 单个 DWG 文件的生命周期：读入 → （规则修改内存模型）→ 版本规范化 → 写出 → 原子替换。
    ///
    /// 为什么需要"会话"这一层：
    ///   AutoCAD 版把回滚交给事务，离线版没有事务。这里用**文件级**回滚替代：
    ///   规则失败时整个 <see cref="Document"/> 丢弃，源文件一个字节都没动过，
    ///   所以"失败即无副作用"是天然成立的——前提是**修改期间绝不写盘**。
    ///   写盘只发生在 <see cref="Save"/> 里，且先写临时文件再替换。
    ///
    /// 版本策略（实测得出的硬约束，见 docs/acadsharp-port-feasibility.md 第 3 节）：
    ///   * AC1021（2007）读得出、写不出——DwgWriter 直接抛不支持；
    ///   * AC1015（2000）写得出，但 AutoCAD 打不开（ErrorStatus=53）；
    ///   * AC1018 / AC1024 / AC1027 / AC1032 往返正常。
    /// 因此默认把输出统一提升到 <see cref="DefaultOutputVersion"/>（AC1032 / 2018），
    /// 只有用户显式指定且该版本属于"已知可写"集合时才沿用。
    /// </summary>
    public sealed class CadFileSession : IDisposable
    {
        /// <summary>默认输出格式：AutoCAD 2018（AC1032）。</summary>
        public const ACadVersion DefaultOutputVersion = ACadVersion.AC1032;

        private static readonly ACadVersion[] SafeWriteVersions =
        {
            ACadVersion.AC1018,
            ACadVersion.AC1024,
            ACadVersion.AC1027,
            ACadVersion.AC1032
        };

        private readonly List<string> _readNotices = new List<string>();
        private readonly List<string> _writeNotices = new List<string>();
        private bool _disposed;

        private CadFileSession()
        {
        }

        /// <summary>源文件路径（绝对路径）。</summary>
        public string SourcePath { get; private set; }

        /// <summary>读入时文件自身的版本。</summary>
        public ACadVersion SourceVersion { get; private set; }

        /// <summary>写出时将使用的版本。</summary>
        public ACadVersion OutputVersion { get; private set; }

        /// <summary>版本是否被提升过（日志/汇总里要说明，否则用户会以为文件版本没变）。</summary>
        public bool VersionUpgraded
        {
            get { return SourceVersion != OutputVersion; }
        }

        public CadDocument Document { get; private set; }

        /// <summary>读入过程中的告警（损坏对象、不支持的实体等）。</summary>
        public IReadOnlyList<string> ReadNotices
        {
            get { return _readNotices; }
        }

        /// <summary>最近一次写出的告警。</summary>
        public IReadOnlyList<string> WriteNotices
        {
            get { return _writeNotices; }
        }

        /// <summary>
        /// 读入一个 DWG 文件。
        /// </summary>
        /// <param name="outputVersion">
        /// 期望的输出版本；null 表示"沿用源版本，但不安全的版本会被提升"。
        /// </param>
        public static CadFileSession Open(string path, ACadVersion? outputVersion)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("文件路径为空。", "path");
            }

            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("文件不存在：" + full, full);
            }

            var session = new CadFileSession { SourcePath = full };

            CadDocument doc = DwgReader.Read(full, (sender, e) =>
            {
                if (e == null)
                {
                    return;
                }

                // Error 与 Warning 都要收：Warning 里往往藏着"某个对象没读出来"这种真问题
                if (e.NotificationType == NotificationType.Error || e.NotificationType == NotificationType.Warning)
                {
                    session._readNotices.Add(string.Format("{0}: {1}", e.NotificationType, e.Message));
                }
            });

            if (doc == null)
            {
                throw new InvalidDataException("读取失败，未返回文档对象：" + full);
            }

            session.Document = doc;
            session.SourceVersion = doc.Header.Version;
            session.OutputVersion = ResolveOutputVersion(session.SourceVersion, outputVersion);

            // 读入后立刻归一化 $PSTYLEMODE，见方法注释——这一步漏了会毁掉打印样式
            session.NormalizePlotStyleMode();
            return session;
        }

        /// <summary>
        /// 把 <c>$PSTYLEMODE</c> 归一成语义值（0 = 命名 STB / 1 = 颜色相关 CTB）。
        ///
        /// **ACadSharp 对这个变量的读和写是不对称的**（AutoCAD 2025 内核实测）：
        ///
        /// | AutoCAD 侧 | 库读出来 | 库写入应给 |
        /// |---|---|---|
        /// | 1（颜色相关，CTB） | **8192** | 1 |
        /// | 0（命名，STB）     | 0       | 0 |
        ///
        /// 把读到的 8192 原样写回去，AutoCAD 读到的就是 **0**——图纸从"颜色相关"
        /// 变成"命名"，打印对话框的打印样式表下拉框就只列 STB，
        /// 用户自建的 CTB 目录（哪怕已经加进支持文件搜索路径）一个都不显示。
        ///
        /// 这条不是"顺手改改"：`PSTYLEMODE` 决定整张图用哪套打印样式表，
        /// 一旦被翻掉，图纸的打印效果全变，而画面上看不出任何异常。
        /// </summary>
        public void NormalizePlotStyleMode()
        {
            if (Document == null)
            {
                return;
            }

            Document.Header.PlotStyleMode = NormalizePlotStyleModeValue(Document.Header.PlotStyleMode);
        }

        /// <summary>
        /// 把库读到的原始值折成语义值：0 = 命名（STB），1 = 颜色相关（CTB）。
        ///
        /// 0 / 1 原样返回（库将来修好读取后就是这两个）；其余一律按"非零即 CTB"处理。
        /// 抽成纯函数是为了能单测——这个 bug 的代价是"图纸打印样式全变但画面看不出来"，
        /// 必须有个不需要真图、不需要 AutoCAD 的断言盯着。
        /// </summary>
        public static short NormalizePlotStyleModeValue(short raw)
        {
            return raw == 0 || raw == 1 ? raw : (short)1;
        }

        /// <summary>
        /// 决定输出用哪个版本。
        ///
        /// 用户显式指定 → 必须属于已知可写集合，否则提升到默认版本；
        /// 用户没指定 → 源版本可写就沿用，不可写就提升。
        /// </summary>
        public static ACadVersion ResolveOutputVersion(ACadVersion source, ACadVersion? requested)
        {
            if (requested.HasValue)
            {
                return IsSafeWriteVersion(requested.Value) ? requested.Value : DefaultOutputVersion;
            }

            return IsSafeWriteVersion(source) ? source : DefaultOutputVersion;
        }

        /// <summary>该版本能否安全写出（AC1021 不能写、AC1015 写出后 AutoCAD 打不开）。</summary>
        public static bool IsSafeWriteVersion(ACadVersion version)
        {
            foreach (ACadVersion v in SafeWriteVersions)
            {
                if (v == version)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把文档版本改写成 <see cref="OutputVersion"/>。必须在写出前调用。
        /// </summary>
        public void ApplyOutputVersion()
        {
            if (Document == null)
            {
                throw new InvalidOperationException("文档尚未读入。");
            }

            Document.Header.Version = OutputVersion;
        }

        /// <summary>
        /// 写出到目标路径。
        ///
        /// 原子性：先写同目录临时文件（同卷才能保证 Move 是原子的），
        /// 成功后再替换目标。替换前把原文件备份为 <c>xxx.dwg.bak</c>（<paramref name="createBackup"/> 为 true 时）。
        /// 中途失败时目标文件保持原样，临时文件被清理。
        /// </summary>
        public void Save(string outputPath, bool createBackup)
        {
            Save(outputPath, createBackup, null);
        }

        /// <summary>
        /// 写出到目标路径，并把备份放到指定目录（用于"按本次运行回滚"）。
        /// </summary>
        /// <param name="backupDirectory">
        /// 备份目录；为空时沿用"目标同目录 xxx.dwg.bak"的老行为。
        /// 指定后备份文件名为 <c>&lt;目录&gt;\&lt;原文件名&gt;.bak</c>，同一批运行的所有备份聚在一起，
        /// 回滚时不用满盘找 .bak。
        /// </param>
        public void Save(string outputPath, bool createBackup, string backupDirectory)
        {
            if (Document == null)
            {
                throw new InvalidOperationException("文档尚未读入。");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径为空。", "outputPath");
            }

            string target = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ApplyOutputVersion();
            _writeNotices.Clear();

            string temp = target + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                DwgWriter.Write(temp, Document, null, (sender, e) =>
                {
                    if (e == null)
                    {
                        return;
                    }

                    if (e.NotificationType == NotificationType.Error || e.NotificationType == NotificationType.Warning)
                    {
                        _writeNotices.Add(string.Format("{0}: {1}", e.NotificationType, e.Message));
                    }
                });

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    throw new IOException("写出后临时文件为空，已放弃替换：" + temp);
                }

                string backupPath = null;
                if (createBackup && !string.IsNullOrWhiteSpace(backupDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(backupDirectory);
                        backupPath = Path.Combine(backupDirectory, Path.GetFileName(target) + ".bak");
                    }
                    catch
                    {
                        backupPath = null;
                    }
                }

                ReplaceAtomically(temp, target, createBackup, backupPath);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>默认备份路径：与目标同目录、同名加 .bak（已存在时加时间戳，避免覆盖上一次备份）。</summary>
        public static string BuildBackupPath(string target)
        {
            string candidate = target + ".bak";
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            string stamped = string.Format("{0}.{1:yyyyMMdd-HHmmss}.bak", target, DateTime.Now);
            return stamped;
        }

        private static void ReplaceAtomically(string temp, string target, bool createBackup, string explicitBackup)
        {
            if (!File.Exists(target))
            {
                File.Move(temp, target);
                return;
            }

            if (createBackup)
            {
                string backup = string.IsNullOrWhiteSpace(explicitBackup) ? BuildBackupPath(target) : explicitBackup;
                try
                {
                    // File.Replace 一次完成"备份旧文件 + 用新文件替换"，比 Copy+Delete 少一个中间态
                    File.Replace(temp, target, backup, true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    // 某些文件系统不支持 Replace，退化成手工三步
                }
                catch (IOException)
                {
                    // 目标被占用等：退化成手工三步，失败时抛出更明确的异常
                }

                File.Copy(target, backup, true);
                File.Delete(target);

                try
                {
                    File.Move(temp, target);
                }
                catch
                {
                    // 回退路径上目标已被删除：把备份拷回去，别让用户丢图
                    try
                    {
                        File.Copy(backup, target, true);
                    }
                    catch
                    {
                        // 恢复也失败时保留备份文件，异常照常抛给调用方
                    }

                    throw;
                }

                return;
            }

            // 不备份：用 File.Replace（第三参数为 null 表示不留备份）保持原子性。
            // 老写法是 Delete + Move，中间目标文件不存在，Move 一失败源图就没了。
            try
            {
                File.Replace(temp, target, null, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // 某些文件系统不支持 Replace，只能退化成 Delete + Move（已在文档里标为已知窗口）
            }
            catch (IOException)
            {
            }

            File.Delete(target);
            File.Move(temp, target);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        /// <summary>版本号的人话名（日志里写 AC1032 没人看得懂）。</summary>
        public static string DescribeVersion(ACadVersion version)
        {
            switch (version)
            {
                case ACadVersion.AC1015: return "AC1015 (2000)";
                case ACadVersion.AC1018: return "AC1018 (2004)";
                case ACadVersion.AC1021: return "AC1021 (2007)";
                case ACadVersion.AC1024: return "AC1024 (2010)";
                case ACadVersion.AC1027: return "AC1027 (2013)";
                case ACadVersion.AC1032: return "AC1032 (2018)";
                case ACadVersion.AC1009: return "AC1009 (R12)";
                case ACadVersion.AC1014: return "AC1014 (R14)";
                case ACadVersion.AC1012: return "AC1012 (R13)";
                default: return version.ToString();
            }
        }

        /// <summary>解析命令行 / 配置里的版本字符串（"AC1032" / "2018" / "R2018" 都认）。</summary>
        public static bool TryParseVersion(string text, out ACadVersion version)
        {
            version = DefaultOutputVersion;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string t = text.Trim().ToUpperInvariant();

            switch (t)
            {
                case "2000": case "R2000": version = ACadVersion.AC1015; return true;
                case "2004": case "R2004": version = ACadVersion.AC1018; return true;
                case "2007": case "R2007": version = ACadVersion.AC1021; return true;
                case "2010": case "R2010": version = ACadVersion.AC1024; return true;
                case "2013": case "R2013": version = ACadVersion.AC1027; return true;
                case "2018": case "R2018": version = ACadVersion.AC1032; return true;
            }

            try
            {
                version = (ACadVersion)Enum.Parse(typeof(ACadVersion), t, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把读入告警拼成一段可读文本（汇总用）。</summary>
        public string DescribeReadNotices(int maxLines)
        {
            if (_readNotices.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            int n = Math.Min(maxLines <= 0 ? _readNotices.Count : maxLines, _readNotices.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append("；");
                }

                sb.Append(_readNotices[i]);
            }

            if (n < _readNotices.Count)
            {
                sb.AppendFormat("（另有 {0} 条，见日志）", _readNotices.Count - n);
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Document = null;
        }
    }
}
