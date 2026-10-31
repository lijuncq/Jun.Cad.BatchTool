using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace Probe
{
    /// <summary>在整批图里找指定的标注文字，用来确认用户截图到底来自哪张图。</summary>
    internal static class TextScan
    {
        public static void Run(string directory)
        {
            var wanted = new HashSet<string> { "2850", "1550", "2500" };

            foreach (string file in Directory.GetFiles(directory, "*.dwg"))
            {
                CadDocument doc;
                try
                {
                    doc = DwgReader.Read(file);
                }
                catch
                {
                    continue;
                }

                var found = new List<string>();
                Collect(doc.ModelSpace, "模型", wanted, found);

                foreach (BlockRecord br in doc.BlockRecords)
                {
                    if (br.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase))
                    {
                        Collect(br, "块 " + br.Name, wanted, found);
                    }
                }

                if (found.Count > 0)
                {
                    Console.WriteLine(Path.GetFileName(file));
                    foreach (string line in found)
                    {
                        Console.WriteLine("    " + line);
                    }
                }
            }

            Console.WriteLine("扫描完成。");
        }

        private static void Collect(BlockRecord space, string where, HashSet<string> wanted, List<string> found)
        {
            foreach (Entity e in space.Entities)
            {
                var dim = e as Dimension;
                if (dim == null || dim.Block == null)
                {
                    continue;
                }

                foreach (Entity c in dim.Block.Entities)
                {
                    string val = null;
                    var m = c as MText;
                    var t = c as TextEntity;
                    if (m != null) val = m.Value;
                    else if (t != null) val = t.Value;
                    if (val == null) continue;

                    string plain = val.Substring(val.LastIndexOf(';') + 1).Trim();
                    if (wanted.Contains(plain))
                    {
                        found.Add(string.Format("{0} 标注 {1} 文字 '{2}'", where, dim.Handle, plain));
                    }
                }
            }
        }
    }
}
