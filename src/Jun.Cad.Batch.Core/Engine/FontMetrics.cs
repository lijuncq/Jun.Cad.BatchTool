using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using ACadSharp.Tables;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 字体字宽度量。
    ///
    /// AutoCAD 文字遮罩的宽度唯一取决于"文字真实渲染宽度"。库（ACadSharp）没有字形度量，
    /// 旧实现用一个常量 <c>LatinWidthFactor</c> 硬估——对所有字体一刀切，
    /// 粗宽字体（~0.78）遮罩漏字、窄字体（Arial Narrow ~0.61）遮罩多出一大截，
    /// 怎么调都满足不了一类又一类图纸（见 2026-11-07 连续两次返工）。
    ///
    /// 本类按"能实测就实测、测不了再查表"的原则给宽度：
    ///   1. TrueType 且本机可加载 → 用 GDI 实测**整串**真实宽度（差量法去掉 GDI+ 留白），
    ///      大写密集的标签（A-FRAME）和大小写混合的正文分别得到各自正确的宽度；
    ///   2. SHX / 本机没有的字体 → 按字体文件名查"平均 advance 系数"表（已在 Windows 上
    ///      用同款字体 GDI 校准），再叠 bold/italic 倍率；
    ///   3. 都查不到 → <c>DefaultLatinFactor</c> 兜底。
    ///
    /// 实测只在 Windows 上做，且包在 try/catch 里；非 Windows 或字体加载失败一律走查表/兜底，
    /// 因此 Core 仍可交叉编译（System.Drawing.Common 在 Linux 仅运行时不触发字体度量）。
    /// </summary>
    public static class FontMetrics
    {
        /// <summary>未知西文字体（TTF）的平均 advance 系数兜底。</summary>
        public const double DefaultLatinFactor = 0.66;

        /// <summary>未知 SHX 字体的平均 advance 系数兜底（SHX 普遍比 TTF 窄）。</summary>
        public const double DefaultShxLatinFactor = 0.58;

        /// <summary>粗体倍率（TrueType Bold 的真实 advance 约比 Regular 宽 12%~18%）。</summary>
        public const double BoldMultiplier = 1.15;

        /// <summary>斜体倍率。</summary>
        public const double ItalicMultiplier = 1.05;

        /// <summary>
        /// 常见西文字体"平均 advance 系数"（advance / 字高），按字体文件名（小写、去扩展名）查。
        /// 数值在 Windows 上用 GDI 差量法测"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -.:"
        /// 这条混合样本取得，约等于工程图里典型正文的每字符平均宽度。
        /// </summary>
        private static readonly Dictionary<string, double> AdvanceTable = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // —— TrueType ——
            { "arialn", 0.61 },        // Arial Narrow
            { "arialnarrow", 0.61 },
            { "arial", 0.66 },
            { "arialblack", 0.92 },
            { "isocpeur", 0.64 },      // AutoCAD ISO 字体（ISOCP 家族）
            { "iso", 0.64 },           // iso*.ttf / iso*.shx 家族兜底
            { "isocteur", 0.64 },
            { "times", 0.50 },
            { "timesnewroman", 0.50 },
            { "calibri", 0.55 },
            { "tahoma", 0.58 },
            { "verdana", 0.58 },
            { "cambria", 0.56 },
            { "consolas", 0.60 },
            { "simsun", 1.0 },         // 中文等宽，CJK 另走 WideWidthFactor，这里只兜底西文
            // —— SHX（AutoCAD 自带矢量字体，普遍比 TTF 窄）——
            { "txt", 0.58 },           // AutoCAD 默认
            { "romans", 0.56 },        // Roman Simplex
            { "simplex", 0.60 },
            { "romand", 0.86 },        // Roman Duplex（偏宽）
            { "roman", 0.86 },
            { "complex", 0.92 },       // Roman Complex（最宽）
            { "italics", 0.55 },
            { "greeks", 0.58 },
        };

        /// <summary>
        /// AutoCAD 实际渲染宽度 / GDI advance 测量值的**字体级修正系数**。
        ///
        /// GDI 测的是字形 advance，AutoCAD 在渲染 TTF 时对某些字体用的字宽与之不同——
        /// 最典型的是 ISOCP 家族（isocpeur.ttf）：AutoCAD 实测比 GDI 宽约 12%。
        /// 2026-11-07 用 AutoCAD 2025 `textbox` 对 5 组字符串做标定，AutoCAD/GDI 比值极稳定
        /// （isocpeur 1.116~1.121、ARIALN 1.009~1.015），与内容无关，故可用单一字体系数修正。
        ///
        /// 只对 **TTF** 生效；SHX 走查表且实测比值≈1.0，不修正。
        /// 无此表项 / 无法判断时返回 1.0（即信任 GDI 测量）。
        /// </summary>
        public static double CorrectionFactor(TextStyle style, bool isShx)
        {
            if (isShx || style == null)
            {
                return 1.0;
            }

            string file = style.Filename ?? string.Empty;
            string key = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(key))
            {
                return 1.0;
            }

            if (key.StartsWith("iso", StringComparison.OrdinalIgnoreCase))
            {
                return 1.12;   // ISOCP 家族（isocpeur / isocp / isoct）
            }

            if (key.Equals("arialn", StringComparison.OrdinalIgnoreCase)
                || key.Equals("arialnarrow", StringComparison.OrdinalIgnoreCase))
            {
                return 1.015;
            }

            return 1.0;
        }

        private static readonly object Sync = new object();

        // 字体对象缓存：key = 字体文件名(小写去扩展名) + "|" + style标记
        private static readonly Dictionary<string, Font> LoadedFonts = new Dictionary<string, Font>();

        // 实测整串宽度缓存：key = 字体对象key + "|" + 文本
        private static readonly Dictionary<string, double> MeasuredWidths = new Dictionary<string, double>();

        /// <summary>
        /// 取某文字样式的"平均 advance 系数"（查表 / 兜底），不含 bold/italic 倍率。
        /// 用于 SHX 或本机无法实测的 TTF 的逐字符估算。
        /// </summary>
        public static double BaseAdvance(TextStyle style, bool isShx)
        {
            if (style == null)
            {
                return isShx ? DefaultShxLatinFactor : DefaultLatinFactor;
            }

            string file = style.Filename ?? string.Empty;
            string key = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(key))
            {
                return isShx ? DefaultShxLatinFactor : DefaultLatinFactor;
            }

            double factor;
            lock (Sync)
            {
                if (AdvanceTable.TryGetValue(key, out factor))
                {
                    return factor;
                }

                // 前缀匹配（iso*.ttf / iso*.shx 等不确定扩展名的）
                if (key.StartsWith("iso", StringComparison.OrdinalIgnoreCase))
                {
                    return 0.64;
                }
            }

            return isShx ? DefaultShxLatinFactor : DefaultLatinFactor;
        }

        /// <summary>
        /// 该样式是否 SHX（矢量）字体——SHX 走查表，不走 GDI 实测。
        /// </summary>
        public static bool IsShx(TextStyle style)
        {
            string file = style != null ? (style.Filename ?? string.Empty) : string.Empty;
            return file.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 真实度量某一字符串在某样式下的渲染宽度（绘制单位）。
        /// 返回 null 表示无法实测（非 Windows / 字体缺失 / SHX / 任何异常），调用方应降级到查表。
        /// </summary>
        public static double? TryMeasureWidth(TextStyle style, bool isShx, string text, double emHeight)
        {
            if (style == null || string.IsNullOrEmpty(text) || emHeight <= 0 || isShx)
            {
                return null;
            }

            Font font = TryLoadFont(style);
            if (font == null)
            {
                return null;
            }

            string cacheKey = FontCacheKey(style) + "|" + text;
            lock (Sync)
            {
                double cached;
                if (MeasuredWidths.TryGetValue(cacheKey, out cached))
                {
                    return cached * emHeight;
                }
            }

            try
            {
                double perEm = MeasureStringPerEm(font, text);
                if (perEm <= 0)
                {
                    return null;
                }

                lock (Sync)
                {
                    MeasuredWidths[cacheKey] = perEm;
                }

                return perEm * emHeight;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 取字体缓存 key（区分粗体/斜体）。
        /// </summary>
        private static string FontCacheKey(TextStyle style)
        {
            string file = style.Filename ?? string.Empty;
            string key = Path.GetFileNameWithoutExtension(file) ?? file;
            bool bold = style.TrueType.HasFlag(FontFlags.Bold);
            bool italic = style.TrueType.HasFlag(FontFlags.Italic);
            return key + "|" + (bold ? "B" : "") + (italic ? "I" : "");
        }

        /// <summary>
        /// 加载字体对象并缓存。优先按家族名（如 "Arial Narrow"），再按 AutoCAD / Windows 字体目录里的文件名加载。</summary>
        private static Font TryLoadFont(TextStyle style)
        {
            string cacheKey = FontCacheKey(style);
            lock (Sync)
            {
                Font existing;
                if (LoadedFonts.TryGetValue(cacheKey, out existing))
                {
                    return existing;
                }
            }

            Font font = ResolveFont(style);
            if (font != null)
            {
                lock (Sync)
                {
                    LoadedFonts[cacheKey] = font;
                }
            }

            return font;
        }

        private static Font ResolveFont(TextStyle style)
        {
            string file = style.Filename ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(file) ?? file;
            FontStyle fs = FontStyle.Regular;
            if (style.TrueType.HasFlag(FontFlags.Bold)) fs |= FontStyle.Bold;
            if (style.TrueType.HasFlag(FontFlags.Italic)) fs |= FontStyle.Italic;

            // 1) 按家族名直接找（Windows 已安装的字体，如 "Arial Narrow"）
            try
            {
                var fam = new FontFamily(baseName);
                if (fam.IsStyleAvailable(fs))
                {
                    return new Font(fam, 1f, fs);
                }
            }
            catch
            {
                // 名字不对，继续
            }

            // 2) 从文件加载（AutoCAD / Windows 字体目录里的 ttf）
            foreach (string dir in FontSearchDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                string full = Path.Combine(dir, file);
                if (File.Exists(full))
                {
                    try
                    {
                        var pfc = new PrivateFontCollection();
                        pfc.AddFontFile(full);
                        FontFamily fam = pfc.Families[0];
                        FontStyle avail = fam.IsStyleAvailable(fs) ? fs : FontStyle.Regular;
                        return new Font(fam, 1f, avail);
                    }
                    catch
                    {
                        // 换下一个目录
                    }
                }
            }

            return null;
        }

        private static readonly string[] FontSearchDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            @"C:\Program Files\Autodesk\AutoCAD 2025\Fonts",
            @"C:\Program Files\Autodesk\AutoCAD 2024\Fonts",
            @"C:\Program Files\Autodesk\AutoCAD 2023\Fonts",
            @"C:\Program Files\Common Files\Autodesk Shared\Fonts",
        };

        /// <summary>
        /// 用 GDI+ 差量法测量字符串在给定字体下的"每 em 宽度"（去除 GDI+ 自带留白）。
        /// realWidth(S) = (width(S + X) - width(X))，leading/trailing padding 在相减时抵消。
        /// 在 em=1000 下测，再除以 1000，得到与字号无关的每 em 宽度系数。
        /// </summary>
        private static double MeasureStringPerEm(Font font, string text)
        {
            const float em = 1000f;
            using (var f = new Font(font.FontFamily, em, font.Style))
            using (var bmp = new Bitmap(8, 8))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                float wS = g.MeasureString(text + "X", f).Width;
                float wX = g.MeasureString("X", f).Width;
                double width = wS - wX;
                return width / em;
            }
        }
    }
}
