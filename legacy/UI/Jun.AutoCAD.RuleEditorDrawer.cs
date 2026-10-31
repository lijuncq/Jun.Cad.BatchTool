using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.UI
{
    /// <summary>
    /// 规则编辑抽屉：按规则类型生成字段表单，替代原来直接编辑 JSON 文本框的方式。
    ///
    /// 视觉上跟随主窗口：浅灰底 + 白色圆角卡片 + 主题色竖条分组标题 + 自绘圆角按钮，
    /// 输入框用 <see cref="InputShell"/> 画圆角边框，避免原生控件的系统感。
    ///
    /// 三个关键行为：
    ///   1. **编辑副本**。构造时把原规则深拷贝一份，所有修改都落在副本上；点「保存」才把结果交回调用方。
    ///      这样"红级校验阻断"不会留下被改脏的模型，"取消"也不需要回滚逻辑。
    ///   2. **类型可改，残留可清**。改类型时把新类型用不上的字段列出来让用户确认后清空——
    ///      否则那些字段会变成"界面上看不见、执行时仍生效"的配置。
    ///   3. **校验分两级**。`RuleValidator` 的 Error 标红并阻断保存，Warning 标黄只提醒；
    ///      名称类提示（`NameValidator`）一律是 Warning。
    /// </summary>
    internal sealed class RuleEditorDrawer : Form
    {
        private readonly RuleConfig _original;
        private readonly RuleConfig _working;
        private readonly INameLookup _nameLookup;
        private readonly Font _font;
        private readonly Font _fontBold;
        private readonly Font _fontSmall;
        private readonly Font _fontMono;

        private readonly List<FieldRow> _rows = new List<FieldRow>();

        /// <summary>字段路径 → "把拾取到的名字填进这个控件"的动作。拾取回来重开抽屉时按路径重放。</summary>
        private readonly Dictionary<string, Action<string>> _applyPicked =
            new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly PickContinuation _pendingPick;
        private PickContinuation _pendingPickOut;

        private Panel _headerHost;
        private Panel _footer;
        private Panel _bodyHost;
        private Panel _body;
        private Panel _jsonHost;
        private TextBox _jsonPreview;
        private Label _status;
        private BatchToolForm.FlatButton _btnJson;

        private TextBox _nameBox;
        private ComboBox _typeBox;

        public RuleEditorDrawer(RuleConfig rule, INameLookup nameLookup,
            Font font, Font fontBold, Font fontSmall, Font fontMono)
            : this(rule, nameLookup, font, fontBold, fontSmall, fontMono, null)
        {
        }

        /// <param name="pendingPick">
        /// 上一次"收起抽屉去图上拾取"的上下文。非 null 时：抽屉按它带回来的副本继续编辑，
        /// 并把 <see cref="PickContinuation.Value"/> 填回当初点「拾取」的那个字段。
        /// </param>
        public RuleEditorDrawer(RuleConfig rule, INameLookup nameLookup,
            Font font, Font fontBold, Font fontSmall, Font fontMono, PickContinuation pendingPick)
        {
            _original = rule;
            _nameLookup = nameLookup;
            _pendingPick = pendingPick;
            _font = font;
            _fontBold = fontBold;
            _fontSmall = fontSmall;
            _fontMono = fontMono;

            // 深拷贝：编辑期间不碰调用方的对象。拷贝失败宁可打不开，也不能拿空规则去覆盖原配置
            _working = Clone(rule);
            if (_working == null)
            {
                throw new InvalidOperationException("无法复制规则内容，已取消编辑（原配置未改动）。");
            }

            BuildUi();
            BuildFields();

            // 拾取结果回填（必须在 BuildFields 之后：那时控件的"填入"动作才存在）
            ApplyPendingPick();
        }

        /// <summary>保存后的规则（仅在 <see cref="DialogResult.OK"/> 时有效）。</summary>
        public RuleConfig Result
        {
            get { return _working; }
        }

        /// <summary>
        /// 用户点了「拾取」：抽屉已关闭，等待宿主去图上取名字。
        /// 为 true 时 <see cref="DialogResult"/> 一定是 Cancel，调用方不能把它当成"取消编辑"。
        /// </summary>
        public bool PickRequested
        {
            get { return _pendingPickOut != null; }
        }

        /// <summary>本次拾取要继续的上下文（<see cref="PickRequested"/> 为 true 时有效）。</summary>
        public PickContinuation PendingPick
        {
            get { return _pendingPickOut; }
        }

        // ------------------------------------------------------------------ 骨架

        private void BuildUi()
        {
            Text = "编辑规则  ·  " + (string.IsNullOrEmpty(_original.Name) ? _original.Type : _original.Name);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = BatchToolForm.BgColor;
            Font = _font;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            // 尺寸按屏幕可用区域夹取：150% DPI 的 1366×768 上，900×600 逻辑像素会超出屏幕
            Rectangle area = Screen.PrimaryScreen == null
                ? new Rectangle(0, 0, 1280, 800)
                : Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(
                Math.Min(900, Math.Max(760, area.Width - 120)),
                Math.Min(620, Math.Max(520, area.Height - 120)));
            MinimumSize = new Size(760, 520);

            BuildHeader();
            BuildBody();
            BuildJsonPanel();
            BuildFooter();

            // Dock 按"后加的在外侧"处理：Fill 先加，其余按 Top/Bottom 依次加
            Controls.Add(_bodyHost);
            Controls.Add(_jsonHost);
            Controls.Add(_footer);
            Controls.Add(_headerHost);
        }

        private void BuildHeader()
        {
            _headerHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = 92,
                BackColor = BatchToolForm.BgColor,
                Padding = new Padding(16, 10, 16, 0)
            };

            var card = new BatchToolForm.CardPanel { Dock = DockStyle.Fill };

            var nameLabel = MakeHint("名称", 4, 16);
            var nameShell = new InputShell { Left = 52, Top = 8, Width = 240, Height = 30 };
            _nameBox = new TextBox
            {
                Text = _working.Name ?? string.Empty,
                BorderStyle = BorderStyle.None,
                BackColor = BatchToolForm.CardColor
            };
            nameShell.Attach(_nameBox);
            _nameBox.TextChanged += delegate { _working.Name = _nameBox.Text; };

            var typeLabel = MakeHint("类型", 312, 16);
            _typeBox = new ComboBox
            {
                Left = 356,
                Top = 10,
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            foreach (string type in RuleFactory.GetRegisteredTypes())
            {
                _typeBox.Items.Add(type);
            }

            // 注册表为空（还没初始化）或当前类型是自研类型时，至少让下拉里能看到当前类型，
            // 否则 SelectedIndex 会被设成一个非法值，直接抛异常
            if (!string.IsNullOrEmpty(_working.Type) && !_typeBox.Items.Contains(_working.Type))
            {
                _typeBox.Items.Add(_working.Type);
            }

            // 先选好再挂事件：构造期触发 OnTypeChanged 会在字段容器还没建好时重建字段
            if (_typeBox.Items.Count > 0)
            {
                int index = _typeBox.Items.IndexOf(_working.Type);
                _typeBox.SelectedIndex = index >= 0 ? index : 0;
            }

            _typeBox.SelectedIndexChanged += OnTypeChanged;

            var enabledSwitch = new BatchToolForm.SwitchBox { Left = 574, Top = 14 };
            enabledSwitch.Checked = _working.Enabled;
            enabledSwitch.CheckedChanged += delegate { _working.Enabled = enabledSwitch.Checked; };

            var enabledLabel = MakeHint("启用", 620, 16);
            enabledLabel.ForeColor = BatchToolForm.TextColor;

            // WinForms 的 z 序是"先加的在上层"：标签先加会盖住输入控件。
            // 这里标签先加、控件后加，再对控件显式 BringToFront，保证点击和文字都不被遮。
            card.Controls.Add(nameLabel);
            card.Controls.Add(typeLabel);
            card.Controls.Add(enabledLabel);
            card.Controls.Add(nameShell);
            card.Controls.Add(_typeBox);
            card.Controls.Add(enabledSwitch);

            nameShell.BringToFront();
            _typeBox.BringToFront();
            enabledSwitch.BringToFront();

            _headerHost.Controls.Add(card);
        }

        private void BuildBody()
        {
            _bodyHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BatchToolForm.BgColor,
                Padding = new Padding(16, 10, 16, 0)
            };

            var card = new BatchToolForm.CardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 8, 8) };

            _body = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = BatchToolForm.CardColor
            };
            _body.ClientSizeChanged += delegate { RelayoutRows(); };

            card.Controls.Add(_body);
            _bodyHost.Controls.Add(card);
        }

        private void BuildJsonPanel()
        {
            _jsonHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 0,
                Visible = false,
                BackColor = BatchToolForm.BgColor,
                Padding = new Padding(16, 8, 16, 0)
            };

            var card = new BatchToolForm.CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10) };

            _jsonPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = _fontMono,
                BorderStyle = BorderStyle.None,
                BackColor = BatchToolForm.CardColor
            };

            card.Controls.Add(_jsonPreview);
            _jsonHost.Controls.Add(card);
        }

        private void BuildFooter()
        {
            _footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                BackColor = BatchToolForm.BgColor
            };

            // 按钮用"右对齐的流式布局"，不算绝对坐标：
            // 构造期的 ClientSize 是 DPI 缩放后的值，用 ClientSize.Width - N 定位会把按钮推到可视区外
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BatchToolForm.BgColor,
                Padding = new Padding(0, 14, 16, 0),
                Margin = Padding.Empty
            };

            _btnJson = MakeFooterButton("显示 JSON", BatchToolForm.FlatButtonKind.Ghost, 96);
            _btnJson.Click += delegate { ToggleJson(); };

            var btnCancel = MakeFooterButton("取消", BatchToolForm.FlatButtonKind.Ghost, 84);
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            var btnSave = MakeFooterButton("保存", BatchToolForm.FlatButtonKind.Primary, 96);
            btnSave.Click += OnSave;

            // RightToLeft：先加的排最右
            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(_btnJson);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = BatchToolForm.TextDim,
                BackColor = Color.Transparent,
                Text = string.Empty,
                AutoSize = false,
                Font = _fontSmall
            };

            _footer.Controls.Add(_status);
            _footer.Controls.Add(buttons);
        }

        private BatchToolForm.FlatButton MakeFooterButton(string text, BatchToolForm.FlatButtonKind kind, int width)
        {
            return new BatchToolForm.FlatButton
            {
                Text = text,
                Kind = kind,
                Width = width,
                Height = 34,
                Margin = new Padding(8, 0, 0, 0)
            };
        }

        private Label MakeHint(string text, int left, int top)
        {
            return new Label
            {
                Text = text,
                Left = left,
                Top = top,
                Width = 44,
                Height = 20,
                ForeColor = BatchToolForm.TextDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                Font = _fontSmall
            };
        }

        // ------------------------------------------------------------------ 字段

        /// <summary>按当前类型重建字段区。</summary>
        private void BuildFields()
        {
            if (_body == null)
            {
                return;
            }

            _body.SuspendLayout();
            _body.Controls.Clear();
            _rows.Clear();
            _applyPicked.Clear();

            List<FieldDescriptor> fields = FieldCatalog.For(_working.Type, _working);
            int width = Math.Max(320, _body.ClientSize.Width - 28);

            int y = 0;
            RuleFieldGroup? currentGroup = null;

            foreach (FieldDescriptor field in fields)
            {
                if (!currentGroup.HasValue || currentGroup.Value != field.Group)
                {
                    currentGroup = field.Group;
                    var header = new SectionHeader(GroupTitle(field.Group), GroupHint(field.Group), _fontBold, _fontSmall)
                    {
                        Left = 0,
                        Top = y,
                        Width = width
                    };
                    _body.Controls.Add(header);
                    y += header.Height + 6;
                }

                int rowHeight;
                Action<string> applyPicked;
                Control editor = FieldControlFactory.Create(field, _working,
                    delegate { RefreshJsonPreview(); }, BeginPick, out rowHeight, out applyPicked);

                if (applyPicked != null)
                {
                    _applyPicked[field.Path] = applyPicked;
                }

                var row = new FieldRow(field, editor, rowHeight, _font, _fontSmall)
                {
                    Left = 0,
                    Top = y,
                    Width = width
                };

                if (field.Disabled && !string.IsNullOrEmpty(field.DisabledReason))
                {
                    row.SetIssue(field.DisabledReason, false);
                }

                _body.Controls.Add(row);
                _rows.Add(row);
                y += row.Height + 4;
            }

            _body.ResumeLayout();
            RefreshJsonPreview();
        }

        /// <summary>
        /// 点「拾取」：把"要继续编辑的副本 + 目标字段"打包，然后**关掉抽屉**，交给主窗口去排队 BATCHPICK。
        ///
        /// 为什么是 Close 而不是 Hide：模态窗口 `Hide()` 会让 `ShowDialog` 立刻返回
        /// （实测 .NET 8 上 13ms 就返回 Cancel），调用方的 using 会顺手 Dispose 掉抽屉，
        /// 窗口直接消失、拾取结果也没地方填。关掉抽屉后由主窗口在非模态上下文里排队命令，
        /// 拿到结果再新建一个抽屉、带着副本和名字重新打开——用户看到的是"表单收起 → 图上点一下 → 表单回来"。
        /// </summary>
        private void BeginPick(FieldDescriptor field, Action<string> apply)
        {
            if (_pendingPickOut != null || field == null || field.Meta == null)
            {
                return;
            }

            NameKind kind = field.Meta.ValidateAs == RuleFieldValidateAs.Block ? NameKind.Block : NameKind.Layer;

            _pendingPickOut = new PickContinuation(_working, field.Path, kind);
            DiagnosticLog.Write(string.Format("抽屉请求拾取：字段={0} 种类={1}", field.Path, kind));

            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>把上一次拾取的结果填回对应字段（抽屉是新实例，控件刚建好）。</summary>
        private void ApplyPendingPick()
        {
            if (_pendingPick == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(_pendingPick.Value))
            {
                SetStatus(string.IsNullOrEmpty(_pendingPick.Message) ? "已取消拾取，可手动输入名称。" : _pendingPick.Message,
                    !string.IsNullOrEmpty(_pendingPick.Message));
                return;
            }

            Action<string> apply;
            if (!_applyPicked.TryGetValue(_pendingPick.FieldPath ?? string.Empty, out apply) || apply == null)
            {
                // 换了规则类型 / 该字段被隐藏：结果无处可填，明确告知而不是静默丢弃
                SetStatus("已拾取：" + _pendingPick.Value + "（当前表单里没有对应字段，未填入）", true);
                return;
            }

            apply(_pendingPick.Value);
            SetStatus("已拾取：" + _pendingPick.Value, false);
        }

        private void SetStatus(string text, bool warn)
        {
            if (_status == null)
            {
                return;
            }

            _status.Text = text;
            _status.ForeColor = warn ? BatchToolForm.Warn : BatchToolForm.TextDim;
        }

        private static string GroupTitle(RuleFieldGroup group)
        {
            switch (group)
            {
                case RuleFieldGroup.Filter:
                    return "过滤条件";
                case RuleFieldGroup.Advanced:
                    return "高级";
                default:
                    return "操作";
            }
        }

        private static string GroupHint(RuleFieldGroup group)
        {
            switch (group)
            {
                case RuleFieldGroup.Filter:
                    return "选中哪些对象";
                case RuleFieldGroup.Advanced:
                    return "扫描范围与遍历行为";
                default:
                    return "改成什么";
            }
        }

        /// <summary>面板宽度变化时让每行跟着变宽（行内控件靠 FieldRow 自己重排）。</summary>
        private void RelayoutRows()
        {
            int width = Math.Max(320, _body.ClientSize.Width - 28);
            foreach (FieldRow row in _rows)
            {
                row.Width = width;
            }

            foreach (Control control in _body.Controls)
            {
                var header = control as SectionHeader;
                if (header != null)
                {
                    header.Width = width;
                }
            }
        }

        private void OnTypeChanged(object sender, EventArgs e)
        {
            string newType = _typeBox.SelectedItem as string;
            if (string.IsNullOrEmpty(newType) || string.Equals(newType, _working.Type, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            List<string> stale = CollectStaleFields(newType);
            if (stale.Count > 0)
            {
                string message = string.Format(
                    "改成 {0} 之后，下面这些字段不再被读取：\n\n{1}\n\n清空它们吗？（不清空的话会留在配置里但不起作用）",
                    newType, string.Join("、", stale.ToArray()));

                if (MessageBox.Show(this, message, "切换类型", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    int oldIndex = _typeBox.Items.IndexOf(_working.Type);
                    _typeBox.SelectedIndexChanged -= OnTypeChanged;
                    _typeBox.SelectedIndex = oldIndex >= 0 ? oldIndex : 0;
                    _typeBox.SelectedIndexChanged += OnTypeChanged;
                    return;
                }

                foreach (string path in stale)
                {
                    SetByPath(path, null);
                }
            }

            _working.Type = newType;
            BuildFields();
        }

        /// <summary>新类型用不上、但当前有值的字段（显示成中文标签，便于用户判断）。</summary>
        private List<string> CollectStaleFields(string newType)
        {
            var labels = new List<string>();
            HashSet<string> notUsed = FieldCatalog.FieldPathsNotUsedBy(newType);

            foreach (FieldDescriptor field in FieldCatalog.All())
            {
                if (!notUsed.Contains(field.Path))
                {
                    continue;
                }

                if (IsSet(field.GetValue(_working)))
                {
                    labels.Add(field.Label);
                }
            }

            return labels;
        }

        private void SetByPath(string path, object value)
        {
            foreach (FieldDescriptor field in FieldCatalog.All())
            {
                if (string.Equals(field.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    field.SetValue(_working, value);
                    return;
                }
            }
        }

        private static bool IsSet(object value)
        {
            if (value == null)
            {
                return false;
            }

            var text = value as string;
            if (text != null)
            {
                return !string.IsNullOrWhiteSpace(text);
            }

            var list = value as System.Collections.IList;
            if (list != null)
            {
                return list.Count > 0;
            }

            return true;
        }

        private void ToggleJson()
        {
            if (_jsonHost.Visible)
            {
                _jsonHost.Visible = false;
                _jsonHost.Height = 0;
                _btnJson.Text = "显示 JSON";
            }
            else
            {
                _jsonPreview.Text = RuleJson.Serialize(_working);
                _jsonHost.Height = 170;
                _jsonHost.Visible = true;
                _btnJson.Text = "隐藏 JSON";
            }
        }

        private void RefreshJsonPreview()
        {
            if (_jsonHost != null && _jsonHost.Visible)
            {
                _jsonPreview.Text = RuleJson.Serialize(_working);
            }
        }

        // ------------------------------------------------------------------ 保存

        private void OnSave(object sender, EventArgs e)
        {
            // 先把焦点挪走，保证"离开字段才解析"的数值框已经提交
            if (ActiveControl != null)
            {
                ActiveControl = null;
            }

            foreach (FieldRow row in _rows)
            {
                row.ClearIssue();
            }

            List<FieldIssue> issues = RuleValidator.Validate(_working);
            if (_nameLookup != null)
            {
                issues.AddRange(NameValidator.Validate(_working, _nameLookup));
            }

            int errors = 0;
            int warnings = 0;

            foreach (FieldIssue issue in issues)
            {
                if (issue.IsError)
                {
                    errors++;
                }
                else
                {
                    warnings++;
                }

                FieldRow target = FindRow(issue.Path);
                if (target == null)
                {
                    continue;
                }

                // 同一个字段已有提示时优先保留 Error
                if (issue.IsError || string.IsNullOrEmpty(target.IssueText))
                {
                    target.SetIssue(issue.Message, issue.IsError);
                }
            }

            if (errors > 0)
            {
                _status.Text = string.Format("有 {0} 处必须修正（红色），{1} 处提醒（黄色）。修正后才能保存。", errors, warnings);
                _status.ForeColor = Color.FromArgb(190, 40, 40);
                return;
            }

            if (warnings > 0)
            {
                _status.Text = string.Format("{0} 处提醒（黄色），不影响保存。", warnings);
                _status.ForeColor = BatchToolForm.Warn;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private FieldRow FindRow(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (FieldRow row in _rows)
            {
                if (string.Equals(row.Field.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>用 JSON 往返做深拷贝：字段全是简单类型，比手写 Clone 可靠。</summary>
        private static RuleConfig Clone(RuleConfig rule)
        {
            if (rule == null)
            {
                return null;
            }

            try
            {
                string json = RuleJson.Serialize(rule);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<RuleConfig>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
