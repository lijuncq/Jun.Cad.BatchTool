using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 打印一张图里所有文字 + 遮罩的几何，用来核对"遮罩比文字大多少"。
    ///
    /// 用法： probe2 mdump &lt;file.dwg&gt; [关键字]
    /// 输出：每行一个文字/遮罩，列出字高、包围盒、遮罩盒、以及 遮罩高/文字高 的比值。
    /// </summary>
    internal static class MaskDump
    {
        public static void Run(string path, string keyword)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + Path.GetFileName(path) + " ===");
            DumpSpace(doc.ModelSpace, "模型", keyword);

            Console.WriteLine("--- 模型空间块参照 ---");
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var ins = e as Insert;
                if (ins == null || ins.Block == null)
                {
                    continue;
                }

                Console.WriteLine(string.Format("  INS {0,-10} {1,-14} 插入点={2} 缩放={3:0.###}",
                    e.Handle.ToString("X"), ins.Block.Name, ins.InsertPoint, ins.XScale));
            }

            DumpLines(doc.ModelSpace, "模型");
            foreach (ACadSharp.Tables.BlockRecord br in doc.BlockRecords)
            {
                if (br.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DumpSpace(br, "块 " + br.Name, keyword);
                DumpLines(br, "块 " + br.Name);
            }
        }

        /// <summary>列出 y 落在 200..240、x 落在 880..1010 的水平/竖直线，用来对齐表格线与文字。</summary>

        /// <summary>找同一个空间里 AABB 几乎重合的 Wipeout（重复叠加的遮罩）。</summary>
        public static void Dups(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + Path.GetFileName(path) + " 重复遮罩 ===");
            int total = 0;
            foreach (ACadSharp.Tables.BlockRecord space in AllSpaces(doc))
            {
                var list = new List<KeyValuePair<Wipeout, Bounds2d>>();
                foreach (Entity e in space.Entities)
                {
                    var w = e as Wipeout;
                    if (w == null) continue;
                    Bounds2d b = TextBounds.ForWipeout(w);
                    if (b.IsValid) list.Add(new KeyValuePair<Wipeout, Bounds2d>(w, b));
                }

                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        Bounds2d a = list[i].Value, b = list[j].Value;
                        double tol = Math.Max(1e-4, Math.Max(a.Width, a.Height) * 0.01);
                        if (Math.Abs(a.CenterX - b.CenterX) <= tol && Math.Abs(a.CenterY - b.CenterY) <= tol
                            && Math.Abs(a.Width - b.Width) <= tol && Math.Abs(a.Height - b.Height) <= tol)
                        {
                            total++;
                            if (true)
                            {
                                Console.WriteLine(string.Format("  {0,-6} {1} 与 {2} 重合 {3}", space.Name,
                                    list[i].Key.Handle.ToString("X"), list[j].Key.Handle.ToString("X"), a));
                            }
                        }
                    }
                }
            }

            Console.WriteLine("重复对数：" + total);
        }

        private static IEnumerable<ACadSharp.Tables.BlockRecord> AllSpaces(CadDocument doc)
        {
            yield return doc.ModelSpace;
            foreach (ACadSharp.Tables.BlockRecord br in doc.BlockRecords)
            {
                if (!br.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase))
                {
                    yield return br;
                }
            }
        }

        private static void DumpLines(ACadSharp.Tables.BlockRecord space, string where)
        {
            foreach (Entity e in space.Entities)
            {
                var line = e as Line;
                if (line == null)
                {
                    continue;
                }

                double x1 = line.StartPoint.X, y1 = line.StartPoint.Y;
                double x2 = line.EndPoint.X, y2 = line.EndPoint.Y;
                if (Math.Abs(y1 - y2) > 1e-6 && Math.Abs(x1 - x2) > 1e-6)
                {
                    continue;
                }

                bool horizontal = Math.Abs(y1 - y2) <= 1e-6;
                double y = horizontal ? y1 : (y1 + y2) * 0.5;
                double x = horizontal ? (x1 + x2) * 0.5 : x1;
                if (y < 185 || y > 240 || x < 880 || x > 1010)
                {
                    continue;
                }

                Console.WriteLine(string.Format("  LINE {0,-8} {1,-8} {2} ({3:0.###},{4:0.###})→({5:0.###},{6:0.###})",
                    e.Handle.ToString("X"), where, horizontal ? "水平" : "竖直", x1, y1, x2, y2));
            }
        }

        private static void DumpSpace(ACadSharp.Tables.BlockRecord space, string where, string keyword)
        {
            var wipeouts = new List<Bounds2d>();
            foreach (Entity e in space.Entities)
            {
                var w = e as Wipeout;
                if (w != null)
                {
                    Bounds2d wb = TextBounds.ForWipeout(w);
                    if (wb.IsValid)
                    {
                        wipeouts.Add(wb);
                    }
                }
            }

            foreach (Entity e in space.Entities)
            {
                var mtext = e as MText;
                var text = e as TextEntity;
                if (mtext == null && text == null)
                {
                    continue;
                }

                string value = mtext != null ? mtext.Value : text.Value;
                value = MTextTokenizer.ToPlainText(value ?? string.Empty).Replace("\n", "|");
                if (keyword != null && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (value.Length > 34)
                {
                    value = value.Substring(0, 34) + "…";
                }

                Bounds2d box;
                TextBounds.TryGet(e, out box);

                string geom = mtext != null
                    ? string.Format("MTEXT h={0:0.##} attach={1} rot={2:0.#}° rectW={3:0.##} rectH={4:0.##} bg={5} scale={6:0.###}",
                        mtext.Height, mtext.AttachmentPoint, mtext.Rotation * 180 / Math.PI,
                        mtext.RectangleWidth, mtext.RectangleHeight, mtext.BackgroundFillFlags, mtext.BackgroundScale)
                    : string.Format("TEXT h={0:0.##} rot={1:0.#}° align={2}/{3} wf={4:0.##}",
                        text.Height, text.Rotation * 180 / Math.PI,
                        text.HorizontalAlignment, text.VerticalAlignment, text.WidthFactor);

                string maskInfo = string.Empty;
                foreach (Bounds2d wb in wipeouts)
                {
                    if (wb.Intersects(box) && wb.IntersectionArea(box) > 0)
                    {
                        maskInfo = string.Format(" | 遮罩 {0} 高比={1:0.00} 宽比={2:0.00}",
                            wb, box.Height > 0 ? wb.Height / box.Height : 0,
                            box.Width > 0 ? wb.Width / box.Width : 0);
                        break;
                    }
                }

                Console.WriteLine(string.Format("  {0,-8} {1,-8} {2} 盒={3} \"{4}\"{5}",
                    e.Handle.ToString("X"), where, geom, box, value, maskInfo));
            }
        }
    }
}
