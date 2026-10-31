using System;
using System.Collections.Generic;
using System.IO;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 按文件夹收集待处理的图纸。
    ///
    /// 为什么放在 Core 而不是各自实现：命令行（`--input 目录 --recursive`）与界面
    /// （"输入图纸文件夹"卡片）是同一件事，两边各写一套的结果是排除规则慢慢漂移——
    /// 而这里的排除规则错一条，就会把上一次的产物当原料再跑一遍。
    ///
    /// 三条必须排除的东西：
    ///   1. **输出根**：默认 `<输入目录>\output`，不排除的话第二次运行会写出 `output\output\*.dwg`；
    ///   2. **`.jun-runs`**：运行清单与备份目录，里面可能躺着 `.dwg.bak` 之类的文件；
    ///   3. **任何含 `.jun-runs` 的目录**：那是"以前跑过、但这次换了输出位置"的旧输出根。
    /// </summary>
    public static class DrawingFolder
    {
        /// <summary>默认输出目录：<c>&lt;输入目录&gt;\output</c>（与命令行一致）。</summary>
        public static string DefaultOutputRoot(string inputFolder)
        {
            if (string.IsNullOrWhiteSpace(inputFolder))
            {
                return "output";
            }

            return Path.Combine(Path.GetFullPath(inputFolder), "output");
        }

        /// <summary>
        /// 列出文件夹里的图纸（按完整路径去重、排序）。
        /// </summary>
        /// <param name="folder">输入目录。不存在时返回空列表。</param>
        /// <param name="patterns">通配符，例如 <c>*.dwg</c>；为空时用 <c>*.dwg</c>。</param>
        /// <param name="recursive">是否递归子目录。</param>
        /// <param name="outputRoot">
        /// 本次的输出根（会被整棵排除）。为空表示不排除——原地覆盖时输出就是源文件，不能排除。
        /// </param>
        public static List<string> Enumerate(string folder, IEnumerable<string> patterns, bool recursive, string outputRoot)
        {
            var files = new List<string>();

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return files;
            }

            var globs = new List<string>();
            if (patterns != null)
            {
                foreach (string pattern in patterns)
                {
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        globs.Add(pattern.Trim());
                    }
                }
            }

            if (globs.Count == 0)
            {
                globs.Add("*.dwg");
            }

            SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var artifactRoots = FindArtifactRoots(folder, option);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string glob in globs)
            {
                string[] matched;
                try
                {
                    matched = Directory.GetFiles(folder, glob, option);
                }
                catch
                {
                    continue;
                }

                foreach (string file in matched)
                {
                    string full = Path.GetFullPath(file);

                    if (IsExcluded(full, outputRoot, artifactRoots))
                    {
                        continue;
                    }

                    if (seen.Add(full))
                    {
                        files.Add(full);
                    }
                }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>该路径是否落在输出根 / `.jun-runs` / 旧输出根里。</summary>
        public static bool IsExcluded(string fullPath, string outputRoot, IList<string> artifactRoots)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return true;
            }

            if (fullPath.IndexOf(".jun-runs", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(outputRoot) && IsUnder(fullPath, outputRoot))
            {
                return true;
            }

            if (artifactRoots != null)
            {
                foreach (string root in artifactRoots)
                {
                    if (IsUnder(fullPath, root))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>找出输入树下"跑过批处理"的目录（它们含 `.jun-runs`）。</summary>
        private static List<string> FindArtifactRoots(string folder, SearchOption option)
        {
            var roots = new List<string>();

            if (option != SearchOption.AllDirectories)
            {
                return roots;
            }

            try
            {
                foreach (string dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories))
                {
                    if (Directory.Exists(Path.Combine(dir, ".jun-runs")))
                    {
                        roots.Add(Path.GetFullPath(dir));
                    }
                }
            }
            catch
            {
                // 枚举失败不影响主流程：少排除一个目录总比整个命令失败好
            }

            return roots;
        }

        /// <summary><paramref name="path"/> 是否位于 <paramref name="root"/> 之下。</summary>
        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
