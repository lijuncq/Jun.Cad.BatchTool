using ACadSharp;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.IO;
using Jun.Cad.Batch.Engine;
using System;
using System.Collections.Generic;
using System.IO;

namespace Probe
{
    /// <summary>诊断：对文件里的单行 MTEXT 计算紧贴遮罩框，与同空间 Wipeout 框对比，找去重漏判的原因。</summary>
    public static class Miss
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + Path.GetFileName(path) + " 去重漏判诊断 ===");
            foreach (ACadSharp.Tables.BlockRecord space in Spaces(doc))
            {
                var wipes = new List<Bounds2d>();
                foreach (Entity e in space.Entities)
                {
                    var w = e as Wipeout;
                    if (w == null) continue;
                    Bounds2d b = TextBounds.ForWipeout(w);
                    if (b.IsValid && b.Area > 0) wipes.Add(b);
                }
                if (wipes.Count == 0) continue;

                foreach (Entity e in space.Entities)
                {
                    var m = e as MText;
                    if (m == null) continue;
                    var lines = MTextTokenizer.ToLines(m.Value);
                    if (lines.Count != 1) continue;
                    double tight = TextBounds.MeasureMTextTightWidth(m);
                    if (m.RectangleWidth > 0 && m.RectangleWidth < tight * 0.95) continue; // 会折行，走原生
                    XY[] verts = TextBounds.TextMaskVertices(m, 0.15 * m.Height, tightFrame: true);
                    Bounds2d target = TextBounds.AabbOf(verts);
                    if (!target.IsValid) continue;

                    // 找最接近的 wipeout
                    Bounds2d best = default;
                    double bestScore = double.MaxValue;
                    foreach (Bounds2d b in wipes)
                    {
                        double s = Math.Abs(b.CenterX - target.CenterX) + Math.Abs(b.CenterY - target.CenterY);
                        if (s < bestScore) { bestScore = s; best = b; }
                    }
                    double tol = Math.Max(1e-4, Math.Max(target.Width, target.Height) * 0.01);
                    bool dup =
                        Math.Abs(best.CenterX - target.CenterX) <= tol &&
                        Math.Abs(best.CenterY - target.CenterY) <= tol &&
                        Math.Abs(best.Width - target.Width) <= tol &&
                        Math.Abs(best.Height - target.Height) <= tol;
                    if (!dup && bestScore < 3)
                    {
                        Console.WriteLine(string.Format(
                            "  [{0}] MTEXT {1} rot={2:0.###} 目标框 {3}",
                            space.Name, m.Handle.ToString("X"), m.Rotation * 180 / Math.PI, target));
                        Console.WriteLine(string.Format(
                            "      最近Wipeout {0}  dCenter=({1:0.000},{2:0.000}) dSize=({3:0.000},{4:0.000}) tol={5:0.000}",
                            best,
                            best.CenterX - target.CenterX, best.CenterY - target.CenterY,
                            best.Width - target.Width, best.Height - target.Height, tol));
                    }
                }
            }
        }


        /// <summary>打印指定句柄 Wipeout 的 UVector/VVector/InsertPoint/ClipBoundary。</summary>
        public static void Wipe(string path, string[] handles)
        {
            CadDocument doc = DwgReader.Read(path);
            foreach (ACadSharp.Tables.BlockRecord space in Spaces(doc))
            {
                foreach (Entity e in space.Entities)
                {
                    var w = e as Wipeout;
                    if (w == null) continue;
                    string h = w.Handle.ToString("X");
                    if (Array.IndexOf(handles, h) < 0) continue;
                    Console.WriteLine(string.Format("{0} [{1}] ins={2} U={3} V={4} size={5} clip={6}",
                        h, space.Name, w.InsertPoint, w.UVector, w.VVector, w.Size,
                        string.Join(" | ", w.ClipBoundaryVertices)));
                    Bounds2d b = TextBounds.ForWipeout(w);
                    Console.WriteLine("   -> 盒 " + b);
                }
            }
        }

        private static IEnumerable<ACadSharp.Tables.BlockRecord> Spaces(CadDocument doc)
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
    }
}
