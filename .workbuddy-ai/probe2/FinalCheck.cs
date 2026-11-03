using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using CSMath;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 终极判定：把**真实图纸**里的旋转 MTEXT 加背景遮罩，写成 DWG，再导出 DXF，
    /// 核对 AutoCAD 到底会看到什么。
    ///
    /// 关键看 DXF 里：
    ///   41 (RectangleWidth) —— 背景框宽度，0 = 背景框没有宽度 = 画不出来
    ///   45 (BackgroundScale)
    ///   50 (Rotation)
    ///   71 (附着点 + 文字方向)
    ///
    /// 再看 DWG 的二进制：DXF 没有的信息（比如 group code 71 的方向位）
    /// 只有 DWG 才有。
    /// </summary>
    internal static class FinalCheck
    {
        public static void Run(string srcPath)
        {
            CadDocument doc = DwgReader.Read(srcPath);

            // 挑旋转的 MTEXT
            MText target = null;
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m != null && Math.Abs(m.Rotation) > 1e-6) { target = m; break; }
            }

            if (target == null)
            {
                Console.WriteLine("没有旋转 MTEXT");
                return;
            }

            Console.WriteLine("=== 目标 MTEXT（加遮罩前）===");
            Console.WriteLine(string.Format("  句柄={0} 旋转={1:0.####}° 值='{2}'",
                target.Handle.ToString("X"), target.Rotation * 180 / Math.PI,
                MTextTokenizer.ToPlainText(target.Value ?? "")));
            Console.WriteLine(string.Format("  RectW={0} RectH={1} HorzW={2} Height={3} BGF={4}",
                target.RectangleWidth, target.RectangleHeight, target.HorizontalWidth,
                target.Height, target.BackgroundFillFlags));
            Console.WriteLine(string.Format("  Attach={0} DrawDir={1} LineSpacing={2} Style={3}",
                target.AttachmentPoint, target.DrawingDirection, target.LineSpacing,
                target.Style == null ? "null" : target.Style.Name));

            target.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            target.BackgroundScale = 1.5;

            string dir = Path.Combine(Path.GetTempPath(), "jun-final");
            Directory.CreateDirectory(dir);
            string dwg = Path.Combine(dir, "final.dwg");
            DwgWriter.Write(dwg, doc, null, null);

            string dxf = Path.Combine(dir, "final.dxf");
            DxfWriter.Write(dxf, doc);

            Console.WriteLine();
            Console.WriteLine("=== 写出后的 DXF 里这个 MTEXT 的 group code ===");
            DumpMText(dxf);

            Console.WriteLine();
            Console.WriteLine("=== DWG 回读 ===");
            var back = DwgReader.Read(dwg);
            foreach (Entity e in back.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null || Math.Abs(m.Rotation) < 1e-6) continue;
                Console.WriteLine(string.Format("  旋转={0:0.###}° RectW={1} RectH={2} HorzW={3} BGF={4} BScale={5} Attach={6} Dir={7}",
                    m.Rotation * 180 / Math.PI, m.RectangleWidth, m.RectangleHeight, m.HorizontalWidth,
                    m.BackgroundFillFlags, m.BackgroundScale, m.AttachmentPoint, m.DrawingDirection));
            }
        }

        private static void DumpMText(string dxfPath)
        {
            var lines = File.ReadAllLines(dxfPath);
            var pairs = new List<KeyValuePair<string, string>>();
            for (int k = 0; k + 1 < lines.Length; k += 2)
            {
                pairs.Add(new KeyValuePair<string, string>(lines[k].Trim(), lines[k + 1].Trim()));
            }

            var segs = new List<Dictionary<string, List<string>>>();
            Dictionary<string, List<string>> cur = null;
            foreach (KeyValuePair<string, string> p in pairs)
            {
                if (p.Key == "0")
                {
                    if (cur != null) segs.Add(cur);
                    cur = p.Value == "MTEXT" ? new Dictionary<string, List<string>>() : null;
                }
                else if (cur != null)
                {
                    List<string> list;
                    if (!cur.TryGetValue(p.Key, out list))
                    {
                        list = new List<string>();
                        cur[p.Key] = list;
                    }
                    list.Add(p.Value);
                }
            }
            if (cur != null) segs.Add(cur);

            int n = 0;
            foreach (Dictionary<string, List<string>> s in segs)
            {
                string v = s.ContainsKey("1") ? s["1"][0] : "";
                if (v.IndexOf("DRAW LIMIT", StringComparison.OrdinalIgnoreCase) < 0
                    && v.IndexOf("LIFTING HATCH", StringComparison.OrdinalIgnoreCase) < 0
                    && v.IndexOf("E 99020", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                n++;
                Console.WriteLine("  --- MTEXT '" + v + "' ---");
                foreach (string c in new[] { "10", "20", "40", "41", "42", "43", "44", "45", "46", "47", "48", "50", "71", "72", "73", "210", "220", "230" })
                {
                    List<string> list;
                    if (s.TryGetValue(c, out list))
                    {
                        Console.WriteLine(string.Format("    {0,-4} {1}", c, string.Join(",", list)));
                    }
                }
            }

            if (n == 0)
            {
                Console.WriteLine("  （DXF 里没找到这些文字——说明 DXF 导出丢实体了）");
            }
        }
    }
}
