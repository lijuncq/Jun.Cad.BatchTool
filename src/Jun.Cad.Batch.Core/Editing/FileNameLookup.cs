using System;
using System.Collections.Generic;
using System.IO;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>
    /// 离线版的名称查询：拿一张**参考图纸**（dwg 文件）当符号表，校验配置里写的图层/块/样式名是否存在。
    ///
    /// 与 AutoCAD 版的区别：那边查的是"当前打开文档"，这边查的是"你指定的那个文件"。
    /// 语义上更明确——批量处理的图纸通常和参考图纸是同一套模板。
    ///
    /// 三态沿用 <see cref="NameLookupResult"/>：读不了文件时返回 Unknown（静默跳过），
    /// 绝不能把"没读到"说成"名字不存在"，否则用户会看到一片假警告。
    /// </summary>
    public sealed class FileNameLookup : INameLookup, IDisposable
    {
        private readonly Dictionary<NameKind, HashSet<string>> _names =
            new Dictionary<NameKind, HashSet<string>>();

        private readonly string _path;

        private FileNameLookup(string path)
        {
            _path = path;
        }

        public string ContextName
        {
            get { return Path.GetFileName(_path); }
        }

        /// <summary>
        /// 打开图纸并建立符号表。读不了时返回 null（调用方据此跳过校验，而不是报一堆"不存在"）。
        /// </summary>
        public static FileNameLookup TryCreate(string path, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "参考图纸不存在：" + path;
                return null;
            }

            var lookup = new FileNameLookup(path);

            try
            {
                using (CadFileSession session = CadFileSession.Open(path, null))
                {
                    var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in session.Document.Layers)
                    {
                        layers.Add(item.Name);
                    }

                    var blocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in session.Document.BlockRecords)
                    {
                        blocks.Add(item.Name);
                    }

                    var textStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in session.Document.TextStyles)
                    {
                        textStyles.Add(item.Name);
                    }

                    var dimStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in session.Document.DimensionStyles)
                    {
                        dimStyles.Add(item.Name);
                    }

                    var linetypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in session.Document.LineTypes)
                    {
                        linetypes.Add(item.Name);
                    }

                    lookup._names[NameKind.Layer] = layers;
                    lookup._names[NameKind.Block] = blocks;
                    lookup._names[NameKind.TextStyle] = textStyles;
                    lookup._names[NameKind.DimStyle] = dimStyles;
                    lookup._names[NameKind.Linetype] = linetypes;
                }
            }
            catch (Exception ex)
            {
                error = "读取参考图纸失败：" + ex.Message;
                return null;
            }

            return lookup;
        }

        public NameLookupResult Lookup(NameKind kind, string name)
        {
            HashSet<string> set;
            if (!_names.TryGetValue(kind, out set) || set == null)
            {
                return NameLookupResult.Unknown();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return NameLookupResult.Unknown();
            }

            string trimmed = name.Trim();
            if (set.Contains(trimmed))
            {
                return NameLookupResult.Exists();
            }

            return NameLookupResult.Missing(FindNearest(set, trimmed));
        }

        /// <summary>
        /// 找最接近的候选：先比忽略大小写的前缀/包含，再退回编辑距离。
        /// 只用于把"你写的名字不存在"翻译成"你大概想写哪个"，猜错也无害。
        /// </summary>
        private static string FindNearest(HashSet<string> candidates, string name)
        {
            string best = null;
            int bestDistance = int.MaxValue;

            foreach (string candidate in candidates)
            {
                int distance = Distance(candidate, name);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            // 差太远就不猜了，免得给一个风马牛不相及的名字
            if (best == null || bestDistance > Math.Max(3, name.Length / 2))
            {
                return null;
            }

            return best;
        }

        private static int Distance(string a, string b)
        {
            if (string.IsNullOrEmpty(a))
            {
                return b == null ? 0 : b.Length;
            }

            if (string.IsNullOrEmpty(b))
            {
                return a.Length;
            }

            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;

                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                Array.Copy(current, previous, current.Length);
            }

            return previous[b.Length];
        }

        public void Dispose()
        {
            _names.Clear();
        }
    }
}
