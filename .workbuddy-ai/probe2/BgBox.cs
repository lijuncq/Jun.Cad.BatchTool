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
    /// 查 MTEXT 原生背景遮罩为什么在 90° 旋转文字上"看不见"。
    ///
    /// AutoCAD 的 MTEXT 背景填充是**用文字矩形（RectangleWidth × RectangleHeight）** 画的，
    /// 矩形是相对文字自身坐标系（随 Rotation 转）。
    /// 如果 RectangleWidth=0，背景就没有宽度 → 画成一条线 → 看着像"没加遮罩"。
    ///
    /// 另外：MTEXT 的 DrawingDirection / ColumnData 等也会影响。
    /// </summary>
    internal static class BgBox
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);

            Console.WriteLine("=== 模型空间全部 MTEXT 的背景框参数 ===");
            Console.WriteLine("  句柄      旋转°   字高   RectW    RectH  HorzW  BGF                     文字");

            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null) continue;

                string txt = MTextTokenizer.ToPlainText(m.Value ?? string.Empty).Replace("\n", "|");
                if (txt.Length > 22) txt = txt.Substring(0, 22) + "…";

                Console.WriteLine(string.Format(
                    "  {0,-9} {1,6:0.##} {2,6:0.##} {3,7:0.##} {4,7:0.##} {5,6:0.##} {6,-24} {7}",
                    m.Handle.ToString("X"), m.Rotation * 180 / Math.PI, m.Height,
                    m.RectangleWidth, m.RectangleHeight, m.HorizontalWidth,
                    m.BackgroundFillFlags.ToString(), txt));
            }

            Console.WriteLine();
            Console.WriteLine("=== 真实 AutoCAD 里 90° MTEXT 的背景是怎么算的 ===");
            Console.WriteLine("  AutoCAD: 背景矩形 = 文字实际排版框（按内容自动算），");
            Console.WriteLine("           旋转后这个框也跟着转 → 依然盖住字。");
            Console.WriteLine("  但 ACadSharp 不排版，RectangleWidth 是**读进来就有的原值**。");
            Console.WriteLine();

            // 看旋转 MTEXT 的 RectangleWidth 是不是 0
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null || Math.Abs(m.Rotation) < 1e-6) continue;

                string txt = MTextTokenizer.ToPlainText(m.Value ?? string.Empty).Replace("\n", "|");
                Console.WriteLine(string.Format(
                    "  旋转 {0:0.##}° '{1}'  RectW={2:0.###} RectH={3:0.###} HorzW={4:0.###} Column={5} Dir={6}",
                    m.Rotation * 180 / Math.PI, txt,
                    m.RectangleWidth, m.RectangleHeight, m.HorizontalWidth,
                    m.HasColumns, m.DrawingDirection));
            }

            Console.WriteLine();
            Console.WriteLine("=== 对比：写回后这些值有没有变 ===");
            string dir = Path.Combine(Path.GetTempPath(), "jun-bgbox");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "bg.dwg");
            DwgWriter.Write(outPath, doc, null, null);
            var back = DwgReader.Read(outPath);

            foreach (Entity e in back.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null || Math.Abs(m.Rotation) < 1e-6) continue;

                string txt = MTextTokenizer.ToPlainText(m.Value ?? string.Empty).Replace("\n", "|");
                Console.WriteLine(string.Format(
                    "  旋转 {0:0.##}° '{1}'  RectW={2:0.###} RectH={3:0.###} HorzW={4:0.###} BGF={5}",
                    m.Rotation * 180 / Math.PI, txt,
                    m.RectangleWidth, m.RectangleHeight, m.HorizontalWidth,
                    m.BackgroundFillFlags));
            }
        }
    }
}
