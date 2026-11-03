using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 诊断"文字遮罩不成功"：把截图里那种**旋转 90° 的文字**找出来，
    /// 打印它们的类型、旋转角、原生遮罩标志、包围盒，
    /// 以及 Wipeout 在块内的坐标是否和文字对齐。
    ///
    /// 关键怀疑点：
    ///   1. MTEXT 原生背景遮罩（BackgroundFillFlags）在**旋转文字**上画出来是不是跟着转；
    ///   2. Wipeout 遮罩只按轴对齐矩形建，旋转文字的矩形是 AABB，实际盖不住斜的文字。
    /// </summary>
    internal static class MaskDiag
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);

            Console.WriteLine("=== 空间概览 ===");
            foreach (BlockRecord br in doc.BlockRecords)
            {
                Console.WriteLine(string.Format("  块 {0,-28} 实体 {1}", br.Name, br.Entities.Count));
            }

            Console.WriteLine();
            Console.WriteLine("=== 模型空间里带旋转的文字实体 ===");

            int rotCount = 0;
            int total = 0;
            var byKind = new Dictionary<string, int>();

            foreach (BlockRecord space in new[] { doc.ModelSpace, doc.PaperSpace })
            {
                if (space == null) continue;

                foreach (Entity e in space.Entities)
                {
                    var mtext = e as MText;
                    var text = e as TextEntity;
                    if (mtext == null && text == null) continue;
                    if (text is AttributeBase) continue;

                    total++;
                    string kind = mtext != null ? "MTEXT" : "TEXT";
                    string rotKey = kind;
                    byKind[rotKey] = byKind.ContainsKey(rotKey) ? byKind[rotKey] + 1 : 1;

                    double rot = mtext != null ? mtext.Rotation : text.Rotation;
                    double h = mtext != null ? mtext.Height : text.Height;
                    string val = mtext != null ? mtext.Value : text.Value;
                    string plain = MTextTokenizer.ToPlainText(val ?? string.Empty);
                    if (plain.Length > 30) plain = plain.Substring(0, 30) + "…";

                    bool rotated = Math.Abs(rot) > 1e-6;
                    if (!rotated) continue;

                    rotCount++;
                    if (rotCount > 25) continue;

                    string flags = "";
                    if (mtext != null)
                    {
                        flags = string.Format("BGF={0}", mtext.BackgroundFillFlags);
                    }

                    Bounds2d b;
                    bool ok = TextBounds.TryGet(e, out b);

                    Console.WriteLine(string.Format(
                        "  {0,-8} rot={1:0.###} h={2:0.###} '{3}' {4} 矩形={5} {6}",
                        kind, rot, h, plain.Replace("\n", "|"), flags,
                        ok ? b.ToString() : "取不到",
                        e.Layer == null ? "" : ("图层=" + e.Layer.Name)));
                }
            }

            Console.WriteLine(string.Format("  文字实体总数 {0}，其中带旋转 {1}", total, rotCount));

            Console.WriteLine();
            Console.WriteLine("=== 含 LIFTING / COAMING / DRAW LIMIT / HATCH 的文字 ===");
            foreach (BlockRecord space in new[] { doc.ModelSpace, doc.PaperSpace })
            {
                if (space == null) continue;

                foreach (Entity e in space.Entities)
                {
                    var mtext = e as MText;
                    var text = e as TextEntity;
                    if (mtext == null && text == null) continue;
                    if (text is AttributeBase) continue;

                    string val = mtext != null ? mtext.Value : text.Value;
                    string plain = (val ?? string.Empty).ToUpperInvariant();
                    if (plain.IndexOf("LIFTING", StringComparison.Ordinal) < 0
                        && plain.IndexOf("COAMING", StringComparison.Ordinal) < 0
                        && plain.IndexOf("DRAW LIMIT", StringComparison.Ordinal) < 0
                        && plain.IndexOf("HATCH", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    double rot = mtext != null ? mtext.Rotation : text.Rotation;
                    double h = mtext != null ? mtext.Height : text.Height;
                    Bounds2d b;
                    bool ok = TextBounds.TryGet(e, out b);

                    double rectW = mtext != null ? mtext.RectangleWidth : 0;
                    double rectH = mtext != null ? mtext.RectangleHeight : 0;
                    double bgScale = mtext != null ? mtext.BackgroundScale : 0;
                    string bgfStr = mtext != null ? mtext.BackgroundFillFlags.ToString() : "-";

                    Console.WriteLine(string.Format(
                        "  {0,-8} rot={1:0.###} h={2:0.###} '{3}' BGF={4} RectW={5:0.##} RectH={6:0.##} BgScale={7:0.##} 矩形={8} 宽{9:0.##} 高{10:0.##} 图层={11}",
                        mtext != null ? "MTEXT" : "TEXT", rot, h,
                        MTextTokenizer.ToPlainText(val ?? string.Empty).Replace("\n", "|"),
                        bgfStr, rectW, rectH, bgScale,
                        ok ? b.ToString() : "取不到",
                        ok ? b.Width : 0, ok ? b.Height : 0,
                        e.Layer == null ? "" : e.Layer.Name));
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 标注 *D 块内的遮罩 vs 文字位置（BlockInternal 路径）===");
            CheckDimensionBlocks(doc);

            Console.WriteLine();
            Console.WriteLine("=== Wipeout 实体清单 ===");
            int wipeTotal = 0;
            foreach (BlockRecord br in doc.BlockRecords)
            {
                foreach (Entity e in br.Entities)
                {
                    if (!(e is Wipeout)) continue;
                    wipeTotal++;
                    if (wipeTotal > 20) continue;

                    var w = (Wipeout)e;
                    Bounds2d b;
                    bool ok = TextBounds.TryGet(w, out b);
                    Console.WriteLine(string.Format(
                        "  块 {0,-22} 插点({1:0.##},{2:0.##}) U({3:0.##},{4:0.##}) V({5:0.##},{6:0.##}) 顶点{7} 矩形{8} 图层={9}",
                        br.Name, w.InsertPoint.X, w.InsertPoint.Y,
                        w.UVector.X, w.UVector.Y, w.VVector.X, w.VVector.Y,
                        w.ClipBoundaryVertices.Count,
                        ok ? b.ToString() : "取不到",
                        w.Layer == null ? "" : w.Layer.Name));
                }
            }
            Console.WriteLine("  Wipeout 总数: " + wipeTotal);

            Console.WriteLine();
            Console.WriteLine("=== 遮罩图层状态 ===");
            foreach (Layer layer in doc.Layers)
            {
                if (layer.Name.IndexOf("MASK", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Console.WriteLine(string.Format("  图层 {0}: IsOn={1} Flags={2} Plot={3} Color={4}",
                    layer.Name, layer.IsOn, layer.Flags, layer.PlotFlag, layer.Color));
            }
        }

        private static void CheckDimensionBlocks(CadDocument doc)
        {
            int shown = 0;
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                var dim = e as Dimension;
                if (dim == null || dim.Block == null) continue;

                Wipeout wipe = null;
                MText mt = null;
                TextEntity tx = null;
                foreach (Entity child in dim.Block.Entities)
                {
                    if (child is Wipeout) wipe = (Wipeout)child;
                    else if (child is MText) mt = (MText)child;
                    else if (child is TextEntity) tx = (TextEntity)child;
                }

                if (wipe == null && mt == null && tx == null) continue;
                shown++;
                if (shown > 12) continue;

                XYZ anchor = default(XYZ);
                double h = 0, rot = 0;
                if (mt != null) { anchor = mt.InsertPoint; h = mt.Height; rot = mt.Rotation; }
                else if (tx != null) { anchor = tx.InsertPoint; h = tx.Height; rot = tx.Rotation; }

                string txt = mt != null ? mt.Value : (tx != null ? tx.Value : "");
                Bounds2d wb = default(Bounds2d);
                bool wok = wipe != null && TextBounds.TryGet(wipe, out wb);

                Console.WriteLine(string.Format(
                    "  DIM 句柄{0} 文字({1:0.##},{2:0.##}) rot={3:0.###} h={4:0.###} '{5}' 遮罩={6} 遮罩矩形={7}",
                    dim.Handle.ToString("X"), anchor.X, anchor.Y, rot, h,
                    MTextTokenizer.ToPlainText(txt ?? "").Replace("\n", "|"),
                    wipe == null ? "无" : "有",
                    wok ? wb.ToString() : "-"));

                if (wipe != null)
                {
                    Console.WriteLine(string.Format("      遮罩插点({0:0.##},{1:0.##}) U({2:0.##},{3:0.##}) V({4:0.##},{5:0.##}) 顶点数={6}",
                        wipe.InsertPoint.X, wipe.InsertPoint.Y,
                        wipe.UVector.X, wipe.UVector.Y, wipe.VVector.X, wipe.VVector.Y,
                        wipe.ClipBoundaryVertices.Count));
                }
            }
            Console.WriteLine("  标注总数（带块）: " + shown);
        }
    }
}
