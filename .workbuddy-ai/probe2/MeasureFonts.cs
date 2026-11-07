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
    /// 用 GDI 差量法（去 padding）实测图纸里每个 TrueType 字体的真实字宽系数（advance / 字高），
    /// 给 Core 的字体字宽表校准。用法： probe2 measurefonts &lt;file.dwg&gt;
    /// </summary>
    internal static class MeasureFonts
    {
        private static readonly string[] CandidateDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            @"C:\Program Files\Autodesk\AutoCAD 2025\Fonts",
            @"C:\Program Files\Autodesk\AutoCAD 2024\Fonts",
            @"C:\Program Files\Common Files\Autodesk Shared\Fonts",
        };

        // 各字体取"平均 advance 系数"用的标准样本（大写/小写/含空格的长句）
        private static readonly Dictionary<string, string> Samples = new Dictionary<string, string>
        {
            { "upper", "ABCDEFGHIJKLMNOPQRSTUVWXYZ" },
            { "lower", "abcdefghijklmnopqrstuvwxyz" },
            { "mixed", "KLD-ZHJT-PIP-LYT-2000ZZ-30210" },
            { "title", "PROVISION OF LEASE OPERATE AND MAINTAIN" },
        };

        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            var fonts = new Dictionary<string, TextStyle>(StringComparer.OrdinalIgnoreCase);
            Collect(doc.ModelSpace, fonts);
            foreach (BlockRecord br in doc.BlockRecords)
            {
                if (br.Name.StartsWith("*", StringComparison.OrdinalIgnoreCase)) continue;
                Collect(br, fonts);
            }

            Console.WriteLine("=== " + Path.GetFileName(path) + " 字体实测（差量法，去 padding）===");
            using (var bmp = new Bitmap(8, 8))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                foreach (var kv in fonts)
                {
                    TextStyle style = kv.Value;
                    string file = style.Filename ?? string.Empty;
                    bool isShx = file.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
                    bool bold = style.TrueType.HasFlag(FontFlags.Bold);
                    bool italic = style.TrueType.HasFlag(FontFlags.Italic);
                    Console.WriteLine();
                    Console.WriteLine(string.Format("字体={0} bold={1} italic={2} shx={3}", file, bold, italic, isShx));
                    if (isShx) { Console.WriteLine("  SHX：GDI 测不了，需 SHX 字形表。"); continue; }

                    Font font = ResolveFont(file, bold, italic);
                    if (font == null) { Console.WriteLine("  找不到可加载 TTF，跳过。"); continue; }

                    foreach (var s in Samples)
                    {
                        double w = RealWidth(g, font, s.Value);
                        double perChar = w / Math.Max(1, s.Value.Length);
                        Console.WriteLine(string.Format("  {0,-6} 总宽={1:0.0} 平均advance={2:0.000}", s.Key, w, perChar));
                    }
                }
            }
        }

        private static void Collect(BlockRecord space, Dictionary<string, TextStyle> fonts)
        {
            foreach (Entity e in space.Entities)
            {
                var text = e as TextEntity;
                var mtext = e as MText;
                var attrib = e as AttributeEntity;
                TextStyle s = text != null ? text.Style : (mtext != null ? mtext.Style : (attrib != null ? attrib.Style : null));
                if (s != null && !fonts.ContainsKey(s.Filename ?? string.Empty)) fonts[s.Filename ?? string.Empty] = s;
            }
        }

        private static double RealWidth(Graphics g, Font font, string text)
        {
            float em = 100f;
            using (var f = new Font(font.FontFamily, em, font.Style))
            {
                // 差量法：realWidth(S) = width(S+X) - width(X)。GDI+ 的 leading/trailing padding 在相减时抵消。
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
