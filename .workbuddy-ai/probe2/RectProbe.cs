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
    /// 验证假设："MTEXT 原生背景遮罩不显示" 的原因是 RectangleWidth = 0。
    ///
    /// 对比导出 DXF：
    ///   原样（RectW=0） vs 显式设 RectW=文字宽度 vs 设 RectW 且 RectH=字高
    /// 看 41/44/45/50/71 的差别。
    ///
    /// 另外确认 HorizontalWidth 的含义（DXF 42 实际宽度）。
    /// </summary>
    internal static class RectProbe
    {
        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            // 变体 1：完全照抄源图（RectW=0, RectH=4.1667, HorzW=0.9）
            var v1 = new MText
            {
                Value = "V1-asis",
                InsertPoint = new XYZ(0, 0, 0),
                Height = 2.5,
                RectangleWidth = 0,
                RectangleHeight = 4.1666666666666667,
                HorizontalWidth = 0.9,
                AttachmentPoint = AttachmentPointType.TopLeft
            };
            v1.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            v1.BackgroundScale = 1.5;
            doc.ModelSpace.Entities.Add(v1);

            // 变体 2：给 RectangleWidth = 文字估算宽度，RectangleHeight = 字高
            var v2 = new MText
            {
                Value = "V2-rectw",
                InsertPoint = new XYZ(50, 0, 0),
                Height = 2.5,
                RectangleWidth = 15.5,
                RectangleHeight = 2.5,
                HorizontalWidth = 0.9,
                AttachmentPoint = AttachmentPointType.TopLeft
            };
            v2.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            v2.BackgroundScale = 1.5;
            doc.ModelSpace.Entities.Add(v2);

            // 变体 3：RectangleWidth = 文字宽，RectH 保持源值
            var v3 = new MText
            {
                Value = "V3-rectw-keeph",
                InsertPoint = new XYZ(100, 0, 0),
                Height = 2.5,
                RectangleWidth = 15.5,
                RectangleHeight = 4.1666666666666667,
                HorizontalWidth = 0.9,
                AttachmentPoint = AttachmentPointType.TopLeft
            };
            v3.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            v3.BackgroundScale = 1.5;
            doc.ModelSpace.Entities.Add(v3);

            // 变体 4：RectW=0 但 RectH=字高（看看是不是宽度才是关键）
            var v4 = new MText
            {
                Value = "V4-norectw",
                InsertPoint = new XYZ(150, 0, 0),
                Height = 2.5,
                RectangleWidth = 0,
                RectangleHeight = 2.5,
                HorizontalWidth = 0.9,
                AttachmentPoint = AttachmentPointType.TopLeft
            };
            v4.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            v4.BackgroundScale = 1.5;
            doc.ModelSpace.Entities.Add(v4);

            string dxf = Path.Combine(dir, "rect.dxf");
            DxfWriter.Write(dxf, doc);

            var lines = File.ReadAllLines(dxf);
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

            foreach (Dictionary<string, List<string>> s in segs)
            {
                string v = s.ContainsKey("1") ? s["1"][0] : "";
                Console.WriteLine("--- " + v + " ---");
                foreach (string c in new[] { "10", "40", "41", "42", "43", "44", "45", "46", "47", "48", "50", "71", "72", "73" })
                {
                    List<string> list;
                    if (s.TryGetValue(c, out list))
                    {
                        Console.WriteLine(string.Format("   {0,-4} {1}", c, string.Join(",", list)));
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 结论参考：DXF 41=矩形宽 42=实际宽 44=矩形高 ===");
            Console.WriteLine("   AutoCAD 画 MTEXT 背景时，用的是 41×44 的矩形（相对文字坐标系）。");
            Console.WriteLine("   41=0 → 背景框宽度 0 → 背景画不出来。");
        }
    }
}
