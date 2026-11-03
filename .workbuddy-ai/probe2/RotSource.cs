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
    /// 弄清 MText.Rotation 到底从哪来（extrusion 210 还是别的），以及写回时能不能保住。
    ///
    /// 关键问题：真实图纸里 90° 的 MTEXT 读出来 Rotation=1.571，
    /// 但我们造一个 Normal=(1,0,0) 的 MTEXT 写出去再读回来 Rotation=0。
    /// 说明 Rotation 的来源不是我们以为的那个字段。
    /// </summary>
    internal static class RotSource
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);

            Console.WriteLine("=== 真实图纸里旋转 MTEXT 的 Normal / Rotation ===");
            int n = 0;
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var m = e as MText;
                if (m == null) continue;
                if (Math.Abs(m.Rotation) < 1e-6) continue;
                n++;
                if (n > 5) break;

                Console.WriteLine(string.Format(
                    "  Rotation={0:0.####}°{1:0.##}° Normal=({2:0.###},{3:0.###},{4:0.###}) 插入点({5:0.##},{6:0.##}) 高{7:0.##}",
                    m.Rotation, m.Rotation * 180 / Math.PI,
                    m.Normal.X, m.Normal.Y, m.Normal.Z,
                    m.InsertPoint.X, m.InsertPoint.Y, m.Height));

                // 导出这个实体的 DXF，看 group code 50 / 210
                var one = new CadDocument();
                one.Header.Version = ACadVersion.AC1032;
                var copy = new MText
                {
                    Value = m.Value,
                    InsertPoint = m.InsertPoint,
                    Height = m.Height,
                    Normal = m.Normal,
                    AttachmentPoint = m.AttachmentPoint
                };
                one.ModelSpace.Entities.Add(copy);
                Console.WriteLine(string.Format("     复制后 Rotation={0:0.####}°", copy.Rotation));
            }

            if (n == 0)
            {
                Console.WriteLine("  （没有旋转的 MTEXT）");
            }

            Console.WriteLine();
            Console.WriteLine("=== 试：把旋转 MTEXT 原样写回 DWG，Rotation 能不能保住 ===");
            TestRoundTrip(doc, path);

            Console.WriteLine();
            Console.WriteLine("=== 试：给旋转 MTEXT 加背景遮罩后写回，Rotation + BGF 能不能一起保住 ===");
            TestWithMask(doc);
        }

        private static void TestRoundTrip(CadDocument doc, string srcPath)
        {
            var mtext = doc.ModelSpace.Entities.OfType<MText>()
                .FirstOrDefault(m => Math.Abs(m.Rotation) > 1e-6);

            if (mtext == null)
            {
                Console.WriteLine("  没有旋转 MTEXT，跳过");
                return;
            }

            string dir = Path.Combine(Path.GetTempPath(), "jun-rotsrc");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "roundtrip.dwg");

            // 整个文档原样写出
            try
            {
                DwgWriter.Write(outPath, doc, null, null);
                var back = DwgReader.Read(outPath);
                var m2 = back.ModelSpace.Entities.OfType<MText>()
                    .FirstOrDefault(m => Math.Abs(m.Rotation) > 1e-6);

                if (m2 == null)
                {
                    Console.WriteLine("  ⚠ 回读后**没有任何旋转的 MTEXT** —— Rotation 在写回时丢了！");
                }
                else
                {
                    Console.WriteLine(string.Format("  ✓ 回读后 Rotation={0:0.####}°", m2.Rotation));
                }

                // 统计旋转 MTEXT 数量
                int src = doc.ModelSpace.Entities.OfType<MText>().Count(m => Math.Abs(m.Rotation) > 1e-6);
                int dst = back.ModelSpace.Entities.OfType<MText>().Count(m => Math.Abs(m.Rotation) > 1e-6);
                Console.WriteLine(string.Format("  旋转 MTEXT 数量：源 {0} → 回读 {1}", src, dst));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  写出失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void TestWithMask(CadDocument doc)
        {
            var mtext = doc.ModelSpace.Entities.OfType<MText>()
                .FirstOrDefault(m => Math.Abs(m.Rotation) > 1e-6);

            if (mtext == null) return;

            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            mtext.BackgroundScale = 1.5;

            string dir = Path.Combine(Path.GetTempPath(), "jun-rotsrc");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "withmask.dwg");

            try
            {
                DwgWriter.Write(outPath, doc, null, null);
                var back = DwgReader.Read(outPath);

                var m2 = back.ModelSpace.Entities.OfType<MText>()
                    .FirstOrDefault(m => Math.Abs(m.Rotation) > 1e-6);

                if (m2 == null)
                {
                    Console.WriteLine("  ⚠ 回读后没有旋转 MTEXT");
                }
                else
                {
                    Console.WriteLine(string.Format("  回读：Rotation={0:0.####}° BGF={1} BScale={2} Normal=({3:0.###},{4:0.###},{5:0.###})",
                        m2.Rotation, m2.BackgroundFillFlags, m2.BackgroundScale,
                        m2.Normal.X, m2.Normal.Y, m2.Normal.Z));
                }

                // 关键：这个 MTEXT 在回读后还在不在？BGF 有没有被写到它身上？
                var m3 = back.ModelSpace.Entities.OfType<MText>().FirstOrDefault(m => m.Value == mtext.Value);
                if (m3 != null)
                {
                    Console.WriteLine(string.Format("  同值 MTEXT 回读：Rotation={0:0.####}° BGF={1}",
                        m3.Rotation, m3.BackgroundFillFlags));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  写出失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
