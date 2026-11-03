using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 验证"旋转 MTEXT 的原生背景遮罩"到底写不写得对。
    ///
    /// 做法：造一个 90° 旋转的 MTEXT，开背景遮罩，写成 DWG 再读回来，
    /// 看 Rotation / BackgroundFillFlags / BackgroundScale / 矩形 是什么。
    /// 同时导出 DXF，肉眼核对 group code 41/71/50 等。
    ///
    /// 顺便验证 Wipeout 的 U/V 向量能不能表达"跟着文字转"——
    /// 这决定修复方案是"原生背景"还是"自己造旋转 Wipeout"。
    /// </summary>
    internal static class RotMask
    {
        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);

            // ---- 案例 A：90° 旋转的 MTEXT + 原生背景遮罩 ----
            CaseA(dir);
            Console.WriteLine();

            // ---- 案例 B：把旋转 MTEXT 的背景属性 dump 到 DXF ----
            CaseB(dir);
            Console.WriteLine();

            // ---- 案例 C：Wipeout 用 U/V 表达旋转，验证回读 ----
            CaseC(dir);
        }

        private static void CaseA(string dir)
        {
            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            // Rotation 是只读的：由 Normal 推导。绕 Z 轴转 90° → Normal = (sin, -cos, 0)
            var mtext = new MText
            {
                Value = "DRAW LIMIT",
                InsertPoint = new XYZ(100, 100, 0),
                Height = 2.5,
                Normal = new XYZ(1, 0, 0),
                AttachmentPoint = AttachmentPointType.MiddleLeft
            };

            Console.WriteLine("=== 案例 A：旋转 MTEXT + 原生背景遮罩 ===");

            // 先摸清 Normal → Rotation 的关系
            foreach (double deg in new[] { 0, 30, 45, 90, 180, 270 })
            {
                double rad = deg * Math.PI / 180.0;
                var m = new MText { Normal = new XYZ(Math.Sin(rad), -Math.Cos(rad), 0) };
                Console.WriteLine(string.Format("  Normal=({0:0.###},{1:0.###}) → Rotation={2:0.####} rad = {3:0.##}°",
                    m.Normal.X, m.Normal.Y, m.Rotation, m.Rotation * 180 / Math.PI));
            }

            // 用 90° 的 Normal 构造目标实体
            mtext.Normal = new XYZ(1, 0, 0);
            Console.WriteLine(string.Format("  写入前：Rotation={0:0.####} BGF={1} BScale={2}",
                mtext.Rotation, mtext.BackgroundFillFlags, mtext.BackgroundScale));

            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            mtext.BackgroundScale = 1.5;

            doc.ModelSpace.Entities.Add(mtext);

            string path = Path.Combine(dir, "rot-mtext.dwg");
            DwgWriter.Write(path, doc, null, null);

            var back = DwgReader.Read(path);
            var m2 = back.ModelSpace.Entities.OfType<MText>().FirstOrDefault();
            if (m2 == null)
            {
                Console.WriteLine("  回读失败：没有 MText");
                return;
            }

            Console.WriteLine(string.Format("  回读后：Rotation={0:0.####} BGF={1} BScale={2} Height={3} 插入点({4:0.##},{5:0.##})",
                m2.Rotation, m2.BackgroundFillFlags, m2.BackgroundScale, m2.Height,
                m2.InsertPoint.X, m2.InsertPoint.Y));

            Console.WriteLine(string.Format("  Normal=({0:0.##},{1:0.##},{2:0.##})  ← 旋转的轴",
                m2.Normal.X, m2.Normal.Y, m2.Normal.Z));

            Bounds2d b;
            if (TextBounds.TryGet(m2, out b))
            {
                Console.WriteLine(string.Format("  TextBounds 矩形={0} 宽{1:0.##} 高{2:0.##}", b, b.Width, b.Height));
            }
        }

        private static void CaseB(string dir)
        {
            Console.WriteLine("=== 案例 B：旋转 MTEXT 的 DXF 输出 ===");

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            var mtext = new MText
            {
                Value = "DRAW LIMIT",
                InsertPoint = new XYZ(100, 100, 0),
                Height = 2.5,
                Normal = new XYZ(1, 0, 0),
                AttachmentPoint = AttachmentPointType.MiddleLeft
            };
            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;
            mtext.BackgroundScale = 1.5;
            doc.ModelSpace.Entities.Add(mtext);

            string path = Path.Combine(dir, "rot-mtext.dxf");
            try
            {
                DxfWriter.Write(path, doc);
                Console.WriteLine("  DXF 已写出：" + path);

                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == "MTEXT")
                    {
                        Console.WriteLine("  --- MTEXT 段 ---");
                        // 从这里往后打印 60 行，够看完关键 group code
                        for (int j = i; j < Math.Min(i + 70, lines.Length); j += 2)
                        {
                            string code = lines[j].Trim();
                            string val = j + 1 < lines.Length ? lines[j + 1] : "";
                            Console.WriteLine(string.Format("    {0,-4} {1}", code, val));
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  DXF 写出失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void CaseC(string dir)
        {
            Console.WriteLine("=== 案例 C：Wipeout 用 U/V 表达旋转 ===");

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            var def = new ImageDefinition("*JUN_TEST")
            {
                Size = new XY(1, 1),
                DefaultSize = new XY(1, 1),
                FileName = string.Empty
            };
            doc.ImageDefinitions.Add(def);

            // 90° 旋转：U = +Y, V = -X（AutoCAD 绕 Z 转 90° 的标准写法）
            var wipe = new Wipeout
            {
                InsertPoint = new XYZ(100, 100, 0),
                Size = new XY(1, 1),
                Definition = def,
                UVector = new XYZ(0, 1, 0),
                VVector = new XYZ(-1, 0, 0)
            };
            wipe.ClipBoundaryVertices.Add(new XY(0, 0));
            wipe.ClipBoundaryVertices.Add(new XY(2.5, 15.5));
            doc.ModelSpace.Entities.Add(wipe);

            string path = Path.Combine(dir, "rot-wipeout.dwg");
            DwgWriter.Write(path, doc, null, null);

            var back = DwgReader.Read(path);
            var w2 = back.ModelSpace.Entities.OfType<Wipeout>().FirstOrDefault();
            if (w2 == null)
            {
                Console.WriteLine("  回读失败：没有 Wipeout");
                return;
            }

            Console.WriteLine(string.Format("  回读后：插点({0:0.##},{1:0.##}) U({2:0.##},{3:0.##}) V({4:0.##},{5:0.##}) 顶点{6}",
                w2.InsertPoint.X, w2.InsertPoint.Y,
                w2.UVector.X, w2.UVector.Y, w2.VVector.X, w2.VVector.Y,
                w2.ClipBoundaryVertices.Count));

            Bounds2d b;
            if (TextBounds.TryGet(w2, out b))
            {
                Console.WriteLine(string.Format("  TextBounds 矩形={0} 宽{1:0.##} 高{2:0.##}", b, b.Width, b.Height));
            }

            Console.WriteLine(string.Format("  顶点数={0} 内容={1}",
                w2.ClipBoundaryVertices.Count,
                string.Join(",", w2.ClipBoundaryVertices.Select(v => string.Format("({0:0.##},{1:0.##})", v.X, v.Y)))));

            // DXF 同样导出，核对 group code 11/12/21/22/13/23
            string dxf = Path.Combine(dir, "rot-wipeout.dxf");
            try
            {
                DxfWriter.Write(dxf, doc);
                var lines = File.ReadAllLines(dxf);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == "WIPEOUT")
                    {
                        Console.WriteLine("  --- WIPEOUT 段 ---");
                        for (int j = i; j < Math.Min(i + 60, lines.Length); j += 2)
                        {
                            string code = lines[j].Trim();
                            string val = j + 1 < lines.Length ? lines[j + 1] : "";
                            Console.WriteLine(string.Format("    {0,-4} {1}", code, val));
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  DXF 写出失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
