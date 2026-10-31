using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;

namespace Jun.Cad.Batch.Win
{
    /// <summary>
    /// 圆角输入框外壳。
    ///
    /// WinForms 的 TextBox 没有圆角，直接摆在白卡片上会显得很"系统"。这里用一个自绘面板画
    /// 1px 圆角边框（聚焦时换成主题色），把无边框的 TextBox / CheckedListBox 塞进去，
    /// 视觉上和主窗口的卡片、按钮是同一套语言。
    /// </summary>
    internal sealed class InputShell : Panel
    {
        private bool _focused;

        public InputShell()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = BatchToolForm.CardColor;
            Padding = new Padding(8, 0, 8, 0);
        }

        public void Attach(Control child)
        {
            child.BackColor = BatchToolForm.CardColor;
            child.GotFocus += delegate { _focused = true; Invalidate(); };
            child.LostFocus += delegate { _focused = false; Invalidate(); };
            Controls.Add(child);
            CenterChild();
            Resize += delegate { CenterChild(); };
        }

        /// <summary>子控件在壳内垂直居中。用 Dock.Fill 会让单行文本贴顶，看着像没对齐。</summary>
        private void CenterChild()
        {
            if (Controls.Count == 0)
            {
                return;
            }

            Control child = Controls[0];
            child.Left = Padding.Left;
            child.Width = Math.Max(20, Width - Padding.Horizontal);
            child.Top = Math.Max(1, (Height - child.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BatchToolForm.CardColor);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // 禁用态的边框要更淡，否则看着像还能输入
            Color border = !Enabled
                ? BatchToolForm.BorderColor
                : (_focused ? BatchToolForm.Accent : BatchToolForm.BorderStrong);

            using (GraphicsPath path = BatchToolForm.RoundRect(rect, 6))
            using (var fill = new SolidBrush(BatchToolForm.CardColor))
            using (var pen = new Pen(border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            base.OnPaint(e);
        }
    }

    /// <summary>
    /// 分组标题行：主题色竖条 + 标题 + 右侧说明，底部一条细分割线。
    /// 与主窗口卡片里"▎标题"的处理保持一致。
    /// </summary>
    internal sealed class SectionHeader : Panel
    {
        private readonly string _title;
        private readonly string _hint;
        private readonly Font _fontTitle;
        private readonly Font _fontHint;

        public SectionHeader(string title, string hint, Font fontTitle, Font fontHint)
        {
            _title = title;
            _hint = hint;
            _fontTitle = fontTitle;
            _fontHint = fontHint;
            Height = 34;
            BackColor = BatchToolForm.CardColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BatchToolForm.CardColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var brush = new SolidBrush(BatchToolForm.Accent))
            {
                g.FillRectangle(brush, 0, 10, 3, 14);
            }

            using (var brush = new SolidBrush(BatchToolForm.TextColor))
            {
                g.DrawString(_title, _fontTitle, brush, 10, 8);
            }

            if (!string.IsNullOrEmpty(_hint))
            {
                using (var brush = new SolidBrush(BatchToolForm.TextDim))
                {
                    g.DrawString(_hint, _fontHint, brush,
                        10 + g.MeasureString(_title, _fontTitle).Width + 8, 10);
                }
            }

            using (var pen = new Pen(BatchToolForm.BorderColor))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    /// <summary>
    /// 属性面板里的一行：中文标签 + 编辑控件 + 单位 + 错误提示。
    ///
    /// 高度由控件种类决定（多行文本与多选列表更高），出错时再加一行提示文字。
    /// 行内不缓存"当前值"，一律通过 <see cref="FieldDescriptor"/> 读写模型，
    /// 避免出现"界面显示的和模型里的不一样"。
    /// </summary>
    internal sealed class FieldRow : Panel
    {
        private const int LabelWidth = 132;

        private readonly FieldDescriptor _field;
        private readonly Control _editor;
        private readonly Label _unit;
        private readonly int _baseHeight;

        private string _error;
        private bool _blocking;

        public FieldRow(FieldDescriptor field, Control editor, int baseHeight, Font fontBase, Font fontSmall)
        {
            _field = field;
            _editor = editor;
            _baseHeight = baseHeight;

            BackColor = BatchToolForm.CardColor;
            Height = baseHeight;

            var label = new Label
            {
                Text = field.Label + (field.Meta != null && field.Meta.Required ? " *" : string.Empty),
                ForeColor = field.Disabled ? BatchToolForm.TextDim : BatchToolForm.TextColor,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Left = 0,
                Top = 0,
                Width = LabelWidth,
                Height = baseHeight,
                Font = fontBase
            };

            if (field.Meta != null && !string.IsNullOrEmpty(field.Meta.Help))
            {
                var tip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300, ShowAlways = true };
                tip.SetToolTip(label, field.Meta.Help);
                tip.SetToolTip(editor, field.Meta.Help);
            }

            _unit = new Label
            {
                Text = field.Meta == null ? string.Empty : field.Meta.Unit,
                ForeColor = BatchToolForm.TextDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Height = baseHeight,
                Font = fontSmall
            };

            if (!string.IsNullOrEmpty(_unit.Text))
            {
                var unitTip = new ToolTip { AutoPopDelay = 12000, ShowAlways = true };
                unitTip.SetToolTip(_unit, _unit.Text);
            }

            Controls.Add(label);
            Controls.Add(editor);
            Controls.Add(_unit);

            // z 序是"先加的在上层"：标签不能盖住编辑器，显式把编辑器提到最前
            _unit.BringToFront();
            editor.BringToFront();

            Resize += delegate { LayoutChildren(); };
            LayoutChildren();
        }

        public FieldDescriptor Field
        {
            get { return _field; }
        }

        public Control Editor
        {
            get { return _editor; }
        }

        /// <summary>当前的提示文案（空表示没有）。</summary>
        public string IssueText
        {
            get { return _error; }
        }

        /// <summary>当前行高（含错误行）。</summary>
        public int PreferredHeight
        {
            get { return _baseHeight + (string.IsNullOrEmpty(_error) ? 0 : 18); }
        }

        /// <summary>设置提示；<paramref name="blocking"/> 决定红/黄。</summary>
        public void SetIssue(string message, bool blocking)
        {
            _error = string.IsNullOrEmpty(message) ? null : message;
            _blocking = blocking;
            Height = PreferredHeight;
            Invalidate();
        }

        public void ClearIssue()
        {
            SetIssue(null, false);
        }

        private void LayoutChildren()
        {
            // 单位文字只占一行：太长就省略，完整内容靠 ToolTip（见构造函数）
            int unitWidth = string.IsNullOrEmpty(_unit.Text) ? 0 : Math.Min(150, _unit.Text.Length * 11 + 8);
            int editorWidth = Math.Max(120, Width - LabelWidth - unitWidth - 16);

            _editor.Left = LabelWidth;
            _editor.Top = (_baseHeight - _editor.Height) / 2;
            _editor.Width = editorWidth;

            _unit.Left = LabelWidth + editorWidth + 8;
            _unit.Top = 0;
            _unit.Width = Math.Max(0, Width - _unit.Left);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (string.IsNullOrEmpty(_error))
            {
                return;
            }

            Color color = _blocking ? Color.FromArgb(190, 40, 40) : BatchToolForm.Warn;
            using (var brush = new SolidBrush(color))
            using (var font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1.5f)))
            {
                e.Graphics.DrawString(_error, font, brush,
                    new RectangleF(LabelWidth, _baseHeight - 4, Width - LabelWidth - 4, 18));
            }
        }
    }

    /// <summary>
    /// 按 <see cref="RuleFieldEditor"/> 造控件并双向绑定。
    ///
    /// 绑定原则：
    ///   * 文本类字段留空即写 <c>null</c>（"不设置"），不写空字符串——精简序列化依赖这个区别；
    ///   * 数值字段在**离开控件**时解析，输入过程中的 `0.` 不会被判成错误；
    ///   * 单/复数配对字段（`LayerName` / `LayerNames` 这类）由 <see cref="ApplyPair"/> 统一处理，
    ///     只写一边，另一侧自动清空。
    /// </summary>
    internal static class FieldControlFactory
    {
        public static Control Create(FieldDescriptor field, RuleConfig rule, EventHandler onChanged,
            out int rowHeight)
        {
            object value = field.GetValue(rule);
            Control control;

            switch (field.Editor)
            {
                case RuleFieldEditor.Multiline:
                {
                    var box = new TextBox
                    {
                        Multiline = true,
                        ScrollBars = ScrollBars.Vertical,
                        Text = value as string ?? string.Empty,
                        Height = 72,
                        BorderStyle = BorderStyle.None,
                        BackColor = BatchToolForm.CardColor
                    };
                    box.TextChanged += delegate
                    {
                        Write(field, rule, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text);
                        Notify(onChanged);
                    };
                    control = Wrap(field, box, 78);
                    rowHeight = 84;
                    break;
                }

                case RuleFieldEditor.Integer:
                case RuleFieldEditor.Double:
                {
                    var box = new TextBox
                    {
                        Text = FormatNumber(value),
                        Height = 26,
                        BorderStyle = BorderStyle.None,
                        BackColor = BatchToolForm.CardColor
                    };
                    box.Leave += delegate
                    {
                        object parsed;
                        if (TryParseNumber(field.Editor, box.Text, out parsed))
                        {
                            Write(field, rule, parsed);
                            box.Text = FormatNumber(parsed);
                        }
                        else
                        {
                            box.Text = FormatNumber(field.GetValue(rule));
                            MessageBox.Show("请输入" + (field.Editor == RuleFieldEditor.Integer ? "整数" : "数字") + "。",
                                field.Label, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }

                        Notify(onChanged);
                    };
                    control = Wrap(field, box, 30);
                    rowHeight = 44;
                    break;
                }

                case RuleFieldEditor.Toggle:
                {
                    var combo = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        FlatStyle = FlatStyle.Flat,
                        Height = 26
                    };
                    combo.Items.Add("不设置");
                    combo.Items.Add("是");
                    combo.Items.Add("否");
                    var current = value as bool?;
                    combo.SelectedIndex = !current.HasValue ? 0 : (current.Value ? 1 : 2);
                    combo.SelectedIndexChanged += delegate
                    {
                        Write(field, rule, combo.SelectedIndex == 0 ? (object)null : (object)(combo.SelectedIndex == 1));
                        Notify(onChanged);
                    };
                    control = combo;
                    rowHeight = 44;
                    break;
                }

                case RuleFieldEditor.Enum:
                {
                    var combo = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        FlatStyle = FlatStyle.Flat,
                        Height = 26
                    };
                    combo.Items.Add("(不设置)");

                    List<KeyValuePair<string, string>> options = ParseValues(field.Meta);
                    foreach (KeyValuePair<string, string> pair in options)
                    {
                        combo.Items.Add(pair.Value);
                    }

                    string text = value == null
                        ? string.Empty
                        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    int index = 0;
                    for (int i = 0; i < options.Count; i++)
                    {
                        if (string.Equals(options[i].Key, text, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i + 1;
                            break;
                        }
                    }

                    if (index == 0 && text.Length > 0)
                    {
                        // 配置里的值不在候选里（例如手改 JSON 写了非法线宽）：
                        // 原样显示并标"无效"，不能显示成"(不设置)"——那会在保存时把用户的值悄悄抹掉
                        options.Add(new KeyValuePair<string, string>(text, text + "（无效值，请重新选择）"));
                        combo.Items.Add(options[options.Count - 1].Value);
                        index = options.Count;
                    }

                    combo.SelectedIndex = index;
                    combo.SelectedIndexChanged += delegate
                    {
                        object result = null;
                        if (combo.SelectedIndex > 0)
                        {
                            result = options[combo.SelectedIndex - 1].Key;
                        }

                        Write(field, rule, result);
                        Notify(onChanged);
                    };
                    control = combo;
                    rowHeight = 44;
                    break;
                }

                case RuleFieldEditor.MultiEnum:
                {
                    var list = new CheckedListBox
                    {
                        CheckOnClick = true,
                        Height = 96,
                        BorderStyle = BorderStyle.None,
                        BackColor = BatchToolForm.CardColor
                    };

                    var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var existing = value as List<string>;
                    if (existing != null)
                    {
                        foreach (string s in existing)
                        {
                            selected.Add(s);
                        }
                    }

                    List<KeyValuePair<string, string>> options = ParseValues(field.Meta);
                    foreach (KeyValuePair<string, string> pair in options)
                    {
                        list.Items.Add(pair.Value, selected.Contains(pair.Key));
                    }

                    list.ItemCheck += delegate
                    {
                        // ItemCheck 触发时勾选状态还没落定，延后到消息队列里读
                        list.BeginInvoke(new Action(delegate
                        {
                            var result = new List<string>();
                            for (int i = 0; i < options.Count && i < list.Items.Count; i++)
                            {
                                if (list.GetItemChecked(i))
                                {
                                    result.Add(options[i].Key);
                                }
                            }

                            Write(field, rule, result.Count == 0 ? null : result);
                            Notify(onChanged);
                        }));
                    };
                    control = Wrap(field, list, 102);
                    rowHeight = 112;
                    break;
                }

                case RuleFieldEditor.TagList:
                {
                    var box = new TextBox
                    {
                        Text = JoinList(value as List<string>),
                        Height = 26,
                        BorderStyle = BorderStyle.None,
                        BackColor = BatchToolForm.CardColor
                    };
                    box.TextChanged += delegate
                    {
                        List<string> items = SplitList(box.Text);
                        Write(field, rule, items == null ? null : (object)items);
                        Notify(onChanged);
                    };
                    control = Wrap(field, box, 30);
                    rowHeight = 44;
                    break;
                }

                default:
                {
                    var box = new TextBox
                    {
                        Text = value as string ?? string.Empty,
                        Height = 26,
                        BorderStyle = BorderStyle.None,
                        BackColor = BatchToolForm.CardColor
                    };
                    box.TextChanged += delegate
                    {
                        Write(field, rule, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                        Notify(onChanged);
                    };
                    control = Wrap(field, box, 30);
                    rowHeight = 44;
                    break;
                }
            }

            control.Enabled = !field.Disabled;
            return control;
        }

        /// <summary>把无边框控件装进圆角外壳；下拉框自带边框，直接返回。</summary>
        private static Control Wrap(FieldDescriptor field, Control child, int height)
        {
            var shell = new InputShell { Height = height };
            shell.Attach(child);
            shell.Enabled = !field.Disabled;
            return shell;
        }

        // ------------------------------------------------------------------ 内部

        private static void Notify(EventHandler handler)
        {
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        /// <summary>写入字段，并处理单/复数配对。</summary>
        private static void Write(FieldDescriptor field, RuleConfig rule, object value)
        {
            field.SetValue(rule, value);
            ApplyPair(field, rule, value);
        }

        /// <summary>
        /// 单/复数配对：列表只有 1 项时写回单数字段，多项时清空单数字段；
        /// 单数字段有值时清空复数列表。避免"两边都填、谁生效说不清"。
        /// </summary>
        private static void ApplyPair(FieldDescriptor field, RuleConfig rule, object value)
        {
            string pairName = field.Meta == null ? null : field.Meta.PairPath;
            if (string.IsNullOrEmpty(pairName))
            {
                return;
            }

            PropertyInfo pairProperty = field.Property.DeclaringType.GetProperty(pairName);
            if (pairProperty == null || !pairProperty.CanWrite)
            {
                return;
            }

            object owner = field.IsFilter ? (object)rule.Filter : rule.Action;
            if (owner == null)
            {
                return;
            }

            try
            {
                if (field.Editor == RuleFieldEditor.TagList)
                {
                    var list = value as List<string>;
                    if (list != null && list.Count == 1 && pairProperty.PropertyType == typeof(string))
                    {
                        pairProperty.SetValue(owner, list[0], null);
                    }
                    else
                    {
                        pairProperty.SetValue(owner, null, null);
                    }
                }
                else
                {
                    string text = value as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        pairProperty.SetValue(owner, null, null);
                    }
                }
            }
            catch
            {
                // 配对失败不影响主字段
            }
        }

        private static string FormatNumber(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is int)
            {
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            }

            double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool TryParseNumber(RuleFieldEditor editor, string text, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true; // 留空 = 不设置
            }

            if (editor == RuleFieldEditor.Integer)
            {
                int i;
                if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    value = i;
                    return true;
                }

                return false;
            }

            double d;
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                value = d;
                return true;
            }

            return false;
        }

        private static string JoinList(List<string> values)
        {
            return values == null ? string.Empty : string.Join(", ", values.ToArray());
        }

        private static List<string> SplitList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var list = new List<string>();
            foreach (string raw in text.Split(new[] { ',', '，', ';', '；' }))
            {
                string item = raw.Trim();
                if (item.Length > 0 && !list.Contains(item))
                {
                    list.Add(item);
                }
            }

            return list.Count == 0 ? null : list;
        }

        /// <summary>把 <c>"值=显示名;值=显示名"</c> 解析成有序键值对。</summary>
        public static List<KeyValuePair<string, string>> ParseValues(RuleFieldAttribute meta)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (meta == null || string.IsNullOrWhiteSpace(meta.Values))
            {
                return list;
            }

            foreach (string raw in meta.Values.Split(';'))
            {
                string item = raw.Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                int eq = item.IndexOf('=');
                if (eq < 0)
                {
                    list.Add(new KeyValuePair<string, string>(item, item));
                }
                else
                {
                    list.Add(new KeyValuePair<string, string>(item.Substring(0, eq).Trim(), item.Substring(eq + 1).Trim()));
                }
            }

            return list;
        }
    }
}
