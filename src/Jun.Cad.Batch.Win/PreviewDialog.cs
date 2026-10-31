using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Jun.Cad.Batch.Engine.Preview;

namespace Jun.Cad.Batch.Win
{
    /// <summary>
    /// 差异预览窗口：执行前把"会发生什么"逐条摆出来。
    ///
    /// 为什么单独一个窗口而不是塞进执行页：
    ///   * 行数可能上万（一张图几百处改动 × 多个文件），必须用虚拟模式，
    ///     和主窗口那套自绘规则行不是一回事；
    ///   * 它是**执行前的确认关口**，模态窗口的语义更合适——看完再决定跑不跑。
    ///
    /// 三条状态与降级标记都直接显示在行里，不允许"降级了但不告诉你"。
    /// </summary>
    internal sealed class PreviewDialog : Form
    {
        private readonly List<ChangeRecord> _records;
        private readonly DataGridView _grid;
        private readonly ComboBox _filter;
        private readonly Label _summary;

        public PreviewDialog(List<ChangeRecord> records)
        {
            _records = records ?? new List<ChangeRecord>();

            Text = "差异预览";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = BatchToolForm.BgColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(1080, 620);
            MinimumSize = new Size(760, 420);
            AutoScaleMode = AutoScaleMode.Dpi;

            _summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(16, 12, 16, 0),
                ForeColor = BatchToolForm.TextColor,
                BackColor = Color.Transparent,
                Text = BuildSummary()
            };

            var bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(16, 6, 16, 0),
                BackColor = BatchToolForm.BgColor
            };

            var filterLabel = new Label
            {
                Text = "只看：",
                AutoSize = true,
                Left = 16,
                Top = 12,
                ForeColor = BatchToolForm.TextDim,
                BackColor = Color.Transparent
            };

            _filter = new ComboBox
            {
                Left = 64,
                Top = 8,
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _filter.Items.Add("全部");
            _filter.Items.Add("Will change");
            _filter.Items.Add("No change");
            _filter.Items.Add("Blocked");
            _filter.Items.Add("仅降级");
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += delegate { Rebind(); };

            bar.Controls.Add(filterLabel);
            bar.Controls.Add(_filter);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                VirtualMode = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BackgroundColor = BatchToolForm.CardColor,
                BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(229, 231, 235)
            };

            _grid.Columns.Add(MakeColumn("规则", 190));
            _grid.Columns.Add(MakeColumn("文件", 150));
            _grid.Columns.Add(MakeColumn("空间", 110));
            _grid.Columns.Add(MakeColumn("句柄", 70));
            _grid.Columns.Add(MakeColumn("类型", 80));
            _grid.Columns.Add(MakeColumn("操作", 150));
            _grid.Columns.Add(MakeColumn("旧值", 200));
            _grid.Columns.Add(MakeColumn("新值", 200));
            _grid.Columns.Add(MakeColumn("状态", 80));
            _grid.Columns.Add(MakeColumn("备注", 260));

            _grid.CellValueNeeded += OnCellValueNeeded;

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

            var btnExport = new BatchToolForm.FlatButton
            {
                Text = "导出 Markdown",
                Kind = BatchToolForm.FlatButtonKind.Secondary,
                Width = 128,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0)
            };
            btnExport.Click += delegate { ExportMarkdown(); };

            flow.Controls.Add(btnClose);
            flow.Controls.Add(btnExport);

            footer.Controls.Add(flow);

            var host = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 0, 16, 8),
                BackColor = BatchToolForm.BgColor
            };
            host.Controls.Add(_grid);

            Controls.Add(host);
            Controls.Add(footer);
            Controls.Add(bar);
            Controls.Add(_summary);

            Rebind();
        }

        private static DataGridViewTextBoxColumn MakeColumn(string header, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        private List<ChangeRecord> _visible = new List<ChangeRecord>();

        private void Rebind()
        {
            string mode = _filter == null ? "全部" : (_filter.SelectedItem as string ?? "全部");
            _visible = new List<ChangeRecord>();

            foreach (ChangeRecord record in _records)
            {
                if (mode == "仅降级")
                {
                    if (!record.Degraded)
                    {
                        continue;
                    }
                }
                else if (mode != "全部")
                {
                    if (!string.Equals(record.StatusText, mode, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                _visible.Add(record);
            }

            if (_grid != null)
            {
                _grid.RowCount = _visible.Count;
                _grid.Invalidate();
            }

            if (_summary != null)
            {
                _summary.Text = BuildSummary();
            }
        }

        private string BuildSummary()
        {
            int willChange = 0;
            int noChange = 0;
            int blocked = 0;
            int degraded = 0;

            foreach (ChangeRecord r in _records)
            {
                switch (r.Status)
                {
                    case ChangeStatus.WillChange: willChange++; break;
                    case ChangeStatus.NoChange: noChange++; break;
                    default: blocked++; break;
                }

                if (r.Degraded)
                {
                    degraded++;
                }
            }

            return string.Format(
                "共 {0} 行：将改动 {1}，无需改动 {2}，被挡住 {3}；其中降级 {4} 处（降级原因见「备注」列）。当前显示 {5} 行。",
                _records.Count, willChange, noChange, blocked, degraded, _visible.Count);
        }

        private void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _visible.Count)
            {
                return;
            }

            ChangeRecord r = _visible[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = r.RuleName; break;
                case 1: e.Value = string.IsNullOrEmpty(r.File) ? string.Empty : Path.GetFileName(r.File); break;
                case 2: e.Value = r.Space; break;
                case 3: e.Value = r.Handle; break;
                case 4: e.Value = r.DxfType; break;
                case 5: e.Value = r.Operation; break;
                case 6: e.Value = r.OldValue; break;
                case 7: e.Value = r.NewValue; break;
                case 8: e.Value = r.StatusText; break;
                case 9: e.Value = r.Degraded ? "⚠ 降级：" + r.Note : r.Note; break;
            }
        }

        private void ExportMarkdown()
        {
            using (var dialog = new SaveFileDialog
            {
                Title = "导出差异预览",
                Filter = "Markdown (*.md)|*.md|文本 (*.txt)|*.txt",
                FileName = string.Format("preview-{0:yyyyMMdd-HHmmss}.md", DateTime.Now)
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# 差异预览");
                    sb.AppendLine();
                    sb.AppendLine(BuildSummary());
                    sb.AppendLine();
                    sb.Append(EntitySnapshot.ToMarkdownTable(_records, 0));
                    File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                    MessageBox.Show(this, "已导出：" + dialog.FileName, "导出", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
