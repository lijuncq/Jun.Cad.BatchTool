using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ACadSharp.Entities;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 资源定位：填充图案库（.pat）与线型库（.lin）。
    ///
    /// 为什么需要它：AutoCAD 版靠内核"按名字去查 acad.pat / acadiso.lin"，
    /// 离线工具没有内核，**必须自己带图案与线型定义**，否则换图案/建线型都无从谈起。
    ///
    /// 查找顺序（第一个命中的赢）：
    ///   1. 命令行 / 配置显式指定的路径（<see cref="PatternFile"/> / <see cref="LinetypeFile"/>）
    ///   2. 程序目录下的 <c>Resources\acadiso.pat</c>、<c>Resources\acadiso.lin</c>
    ///   3. 本机 AutoCAD 安装目录（仅作为开发机便利，生产环境不应依赖）
    /// </summary>
    public sealed class ResourceLocator
    {
        private readonly Dictionary<string, HatchPattern> _patterns =
            new Dictionary<string, HatchPattern>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, LineTypeDefinition> _linetypes =
            new Dictionary<string, LineTypeDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>acad.pat（英制图案库）里出现过的图案名，只用于"这个名字在英制图纸里认不认得出"。</summary>
        private readonly HashSet<string> _imperialPatternNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _patternsLoaded;
        private bool _linetypesLoaded;
        private bool _imperialLoaded;

        /// <summary>显式指定的 .pat 路径。</summary>
        public string PatternFile { get; set; }

        /// <summary>
        /// 显式指定的英制图案库（acad.pat）路径。
        /// 只影响 <see cref="RequiresMetric"/> 的判断，不参与"按名字取图案定义"。
        /// </summary>
        public string ImperialPatternFile { get; set; }

        /// <summary>显式指定的 .lin 路径。</summary>
        public string LinetypeFile { get; set; }

        /// <summary>图案库是否可用。</summary>
        /// <summary>实际加载成功的图案库文件（按加载顺序）。为空表示一个都没加载到。</summary>
        public IList<string> LoadedPatternFiles
        {
            get { return _loadedPatternFiles; }
        }

        /// <summary>实际加载成功的线型库文件。</summary>
        public IList<string> LoadedLinetypeFiles
        {
            get { return _loadedLinetypeFiles; }
        }

        private readonly List<string> _loadedPatternFiles = new List<string>();
        private readonly List<string> _loadedLinetypeFiles = new List<string>();

        public bool HasPatterns
        {
            get
            {
                EnsurePatterns();
                return _patterns.Count > 0;
            }
        }

        /// <summary>按名字取图案定义（找不到返回 null）。</summary>
        public HatchPattern GetPattern(string name)
        {
            EnsurePatterns();
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            HatchPattern p;
            return _patterns.TryGetValue(name.Trim(), out p) ? p : null;
        }

        /// <summary>按名字取线型定义（找不到返回 null）。</summary>
        public LineTypeDefinition GetLinetype(string name)
        {
            EnsureLinetypes();
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            LineTypeDefinition d;
            return _linetypes.TryGetValue(name.Trim(), out d) ? d : null;
        }

        /// <summary>是否加载到了英制图案库（acad.pat）。</summary>
        public bool HasImperialPatternLibrary
        {
            get
            {
                EnsureImperialPatterns();
                return _imperialPatternNames.Count > 0;
            }
        }

        /// <summary>
        /// 这个图案名是不是"只有公制图案库（acadiso.pat）里才有"。
        ///
        /// 为什么需要它：AutoCAD 按图形的 <c>$MEASUREMENT</c> 选图案库——
        /// 0（英制）读 <c>acad.pat</c>，1（公制）读 <c>acadiso.pat</c>。
        /// 实测（AutoCAD 2025 内核）：<c>$MEASUREMENT=0</c> 的图上
        /// <c>(setvar "HPNAME" "JIS_LC_8A")</c> 直接报"无效的图案名称"，
        /// 而 <c>ANSI31</c> 正常——因为 JIS 系列只存在于 acadiso.pat。
        /// 结果就是：填充画得出来，但属性面板的图案列表里找不到这个名字。
        ///
        /// 判定口径：
        ///   * 名字不在本工具的图案库里 → 无从判断，返回 false（不去猜）；
        ///   * 没加载到 acad.pat → 保守返回 true（本工具用的是 acadiso.pat，
        ///     只有公制图纸才保证认得它写出去的每个名字）。
        /// </summary>
        public bool RequiresMetric(string patternName)
        {
            EnsurePatterns();
            EnsureImperialPatterns();

            if (string.IsNullOrWhiteSpace(patternName))
            {
                return false;
            }

            string name = patternName.Trim();
            if (!_patterns.ContainsKey(name))
            {
                return false;
            }

            if (_imperialPatternNames.Count == 0)
            {
                return true;
            }

            return !_imperialPatternNames.Contains(name);
        }

        public IEnumerable<string> PatternNames
        {
            get
            {
                EnsurePatterns();
                return _patterns.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            }
        }

        // ---------------- 图案 ----------------

        private void EnsurePatterns()
        {
            if (_patternsLoaded)
            {
                return;
            }

            _patternsLoaded = true;

            foreach (string path in CandidateFiles(PatternFile, "acadiso.pat", "acad.pat"))
            {
                try
                {
                    int loaded = 0;
                    foreach (HatchPattern p in HatchPattern.LoadFrom(path))
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.Name))
                        {
                            continue;
                        }

                        if (!_patterns.ContainsKey(p.Name))
                        {
                            _patterns[p.Name] = p;
                            loaded++;
                        }
                    }

                    if (loaded > 0)
                    {
                        _loadedPatternFiles.Add(path);
                    }
                }
                catch
                {
                    // 单个库文件解析失败不影响其它候选
                }
            }
        }

        /// <summary>
        /// 单独加载 acad.pat，只收名字（不收定义）。
        /// 与 <see cref="EnsurePatterns"/> 分开是必须的：主库按"先 acadiso.pat"顺序合并，
        /// 混在一起就分不清某个名字到底来自哪份文件。
        /// </summary>
        private void EnsureImperialPatterns()
        {
            if (_imperialLoaded)
            {
                return;
            }

            _imperialLoaded = true;

            // 与 LogFile 一致：显式写 "none" 表示"别去找"，便于把"没有英制库"这条分支测出来
            if (!string.IsNullOrWhiteSpace(ImperialPatternFile)
                && string.Equals(ImperialPatternFile.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (string path in CandidateFiles(ImperialPatternFile, "acad.pat"))
            {
                try
                {
                    foreach (HatchPattern p in HatchPattern.LoadFrom(path))
                    {
                        if (p != null && !string.IsNullOrWhiteSpace(p.Name))
                        {
                            _imperialPatternNames.Add(p.Name);
                        }
                    }
                }
                catch
                {
                    // 解析失败就当没这份库，判定会走"保守"分支
                }
            }
        }

        // ---------------- 线型 ----------------

        private void EnsureLinetypes()
        {
            if (_linetypesLoaded)
            {
                return;
            }

            _linetypesLoaded = true;

            foreach (string path in CandidateFiles(LinetypeFile, "acadiso.lin", "acad.lin"))
            {
                try
                {
                    foreach (LineTypeDefinition d in LinetypeParser.ParseFile(path))
                    {
                        if (!_linetypes.ContainsKey(d.Name))
                        {
                            _linetypes[d.Name] = d;
                        }
                    }
                }
                catch
                {
                    // 忽略
                }
            }
        }

        private IEnumerable<string> CandidateFiles(string explicitPath, params string[] fileNames)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                if (seen.Add(Path.GetFullPath(explicitPath)))
                {
                    yield return explicitPath;
                }
            }

            foreach (string dir in CandidateDirectories())
            {
                foreach (string fileName in fileNames)
                {
                    string full = Path.Combine(dir, fileName);
                    if (File.Exists(full) && seen.Add(Path.GetFullPath(full)))
                    {
                        yield return full;
                    }
                }
            }
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            string asmDir = null;
            try
            {
                asmDir = AppContext.BaseDirectory;
            }
            catch
            {
                // ignore
            }

            if (!string.IsNullOrEmpty(asmDir))
            {
                yield return Path.Combine(asmDir, "Resources");
                yield return asmDir;
            }

            // 开发机便利：本机装了 AutoCAD 时直接借用它的图案库（生产环境请把 .pat/.lin 放到 Resources 下）
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                string acadRoot = Path.Combine(programFiles, "Autodesk");
                if (Directory.Exists(acadRoot))
                {
                    foreach (string version in Directory.GetDirectories(acadRoot, "AutoCAD *").OrderByDescending(x => x))
                    {
                        foreach (string sub in new[] { "UserDataCache\\zh-cn\\Support", "UserDataCache\\en-us\\Support", "Support" })
                        {
                            string dir = Path.Combine(version, sub);
                            if (Directory.Exists(dir))
                            {
                                yield return dir;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>一条线型定义（从 .lin 解析出来）。</summary>
    public sealed class LineTypeDefinition
    {
        public LineTypeDefinition(string name, string description, double patternLength, IList<double> elements)
        {
            Name = name;
            Description = description;
            PatternLength = patternLength;
            Elements = elements;
        }

        public string Name { get; private set; }

        public string Description { get; private set; }

        public double PatternLength { get; private set; }

        /// <summary>正数=实线段，负数=空白段，0=点。</summary>
        public IList<double> Elements { get; private set; }
    }

    /// <summary>
    /// 极简 .lin 解析器：只处理"纯线段"线型（绝大多数工程线型都是这种）。
    /// 含形（shape）/文字（text）的复杂线型会解析出线段部分并跳过非数值元素，保证不会崩。
    /// </summary>
    public static class LinetypeParser
    {
        public static IEnumerable<LineTypeDefinition> ParseFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                yield break;
            }

            string[] lines;
            try
            {
                // .lin 可能是 ANSI/GBK，也可能是 UTF-8；用 Latin1 兜底保证不会因编码抛异常
                lines = File.ReadAllLines(path, System.Text.Encoding.Default);
            }
            catch
            {
                yield break;
            }

            string pendingName = null;
            string pendingDescription = null;

            foreach (string raw in lines)
            {
                string line = raw == null ? string.Empty : raw.Trim();
                if (line.Length == 0 || line.StartsWith(";"))
                {
                    continue;
                }

                if (line.StartsWith("*"))
                {
                    // *NAME,Description
                    string body = line.Substring(1);
                    int comma = body.IndexOf(',');
                    pendingName = (comma < 0 ? body : body.Substring(0, comma)).Trim();
                    pendingDescription = comma < 0 ? string.Empty : body.Substring(comma + 1).Trim();
                    continue;
                }

                if (pendingName == null || !line.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var elements = new List<double>();
                double total = 0;
                string[] parts = line.Substring(1).Split(',');
                foreach (string part in parts)
                {
                    double v;
                    if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        elements.Add(v);
                        total += Math.Abs(v);
                    }
                }

                if (elements.Count > 0)
                {
                    yield return new LineTypeDefinition(pendingName, pendingDescription, total, elements);
                }

                pendingName = null;
                pendingDescription = null;
            }
        }
    }
}
