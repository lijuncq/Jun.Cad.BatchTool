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
    /// 验证修复方案：给 MTEXT 补 RectangleWidth / RectangleHeight 后，
    /// 写出→读回是否保得住，以及 DXF 里 41/44 是否变成有效值。
    ///
    /// 同时确认：MTEXT 的 Rotation 是只读（从 extrusion 或 50 码来），
    /// 改 RectangleWidth 不会影响旋转。
    /// </summary>
    internal static class FixVerify
    {
        public static void Run(string srcPath)
        {
            CadDocument doc = DwgReader.Read(srcPath);

            Console.WriteLine("=== 修复前：旋转 MTEXT 的矩形 ===");
            var targets = new List<MText>();
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null) continue;
                targets.Add(m);
            }

            foreach (MText m in targets.Where(x => Math.Abs(x.Rotation) > 1e-6))
            {
                Console.WriteLine(string.Format("  '{0}' RectW={1} RectH={2} Height={3} BGF={4}",
                    MTextTokenizer.ToPlainText(m.Value ?? ""), m.RectangleWidth, m.RectangleHeight,
                    m.Height, m.BackgroundFillFlags));
            }

            // ---- 应用修复 ----
            Console.WriteLine();
            Console.WriteLine("=== 应用修复：RectangleWidth = 估算文字宽，RectangleHeight = 字高 ===");

            foreach (MText m in targets)
            {
                string plain = MTextTokenizer.ToPlainText(m.Value ?? "");
                if (string.IsNullOrWhiteSpace(plain)) continue;

                double width = TextBounds.MeasureWidth(plain, m.Height);

                // 只在"没有矩形"时补，不覆盖调用方已经设好的
                if (m.RectangleWidth <= 0)
                {
                    m.RectangleWidth = width;
                }

                if (m.RectangleHeight <= 0)
                {
                    m.RectangleHeight = m.Height;
                }

                m.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
                m.BackgroundScale = 1.5;
            }

            foreach (MText m in targets.Where(x => Math.Abs(x.Rotation) > 1e-6))
            {
                Console.WriteLine(string.Format("  '{0}' RectW={1:0.###} RectH={2:0.###} Height={3} BGF={4} 旋转={5:0.##}°",
                    MTextTokenizer.ToPlainText(m.Value ?? ""), m.RectangleWidth, m.RectangleHeight,
                    m.Height, m.BackgroundFillFlags, m.Rotation * 180 / Math.PI));
            }

            // ---- 写出 → 读回 ----
            string dir = Path.Combine(Path.GetTempPath(), "jun-fixverify");
            Directory.CreateDirectory(dir);
            string dwg = Path.Combine(dir, "fixed.dwg");
            DwgWriter.Write(dwg, doc, null, null);

            Console.WriteLine();
            Console.WriteLine("=== DWG 写回后读回（旋转 MTEXT）===");
            var back = DwgReader.Read(dwg);
            foreach (MText m in back.ModelSpace.Entities.OfType<MText>().Where(x => Math.Abs(x.Rotation) > 1e-6))
            {
                Console.WriteLine(string.Format("  '{0}' RectW={1:0.###} RectH={2:0.###} Height={3} BGF={4} 旋转={5:0.##}°",
                    MTextTokenizer.ToPlainText(m.Value ?? ""), m.RectangleWidth, m.RectangleHeight,
                    m.Height, m.BackgroundFillFlags, m.Rotation * 180 / Math.PI));
            }

            // ---- DXF 里 41/44 的实际值 ----
            string dxf = Path.Combine(dir, "fixed.dxf");
            DxfWriter.Write(dxf, doc);
            Console.WriteLine();
            Console.WriteLine("=== 写出 DXF 的 group code（关键：41=矩形宽, 44=矩形高, 45=背景缩放）===");
            DumpCodes(dxf);

            Console.WriteLine();
            Console.WriteLine("=== 结论 ===");
            Console.WriteLine("  MTEXT 原生背景遮罩要画出来，AutoCAD 需要一个**非零的矩形宽**（DXF 41）。");
            Console.WriteLine("  源图的 RectangleWidth=0（AutoCAD 自己的'自动'排版宽度），");
            Console.WriteLine("  ACadSharp 不排版、也不补这个值 → 写出去还是 0 → 背景画不出来。");
        }

        private static void DumpCodes(string dxfPath)
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
                bool interesting = v.IndexOf("DRAW LIMIT", StringComparison.OrdinalIgnoreCase) >= 0
                    || v.IndexOf("LIFTING HATCH", StringComparison.OrdinalIgnoreCase) >= 0
                    || v.IndexOf("E 99020", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!interesting) continue;
                n++;
                if (n > 4) break;

                Console.WriteLine("  --- '" + v + "' ---");
                foreach (string c in new[] { "40", "41", "44", "45", "46", "50", "71", "72" })
                {
                    List<string> list;
                    if (s.TryGetValue(c, out list))
                    {
                        Console.WriteLine(string.Format("     {0,-4} {1}", c, string.Join(",", list)));
                    }
                }
            }
        }
    }
}
