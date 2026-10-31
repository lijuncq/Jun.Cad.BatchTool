using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Jun.Cad.Batch.Engine.Preflight;

namespace Jun.Cad.Batch.Win
{
    /// <summary>
    /// 前置检查面板：把能力探测的结果**显示出来**，而不是只写进日志。
    ///
    /// 为什么要单独一屏：用户在执行前必须知道
    /// "这次遮罩会不会降级成 Solid""表格规则是不是压根没验证过"，
    /// 这些信息藏在日志里等于没有。
    /// </summary>
    internal sealed class PreflightDialog : Form
    {
        private readonly FlowLayoutPanel _list;
        private readonly Label _summary;
        private string _tableSample;

        public PreflightDialog(string tableSample)
        {
            _tableSample = tableSample;

            Text = "前置检查";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = BatchToolForm.BgColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(720, 420);
            AutoScaleMode = AutoScaleMode.Dpi;

            _summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 54,
                Padding = new Padding(16, 12, 16, 0),
                ForeColor = BatchToolForm.TextColor,
                BackColor = Color.Transparent
            };

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(16, 0, 16, 0),
                BackColor = BatchToolForm.BgColor
            };

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = BatchToolForm.BgColor
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(0, 12, 16, 0),
                BackColor = Color.Transparent
            };

            var btnClose = new BatchToolForm.FlatButton
            {
                Text = "关闭",
                Kind = BatchToolForm.FlatButtonKind.Ghost,
                Width = 84,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0)
            };
            btnClose.Click += delegate { Close(); };

            var btnRerun = new BatchToolForm.FlatButton
            {
                Text = "重新检查",
                Kind = BatchToolForm.FlatButtonKind.Primary,
                Width = 108,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0)
            };
            btnRerun.Click += delegate
            {
                CapabilityProbe.Reset();
                Refresh2();
            };

            flow.Controls.Add(btnClose);
            flow.Controls.Add(btnRerun);

            footer.Controls.Add(flow);

            Controls.Add(_list);
            Controls.Add(footer);
            Controls.Add(_summary);

            Refresh2();
        }

        /// <summary>把"参考图纸"当作表格样图（如果它确实含表格，探测就能跑起来）。</summary>
        public string TableSample
        {
            get { return _tableSample; }
            set
            {
                _tableSample = value;
                Refresh2();
            }
        }

        private void Refresh2()
        {
            _list.SuspendLayout();
            _list.Controls.Clear();

            List<ProbeResult> results = CapabilityProbe.RunAll(_tableSample);

            int failed = 0;
            int degraded = 0;
            int notTested = 0;

            foreach (ProbeResult r in results)
            {
                if (r.Status == ProbeStatus.Failed)
                {
                    failed++;
                }
                else if (r.Status == ProbeStatus.Degraded)
                {
                    degraded++;
                }
                else if (r.Status == ProbeStatus.NotTested)
                {
                    notTested++;
                }

                _list.Controls.Add(BuildRow(r));
            }

            _summary.Text = string.Format(
                failed > 0
                    ? "有 {0} 项失败：相关规则会自动降级，降级原因会写进差异预览。另有降级 {1} 项、未测 {2} 项。"
                    : "没有失败项。降级 {1} 项、未测 {2} 项（降级不是错误，是纯文件模式的边界）。",
                failed, degraded, notTested);

            _list.ResumeLayout();
        }

        private Control BuildRow(ProbeResult result)
        {
            var card = new Panel
            {
                Width = Math.Max(560, _list.ClientSize.Width - 40),
                Height = 76,
                BackColor = BatchToolForm.CardColor,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(12, 8, 12, 8)
            };

            Color color;
            switch (result.Status)
            {
                case ProbeStatus.Passed: color = Color.FromArgb(22, 163, 74); break;
                case ProbeStatus.Failed: color = Color.FromArgb(190, 40, 40); break;
                case ProbeStatus.Degraded: color = Color.FromArgb(180, 120, 20); break;
                default: color = Color.FromArgb(120, 120, 120); break;
            }

            var badge = new Label
            {
                Text = result.StatusText,
                Left = 12,
                Top = 10,
                Width = 48,
                Height = 20,
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = new Font(Font, FontStyle.Bold)
            };

            var title = new Label
            {
                Text = result.Title,
                Left = 68,
                Top = 10,
                Width = card.Width - 90,
                Height = 20,
                ForeColor = BatchToolForm.TextColor,
                BackColor = Color.Transparent
            };

            var detail = new Label
            {
                Text = result.Detail + (string.IsNullOrEmpty(result.Affects) ? string.Empty : "　（影响：" + result.Affects + "）"),
                Left = 68,
                Top = 32,
                Width = card.Width - 90,
                Height = 36,
                ForeColor = BatchToolForm.TextDim,
                BackColor = Color.Transparent
            };

            card.Controls.Add(badge);
            card.Controls.Add(title);
            card.Controls.Add(detail);
            return card;
        }
    }
}
