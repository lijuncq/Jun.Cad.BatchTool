using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json;
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.UI
{
    /// <summary>
    /// 批量处理主界面。
    ///
    /// 视觉约定（改版后）：
    ///   * 浅灰底 + 白色圆角卡片，1px 描边，弱化分割线；
    ///   * 规则列表用自绘的"卡片行"（开关 + 名称 + 类型徽标 + 摘要），
    ///     替代原生 ListView——原生控件在高 DPI 下观感差，且勾选列无法定制；
    ///   * 主操作用实心蓝按钮，次要操作用描边/无边框按钮，形成明确层级。
    ///
    /// 功能不变：打开即加载默认配置、勾选规则、调执行设置、保存设置、执行。
    /// </summary>
    public sealed class BatchToolForm : Form
    {
        // ------------------------------------------------------------------ 配色

        internal static readonly Color BgColor = Color.FromArgb(245, 246, 250);
        internal static readonly Color CardColor = Color.White;
        internal static readonly Color BorderColor = Color.FromArgb(228, 231, 238);
        internal static readonly Color BorderStrong = Color.FromArgb(206, 212, 224);
        internal static readonly Color TextColor = Color.FromArgb(31, 36, 48);
        internal static readonly Color TextDim = Color.FromArgb(107, 114, 128);
        internal static readonly Color Accent = Color.FromArgb(47, 111, 237);
        internal static readonly Color AccentSoft = Color.FromArgb(237, 242, 254);
        internal static readonly Color Warn = Color.FromArgb(180, 108, 6);
        internal static readonly Color WarnDot = Color.FromArgb(234, 179, 8);
        internal static readonly Color RowHover = Color.FromArgb(250, 251, 255);

        // ------------------------------------------------------------------ 字体

        private Font _fontBase;
        private Font _fontBold;
        private Font _fontTitle;
        private Font _fontSmall;
        private Font _fontMono;

        // ------------------------------------------------------------------ 控件

        private Panel _body;
        private Panel _header;
        private Panel _cardSettings;
        private Panel _cardConfig;
        private Panel _footer;

        // Tab 切换
        private FlatButton _tabRunBtn;
        private FlatButton _tabEditBtn;
        private Panel _tabContent;
        private Panel _panelRun;
        private Panel _panelEdit;

        private InputPanel _inputPath;
        private FlatButton _btnBrowse;
        private FlatButton _btnReload;
        private Label _lblStatus;

        private ComboBox _cboMode;
        private SwitchBox _swContinue;
        private SwitchBox _swVerbose;

        private FlowLayoutPanel _rules;

        private FlatButton _btnAll;
        private FlatButton _btnNone;
        private FlatButton _btnOpen;
        private FlatButton _btnRun;
        private FlatButton _btnClose;

        // ------------------------------------------------------------------ 状态

        private readonly List<string> _warnings = new List<string>();
        private readonly List<RuleRow> _rows = new List<RuleRow>();
        private BatchConfig _config;
        private string _selectedPath = string.Empty;
        private RuleRow _selectedRow;
        private RuleDetailForm _detailForm;
        private bool _suppressStatus;

        // Tab2（规则编辑）状态
        private readonly List<EditRuleRow> _editRows = new List<EditRuleRow>();
        private TextBox _jsonEditor;
        private Label _editStatus;
        private int _selectedEditIndex = -1;
        private FlatButton _btnAddRule;
        private FlatButton _btnDelRule;
        private FlatButton _btnEditRule;

        /// <summary>「添加规则」用哪种类型做模板——类型清单来自 RuleFactory，新增规则类型自动出现在这里。</summary>
        private ComboBox _cmbNewType;

        /// <summary>名称查询（可为 null）。只用于保存前的"这个名字在当前图纸里存在吗"提醒。</summary>
        private readonly INameLookup _nameLookup;

        /// <summary>拾取命令的排队入口（可为 null，此时表单里没有「拾取」按钮）。</summary>
        private readonly IPickHost _pickHost;

        /// <summary>拾取命令的看门狗：只在"命令还没跑起来"时兜底，命令一开始等用户点选就停掉。</summary>
        private Timer _pickWatchdog;
        private int _pickToken;

        /// <summary>规则行的黄色徽标文案（延迟创建，只在真正校验时才需要）。</summary>
        private ToolTip _rowTip;

        public BatchToolForm(string configPath)
            : this(configPath, null, null)
        {
        }

        /// <summary>
        /// 带名称查询的构造。传 null 表示不做名称校验（例如在没有文档的环境里构造，或测试时）。
        /// 之所以用接口注入而不是让 UI 直接读符号表：UI 层不得引用 AutoCAD / 中望CAD 的类型。
        /// </summary>
        public BatchToolForm(string configPath, INameLookup nameLookup)
            : this(configPath, nameLookup, null)
        {
        }

        public BatchToolForm(string configPath, INameLookup nameLookup, IPickHost pickHost)
        {
            _nameLookup = nameLookup;
            _pickHost = pickHost;
            CreateFonts();

            Text = "批量处理  ·  Jun.AutoCAD.BatchTool";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = BgColor;
            // 宽度取原设计的 3/4（980 → 735），高度不变
            ClientSize = new Size(735, 700);
            MinimumSize = new Size(600, 560);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = _fontBase;
            DoubleBuffered = true;

            BuildUi();

            if (!string.IsNullOrEmpty(configPath))
            {
                _inputPath.Text = configPath;
                LoadConfig(configPath, false);
            }
        }

        /// <summary>界面上的配置（含勾选后的 Enabled 状态）。</summary>
        public BatchConfig Config
        {
            get { return _config; }
        }

        /// <summary>当前选中的配置文件路径（读缓存，窗口释放后仍可访问）。</summary>
        public string SelectedConfigPath
        {
            get { return _selectedPath; }
        }

        private void CreateFonts()
        {
            string family = PickFontFamily();
            _fontBase = new Font(family, 9F, FontStyle.Regular, GraphicsUnit.Point);
            _fontBold = new Font(family, 9.5F, FontStyle.Bold, GraphicsUnit.Point);
            _fontTitle = new Font(family, 14F, FontStyle.Bold, GraphicsUnit.Point);
            _fontSmall = new Font(family, 8.25F, FontStyle.Regular, GraphicsUnit.Point);
            _fontMono = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static string PickFontFamily()
        {
            try
            {
                foreach (FontFamily f in FontFamily.Families)
                {
                    if (string.Equals(f.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase))
                    {
                        return f.Name;
                    }
                }
            }
            catch
            {
                // 忽略，走回退
            }

            return "Segoe UI";
        }

        // ------------------------------------------------------------------ 布局

        private void BuildUi()
        {
            _header = BuildHeader();

            // Tab 内容容器
            _tabContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor
            };

            _panelRun = BuildRunPanel();
            _panelEdit = BuildEditPanel();

            // 后加的在上层：默认显示执行面板（_panelEdit 在上层但隐藏）
            _tabContent.Controls.Add(_panelRun);
            _tabContent.Controls.Add(_panelEdit);
            _panelEdit.Visible = false;

            Controls.Add(_tabContent); // Fill（最后布局）
            Controls.Add(_header);     // Top（最先布局）
        }

        /// <summary>构建"执行"面板（tab1）：原来的 body + footer。</summary>
        private Panel BuildRunPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor
            };

            _footer = BuildFooter();
            _rules = BuildRulesHost();
            _cardSettings = BuildSettingsCard();

            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor,
                Padding = new Padding(16, 10, 16, 8)
            };
            _body.Controls.Add(_rules);
            _body.Controls.Add(_cardSettings);

            panel.Controls.Add(_body);
            panel.Controls.Add(_footer);
            return panel;
        }

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 108,
                BackColor = CardColor,
                Padding = new Padding(24, 14, 24, 0)
            };
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                }
            };

            var accentBar = new Panel
            {
                BackColor = Accent,
                Location = new Point(22, 17),
                Size = new Size(4, 26)
            };

            var title = new Label
            {
                Text = "批量处理",
                Font = _fontTitle,
                ForeColor = TextColor,
                AutoSize = true,
                Location = new Point(36, 14),
                BackColor = Color.Transparent
            };

            var subtitle = new Label
            {
                Text = "Jun.AutoCAD.BatchTool  ·  " + PluginInfo.Describe(),
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(38, 46),
                BackColor = Color.Transparent
            };

            // ---------- Tab 切换栏 ----------
            _tabRunBtn = new FlatButton
            {
                Text = "执行",
                Kind = FlatButtonKind.Ghost,
                Width = 72,
                Height = 32,
                Top = 72,
                Left = 20,
                Font = _fontBold
            };
            _tabRunBtn.Click += delegate { SwitchTab(0); };

            _tabEditBtn = new FlatButton
            {
                Text = "规则编辑",
                Kind = FlatButtonKind.Ghost,
                Width = 88,
                Height = 32,
                Top = 72,
                Left = 96,
                Font = _fontBase
            };
            _tabEditBtn.Click += delegate { SwitchTab(1); };

            // Tab 指示条（选中态下划线）
            var tabIndicator = new Panel
            {
                Name = "tabIndicator",
                BackColor = Accent,
                Height = 2,
                Width = 72,
                Top = 104,
                Left = 20
            };

            header.Controls.Add(accentBar);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(_tabRunBtn);
            header.Controls.Add(_tabEditBtn);
            header.Controls.Add(tabIndicator);
            return header;
        }

        /// <summary>
        /// 执行设置卡片（tab1「执行」页）：只保留执行模式 / 出错继续 / 详细输出。
        ///
        /// 配置文件相关内容（路径输入框、浏览、重新加载、状态行）已移到 tab2「规则编辑」页，
        /// 执行页只放"跑批处理"直接相关的设置。
        /// </summary>
        private Panel BuildSettingsCard()
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Top,
                Height = 88,
                Padding = new Padding(20, 12, 20, 12)
            };

            var captionSettings = new Label
            {
                Text = "执行设置",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 10),
                BackColor = Color.Transparent
            };

            var lblMode = new Label
            {
                Text = "执行模式",
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(20, 40),
                BackColor = Color.Transparent
            };

            _cboMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Standard,
                BackColor = CardColor,
                ForeColor = TextColor,
                Location = new Point(88, 37),
                Width = 150
            };
            _cboMode.Items.Add("Sequential  单事务");
            _cboMode.Items.Add("Isolated  逐条提交");
            _cboMode.SelectedIndex = 0;

            _swContinue = new SwitchBox
            {
                Location = new Point(256, 39),
                Checked = true
            };
            _swContinue.CheckedChanged += delegate { UpdateStatus(); };

            var lblContinue = new Label
            {
                Text = "单条失败后继续",
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(302, 40),
                BackColor = Color.Transparent
            };

            _swVerbose = new SwitchBox
            {
                Location = new Point(420, 39),
                Checked = true
            };

            var lblVerbose = new Label
            {
                Text = "命令行输出明细",
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(466, 40),
                BackColor = Color.Transparent
            };

            card.Controls.Add(captionSettings);
            card.Controls.Add(lblMode);
            card.Controls.Add(_cboMode);
            card.Controls.Add(_swContinue);
            card.Controls.Add(lblContinue);
            card.Controls.Add(_swVerbose);
            card.Controls.Add(lblVerbose);

            return card;
        }

        /// <summary>
        /// 配置文件卡片（tab2「规则编辑」页）：路径输入框 + 浏览/重新加载 + 状态行。
        ///
        /// 布局要点：路径输入框与「浏览/重新加载」按钮**同一水平线**（Top 都是 32，高度都是 32）。
        /// </summary>
        private Panel BuildConfigCard()
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Top,
                Height = 104,
                Padding = new Padding(20, 12, 20, 12)
            };

            var captionConfig = new Label
            {
                Text = "配置文件",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 10),
                BackColor = Color.Transparent
            };

            const int inputTop = 32;
            const int inputHeight = 32;

            _inputPath = new InputPanel
            {
                Location = new Point(20, inputTop),
                Height = inputHeight,
                ReadOnly = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnBrowse = new FlatButton
            {
                Text = "浏览...",
                Kind = FlatButtonKind.Secondary,
                Width = 82,
                Height = inputHeight,
                Top = inputTop,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowse.Click += OnBrowse;

            _btnReload = new FlatButton
            {
                Text = "重新加载",
                Kind = FlatButtonKind.Secondary,
                Width = 90,
                Height = inputHeight,
                Top = inputTop,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnReload.Click += delegate { LoadConfig(_selectedPath, true); };

            _lblStatus = new Label
            {
                Text = string.Empty,
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = false,
                Height = 18,
                Location = new Point(22, 72),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent
            };

            card.Controls.Add(captionConfig);
            card.Controls.Add(_inputPath);
            card.Controls.Add(_btnBrowse);
            card.Controls.Add(_btnReload);
            card.Controls.Add(_lblStatus);

            card.Resize += delegate
            {
                int right = card.ClientSize.Width - 20;

                // 按钮与输入框同一水平线：Top/Height 一致，只调整水平位置
                _btnReload.Left = right - _btnReload.Width;
                _btnBrowse.Left = _btnReload.Left - 8 - _btnBrowse.Width;
                _inputPath.Width = Math.Max(180, _btnBrowse.Left - 12 - _inputPath.Left);
                _lblStatus.Width = Math.Max(100, right - _lblStatus.Left);
            };

            return card;
        }


        private FlowLayoutPanel BuildRulesHost()
        {
            var host = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = BgColor,
                Padding = new Padding(0, 10, 0, 8)
            };

            host.ClientSizeChanged += delegate { ResizeRows(); };
            host.ControlAdded += delegate { ResizeRows(); };

            return host;
        }

        // ------------------------------------------------------------------ Tab 切换

        private void SwitchTab(int index)
        {
            if (index == 0)
            {
                _panelRun.Visible = true;
                _panelEdit.Visible = false;
                _tabRunBtn.Font = _fontBold;
                _tabEditBtn.Font = _fontBase;
                MoveTabIndicator(_tabRunBtn);
            }
            else
            {
                _panelRun.Visible = false;
                _panelEdit.Visible = true;
                _tabRunBtn.Font = _fontBase;
                _tabEditBtn.Font = _fontBold;
                MoveTabIndicator(_tabEditBtn);
                RefreshEditPanel();
            }
        }

        private void MoveTabIndicator(Control target)
        {
            foreach (Control c in _header.Controls)
            {
                if (c.Name == "tabIndicator")
                {
                    c.Left = target.Left;
                    c.Width = target.Width;
                    c.Invalidate();
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ 规则编辑面板 (tab2)

        private Panel BuildEditPanel()
        {
            // 先把内置规则注册上：面板构造早于 LoadConfig，而注册原本只在加载配置时发生，
            // 结果就是"类型下拉是空的"。RegisterBuiltIns 是幂等的，这里调用没有副作用。
            RuleFactory.RegisterBuiltIns();

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor,
                Padding = new Padding(16, 10, 16, 8)
            };

            // 工具栏
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = BgColor
            };

            _cmbNewType = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Left = 0,
                Top = 6,
                Width = 150,
                Font = _fontSmall
            };
            foreach (string type in RuleFactory.GetRegisteredTypes())
            {
                _cmbNewType.Items.Add(type);
            }

            if (_cmbNewType.Items.Count > 0)
            {
                // 默认选中 EntityLayerChange（最常用的通用改层规则），没有就选第一个
                int index = _cmbNewType.Items.IndexOf("EntityLayerChange");
                _cmbNewType.SelectedIndex = index >= 0 ? index : 0;
            }

            // 「编辑规则」是主入口：双击行也能打开，但双击没有任何视觉提示，必须给一个显式按钮
            _btnEditRule = MakeButton("编辑规则", FlatButtonKind.Primary, 92);
            _btnEditRule.Left = 158;
            _btnEditRule.Top = 4;
            _btnEditRule.Click += delegate { OpenRuleEditor(_selectedEditIndex); };

            _btnAddRule = MakeButton("添加规则", FlatButtonKind.Secondary, 92);
            _btnAddRule.Left = 258;
            _btnAddRule.Top = 4;
            _btnAddRule.Click += OnAddRule;

            _btnDelRule = MakeButton("删除规则", FlatButtonKind.Ghost, 92);
            _btnDelRule.Left = 358;
            _btnDelRule.Top = 4;
            _btnDelRule.Click += OnDelRule;

            var btnUp = MakeButton("上移", FlatButtonKind.Ghost, 60);
            btnUp.Left = 458;
            btnUp.Top = 4;
            btnUp.Click += OnMoveUp;

            var btnDown = MakeButton("下移", FlatButtonKind.Ghost, 60);
            btnDown.Left = 522;
            btnDown.Top = 4;
            btnDown.Click += OnMoveDown;

            // 「打开配置文件」从执行页挪到这里：它属于"改配置"而不是"跑批处理"
            _btnOpen = MakeButton("打开配置文件", FlatButtonKind.Ghost, 110);
            _btnOpen.Left = 584;
            _btnOpen.Top = 4;
            _btnOpen.Click += OnOpenJson;

            toolbar.Controls.Add(_cmbNewType);
            toolbar.Controls.Add(_btnEditRule);
            toolbar.Controls.Add(_btnAddRule);
            toolbar.Controls.Add(_btnDelRule);
            toolbar.Controls.Add(btnUp);
            toolbar.Controls.Add(btnDown);
            toolbar.Controls.Add(_btnOpen);

            // 配置文件卡片（从执行页挪来）
            _cardConfig = BuildConfigCard();

            // 规则列表区
            var listCard = new CardPanel
            {
                Dock = DockStyle.Top,
                Height = 240,
                Padding = new Padding(12, 10, 12, 10)
            };

            var listHost = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CardColor
            };
            listHost.ClientSizeChanged += delegate
            {
                int w = Math.Max(200, listHost.ClientSize.Width - 24);
                foreach (EditRuleRow r in _editRows)
                {
                    r.Width = w;
                }
            };
            listCard.Controls.Add(listHost);

            // JSON 编辑区
            var jsonCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };

            var jsonCaption = new Label
            {
                Text = "规则 JSON（只读预览；编辑请点「编辑规则」或双击规则行）",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(12, 8),
                BackColor = Color.Transparent
            };

            _jsonEditor = new TextBox
            {
                Location = new Point(12, 28),
                Size = new Size(300, 200),
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(243, 244, 247),
                ForeColor = Color.FromArgb(90, 96, 110),
                Font = _fontMono,
                // 2026-10-29 起这里只做**只读预览**：编辑走双击打开的抽屉（RuleEditorDrawer）。
                // 保留这个框是因为"看一眼当前配置长什么样"仍然有用，也方便复制。
                ReadOnly = true,
                // 尺寸由 jsonCard 的 Resize 统一算，这里不叠 Anchor，避免两套逻辑互相拉扯
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            jsonCard.Controls.Add(_jsonEditor);
            jsonCard.Controls.Add(jsonCaption);

            // 底部保存栏
            var editFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = BgColor
            };

            _editStatus = new Label
            {
                Text = string.Empty,
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(0, 20),
                BackColor = Color.Transparent
            };

            var btnSaveRules = MakeButton("保存规则到配置", FlatButtonKind.Primary, 136);
            btnSaveRules.Top = 10;
            btnSaveRules.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSaveRules.Click += OnSaveRules;

            editFooter.Controls.Add(_editStatus);
            editFooter.Controls.Add(btnSaveRules);

            // 保存按钮靠右、状态标签靠左：随底部栏宽度调整
            editFooter.Resize += delegate
            {
                btnSaveRules.Left = Math.Max(0, editFooter.ClientSize.Width - btnSaveRules.Width);
            };

            // JSON 编辑框跟随卡片大小
            jsonCard.Resize += delegate
            {
                _jsonEditor.Width = Math.Max(200, jsonCard.ClientSize.Width - 24);
                _jsonEditor.Height = Math.Max(80, jsonCard.ClientSize.Height - 40);
            };

            // 停靠顺序（Controls 逆序布局）：后加的控件先占位，
            // 所以 Fill 的 jsonCard 必须【最先添加】，其余按"从下到上"逆着加。
            // _cardConfig 加在 toolbar 之后 → 比 toolbar 更早布局 → 显示在工具栏上方。
            panel.Controls.Add(jsonCard);     // Fill（最后布局，占剩余空间）
            panel.Controls.Add(editFooter);   // Bottom
            panel.Controls.Add(listCard);     // Top
            panel.Controls.Add(toolbar);      // Top
            panel.Controls.Add(_cardConfig);  // Top（最上方）

            // 记住列表宿主，供后续刷新用
            panel.Tag = listHost;
            return panel;
        }

        private Panel BuildFooter()
        {
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 64,
                BackColor = CardColor,
                Padding = new Padding(20, 14, 20, 14)
            };
            footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
                }
            };

            _btnClose = MakeButton("关闭", FlatButtonKind.Ghost, 84);
            _btnClose.Click += delegate { Close(); };

            _btnRun = MakeButton("执行", FlatButtonKind.Primary, 108);
            _btnRun.Click += OnRun;

            _btnNone = MakeButton("全不选", FlatButtonKind.Ghost, 76);
            _btnNone.Click += delegate { SetAllChecked(false); };

            _btnAll = MakeButton("全选", FlatButtonKind.Ghost, 68);
            _btnAll.Click += delegate { SetAllChecked(true); };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.Transparent
            };

            // RightToLeft：先加的在最右
            flow.Controls.Add(_btnClose);
            flow.Controls.Add(_btnRun);
            flow.Controls.Add(_btnNone);
            flow.Controls.Add(_btnAll);

            var hint = new Label
            {
                Text = "勾选要执行的规则，点「执行」运行；点「关闭」退出窗口",
                Font = _fontSmall,
                ForeColor = Color.FromArgb(156, 163, 175),
                AutoSize = true,
                Location = new Point(20, 22),
                BackColor = Color.Transparent
            };

            footer.Controls.Add(hint);
            footer.Controls.Add(flow);
            return footer;
        }

        private FlatButton MakeButton(string text, FlatButtonKind kind, int width)
        {
            return new FlatButton
            {
                Text = text,
                Kind = kind,
                Width = width,
                Height = 34,
                Margin = new Padding(8, 0, 0, 0)
            };
        }

        private void ResizeRows()
        {
            int width = _rules.ClientSize.Width - _rules.Padding.Horizontal - 20;
            if (width < 200)
            {
                return;
            }

            _rules.SuspendLayout();
            foreach (RuleRow row in _rows)
            {
                row.Width = width;
            }

            _rules.ResumeLayout();
        }

        // ------------------------------------------------------------------ 规则编辑逻辑

        private void RefreshEditPanel()
        {
            if (_panelEdit == null)
            {
                return;
            }

            var listHost = _panelEdit.Tag as FlowLayoutPanel;
            if (listHost == null)
            {
                return;
            }

            listHost.Controls.Clear();
            _editRows.Clear();
            _selectedEditIndex = -1;
            _jsonEditor.Text = string.Empty;

            if (_config == null || _config.Rules == null)
            {
                _editStatus.Text = "尚未加载配置。";
                return;
            }

            for (int i = 0; i < _config.Rules.Count; i++)
            {
                var rule = _config.Rules[i];
                var row = new EditRuleRow(rule, i, _fontBase, _fontSmall)
                {
                    Width = Math.Max(200, listHost.ClientSize.Width - 24),
                    Margin = new Padding(0, 0, 0, 6)
                };
                int idx = i;
                row.RowClick += delegate { SelectEditRow(idx); };
                row.RowDoubleClick += delegate { OpenRuleEditor(idx); };
                listHost.Controls.Add(row);
                _editRows.Add(row);
            }

            if (_editRows.Count > 0)
            {
                SelectEditRow(0);
            }

            _editStatus.Text = string.Format("共 {0} 条规则", _config.Rules.Count);
        }

        private void SelectEditRow(int index)
        {
            if (index < 0 || index >= _editRows.Count)
            {
                return;
            }

            // 先保存当前编辑的 JSON 到内存（不写入文件）
            if (_selectedEditIndex >= 0 && _selectedEditIndex < _config.Rules.Count)
            {
                TryApplyJsonToRule(_selectedEditIndex);
            }

            _selectedEditIndex = index;
            foreach (var r in _editRows)
            {
                r.SetSelected(false);
            }

            _editRows[index].SetSelected(true);

            // 显示选中规则的 JSON（精简版：null / 空集合 / 空 Filter 一律不输出，
            // 只留下真正需要改的字段，见 Core/RuleJson.cs）
            var rule = _config.Rules[index];
            _jsonEditor.Text = RuleJson.Serialize(rule);
        }

        private bool TryApplyJsonToRule(int index)
        {
            // 编辑区已改为只读预览，内容不再回写模型；真正的编辑走 RuleEditorDrawer
            if (_jsonEditor.ReadOnly)
            {
                return true;
            }

            try
            {
                string json = _jsonEditor.Text;
                if (string.IsNullOrWhiteSpace(json))
                {
                    return false;
                }

                var updated = JsonConvert.DeserializeObject<RuleConfig>(json);
                if (updated != null)
                {
                    _config.Rules[index] = updated;
                    _editRows[index].UpdateRule(updated);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _editStatus.Text = "JSON 格式错误：" + ex.Message;
                _editStatus.ForeColor = Warn;
            }

            return false;
        }

        private void OnAddRule(object sender, EventArgs e)
        {
            if (_config == null)
            {
                _editStatus.Text = "请先加载配置文件。";
                _editStatus.ForeColor = Warn;
                return;
            }

            // 保存当前编辑
            if (_selectedEditIndex >= 0)
            {
                TryApplyJsonToRule(_selectedEditIndex);
            }

            string type = _cmbNewType == null ? "EntityLayerChange" : (_cmbNewType.SelectedItem as string ?? "EntityLayerChange");
            var newRule = CreateRuleTemplate(type);

            _config.Rules.Add(newRule);
            RefreshEditPanel();
            SelectEditRow(_editRows.Count - 1);
            _editStatus.Text = string.Format("已添加 {0} 模板规则，请修改 JSON 后保存。", type);
            _editStatus.ForeColor = TextDim;
        }

        /// <summary>
        /// 按规则类型生成一条"能直接改名字就跑"的模板规则。
        /// 只给最常用的几种写专属模板，其余统一给通用改层模板（用户改 JSON 即可）。
        /// </summary>
        private static RuleConfig CreateRuleTemplate(string type)
        {
            var rule = new RuleConfig
            {
                Name = "新规则",
                Type = type,
                Enabled = true
            };

            switch ((type ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "BLOCKLAYERCHANGE":
                    // 块归层：最常用的形态是"一个块名 → 一个图层"
                    rule.Filter = new RuleFilter { SourceBlockName = "A1_TITLE" };
                    rule.Action = new RuleAction
                    {
                        TargetLayer = "NEW_LAYER",
                        CreateIfMissing = true
                    };
                    break;

                case "BLOCKREPLACE":
                    rule.Filter = new RuleFilter { SourceBlockName = "A1_TITLE" };
                    rule.Action = new RuleAction { TargetBlockName = "A1_TITLE_NEW", MigrateAttributes = true };
                    break;

                default:
                    rule.Filter = new RuleFilter { EntityTypes = new List<string> { "LINE" } };
                    rule.Action = new RuleAction { TargetLayer = "NEW_LAYER" };
                    break;
            }

            return rule;
        }

        private void OnDelRule(object sender, EventArgs e)
        {
            if (_config == null || _selectedEditIndex < 0 || _selectedEditIndex >= _config.Rules.Count)
            {
                return;
            }

            var rule = _config.Rules[_selectedEditIndex];
            string name = string.IsNullOrEmpty(rule.Name) ? rule.Type : rule.Name;

            var result = MessageBox.Show(this,
                "确定删除规则 \"" + name + "\" 吗？",
                "删除规则",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                _config.Rules.RemoveAt(_selectedEditIndex);
                RefreshEditPanel();
                _editStatus.Text = "已删除规则。";
                _editStatus.ForeColor = TextDim;
            }
        }

        private void OnMoveUp(object sender, EventArgs e)
        {
            if (_config == null || _selectedEditIndex <= 0)
            {
                return;
            }

            // 保存当前编辑
            TryApplyJsonToRule(_selectedEditIndex);

            var temp = _config.Rules[_selectedEditIndex];
            _config.Rules[_selectedEditIndex] = _config.Rules[_selectedEditIndex - 1];
            _config.Rules[_selectedEditIndex - 1] = temp;

            RefreshEditPanel();
            SelectEditRow(_selectedEditIndex - 1);
        }

        private void OnMoveDown(object sender, EventArgs e)
        {
            if (_config == null || _selectedEditIndex < 0 || _selectedEditIndex >= _config.Rules.Count - 1)
            {
                return;
            }

            // 保存当前编辑
            TryApplyJsonToRule(_selectedEditIndex);

            var temp = _config.Rules[_selectedEditIndex];
            _config.Rules[_selectedEditIndex] = _config.Rules[_selectedEditIndex + 1];
            _config.Rules[_selectedEditIndex + 1] = temp;

            RefreshEditPanel();
            SelectEditRow(_selectedEditIndex + 1);
        }

        private void OnSaveRules(object sender, EventArgs e)
        {
            if (_config == null || _selectedEditIndex < 0)
            {
                return;
            }

            // 先应用当前 JSON
            if (!TryApplyJsonToRule(_selectedEditIndex))
            {
                return;
            }

            try
            {
                // 规则编辑页改的是整条规则（含增删/排序），必须整段替换 Rules 数组；
                // SaveRules 会保留数组外的注释与排版，写盘前自动备份 config.json.bak
                ConfigSaveResult saved = ConfigSaver.SaveRules(_selectedPath, _config);

                string suffix = saved.UsedFullRewrite ? "（排版特殊，已整体重写）" : string.Empty;
                if (saved.RulesCommentsBackedUp)
                {
                    suffix += "（Rules 数组内的注释已备份到 config.json.rules.bak）";
                }

                _editStatus.Text = string.Format("规则已保存到 {0}{1}", Path.GetFileName(_selectedPath), suffix);
                _editStatus.ForeColor = Color.FromArgb(34, 197, 94);

                // 同步刷新执行面板（会重建规则行，所以徽标必须在重建之后再打）
                LoadConfig(_selectedPath, false);

                int warnings = ApplyNameValidation();
                if (warnings > 0)
                {
                    _editStatus.Text = string.Format("规则已保存到 {0}{1}；名称提醒 {2} 条（黄色，不阻断）",
                        Path.GetFileName(_selectedPath), suffix, warnings);
                    _editStatus.ForeColor = Warn;
                }
            }
            catch (Exception ex)
            {
                _editStatus.Text = "保存失败：" + ex.Message;
                _editStatus.ForeColor = Warn;
            }
        }

        /// <summary>
        /// 双击规则行：打开表单抽屉编辑这条规则。
        ///
        /// 抽屉内部编辑的是**深拷贝**，只有点了「保存」才把结果交回来；
        /// 这里负责把结果放回内存、刷新列表、并立即落盘（与「保存规则到配置」同一条写盘路径）。
        ///
        /// 拾取（P3）走的是"抽屉关掉 → 命令取名字 → 抽屉重开"：
        /// 模态窗口里 `Hide()` 会让 `ShowDialog` 立即返回（见 PickBridge 说明），
        /// 所以不能在抽屉里直接排队 BATCHPICK。这里在抽屉关闭之后、主窗口（非模态）上下文里排队，
        /// 结果回来后带着同一份副本重开抽屉，把名字填回当初那个字段。
        /// </summary>
        private void OpenRuleEditor(int index)
        {
            OpenRuleEditor(index, null);
        }

        /// <param name="pending">
        /// 上一次拾取的上下文；为 null 表示新开一次编辑。
        /// 非 null 时用它带回来的副本继续编辑，并把拾取结果填回 <see cref="PickContinuation.FieldPath"/>。
        /// </param>
        private void OpenRuleEditor(int index, PickContinuation pending)
        {
            if (_config == null || index < 0 || index >= _config.Rules.Count)
            {
                return;
            }

            RuleConfig rule = pending != null ? pending.Working : _config.Rules[index];
            if (rule == null)
            {
                return;
            }

            RuleEditorDrawer drawer;
            try
            {
                drawer = new RuleEditorDrawer(rule, _nameLookup, _fontBase, _fontBold, _fontSmall, _fontMono, pending);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("打开规则编辑器失败：" + ex);
                MessageBox.Show(this, "打开编辑器失败：" + ex.Message, "编辑规则",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult result;
            using (drawer)
            {
                result = drawer.ShowDialog(this);
            }

            if (result == DialogResult.OK)
            {
                _config.Rules[index] = drawer.Result;
                RefreshEditPanel();
                SelectEditRow(index);
                SaveAfterEdit();
                return;
            }

            if (!drawer.PickRequested)
            {
                return; // 用户取消编辑
            }

            StartPickForEditor(index, drawer.PendingPick);
        }

        /// <summary>
        /// 抽屉已关闭：在非模态上下文里排队 BATCHPICK，等结果回来后重开抽屉。
        /// 命令若 10 秒内没跑起来（没有活动文档 / 命令名冲突），看门狗取消请求并带提示重开抽屉，
        /// 不会让用户对着"表单消失"发呆；命令一旦开始等用户点选就停掉看门狗——点多久都不算超时。
        /// </summary>
        private void StartPickForEditor(int index, PickContinuation pending)
        {
            if (pending == null)
            {
                return;
            }

            if (_pickHost == null)
            {
                pending.Message = "当前环境无法启动拾取命令，请手动输入名称。";
                OpenRuleEditor(index, pending);
                return;
            }

            int token = PickBridge.Request(pending.Kind, delegate(string name)
            {
                StopPickWatchdog();

                pending.Value = name;

                // 回调发生在 BATCHPICK 命令内部：先排到消息队列，等命令返回后再开模态窗口
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (_editStatus != null)
                        {
                            _editStatus.Text = string.Empty;
                        }

                        OpenRuleEditor(index, pending);
                    }));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("拾取回调排队失败：" + ex);
                }
            });

            if (token <= 0)
            {
                pending.Message = "已有拾取在进行中，请先完成或按 Esc。";
                OpenRuleEditor(index, pending);
                return;
            }

            if (!_pickHost.StartPick())
            {
                // 排队失败 → 取消请求会走回调，由回调把抽屉重新打开
                PickBridge.Cancel(token);
                return;
            }

            // 抽屉已经关掉，给主窗一个"现在去图上点"的提示；回调重开抽屉时会被清空
            if (_editStatus != null)
            {
                _editStatus.Text = pending.Kind == NameKind.Block
                    ? "请在图中点选块参照（按 Esc 取消）"
                    : "请在图中点选对象（按 Esc 取消）";
                _editStatus.ForeColor = TextDim;
            }

            StartPickWatchdog(token, pending);
        }

        private void StartPickWatchdog(int token, PickContinuation pending)
        {
            StopPickWatchdog();

            _pickToken = token;
            _pickWatchdog = new Timer { Interval = 10000 };
            _pickWatchdog.Tick += delegate
            {
                if (!PickBridge.IsPending(token))
                {
                    StopPickWatchdog();
                    return;
                }

                if (PickBridge.HasStarted(token))
                {
                    // 命令已经在等用户点选，不能打断
                    StopPickWatchdog();
                    return;
                }

                StopPickWatchdog();
                pending.Message = "拾取命令没有执行（可以手动输入名称）。";
                PickBridge.Cancel(token);
            };
            _pickWatchdog.Start();
        }

        private void StopPickWatchdog()
        {
            if (_pickWatchdog == null)
            {
                return;
            }

            _pickWatchdog.Stop();
            _pickWatchdog.Dispose();
            _pickWatchdog = null;
        }

        /// <summary>抽屉保存后立即写盘，并刷新名称提醒。</summary>
        private void SaveAfterEdit()
        {
            if (string.IsNullOrEmpty(_selectedPath))
            {
                return;
            }

            try
            {
                ConfigSaveResult saved = ConfigSaver.SaveRules(_selectedPath, _config);
                string suffix = saved.UsedFullRewrite ? "（排版特殊，已整体重写）" : string.Empty;
                if (saved.RulesCommentsBackedUp)
                {
                    suffix += "（Rules 数组内的注释已备份到 config.json.rules.bak）";
                }

                int warnings = ApplyNameValidation();
                if (warnings > 0)
                {
                    _editStatus.Text = string.Format("已保存到 {0}{1}；名称提醒 {2} 条（黄色，不阻断）",
                        Path.GetFileName(_selectedPath), suffix, warnings);
                    _editStatus.ForeColor = Warn;
                }
                else
                {
                    _editStatus.Text = string.Format("已保存到 {0}{1}", Path.GetFileName(_selectedPath), suffix);
                    _editStatus.ForeColor = Color.FromArgb(34, 197, 94);
                }
            }
            catch (Exception ex)
            {
                _editStatus.Text = "保存失败：" + ex.Message;
                _editStatus.ForeColor = Warn;
            }
        }

        /// <summary>
        /// 对照当前图纸的符号表，给每条规则打"名称可能写错"的黄色徽标。
        /// 返回提示总数；<see cref="_nameLookup"/> 为 null（没有文档 / 测试环境）时直接跳过。
        ///
        /// 只提示不阻断：批处理的目标是目录里的一批图纸，当前打开的这张不是判据。
        /// </summary>
        private int ApplyNameValidation()
        {
            if (_nameLookup == null || _config == null || _config.Rules == null)
            {
                return 0;
            }

            if (_rowTip == null)
            {
                _rowTip = new ToolTip
                {
                    AutoPopDelay = 10000,
                    InitialDelay = 300,
                    ReshowDelay = 100,
                    ShowAlways = true
                };
            }

            int total = 0;
            for (int i = 0; i < _config.Rules.Count; i++)
            {
                RuleConfig rule = _config.Rules[i];
                if (rule == null)
                {
                    continue;
                }

                List<FieldIssue> issues;
                try
                {
                    issues = NameValidator.Validate(rule, _nameLookup);
                }
                catch (Exception ex)
                {
                    // 校验本身出错不能影响保存
                    DiagnosticLog.Write("名称校验异常：" + ex.Message);
                    continue;
                }

                string first = null;
                if (issues.Count > 0)
                {
                    total += issues.Count;
                    first = issues[0].Message;
                    if (issues.Count > 1)
                    {
                        first += string.Format("（另有 {0} 条）", issues.Count - 1);
                    }
                }

                if (i < _editRows.Count)
                {
                    _editRows[i].SetWarning(first);
                    _rowTip.SetToolTip(_editRows[i], first ?? string.Empty);
                }
            }

            return total;
        }

        // ------------------------------------------------------------------ 数据绑定

        private bool LoadConfig(string path, bool showError)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _config = null;
                ClearRows();
                SetStatus(string.IsNullOrWhiteSpace(path)
                    ? "未指定配置文件，点「浏览...」选择 config.json。"
                    : "配置文件不存在：" + path, true);
                UpdateButtonState();
                return false;
            }

            List<string> warnings;
            BatchConfig config;
            try
            {
                config = ConfigLoader.Load(path, out warnings);
            }
            catch (Exception ex)
            {
                _config = null;
                ClearRows();
                SetStatus("配置加载失败：" + ex.Message, true);
                UpdateButtonState();
                if (showError)
                {
                    MessageBox.Show(this, ex.Message, "配置加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return false;
            }

            _config = config;
            _warnings.Clear();
            if (warnings != null)
            {
                _warnings.AddRange(warnings);
            }

            _inputPath.Text = Path.GetFullPath(path);
            _selectedPath = _inputPath.Text;

            _cboMode.SelectedIndex = string.Equals(config.ExecutionMode, "Isolated", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _swContinue.Checked = config.ContinueOnError;
            _swVerbose.Checked = config.Verbose;

            _suppressStatus = true;
            _rules.SuspendLayout();
            try
            {
                ClearRows();

                foreach (RuleConfig rule in config.Rules)
                {
                    if (rule == null)
                    {
                        continue;
                    }

                    var row = new RuleRow(rule, _fontBold, _fontSmall, _fontSmall)
                    {
                        Width = Math.Max(300, _rules.ClientSize.Width - _rules.Padding.Horizontal - 20),
                        Margin = new Padding(0, 0, 0, 8)
                    };
                    row.CheckedChanged += OnRowCheckedChanged;
                    row.RowClick += OnRowClick;
                    row.RowDoubleClick += OnRowDoubleClick;

                    _rows.Add(row);
                    _rules.Controls.Add(row);
                    row.Checked = rule.Enabled;
                }

                _selectedRow = _rows.Count > 0 ? _rows[0] : null;
                if (_selectedRow != null)
                {
                    _selectedRow.IsSelected = true;
                }
            }
            finally
            {
                _rules.ResumeLayout();
                _suppressStatus = false;
            }

            UpdateStatus();
            UpdateButtonState();
            return true;
        }

        private void ClearRows()
        {
            foreach (RuleRow row in _rows)
            {
                row.CheckedChanged -= OnRowCheckedChanged;
                row.RowClick -= OnRowClick;
                row.RowDoubleClick -= OnRowDoubleClick;
            }

            _rows.Clear();
            _rules.Controls.Clear();
            _selectedRow = null;
        }

        private void OnRowCheckedChanged(object sender, EventArgs e)
        {
            UpdateStatus();
        }

        private void OnRowClick(object sender, EventArgs e)
        {
            var row = sender as RuleRow;
            if (row == null)
            {
                return;
            }

            SelectRow(row);
            row.Checked = !row.Checked;
        }

        private void OnRowDoubleClick(object sender, EventArgs e)
        {
            var row = sender as RuleRow;
            if (row != null)
            {
                SelectRow(row);
                ShowDetail();
            }
        }

        private void SelectRow(RuleRow row)
        {
            if (_selectedRow != null && _selectedRow != row)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = row;
            row.IsSelected = true;
        }

        private void UpdateStatus()
        {
            if (_suppressStatus || _config == null || _rules == null || _lblStatus == null)
            {
                return;
            }

            try
            {
                int enabled = 0;
                foreach (RuleRow row in _rows)
                {
                    if (row != null && row.Checked)
                    {
                        enabled++;
                    }
                }

                string text = string.Format("共 {0} 条规则，已勾选 {1} 条。单击行切换启用，双击行或点「编辑规则」打开表单。",
                    _rows.Count, enabled);

                if (_warnings.Count > 0)
                {
                    text += string.Format("   ·   配置警告 {0} 条", _warnings.Count);
                }

                SetStatus(text, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("BatchToolForm.UpdateStatus 异常：" + ex);
            }
        }

        private void SetStatus(string text, bool isError)
        {
            if (_lblStatus == null)
            {
                return;
            }

            _lblStatus.Text = text;
            _lblStatus.ForeColor = isError ? Color.FromArgb(190, 40, 40) : (_warnings.Count > 0 ? Warn : TextDim);
        }

        private void UpdateButtonState()
        {
            bool hasConfig = _config != null;
            _btnRun.Enabled = hasConfig;
            _btnOpen.Enabled = hasConfig;
            if (_btnEditRule != null)
            {
                // 没选中规则时不能编辑（新增/删除/上下移之后状态会重算）
                _btnEditRule.Enabled = hasConfig && _selectedEditIndex >= 0 && _selectedEditIndex < _config.Rules.Count;
            }
            _btnAll.Enabled = hasConfig;
            _btnNone.Enabled = hasConfig;
        }

        private void SetAllChecked(bool value)
        {
            _suppressStatus = true;
            try
            {
                foreach (RuleRow row in _rows)
                {
                    if (row != null)
                    {
                        row.Checked = value;
                    }
                }
            }
            finally
            {
                _suppressStatus = false;
            }

            UpdateStatus();
        }

        private BatchConfig BuildConfigFromUi()
        {
            if (_config == null)
            {
                return null;
            }

            _config.ExecutionMode = _cboMode.SelectedIndex == 1 ? "Isolated" : "Sequential";
            _config.ContinueOnError = _swContinue.Checked;
            _config.Verbose = _swVerbose.Checked;
            // 日志路径不再由界面维护：LogFile 保持配置文件里的原值；
            // 为空时插件默认写到「配置文件同目录\Logs\batch-yyyyMMdd-HHmmss.log」。

            foreach (RuleRow row in _rows)
            {
                if (row != null && row.Rule != null)
                {
                    row.Rule.Enabled = row.Checked;
                }
            }

            return _config;
        }

        // ------------------------------------------------------------------ 事件

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择配置文件";
                dialog.Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;

                string current = _selectedPath;
                if (!string.IsNullOrEmpty(current))
                {
                    try
                    {
                        dialog.InitialDirectory = Path.GetDirectoryName(current);
                        dialog.FileName = Path.GetFileName(current);
                    }
                    catch
                    {
                        // 路径异常时用默认目录
                    }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    LoadConfig(dialog.FileName, true);
                }
            }
        }

        private void OnRun(object sender, EventArgs e)
        {
            _selectedPath = _inputPath.Text == null ? string.Empty : _inputPath.Text.Trim();

            BatchConfig config = BuildConfigFromUi();
            if (config == null)
            {
                MessageBox.Show(this, "请先选择有效的配置文件。", "无法执行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(_selectedPath))
            {
                MessageBox.Show(this, "配置文件不存在：" + _selectedPath, "无法执行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int enabled = 0;
            foreach (RuleRow row in _rows)
            {
                if (row != null && row.Checked)
                {
                    enabled++;
                }
            }

            if (enabled == 0)
            {
                MessageBox.Show(this, "当前没有勾选任何规则，执行不会产生任何效果。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 非模式窗口：不关窗，把执行排队回命令上下文（BATCHUIRUN）
            try
            {
                UiRunBridge.RequestRun(config, _selectedPath);
                SetStatus("已提交执行，结果见命令行输出；可继续调整后再次执行。", false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("界面提交执行失败：" + ex);
                MessageBox.Show(this, "提交执行失败：" + ex.Message, "执行", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenJson(object sender, EventArgs e)
        {
            if (!File.Exists(_selectedPath))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_selectedPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开文件：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowDetail()
        {
            RuleRow row = _selectedRow;
            if (row == null || row.Rule == null)
            {
                return;
            }

            string json;
            try
            {
                json = RuleJson.Serialize(row.Rule);
            }
            catch (Exception ex)
            {
                json = "序列化失败：" + ex.Message;
            }

            // 详情窗同样用非模式，避免在 AutoCAD 主线程里再起一个嵌套消息循环
            if (_detailForm != null && !_detailForm.IsDisposed)
            {
                _detailForm.UpdateContent(row.Rule.Name, json);
                _detailForm.Activate();
                return;
            }

            _detailForm = new RuleDetailForm(row.Rule.Name, json, _fontMono, _fontBase);
            _detailForm.FormClosed += delegate { _detailForm = null; };
            _detailForm.Show(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // AutoScaleMode.Dpi 的缩放要到显示阶段才落定，首帧可能用的是缩放前的字体；
            // 这里强制整窗重绘一次，避免文字重影残留。
            Invalidate(true);
            Update();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 拾取在途时关窗：取消请求并停掉看门狗，避免回调里再去开一个已经没主的窗口
                StopPickWatchdog();
                PickBridge.CancelPending();

                // 自建字体需要显式释放
                Font[] fonts = { _fontBase, _fontBold, _fontTitle, _fontSmall, _fontMono };
                foreach (Font f in fonts)
                {
                    if (f != null)
                    {
                        f.Dispose();
                    }
                }

                if (_rowTip != null)
                {
                    _rowTip.Dispose();
                    _rowTip = null;
                }
            }

            base.Dispose(disposing);
        }

        // ================================================================== 自绘控件

        /// <summary>圆角按钮的视觉层级。</summary>
        internal enum FlatButtonKind
        {
            Primary,
            Secondary,
            Ghost
        }

        /// <summary>白色圆角卡片容器。</summary>
        internal sealed class CardPanel : Panel
        {
            public CardPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                // 卡片实际底色是白色：子控件取父底色时要拿到白，而不是页面灰
                BackColor = CardColor;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 圆角之外的四个角要露出页面底色
                g.Clear(BgColor);

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(rect, 10))
                using (var fill = new SolidBrush(CardColor))
                using (var pen = new Pen(BorderColor))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }

                base.OnPaint(e);
            }
        }

        /// <summary>
        /// 规则编辑面板中的规则行（可点击选中，不自带开关）。
        /// </summary>
        private sealed class EditRuleRow : Panel
        {
            private readonly RuleConfig _rule;
            private readonly int _index;
            private readonly Font _font;
            private readonly Font _fontSmall;
            private bool _selected;

            /// <summary>名称校验的提示文案（空表示没有提示）。只画一个黄点，详细文案走 ToolTip。</summary>
            private string _warning;

            /// <summary>点击行内任意位置触发（含子标签）。</summary>
            public event EventHandler RowClick;

            /// <summary>双击行内任意位置触发：打开规则编辑抽屉。</summary>
            public event EventHandler RowDoubleClick;

            public string Warning
            {
                get { return _warning; }
            }

            /// <summary>设置/清除黄色徽标。传 null 或空串即清除。</summary>
            public void SetWarning(string text)
            {
                _warning = string.IsNullOrWhiteSpace(text) ? null : text;
                Invalidate();
            }

            public EditRuleRow(RuleConfig rule, int index, Font font, Font fontSmall)
            {
                _rule = rule;
                _index = index;
                _font = font;
                _fontSmall = fontSmall;
                Height = 44;
                Cursor = Cursors.Hand;
                BackColor = CardColor;
                DoubleBuffered = true;

                var nameLabel = new Label
                {
                    Text = string.IsNullOrEmpty(rule.Name) ? rule.Type : rule.Name,
                    Font = _font,
                    ForeColor = TextColor,
                    AutoSize = true,
                    Location = new Point(12, 6),
                    BackColor = Color.Transparent
                };

                var typeLabel = new Label
                {
                    Text = rule.Type,
                    Font = _fontSmall,
                    ForeColor = Accent,
                    AutoSize = true,
                    Location = new Point(12, 24),
                    BackColor = Color.Transparent
                };

                var enabledLabel = new Label
                {
                    Text = rule.Enabled ? "启用" : "停用",
                    Font = _fontSmall,
                    ForeColor = rule.Enabled ? Color.FromArgb(34, 197, 94) : TextDim,
                    AutoSize = true,
                    Location = new Point(180, 14),
                    BackColor = Color.Transparent
                };

                Controls.Add(nameLabel);
                Controls.Add(typeLabel);
                Controls.Add(enabledLabel);

                // 点击任意子控件都视为点击本行（Label 默认不吃 Click）
                EventHandler forward = delegate { if (RowClick != null) { RowClick(this, EventArgs.Empty); } };
                EventHandler forwardDouble = delegate { if (RowDoubleClick != null) { RowDoubleClick(this, EventArgs.Empty); } };
                Click += forward;
                DoubleClick += forwardDouble;
                nameLabel.Click += forward;
                nameLabel.DoubleClick += forwardDouble;
                typeLabel.Click += forward;
                typeLabel.DoubleClick += forwardDouble;
                enabledLabel.Click += forward;
                enabledLabel.DoubleClick += forwardDouble;
            }

            public RuleConfig Rule => _rule;
            public int Index => _index;

            public void SetSelected(bool selected)
            {
                _selected = selected;
                BackColor = selected ? AccentSoft : CardColor;
                Invalidate();
            }

            public void UpdateRule(RuleConfig rule)
            {
                foreach (Control c in Controls)
                {
                    if (c is Label lbl)
                    {
                        if (lbl.Left == 12 && lbl.Top == 6)
                        {
                            lbl.Text = string.IsNullOrEmpty(rule.Name) ? rule.Type : rule.Name;
                        }
                        else if (lbl.Left == 12 && lbl.Top == 24)
                        {
                            lbl.Text = rule.Type;
                        }
                        else if (lbl.Left == 180 && lbl.Top == 14)
                        {
                            lbl.Text = rule.Enabled ? "启用" : "停用";
                            lbl.ForeColor = rule.Enabled ? Color.FromArgb(34, 197, 94) : TextDim;
                        }
                    }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_selected)
                {
                    using (var pen = new Pen(Accent, 2))
                    {
                        e.Graphics.DrawLine(pen, 0, 0, 0, Height - 1);
                    }
                }

                // 名称校验的黄色徽标：只有提示，不阻断。鼠标悬停看具体文案。
                if (!string.IsNullOrEmpty(_warning))
                {
                    int cx = Math.Max(20, Width - 18);
                    using (var brush = new SolidBrush(WarnDot))
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.FillEllipse(brush, cx - 4, 16, 8, 8);
                    }
                }
            }
        }

        /// <summary>
        /// 圆角按钮，三种视觉层级。
        ///
        /// 刻意**不继承 Button**：ButtonBase 自带一套主题绘制逻辑，与 UserPaint 叠加时
        /// 会出现"文字被画两遍/首帧残影"的问题。这里直接从 Control 派生，
        /// 自己实现 IButtonControl，从而完全掌握绘制，同时保留 DialogResult / AcceptButton 行为。
        /// </summary>
        internal sealed class FlatButton : Control, IButtonControl
        {
            private bool _hover;
            private bool _pressed;

            public FlatButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque |
                         ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
                Cursor = Cursors.Hand;
                TabStop = false;
                Size = new Size(100, 34);
            }

            public FlatButtonKind Kind { get; set; }

            public DialogResult DialogResult { get; set; }

            public void NotifyDefault(bool value)
            {
                Invalidate();
            }

            public void PerformClick()
            {
                if (CanSelect)
                {
                    RaiseClick();
                }
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hover = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hover = false;
                _pressed = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs mevent)
            {
                if (mevent.Button == MouseButtons.Left)
                {
                    _pressed = true;
                    Invalidate();
                }

                base.OnMouseDown(mevent);
            }

            protected override void OnMouseUp(MouseEventArgs mevent)
            {
                if (mevent.Button == MouseButtons.Left)
                {
                    _pressed = false;
                    Invalidate();

                    if (ClientRectangle.Contains(mevent.Location))
                    {
                        RaiseClick();
                    }
                }

                base.OnMouseUp(mevent);
            }

            private void RaiseClick()
            {
                OnClick(EventArgs.Empty);

                if (DialogResult != DialogResult.None)
                {
                    Form owner = FindForm();
                    if (owner != null)
                    {
                        owner.DialogResult = DialogResult;
                    }
                }
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                // 背景由 OnPaint 统一铺满
            }

            protected override void OnPaint(PaintEventArgs pevent)
            {
                Graphics g = pevent.Graphics;

                // 每次都铺满底色：自绘控件不清底就会残留上一帧的像素
                using (var bg = new SolidBrush(EffectiveBackground(this)))
                {
                    g.FillRectangle(bg, ClientRectangle);
                }

                if (Width < 24 || Height < 14)
                {
                    return;
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color back;
                Color fore;
                Color border = Color.Empty;

                switch (Kind)
                {
                    case FlatButtonKind.Primary:
                        back = _pressed ? Darken(Accent, 0.88) : (_hover ? Darken(Accent, 0.92) : Accent);
                        fore = Color.White;
                        break;

                    case FlatButtonKind.Secondary:
                        back = _pressed ? Color.FromArgb(232, 236, 244) : (_hover ? Color.FromArgb(240, 243, 249) : CardColor);
                        fore = TextColor;
                        border = BorderStrong;
                        break;

                    default:
                        back = _pressed ? Color.FromArgb(230, 234, 242) : (_hover ? Color.FromArgb(240, 243, 249) : Color.Transparent);
                        fore = Color.FromArgb(71, 85, 105);
                        break;
                }

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(rect, 6))
                {
                    if (back != Color.Transparent)
                    {
                        using (var brush = new SolidBrush(back))
                        {
                            g.FillPath(brush, path);
                        }
                    }

                    if (border != Color.Empty)
                    {
                        using (var pen = new Pen(border))
                        {
                            g.DrawPath(pen, path);
                        }
                    }
                }

                Color textColor = Enabled ? fore : Color.FromArgb(170, 176, 188);
                TextRenderer.DrawText(g, Text, Font, rect, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            private static Color Darken(Color color, double factor)
            {
                return Color.FromArgb(
                    color.A,
                    (int)(color.R * factor),
                    (int)(color.G * factor),
                    (int)(color.B * factor));
            }
        }

        /// <summary>开关样式的勾选控件（自绘）。</summary>
        internal sealed class SwitchBox : CheckBox
        {
            public SwitchBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
                Text = string.Empty;
                Size = new Size(38, 20);
                Cursor = Cursors.Hand;
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
            }

            protected override void OnPaint(PaintEventArgs pevent)
            {
                Graphics g = pevent.Graphics;
                using (var bg = new SolidBrush(EffectiveBackground(this)))
                {
                    g.FillRectangle(bg, ClientRectangle);
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;

                var track = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(track, Height / 2))
                using (var brush = new SolidBrush(Checked ? Accent : Color.FromArgb(203, 209, 220)))
                {
                    g.FillPath(brush, path);
                }

                int knob = Height - 6;
                int knobX = Checked ? Width - knob - 3 : 3;
                using (var brush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(brush, knobX, 3, knob, knob);
                }
            }
        }

        /// <summary>带圆角边框的输入框（内部是无边框 TextBox）。</summary>
        internal sealed class InputPanel : Panel
        {
            private readonly TextBox _box;
            private bool _focused;

            public InputPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = CardColor;
                Padding = new Padding(10, 0, 10, 0);

                _box = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(249, 250, 252),
                    ForeColor = TextColor
                };
                _box.GotFocus += delegate { _focused = true; Invalidate(); };
                _box.LostFocus += delegate { _focused = false; Invalidate(); };
                _box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };

                Controls.Add(_box);
                Height = 32;
            }

            public override string Text
            {
                get { return _box.Text; }
                set { _box.Text = value; }
            }

            public bool ReadOnly
            {
                get { return _box.ReadOnly; }
                set
                {
                    _box.ReadOnly = value;
                    _box.BackColor = value ? Color.FromArgb(249, 250, 252) : CardColor;
                }
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                _box.SetBounds(Padding.Left, (Height - _box.PreferredHeight) / 2 + 1,
                    Math.Max(20, Width - Padding.Horizontal), _box.PreferredHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (var bg = new SolidBrush(EffectiveBackground(this)))
                {
                    g.FillRectangle(bg, ClientRectangle);
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(rect, 6))
                using (var fill = new SolidBrush(Color.FromArgb(249, 250, 252)))
                using (var pen = new Pen(_focused ? Accent : BorderColor))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>一行规则：开关 + 名称 + 类型徽标 + 摘要，整行自绘。</summary>
        private sealed class RuleRow : Control
        {
            private readonly Font _fontName;
            private readonly Font _fontSummary;
            private readonly Font _fontChip;

            private bool _checked;
            private bool _hover;
            private bool _selected;

            public RuleRow(RuleConfig rule, Font fontName, Font fontSummary, Font fontChip)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Rule = rule;
                _fontName = fontName;
                _fontSummary = fontSummary;
                _fontChip = fontChip;
                Height = 58;
                Cursor = Cursors.Hand;
                BackColor = BgColor;
            }

            public RuleConfig Rule { get; private set; }

            public event EventHandler CheckedChanged;
            public event EventHandler RowClick;
            public event EventHandler RowDoubleClick;

            public bool Checked
            {
                get { return _checked; }
                set
                {
                    if (_checked == value)
                    {
                        return;
                    }

                    _checked = value;
                    Invalidate();
                    if (CheckedChanged != null)
                    {
                        CheckedChanged(this, EventArgs.Empty);
                    }
                }
            }

            public bool IsSelected
            {
                get { return _selected; }
                set
                {
                    if (_selected == value)
                    {
                        return;
                    }

                    _selected = value;
                    Invalidate();
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hover = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hover = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnClick(EventArgs e)
            {
                if (RowClick != null)
                {
                    RowClick(this, EventArgs.Empty);
                }

                base.OnClick(e);
            }

            protected override void OnDoubleClick(EventArgs e)
            {
                if (RowDoubleClick != null)
                {
                    RowDoubleClick(this, EventArgs.Empty);
                }

                base.OnDoubleClick(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(BgColor);

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = RoundRect(rect, 8))
                {
                    Color fill = _hover ? RowHover : CardColor;
                    using (var brush = new SolidBrush(fill))
                    {
                        g.FillPath(brush, path);
                    }

                    Color border = _selected ? Accent : BorderColor;
                    using (var pen = new Pen(border))
                    {
                        g.DrawPath(pen, path);
                    }
                }

                // 左侧启用指示条
                if (_checked)
                {
                    using (var brush = new SolidBrush(Accent))
                    using (GraphicsPath bar = RoundRect(new Rectangle(0, 12, 4, Height - 24), 2))
                    {
                        g.FillPath(brush, bar);
                    }
                }

                // 开关
                int switchX = 16;
                int switchY = (Height - 20) / 2;
                var track = new Rectangle(switchX, switchY, 38, 20);
                using (GraphicsPath path = RoundRect(track, 10))
                using (var brush = new SolidBrush(_checked ? Accent : Color.FromArgb(203, 209, 220)))
                {
                    g.FillPath(brush, path);
                }

                int knob = 14;
                int knobX = _checked ? switchX + 38 - knob - 3 : switchX + 3;
                using (var brush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(brush, knobX, switchY + 3, knob, knob);
                }

                // 名称
                string name = string.IsNullOrEmpty(Rule.Name) ? "(未命名)" : Rule.Name;
                var nameRect = new Rectangle(70, 9, Math.Max(60, Width - 90), 20);
                TextRenderer.DrawText(g, name, _fontName, nameRect,
                    _checked ? TextColor : Color.FromArgb(120, 126, 140),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // 类型徽标（跟在名称后面，空间不够就不画）
                int nameWidth = TextRenderer.MeasureText(g, name, _fontName, new Size(int.MaxValue, 20),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                string type = Rule.Type ?? "?";
                int chipWidth = TextRenderer.MeasureText(g, type, _fontChip, new Size(int.MaxValue, 20),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 14;

                int chipX = 70 + nameWidth + 10;
                if (chipX + chipWidth < Width - 16)
                {
                    var chipRect = new Rectangle(chipX, 10, chipWidth, 18);
                    using (GraphicsPath path = RoundRect(chipRect, 9))
                    using (var brush = new SolidBrush(AccentSoft))
                    {
                        g.FillPath(brush, path);
                    }

                    TextRenderer.DrawText(g, type, _fontChip, chipRect, Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                // 摘要
                string summary = RuleSummary.Describe(Rule);
                var summaryRect = new Rectangle(70, 31, Math.Max(60, Width - 90), 18);
                TextRenderer.DrawText(g, summary, _fontSummary, summaryRect, TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // ================================================================== 规则详情窗

        private sealed class RuleDetailForm : Form
        {
            private readonly TextBox _box;

            public RuleDetailForm(string title, string text, Font mono, Font baseFont)
            {
                Text = "规则详情  ·  " + (title ?? string.Empty);
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(760, 560);
                MinimumSize = new Size(520, 360);
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                BackColor = BgColor;
                Font = baseFont;

                var bottom = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 56,
                    BackColor = BgColor
                };

                var close = new FlatButton
                {
                    Text = "关闭",
                    Kind = FlatButtonKind.Primary,
                    Width = 92,
                    Height = 34,
                    DialogResult = DialogResult.OK,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                close.Location = new Point(Math.Max(0, bottom.Width - 92 - 20), 12);

                var card = new CardPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(12)
                };

                _box = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(249, 250, 252),
                    ForeColor = TextColor,
                    Font = mono,
                    Text = text
                };

                bottom.Controls.Add(close);
                card.Controls.Add(_box);

                Controls.Add(card);
                Controls.Add(bottom);

                AcceptButton = close;
                CancelButton = close;
            }

            /// <summary>复用同一个详情窗时刷新内容。</summary>
            public void UpdateContent(string title, string text)
            {
                Text = "规则详情  ·  " + (title ?? string.Empty);
                if (_box != null)
                {
                    _box.Text = text;
                }
            }
        }

        // ================================================================== 绘图工具

        /// <summary>取控件实际显示用的底色（向上找第一个不透明的父容器）。</summary>
        internal static Color EffectiveBackground(Control control)
        {
            Control p = control == null ? null : control.Parent;
            while (p != null)
            {
                if (p.BackColor.A == 255)
                {
                    return p.BackColor;
                }

                p = p.Parent;
            }

            return SystemColors.Control;
        }

        internal static GraphicsPath RoundRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);

            if (rect.Width <= d || rect.Height <= d)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
