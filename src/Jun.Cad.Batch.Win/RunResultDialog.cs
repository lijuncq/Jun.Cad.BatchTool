using System;
using System.Drawing;
using System.Windows.Forms;

namespace Jun.Cad.Batch.Win
{
    /// <summary>执行结果窗口：把整批日志原文摊开，方便对着句柄排查。</summary>
    internal sealed class RunResultDialog : Form
    {
        public RunResultDialog(string report)
        {
            Text = "执行结果  ·  " + AppInfo.Describe();
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(520, 320);
            BackColor = BatchToolForm.BgColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = BatchToolForm.CardColor,
                // 汇总表是"等宽列"排版（见 Core 的 TextColumns），字体必须满足
                // "汉字宽度 = 两个西文宽度"，否则数字列会被中文规则名顶歪。
                // 10.5pt = 14px，比正文的 9pt 大一号，长日志读起来不累。
                Font = PickMonoFont(10.5F),
                Text = report ?? string.Empty
            };

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = BatchToolForm.BgColor
            };

            var close = new BatchToolForm.FlatButton
            {
                Text = "关闭",
                Kind = BatchToolForm.FlatButtonKind.Primary,
                Width = 88,
                Height = 32
            };
            close.Click += delegate { Close(); };

            bottom.Controls.Add(close);
            bottom.Resize += delegate
            {
                close.Left = bottom.ClientSize.Width - close.Width - 16;
                close.Top = (bottom.ClientSize.Height - close.Height) / 2;
            };

            // 停靠顺序：Fill 必须待在索引 0（后加的在下层），所以先加正文、后加底栏
            Controls.Add(text);
            Controls.Add(bottom);

            // 打开就把光标放到末尾，用户一眼看到汇总行
            Shown += delegate
            {
                text.SelectionStart = text.TextLength;
                text.ScrollToCaret();
            };
        }

        /// <summary>
        /// 挑一个"汉字宽度正好是两个西文宽度"的等宽字体。
        ///
        /// 为什么不是 Consolas：它没有汉字，回退字体的宽度比不保证 2:1，
        /// 中文规则名会把汇总表的数字列顶歪（实测偏移 6~10px）。
        ///
        /// 为什么首选黑体而不是新宋体：新宋体（NSimSun）自带小字号点阵，
        /// 经 WM_PRINT（DrawToBitmap 走的路径）绘制时点阵与基线错位，整段文字看起来像被横线划过；
        /// 黑体是纯 TrueType 轮廓，任何字号都清晰，且 ASCII 正好是汉字宽度的一半。
        ///
        /// <c>new Font(不存在的名字, size)</c> 不抛异常，会静默回退到 Microsoft Sans Serif，
        /// 所以必须用 <see cref="Font.Name"/> 回读确认真的选上了。
        /// </summary>
        private static Font PickMonoFont(float size)
        {
            string[] candidates = { "SimHei", "NSimSun", "SimSun", "MS Gothic", "Consolas" };
            foreach (string name in candidates)
            {
                try
                {
                    var font = new Font(name, size);
                    if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return font;
                    }

                    font.Dispose();
                }
                catch
                {
                    // 该字体不可用，试下一个
                }
            }

            return new Font(FontFamily.GenericMonospace, size);
        }
    }
}
