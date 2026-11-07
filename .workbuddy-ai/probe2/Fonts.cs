using System;
using System.Collections.Generic;
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
    /// 列出一张图里所有文字实体的字体信息，用来判断"遮罩太宽/太窄"是不是字体估计系数不对。
    /// 用法： probe2 fonts &lt;file.dwg&gt;
    /// </summary>
    internal static class Fonts
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + Path.GetFileName(path) + " ===");

            var seen = new Dictionary<string, int>();
            DumpSpace(doc.ModelSpace, "模型", seen);
            foreach (BlockRecord br in doc.BlockRecords)
            {
                if (br.Name.StartsWith("*", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DumpSpace(br, "块 " + br.Name, seen);
            }

            Console.WriteLine();
            Console.WriteLine("--- 用到的字体（去重） ---");
            foreach (var kv in seen)
            {
                Console.WriteLine("  " + kv.Key + "  ×" + kv.Value);
            }
        }

        private static void DumpSpace(BlockRecord space, string where, Dictionary<string, int> seen)
        {
            foreach (Entity e in space.Entities)
            {
                var text = e as TextEntity;
                AttributeEntity attrib = e as AttributeEntity;
                var mtext = e as MText;
                if (text == null && attrib == null && mtext == null)
                {
                    continue;
                }

                string value = text != null ? text.Value
                    : attrib != null ? attrib.Value
                    : MTextTokenizer.ToPlainText(mtext.Value ?? string.Empty);
                value = (value ?? string.Empty).Replace("\n", " ").Replace("\r", " ").Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                TextStyle style = text != null ? text.Style
                    : attrib != null ? attrib.Style
                    : mtext.Style;
                string fontFile = style != null ? (style.Filename ?? string.Empty) : "(无样式)";
                bool isShx = fontFile.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
                string ttfFlags = style != null ? style.TrueType.ToString() : "-";
                bool bold = style != null && style.TrueType.HasFlag(FontFlags.Bold);
                bool italic = style != null && style.TrueType.HasFlag(FontFlags.Italic);

                double height = text != null ? text.Height : (mtext != null ? mtext.Height : attrib.Height);
                double widthFactor = text != null ? text.WidthFactor
                    : attrib != null ? attrib.WidthFactor : 1.0;
                if (widthFactor <= 0) widthFactor = 1.0;

                int latin = 0, wide = 0;
                foreach (char c in value)
                {
                    if (TextBounds.IsWide(c)) wide++;
                    else latin++;
                }

                double est = TextBounds.MeasureWidth(value, height * widthFactor);

                string fontKey = (isShx ? "SHX:" : "TTF:") + fontFile + (bold ? " BOLD" : "") + (italic ? " ITALIC" : "");
                if (!seen.ContainsKey(fontKey)) seen[fontKey] = 0;
                seen[fontKey]++;

                string shown = value.Length > 30 ? value.Substring(0, 30) + "…" : value;
                Console.WriteLine(string.Format(
                    "  {0,-6} {1,-9} {2} h={3:0.##} wf={4:0.##} L{5}/W{6} 估计宽={7:0.##} | {8} [{9}] \"{10}\"",
                    e.Handle.ToString("X"), where, e.GetType().Name, height, widthFactor, latin, wide, est,
                    fontFile, ttfFlags, shown));
            }
        }
    }
}
