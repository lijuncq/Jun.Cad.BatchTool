using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 诊断：每个 MTEXT 的 DXF41 文字框宽 (RectangleWidth) 相对"真实测得字宽"大多少。
    /// 如果 ratio >> 1，说明遮罩太宽的根因是文字框本身太宽（原生背景按框填，或 tight 取了 min(估算,框宽)）。
    /// 用法： probe2 boxwidth &lt;file.dwg&gt;
    /// </summary>
    internal static class BoxWidth
    {
        private static readonly string[] CandidateDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            @"C:\Program Files\Autodesk\AutoCAD 2025\Fonts",
            @"C:\Program Files\Autodesk\AutoCAD 2024\Fonts",
            @"C:\Program Files\Common Files\Autodesk Shared\Fonts",
        };

        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + Path.GetFileName(path) + " MTEXT 框宽 vs 真实字宽 ===");

            int shown = 0;
            using (var bmp = new Bitmap(8, 8))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                DumpSpace(doc.ModelSpace, g, ref shown);
                foreach (BlockRecord br in doc.BlockRecords)
                {
                    if (br.Name.StartsWith("*", StringComparison.OrdinalIgnoreCase)) continue;
                    DumpSpace(br, g, ref shown);
                }
            }
            Console.WriteLine("共显示 " + shown + " 条 MTEXT（框宽=0 或 框宽>真实宽 1.15 倍）。");
        }

        private static void DumpSpace(BlockRecord space, Graphics g, ref int shown)
        {
            foreach (Entity e in space.Entities)
            {
                var mtext = e as MText;
                if (mtext == null) continue;
                string plain = MTextTokenizer.ToPlainText(mtext.Value ?? string.Empty).Replace("\n", "|").Replace("\r", "");
                if (plain.Length == 0) continue;

                double h = mtext.Height > 0 ? mtext.Height : 2.5;
                double rectW = mtext.RectangleWidth;
                double rectH = mtext.RectangleHeight;

                TextStyle style = mtext.Style;
                string file = style != null ? (style.Filename ?? "") : "";
                bool bold = style != null && style.TrueType.HasFlag(FontFlags.Bold);
                bool italic = style != null && style.TrueType.HasFlag(FontFlags.Italic);

                double realW = MeasureStringWidth(g, file, bold, italic, plain, h);

                string disp = plain.Length > 26 ? plain.Substring(0, 26) + "…" : plain;
                double ratio = realW > 0 ? rectW / realW : 0;
                if (rectW <= 0 || ratio > 1.15)
                {
                    Console.WriteLine(string.Format(
                        "  {0,-6} {1,-7} h={2:0.##} 框宽41={3:0.##} 真实宽={4:0.##} ratio={5:0.00} ({6}行) \"{7}\"",
                        e.Handle.ToString("X"), space.Name.Length > 7 ? space.Name.Substring(0,7) : space.Name,
                        h, rectW, realW, ratio, mtext.Value.Split('\n').Length, disp));
                    shown++;
                }
            }
        }

        /// <summary>用 GDI+ 差量法去 padding：realWidth(S) = width(S+X) - width(X)。</summary>
        private static double MeasureStringWidth(Graphics g, string file, bool bold, bool italic, string text, double height)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            Font font = ResolveFont(file, bold, italic);
            if (font == null) return 0;
            float em = (float)height;
            using (var f = new Font(font.FontFamily, em, font.Style))
            {
                float wS = g.MeasureString(text + "X", f).Width;
                float wX = g.MeasureString("X", f).Width;
                return wS - wX;
            }
        }

        private static Font ResolveFont(string file, bool bold, bool italic)
        {
            string baseName = Path.GetFileNameWithoutExtension(file) ?? file;
            FontStyle fs = FontStyle.Regular;
            if (bold) fs |= FontStyle.Bold;
            if (italic) fs |= FontStyle.Italic;

            try { var fam = new FontFamily(baseName); if (fam.IsStyleAvailable(fs)) return new Font(fam, 100f, fs); } catch { }
            foreach (string dir in CandidateDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string full = Path.Combine(dir, file);
                if (File.Exists(full))
                {
                    try
                    {
                        var pfc = new PrivateFontCollection();
                        pfc.AddFontFile(full);
                        var fam = pfc.Families[0];
                        FontStyle avail = fam.IsStyleAvailable(fs) ? fs : FontStyle.Regular;
                        return new Font(fam, 100f, avail);
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
