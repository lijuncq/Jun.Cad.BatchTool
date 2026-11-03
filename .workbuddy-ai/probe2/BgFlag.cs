using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using CSMath;

namespace Probe
{
    /// <summary>
    /// 确认 ACadSharp 的 MTEXT 背景填充标志到底有没有写进 DXF/DWG。
    ///
    /// DXF group code 45/46 = MTEXT 背景填充：
    ///   45 = 文字背景填充开关(1=开)
    ///   46 = 背景填充颜色开关(0=用图纸背景色, 1=自定义颜色)
    /// 写入时这两个 code 必须有值，否则 AutoCAD 收到的是"没有背景填充"。
    /// </summary>
    internal static class BgFlag
    {
        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);

            Console.WriteLine("=== 各 BackgroundFillFlags 组合写出的 45/46 ===");
            Console.WriteLine("  组合 | DXF 45 | DXF 46 | DWG 回读 BGF");

            var combos = new[]
            {
                BackgroundFillFlags.None,
                BackgroundFillFlags.UseDrawingWindowColor,
                BackgroundFillFlags.UseBackgroundFillColor,
                BackgroundFillFlags.TextFrame,
                BackgroundFillFlags.UseDrawingWindowColor | BackgroundFillFlags.TextFrame,
                BackgroundFillFlags.UseBackgroundFillColor | BackgroundFillFlags.TextFrame
            };

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;
            var names = new List<string>();

            int i = 0;
            foreach (BackgroundFillFlags flags in combos)
            {
                var m = new MText
                {
                    Value = "CASE" + i,
                    InsertPoint = new XYZ(i * 20, 0, 0),
                    Height = 2.5,
                    RectangleWidth = 15.5,
                    RectangleHeight = 2.5,
                    AttachmentPoint = AttachmentPointType.MiddleLeft
                };
                m.BackgroundFillFlags = flags;
                m.BackgroundScale = 1.5;
                m.BackgroundColor = new Color((short)3);
                doc.ModelSpace.Entities.Add(m);
                names.Add(flags.ToString());
                i++;
            }

            string dxf = Path.Combine(dir, "bgflag.dxf");
            DxfWriter.Write(dxf, doc);

            // 解析 DXF
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

            for (int k = 0; k < segs.Count && k < names.Count; k++)
            {
                string c45 = segs[k].ContainsKey("45") ? string.Join(",", segs[k]["45"]) : "(无)";
                string c46 = segs[k].ContainsKey("46") ? string.Join(",", segs[k]["46"]) : "(无)";
                string c41 = segs[k].ContainsKey("41") ? string.Join(",", segs[k]["41"]) : "(无)";
                Console.WriteLine(string.Format("  {0,-45} 45={1,-4} 46={2,-4} 41={3}", names[k], c45, c46, c41));
            }

            // DWG 回读
            string dwg = Path.Combine(dir, "bgflag.dwg");
            DwgWriter.Write(dwg, doc, null, null);
            var back = DwgReader.Read(dwg);
            int j = 0;
            foreach (Entity e in back.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null) continue;
                Console.WriteLine(string.Format("    DWG 回读 #{0}: BGF={1} BScale={2} RectW={3} RectH={4} BGColor={5}",
                    j++, m.BackgroundFillFlags, m.BackgroundScale,
                    m.RectangleWidth, m.RectangleHeight, m.BackgroundColor));
            }
        }
    }
}
