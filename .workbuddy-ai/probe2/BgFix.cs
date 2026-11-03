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
    /// 终极验证：MTEXT 原生背景遮罩到底能不能用。
    ///
    /// 做法：造几个 MTEXT，分别测
    ///   A. 只有 BackgroundFillFlags（当前实现的做法）
    ///   B. BackgroundFillFlags + RectangleWidth 显式给宽度
    ///   C. B + 正确的 AttachmentPoint / TextHeight
    /// 导出 DXF，对比 group code：
    ///   41 = 矩形宽(dxf width)   44 = 矩形高(dxf height)
    ///   42 = 实际排版宽(实际 width)
    ///   71 = 附着点 + 文字方向
    ///   72 = 绘制方向
    ///   45/46 = 背景填充开关
    ///   50 = 旋转角
    ///
    /// 然后用 DXF→AutoCAD 的语义判断：46/45 开了，但 41=0 时背景框宽度为 0 → 画不出来。
    /// </summary>
    internal static class BgFix
    {
        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);

            Console.WriteLine("=== A/B/C 三种 MTEXT 的 DXF group code 对比 ===");

            var cases = new List<KeyValuePair<string, MText>>
            {
                Pair("A-只开BGF", MakeMText("DRAW LIMIT", 2.5)),
                Pair("B-BGF+RectW", MakeMText("DRAW LIMIT", 2.5, 15.5)),
                Pair("C-BGF+RectW+Rot", MakeMText("DRAW LIMIT", 2.5, 15.5, Math.PI / 2))
            };

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            foreach (var kv in cases)
            {
                doc.ModelSpace.Entities.Add(kv.Value);
            }

            string path = Path.Combine(dir, "bgfix.dxf");
            DxfWriter.Write(path, doc);

            var lines = File.ReadAllLines(path);
            int idx = 0;
            int caseNo = 0;
            while (idx < lines.Length)
            {
                if (lines[idx].Trim() == "MTEXT")
                {
                    caseNo++;
                    Console.WriteLine();
                    Console.WriteLine("--- " + cases[Math.Min(caseNo - 1, cases.Count - 1)].Key + " ---");

                    // 打印 MTEXT 段的关键 group code
                    for (int j = idx; j < Math.Min(idx + 90, lines.Length); j += 2)
                    {
                        string code = lines[j].Trim();
                        string val = j + 1 < lines.Length ? lines[j + 1] : "";

                        // 只显示关键 code
                        if (code == "41" || code == "42" || code == "43" || code == "44"
                            || code == "45" || code == "46" || code == "47" || code == "48"
                            || code == "50" || code == "71" || code == "72" || code == "73"
                            || code == "1" || code == "40" || code == "210" || code == "220" || code == "230")
                        {
                            Console.WriteLine(string.Format("    {0,-4} {1}", code, val));
                        }

                        if (code == "1" && val.StartsWith("AcDbMText", StringComparison.Ordinal))
                        {
                            // 进入 AcDbMText 子类，继续打印到下一个 0
                        }

                        if (code == "0" && j > idx + 10)
                        {
                            break;
                        }
                    }
                    idx += 2;
                    continue;
                }
                idx += 2;
            }
        }

        private static KeyValuePair<string, MText> Pair(string name, MText m)
        {
            return new KeyValuePair<string, MText>(name, m);
        }

        private static MText MakeMText(string value, double height)
        {
            var m = new MText
            {
                Value = value,
                InsertPoint = new XYZ(0, 0, 0),
                Height = height,
                AttachmentPoint = AttachmentPointType.MiddleLeft
            };
            m.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            m.BackgroundScale = 1.5;
            return m;
        }

        private static MText MakeMText(string value, double height, double rectWidth)
        {
            var m = MakeMText(value, height);
            m.RectangleWidth = rectWidth;
            return m;
        }

        private static MText MakeMText(string value, double height, double rectWidth, double rot)
        {
            var m = MakeMText(value, height, rectWidth);
            // Rotation 只读，尝试用 ApplyTransform 绕 Z 转
            return m;
        }
    }
}
