using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconLab
{
    /// <summary>
    /// 图标概念实验室：把每个候选方案按**真实像素尺寸**画出来（不是缩略示意图），
    /// 并打包成多尺寸 .ico。
    ///
    /// 为什么不用 SVG 再转：小尺寸图标（16/32）靠的是"笔画粗细是否撑得住"，
    /// 只有在目标像素上直接光栅化才看得准；先画大图再缩小会把细节糊掉，
    /// 让人误以为某个方案在 16px 下也能看。
    /// </summary>
    internal static class Program
    {
        private static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };

        // ---------------- 调色板 ----------------
        private static readonly Color BlueDeep = Color.FromArgb(0x12, 0x53, 0x9F);
        private static readonly Color Blue = Color.FromArgb(0x1E, 0x6F, 0xD9);
        private static readonly Color BlueLight = Color.FromArgb(0x2A, 0x7B, 0xE4);
        private static readonly Color SheetBack = Color.FromArgb(0xD7, 0xE6, 0xF7);
        private static readonly Color SheetMid = Color.FromArgb(0xE8, 0xF1, 0xFB);
        private static readonly Color SheetBorder = Color.FromArgb(0x9F, 0xBE, 0xDF);
        private static readonly Color TextLine = Color.FromArgb(0xC3, 0xD6, 0xEC);
        private static readonly Color White = Color.White;

        private static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "sheet")
            {
                DrawSheet(args[1]);
                return;
            }

            if (args.Length >= 3 && args[0] == "ico")
            {
                WriteIco(args[1], args[2]);
                return;
            }

            if (args.Length >= 2 && args[0] == "all")
            {
                foreach (string key in new[] { "A", "B", "C", "D", "E" })
                {
                    WriteIco(key, Path.Combine(args[1], "icon-" + key + ".ico"));
                }

                return;
            }

            Console.WriteLine("用法： iconlab sheet <out.png> | ico <A|B|C|D|E> <out.ico> | all <dir>");
        }

        // ================================================================ 概念

        /// <summary>按 256 设计单位绘制；调用方已经 ScaleTransform 过。</summary>
        private static void Paint(string key, Graphics g)
        {
            switch (key)
            {
                case "A": PaintStackedSheets(g); break;
                case "B": PaintLetterMark(g); break;
                case "C": PaintSheetGear(g); break;
                case "D": PaintLayers(g); break;
                case "E": PaintBatchGrid(g); break;
                default: throw new ArgumentException("未知方案：" + key);
            }
        }

        /// <summary>A：三张错位叠放的图纸 + 右下角对勾徽章 —— "批量处理完成"。</summary>
        private static void PaintStackedSheets(Graphics g)
        {
            using (var path = Round(58, 22, 140, 140, 12))
            using (var b = new SolidBrush(SheetBack))
            {
                g.FillPath(b, path);
            }

            using (var path = Round(45, 36, 140, 140, 12))
            using (var b = new SolidBrush(SheetMid))
            {
                g.FillPath(b, path);
            }

            using (var path = Round(32, 50, 140, 140, 12))
            {
                using (var b = new SolidBrush(White)) g.FillPath(b, path);
                using (var p = new Pen(SheetBorder, 4)) g.DrawPath(p, path);
            }

            // 前一张上的"文字行"，长度递减，像一份文档
            using (var b = new SolidBrush(TextLine))
            {
                for (int i = 0; i < 3; i++)
                {
                    using (var line = Round(52, 82 + i * 24, 78 - i * 14, 10, 5))
                    {
                        g.FillPath(b, line);
                    }
                }
            }

            // 对勾徽章：先白圈再蓝盘，最后白勾
            using (var b = new SolidBrush(White))
            {
                g.FillEllipse(b, 146, 146, 104, 104);
            }

            using (var b = new SolidBrush(Blue))
            {
                g.FillEllipse(b, 154, 154, 88, 88);
            }

            using (var p = new Pen(White, 16) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(p, new[]
                {
                    new PointF(174, 198), new PointF(192, 216), new PointF(224, 180)
                });
            }
        }

        /// <summary>B：圆角方块 + 折角 + 白色 "J" 字标 —— 纯字形，16px 最稳。</summary>
        private static void PaintLetterMark(Graphics g)
        {
            using (var path = Round(16, 16, 224, 224, 54))
            using (var b = new LinearGradientBrush(
                new Rectangle(0, 0, 256, 256), BlueLight, BlueDeep, LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(b, path);
            }

            // 右上折角：一块更亮的三角 + 一条折痕线
            using (var fold = new GraphicsPath())
            {
                fold.AddPolygon(new[]
                {
                    new PointF(182, 16), new PointF(240, 16), new PointF(240, 74)
                });
                using (var b = new SolidBrush(Color.FromArgb(0x4C, 0x93, 0xEA)))
                {
                    g.FillPath(b, fold);
                }

                using (var p = new Pen(Color.FromArgb(0x9C, 0xC6, 0xF5), 5))
                {
                    g.DrawLine(p, 182, 16, 240, 74);
                }
            }

            // "J"：竖笔 + 底部左弯
            using (var j = new GraphicsPath())
            {
                j.StartFigure();
                j.AddLine(172, 56, 172, 152);
                j.AddBezier(172, 190, 152, 214, 126, 212, 112, 208);
                j.AddLine(112, 208, 96, 200);

                using (var p = new Pen(White, 34)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                })
                {
                    g.DrawPath(p, j);
                }
            }
        }

        /// <summary>C：折角图纸 + 右下角齿轮 —— "按规则批处理"。</summary>
        private static void PaintSheetGear(Graphics g)
        {
            using (var sheet = new GraphicsPath())
            {
                sheet.AddPolygon(new[]
                {
                    new PointF(36, 28), new PointF(160, 28), new PointF(206, 74),
                    new PointF(206, 196), new PointF(36, 196)
                });

                using (var b = new SolidBrush(White)) g.FillPath(b, sheet);
                using (var p = new Pen(SheetBorder, 4)) g.DrawPath(p, sheet);
            }

            // 折角
            using (var fold = new GraphicsPath())
            {
                fold.AddPolygon(new[]
                {
                    new PointF(160, 28), new PointF(160, 74), new PointF(206, 74)
                });
                using (var b = new SolidBrush(SheetBack)) g.FillPath(b, fold);
                using (var p = new Pen(SheetBorder, 4)) g.DrawPath(p, fold);
            }

            // 文档内容线
            using (var b = new SolidBrush(TextLine))
            {
                for (int i = 0; i < 3; i++)
                {
                    using (var line = Round(56, 96 + i * 22, 96 - i * 18, 9, 4))
                    {
                        g.FillPath(b, line);
                    }
                }
            }

            // 齿轮（带真正的镂空孔：Alternate 填充）。压到右下角、别压住内容线
            using (var gear = GearPath(198, 198, 44, 13, 8))
            using (var b = new SolidBrush(Blue))
            using (var ring = new Pen(White, 9))
            {
                g.FillPath(b, gear);
                g.DrawPath(ring, gear);
            }
        }

        /// <summary>D：三层等轴薄板，顶层带 45° 斜线 —— "图层/堆叠"。</summary>
        private static void PaintLayers(Graphics g)
        {
            DrawPlate(g, 128, 92, 96, 50, SheetBack, null);
            DrawPlate(g, 128, 134, 96, 50, Color.FromArgb(0x8F, 0xB6, 0xE4), null);
            DrawPlate(g, 128, 176, 96, 50, Blue, White);
        }

        private static void DrawPlate(Graphics g, float cx, float cy, float hw, float hh, Color fill, Color? hatch)
        {
            PointF[] pts =
            {
                new PointF(cx, cy - hh),
                new PointF(cx + hw, cy),
                new PointF(cx, cy + hh),
                new PointF(cx - hw, cy)
            };

            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                using (var b = new SolidBrush(fill))
                {
                    g.FillPath(b, path);
                }

                if (hatch.HasValue)
                {
                    Region old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    using (var p = new Pen(hatch.Value, 4))
                    {
                        for (float x = cx - hw - hh; x < cx + hw + hh; x += 32)
                        {
                            g.DrawLine(p, x, cy - hh - 10, x + 2 * hh + 20, cy + hh + 10);
                        }
                    }

                    g.Clip = old;
                }

                using (var p = new Pen(Color.FromArgb(90, 0x12, 0x53, 0x9F), 3))
                {
                    g.DrawPath(p, path);
                }
            }
        }

        /// <summary>E：2×2 四张图纸，其中一张高亮并带勾 —— "一次处理一批"。</summary>
        private static void PaintBatchGrid(Graphics g)
        {
            float[] xs = { 28, 138 };
            float[] ys = { 28, 138 };
            const float side = 90;

            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    bool active = row == 1 && col == 1;

                    using (var path = Round(xs[col], ys[row], side, side, 14))
                    {
                        using (var b = new SolidBrush(active ? Blue : SheetMid))
                        {
                            g.FillPath(b, path);
                        }

                        using (var p = new Pen(active ? BlueDeep : SheetBorder, 4))
                        {
                            g.DrawPath(p, path);
                        }
                    }

                    if (active)
                    {
                        using (var p = new Pen(White, 14)
                        {
                            StartCap = LineCap.Round,
                            EndCap = LineCap.Round,
                            LineJoin = LineJoin.Round
                        })
                        {
                            g.DrawLines(p, new[]
                            {
                                new PointF(xs[col] + 24, ys[row] + 48),
                                new PointF(xs[col] + 40, ys[row] + 64),
                                new PointF(xs[col] + 68, ys[row] + 30)
                            });
                        }
                    }
                    else
                    {
                        // 非高亮的那几张给两道短横线，暗示"文档"而不是"空方块"
                        using (var b = new SolidBrush(SheetBorder))
                        {
                            using (var l1 = Round(xs[col] + 20, ys[row] + 36, 50, 8, 4)) g.FillPath(b, l1);
                            using (var l2 = Round(xs[col] + 20, ys[row] + 54, 34, 8, 4)) g.FillPath(b, l2);
                        }
                    }
                }
            }
        }

        // ================================================================ 工具

        private static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>齿轮：外圈方齿 + 中心镂空孔（Alternate 填充成透明洞）。</summary>
        private static GraphicsPath GearPath(float cx, float cy, float outer, float tooth, int count)
        {
            var path = new GraphicsPath(FillMode.Alternate);
            var pts = new List<PointF>();

            // 每个齿 4 段：齿顶两条边 + 齿根两条边。齿顶/齿根用半径切换，
            // 角度均匀分，这样齿是"方的"而不是三角尖刺。
            double step = Math.PI * 2 / (count * 4);
            for (int i = 0; i < count * 4; i++)
            {
                double a = i * step;
                float r = (i % 4 == 0 || i % 4 == 1) ? outer + tooth : outer;
                pts.Add(new PointF(
                    cx + (float)(Math.Cos(a) * r),
                    cy + (float)(Math.Sin(a) * r)));
            }

            path.AddPolygon(pts.ToArray());
            path.AddEllipse(cx - 20, cy - 20, 40, 40);
            return path;
        }

        private static Bitmap Render(string key, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                float s = size / 256f;
                g.ScaleTransform(s, s);
                Paint(key, g);
            }

            return bmp;
        }

        // ================================================================ 对照表

        private static void DrawSheet(string outputPath)
        {
            string[] keys = { "A", "B", "C", "D", "E" };
            int colW = 214;
            int width = 24 + keys.Length * colW + 24;
            int height = 396;

            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0xFA, 0xFB, 0xFC));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (var titleFont = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var labelFont = new Font("Segoe UI", 8.5f))
                using (var titleBrush = new SolidBrush(Color.FromArgb(0x20, 0x24, 0x2B)))
                using (var dimBrush = new SolidBrush(Color.FromArgb(0x6B, 0x72, 0x80)))
                {
                    for (int i = 0; i < keys.Length; i++)
                    {
                        int x0 = 24 + i * colW;

                        g.DrawString(keys[i], titleFont, titleBrush, x0 + 4, 8);

                        // 大图 192
                        using (Bitmap big = Render(keys[i], 192))
                        {
                            g.DrawImage(big, x0 + 9, 32, 192, 192);
                        }

                        // 浅色条：48 / 32 / 16
                        using (var bg = new SolidBrush(Color.FromArgb(0xF0, 0xF2, 0xF5)))
                        {
                            g.FillRectangle(bg, x0, 236, colW - 8, 64);
                        }

                        DrawSizes(g, keys[i], x0, 244, Color.FromArgb(0x6B, 0x72, 0x80), labelFont);

                        // 深色条（任务栏 / 深色主题）
                        using (var bg = new SolidBrush(Color.FromArgb(0x23, 0x27, 0x2F)))
                        {
                            g.FillRectangle(bg, x0, 308, colW - 8, 64);
                        }

                        DrawSizes(g, keys[i], x0, 316, Color.FromArgb(0xC9, 0xD0, 0xDA), labelFont);
                    }

                    g.DrawString("48px / 32px / 16px — 上排浅色底、下排深色底（任务栏）",
                        labelFont, dimBrush, 24, height - 20);
                }

                bmp.Save(outputPath, ImageFormat.Png);
            }

            Console.WriteLine("已生成对照表：" + outputPath);
        }

        private static void DrawSizes(Graphics g, string key, int x0, int y0, Color labelColor, Font font)
        {
            using (Bitmap b48 = Render(key, 48))
            using (Bitmap b32 = Render(key, 32))
            using (Bitmap b16 = Render(key, 16))
            using (var brush = new SolidBrush(labelColor))
            {
                g.DrawImage(b48, x0 + 10, y0, 48, 48);
                g.DrawImage(b32, x0 + 68, y0 + 8, 32, 32);
                g.DrawImage(b16, x0 + 110, y0 + 16, 16, 16);

                g.DrawString("48", font, brush, x0 + 22, y0 + 48);
                g.DrawString("32", font, brush, x0 + 76, y0 + 44);
                g.DrawString("16", font, brush, x0 + 112, y0 + 36);
            }
        }

        // ================================================================ ICO

        /// <summary>打包多尺寸 .ico（每个尺寸单独光栅化，不靠缩放大图）。</summary>
        private static void WriteIco(string key, string outputPath)
        {
            var pngs = new List<byte[]>();
            foreach (int size in IcoSizes)
            {
                using (Bitmap bmp = Render(key, size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type = icon
                w.Write((ushort)pngs.Count);

                int offset = 6 + 16 * pngs.Count;
                for (int i = 0; i < pngs.Count; i++)
                {
                    int size = IcoSizes[i];
                    w.Write((byte)(size >= 256 ? 0 : size));   // 0 表示 256
                    w.Write((byte)(size >= 256 ? 0 : size));
                    w.Write((byte)0);            // 调色板数
                    w.Write((byte)0);            // 保留
                    w.Write((ushort)1);          // planes
                    w.Write((ushort)32);         // bpp
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }

                foreach (byte[] data in pngs)
                {
                    w.Write(data);
                }
            }

            Console.WriteLine(string.Format("已生成 {0}（{1} 种尺寸）", outputPath, pngs.Count));
        }
    }
}
