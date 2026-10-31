using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Editing;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Engine.Preview;
using Jun.Cad.Batch.Engine.Runs;

namespace Jun.Cad.Batch.Win
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
        private Panel _cardInput;
        private Panel _progressHost;
        private ProgressStrip _progress;
        private FlatButton _btnPreview;
        private FlatButton _btnPreflight;
        private FlatButton _btnRollback;
        private InputPanel _inputFolder;
        private FlatButton _btnBrowseFolder;
        private CheckBox _chkRecursive;
        private Label _lblFolderHint;
        private Panel _footer;

        // Tab 切换
        private FlatButton _tabRunBtn;
        private FlatButton _tabEditBtn;
        private FlatButton _tabLogBtn;
        private Panel _tabContent;
        private Panel _panelLog;
        private TextBox _logBox;
        private Panel _panelRun;
        private Panel _panelEdit;

        private InputPanel _inputPath;
        private FlatButton _btnBrowse;
        private FlatButton _btnReload;
        private FlatButton _btnReference;
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
        private INameLookup _nameLookup;

        /// <summary>参考图纸的显示名（空 = 还没选，名称校验不生效）。</summary>
        private string _referenceName;

        /// <summary>最近一次名称校验的提示条数，供状态行显示。</summary>
        private int _nameWarningCount;

        /// <summary>规则行的黄色徽标文案（延迟创建，只在真正校验时才需要）。</summary>
        private ToolTip _rowTip;

        public BatchToolForm(string configPath)
            : this(configPath, null)
        {
        }

        /// <summary>
        /// 带名称查询的构造。传 null 表示不做名称校验（例如还没选参考图纸时）。
        /// 之所以用接口注入而不是让 UI 直接读符号表：UI 层不直接碰 dwg 解析细节。
        /// </summary>
        public BatchToolForm(string configPath, INameLookup nameLookup)
        {
            _nameLookup = nameLookup;
            // 构造时已注入名称查询（命令行自检会传参考图纸）就当作"已选参考图纸"
            _referenceName = nameLookup == null ? null : nameLookup.ContextName;
            CreateFonts();

            Text = "批量处理  ·  Jun.Cad.Batch";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            // 标题栏/任务栏图标：只靠 ApplicationIcon 不够，见 AppInfo.AppIcon 的注释
            if (AppInfo.AppIcon != null)
            {
                Icon = AppInfo.AppIcon;
            }


            // 这是 Application.Run 的主窗体，**必须**出现在任务栏里。
            // 早期从 AutoCAD 内面板平移过来时沿用了"模态对话框"的写法
            // （ShowInTaskbar=false + MinimizeBox=false），结果主窗口在任务栏上找不到：
            // 窗口被别的程序盖住之后只能靠 Alt+Tab 找回来，用户以为程序没启动。
            // ShowInTaskbar=false 会让窗口带上 WS_EX_TOOLWINDOW，任务栏直接忽略它。
            ShowInTaskbar = true;

            // 任务栏按钮要能正常最小化/还原，所以最小化按钮也要给；
            // MaximizeBox 仍关掉：布局是绝对坐标 + 定宽的，放大并不会更好用。
            MinimizeBox = true;
            MaximizeBox = false;
            BackColor = BgColor;
            // 宽度取原设计的 3/4（980 → 735），高度不变
            ClientSize = new Size(735, 700);
            // 下限 700 而不是 600：「执行设置」卡片里三个设置项是绝对坐标排布的，
            // 卡片内宽低于约 580 时「命令行输出明细」会被裁掉（见 BuildSettingsCard 注释）。
            MinimumSize = new Size(700, 560);
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
            _panelLog = BuildLogPanel();

            // 后加的在上层：默认显示执行面板（另外两个在上层但隐藏）
            _tabContent.Controls.Add(_panelRun);
            _tabContent.Controls.Add(_panelEdit);
            _tabContent.Controls.Add(_panelLog);
            _panelEdit.Visible = false;
            _panelLog.Visible = false;

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
            _cardInput = BuildInputFolderCard();

            // 两张 Dock=Top 的卡片会紧贴在一起（Dock 不吃 Margin），插一个纯色空条当间距
            var gap = new Panel
            {
                Dock = DockStyle.Top,
                Height = 8,
                BackColor = BgColor
            };

            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor,
                Padding = new Padding(16, 10, 16, 8)
            };
            // 停靠布局按"后加的贴边"处理：Fill 必须最先加，其余从上到下逆序加。
            // 结果自上而下：输入图纸文件夹 → 间距 → 执行设置卡片 → 规则列表（Fill）。
            _body.Controls.Add(_rules);
            _body.Controls.Add(_cardSettings);
            _body.Controls.Add(gap);
            _body.Controls.Add(_cardInput);

            // 进度条：夹在内容区与底部按钮之间，默认隐藏，跑的时候才出现。
            // 停靠顺序是"后加的贴边"：footer 先占最底，进度条占它上面一条，body 填剩余。
            _progressHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = BgColor,
                Padding = new Padding(16, 2, 16, 2),
                Visible = false
            };

            _progress = new ProgressStrip(_fontSmall)
            {
                Dock = DockStyle.Fill
            };
            _progressHost.Controls.Add(_progress);

            panel.Controls.Add(_body);
            panel.Controls.Add(_progressHost);
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
                Text = "Jun.Cad.Batch  ·  " + AppInfo.Describe(),
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

            _tabLogBtn = new FlatButton
            {
                Text = "日志",
                Kind = FlatButtonKind.Ghost,
                Width = 72,
                Height = 32,
                Top = 72,
                Left = 192,
                Font = _fontBase
            };
            _tabLogBtn.Click += delegate { SwitchTab(2); };

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
            header.Controls.Add(_tabLogBtn);
            header.Controls.Add(tabIndicator);
            return header;
        }

        /// <summary>
        /// 执行设置卡片（tab1「执行」页）：执行模式 / 出错继续 / 详细输出，单行排布。
        ///
        /// 坐标是手算的，改文案必须重算宽度：9pt 微软雅黑下一个汉字约 12px、一个西文半角约 7px，
        /// 下拉框还要额外留 20px 给右侧箭头。当前占用到 x≈580，卡片内宽在 700px 窗口下约 612，
        /// 所以 <see cref="Form.MinimumSize"/> 把宽度下限卡在 700——低于这个值这一行会被裁掉。
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
                // 「顺序执行（无事务）」= 10 个全角宽字符 ≈ 130px，再加 20px 箭头位与边距
                Width = 176
            };
            // 离线版没有事务：所有规则顺序改内存，写盘时才落文件。
            // 因此这里只保留唯一一种真实语义，不再提供 Isolated/Parallel（它们只会被降级）。
            _cboMode.Items.Add("顺序执行（无事务）");
            _cboMode.SelectedIndex = 0;
            _cboMode.Enabled = false;

            _swContinue = new SwitchBox
            {
                Location = new Point(280, 39),
                Checked = true
            };
            _swContinue.CheckedChanged += delegate { UpdateStatus(); };

            var lblContinue = new Label
            {
                Text = "单条失败后继续",
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(324, 40),
                BackColor = Color.Transparent
            };

            _swVerbose = new SwitchBox
            {
                Location = new Point(444, 39),
                Checked = true
            };

            var lblVerbose = new Label
            {
                Text = "命令行输出明细",
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(488, 40),
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
        /// 配置文件卡片（tab2「规则编辑」页顶部）：路径 + 浏览/重新加载 + 状态行 + 打开配置/参考图纸。
        ///
        /// 为什么放在「规则编辑」页：这张卡片管的是"改哪份配置"，和编辑动作是同一件事；
        /// 执行页现在只管"跑哪些图、输出到哪"，两页各管一头。
        /// 执行页顶部换成了"输入图纸文件夹"卡片（见 <see cref="BuildInputFolderCard"/>）。
        ///
        /// 两行布局：
        ///   第 1 行（Top=32，高 32）路径输入框 + 浏览/重新加载——"换文件"这类主操作；
        ///   第 2 行（Top=70，高 24）状态行 + 打开配置/参考图纸——"辅助动作"。
        /// 早先四个按钮挤在一行时，路径只剩 249px，长路径会被截掉半截；
        /// 拆成两行后路径有 450px 以上，辅助按钮用小号 Ghost 样式也不会被误读成说明文字。
        /// </summary>
        private Panel BuildConfigCard()
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Top,
                Height = 108,
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
            const int auxTop = 70;
            const int auxHeight = 24;

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

            // 「打开配置」从规则编辑页的工具栏挪到这里：它打开的就是这行显示的这份 JSON，
            // 放在路径旁边比放在工具栏更好找；顺带也解决了工具栏在最小窗口宽度下会挤爆的问题。
            _btnOpen = new FlatButton
            {
                Text = "打开配置",
                Kind = FlatButtonKind.Ghost,
                Width = 80,
                Height = auxHeight,
                Top = auxTop,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnOpen.Click += OnOpenJson;

            // 离线版没有"当前文档"，所以名称校验要显式指定一张参考图纸
            _btnReference = new FlatButton
            {
                Text = "参考图纸...",
                Kind = FlatButtonKind.Ghost,
                Width = 88,
                Height = auxHeight,
                Top = auxTop,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnReference.Click += OnChooseReferenceDrawing;

            _lblStatus = new Label
            {
                Text = string.Empty,
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = false,
                // 单行固定宽：超长时省略号 + 悬停看全文，不会顶到右侧按钮上
                AutoEllipsis = true,
                Height = 18,
                Location = new Point(22, auxTop + 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent
            };

            card.Controls.Add(captionConfig);
            card.Controls.Add(_inputPath);
            card.Controls.Add(_btnBrowse);
            card.Controls.Add(_btnReload);
            card.Controls.Add(_btnOpen);
            card.Controls.Add(_btnReference);
            card.Controls.Add(_lblStatus);

            card.Resize += delegate
            {
                int right = card.ClientSize.Width - 20;

                // 第 1 行：路径占满，按钮自右向左排（重新加载 → 浏览）
                _btnReload.Left = right - _btnReload.Width;
                _btnBrowse.Left = _btnReload.Left - 8 - _btnBrowse.Width;
                _inputPath.Width = Math.Max(180, _btnBrowse.Left - 12 - _inputPath.Left);

                // 第 2 行：状态行靠左，辅助按钮靠右（参考图纸 → 打开配置）
                _btnReference.Left = right - _btnReference.Width;
                _btnOpen.Left = _btnReference.Left - 4 - _btnOpen.Width;
                _lblStatus.Width = Math.Max(100, _btnOpen.Left - 12 - _lblStatus.Left);
            };

            return card;
        }


        /// <summary>
        /// 输入图纸文件夹卡片（tab1「执行」页顶部）。
        ///
        /// 为什么要有它：这个工具的典型用法是"把一整个目录的图批一遍"，
        /// 每张图都弹一次文件选择器等于把批处理拆成手工。
        /// 填了文件夹就按文件夹跑（含子文件夹可选），留空仍可逐张选。
        ///
        /// 输出目录固定为 <c>&lt;文件夹&gt;\output</c>，与命令行默认一致。
        /// </summary>
        private Panel BuildInputFolderCard()
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Top,
                Height = 96,
                Padding = new Padding(20, 12, 20, 12)
            };

            var caption = new Label
            {
                Text = "输入图纸文件夹",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 10),
                BackColor = Color.Transparent
            };

            _inputFolder = new InputPanel
            {
                Location = new Point(20, 32),
                Height = 32,
                ReadOnly = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnBrowseFolder = new FlatButton
            {
                Text = "浏览...",
                Kind = FlatButtonKind.Secondary,
                Width = 82,
                Height = 32,
                Top = 32,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowseFolder.Click += OnBrowseFolder;

            _chkRecursive = new CheckBox
            {
                Text = "包含子文件夹",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 70),
                BackColor = Color.Transparent
            };
            _chkRecursive.CheckedChanged += delegate { UpdateFolderHint(); };

            _lblFolderHint = new Label
            {
                Text = "留空则执行时逐张选择图纸",
                Font = _fontSmall,
                ForeColor = TextDim,
                AutoSize = false,
                AutoEllipsis = true,
                Height = 18,
                Location = new Point(130, 72),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent
            };

            card.Controls.Add(caption);
            card.Controls.Add(_inputFolder);
            card.Controls.Add(_btnBrowseFolder);
            card.Controls.Add(_chkRecursive);
            card.Controls.Add(_lblFolderHint);

            card.Resize += delegate
            {
                int right = card.ClientSize.Width - 20;
                _btnBrowseFolder.Left = right - _btnBrowseFolder.Width;
                _inputFolder.Width = Math.Max(180, _btnBrowseFolder.Left - 12 - _inputFolder.Left);
                _lblFolderHint.Width = Math.Max(100, right - _lblFolderHint.Left);
            };

            return card;
        }

        /// <summary>选输入文件夹，选完立刻数一下里面有多少张图。</summary>
        private void OnBrowseFolder(object sender, EventArgs e)
        {
            string current = _inputFolder.Text == null ? string.Empty : _inputFolder.Text.Trim();

            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择要批处理的 DWG 所在文件夹",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(current) ? current : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _inputFolder.Text = dialog.SelectedPath;
                UpdateFolderHint();
            }
        }

        /// <summary>把"这个文件夹里有多少张图、输出到哪"直接写在卡片上。</summary>
        private void UpdateFolderHint()
        {
            if (_lblFolderHint == null || _inputFolder == null)
            {
                return;
            }

            string folder = _inputFolder.Text == null ? string.Empty : _inputFolder.Text.Trim();

            if (string.IsNullOrEmpty(folder))
            {
                _lblFolderHint.Text = "留空则执行时逐张选择图纸";
                _lblFolderHint.ForeColor = TextDim;
                return;
            }

            if (!Directory.Exists(folder))
            {
                _lblFolderHint.Text = "文件夹不存在：" + folder;
                _lblFolderHint.ForeColor = Warn;
                return;
            }

            int count = EnumerateDrawings(folder, IsRecursive()).Count;
            if (count == 0)
            {
                _lblFolderHint.Text = "该文件夹下没有 .dwg 文件";
                _lblFolderHint.ForeColor = Warn;
                return;
            }

            _lblFolderHint.Text = string.Format("找到 {0} 个 .dwg，输出到 {1}",
                count, Path.Combine(folder, "output"));
            _lblFolderHint.ForeColor = TextDim;
        }

        private bool IsRecursive()
        {
            return _chkRecursive != null && _chkRecursive.Checked;
        }

        /// <summary>
        /// 列出文件夹里的 DWG。
        ///
        /// 实现放在 <see cref="DrawingFolder"/>（Core），与命令行共用同一套排除规则：
        /// 输出根、`.jun-runs`、以及"以前跑过"的旧输出根都要跳过，
        /// 否则第二次运行会把上一次的产物当输入。
        /// </summary>
        private static List<string> EnumerateDrawings(string folder, bool recursive)
        {
            return DrawingFolder.Enumerate(folder, null, recursive, DrawingFolder.DefaultOutputRoot(folder));
        }

        /// <summary>
        /// 本次要处理哪些文件：填了文件夹就用文件夹里的全部 DWG，留空退回"逐张选"。
        /// 返回 null 表示用户取消或选择不合法。
        /// </summary>
        private List<string> ResolveInputFiles()
        {
            string folder = _inputFolder == null || _inputFolder.Text == null
                ? string.Empty
                : _inputFolder.Text.Trim();

            if (string.IsNullOrEmpty(folder))
            {
                return AskForDrawings();
            }

            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "输入文件夹不存在：" + folder, "无法执行",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            List<string> files = EnumerateDrawings(folder, IsRecursive());
            if (files.Count == 0)
            {
                MessageBox.Show(this, "该文件夹下没有 .dwg 文件：" + folder, "无法执行",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return files;
        }

        /// <summary>
        /// 输出目录：填了输入文件夹就固定写到 `&lt;文件夹&gt;\output`（与命令行一致），
        /// 否则问用户要一个。
        /// </summary>
        private string ResolveOutputDirectory(List<string> files)
        {
            string folder = _inputFolder == null || _inputFolder.Text == null
                ? string.Empty
                : _inputFolder.Text.Trim();

            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                return Path.Combine(folder, "output");
            }

            return AskForOutputDirectory(files[0]);
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

        /// <summary>离屏自检用：切换页签而不模拟鼠标点击（见 Program.RenderAllTabs）。</summary>
        internal void SelectTab(int index)
        {
            SwitchTab(index);
        }

        /// <summary>
        /// 离屏自检用：把进度条摆到指定状态再渲染。
        /// 进度条默认隐藏，不这样"假跑一次"就永远截不到它，也就看不出停靠和遮挡问题。
        /// </summary>
        internal void ShowProgressSample(int current, int total, string text)
        {
            SetBusy(true, "执行");
            _progress.Update(current, total, text);
        }

        private void SwitchTab(int index)
        {
            _panelRun.Visible = index == 0;
            _panelEdit.Visible = index == 1;
            _panelLog.Visible = index == 2;

            _tabRunBtn.Font = index == 0 ? _fontBold : _fontBase;
            _tabEditBtn.Font = index == 1 ? _fontBold : _fontBase;
            _tabLogBtn.Font = index == 2 ? _fontBold : _fontBase;

            MoveTabIndicator(index == 0 ? _tabRunBtn : (index == 1 ? _tabEditBtn : _tabLogBtn));

            if (index == 1)
            {
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

            var btnDup = MakeButton("复制", FlatButtonKind.Ghost, 60);
            btnDup.Left = 358;
            btnDup.Top = 4;
            btnDup.Click += OnDuplicateRule;

            _btnDelRule = MakeButton("删除", FlatButtonKind.Ghost, 60);
            _btnDelRule.Left = 426;
            _btnDelRule.Top = 4;
            _btnDelRule.Click += OnDelRule;

            var btnUp = MakeButton("上移", FlatButtonKind.Ghost, 52);
            btnUp.Left = 494;
            btnUp.Top = 4;
            btnUp.Click += OnMoveUp;

            var btnDown = MakeButton("下移", FlatButtonKind.Ghost, 52);
            btnDown.Left = 554;
            btnDown.Top = 4;
            btnDown.Click += OnMoveDown;

            toolbar.Controls.Add(_cmbNewType);
            toolbar.Controls.Add(_btnEditRule);
            toolbar.Controls.Add(_btnAddRule);
            toolbar.Controls.Add(btnDup);
            toolbar.Controls.Add(_btnDelRule);
            toolbar.Controls.Add(btnUp);
            toolbar.Controls.Add(btnDown);

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

            var btnExportRule = MakeButton("导出规则", FlatButtonKind.Ghost, 84);
            btnExportRule.Top = 10;
            btnExportRule.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnExportRule.Click += OnExportRule;

            var btnImportRule = MakeButton("导入规则", FlatButtonKind.Ghost, 84);
            btnImportRule.Top = 10;
            btnImportRule.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnImportRule.Click += OnImportRule;

            editFooter.Controls.Add(_editStatus);
            editFooter.Controls.Add(btnSaveRules);
            editFooter.Controls.Add(btnExportRule);
            editFooter.Controls.Add(btnImportRule);

            // 按钮靠右、状态标签靠左：随底部栏宽度调整（RightToLeft 顺序：保存 → 导出 → 导入）
            editFooter.Resize += delegate
            {
                btnSaveRules.Left = Math.Max(0, editFooter.ClientSize.Width - btnSaveRules.Width);
                btnExportRule.Left = Math.Max(0, btnSaveRules.Left - btnExportRule.Width - 6);
                btnImportRule.Left = Math.Max(0, btnExportRule.Left - btnImportRule.Width - 6);
            };

            // JSON 编辑框跟随卡片大小
            jsonCard.Resize += delegate
            {
                _jsonEditor.Width = Math.Max(200, jsonCard.ClientSize.Width - 24);
                _jsonEditor.Height = Math.Max(80, jsonCard.ClientSize.Height - 40);
            };

            // 停靠顺序（Controls 逆序布局）：后加的控件先占位，
            // 所以 Fill 的 jsonCard 必须【最先添加】，其余按"从下到上"逆着加。
            // 配置文件卡片最后加 → 落在最上面：改规则之前先确认改的是哪份配置。
            _cardConfig = BuildConfigCard();
            panel.Controls.Add(jsonCard);     // Fill（最后布局，占剩余空间）
            panel.Controls.Add(editFooter);   // Bottom
            panel.Controls.Add(listCard);     // Top
            panel.Controls.Add(toolbar);      // Top
            panel.Controls.Add(_cardConfig);  // Top（最上面）

            // 记住列表宿主，供后续刷新用
            panel.Tag = listHost;
            return panel;
        }

        /// <summary>
        /// 日志页：只放**当次**点击（执行或预览）的输出明细。
        ///
        /// 为什么不保留历史：这份日志的用途是"刚跑完，看看刚才那批做了什么"。
        /// 累积几十次之后，找当次结果要往下翻几百行；要留档有运行清单与 `--log` 文件。
        /// 所以每次点击先清空再写。
        /// </summary>
        private Panel BuildLogPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgColor,
                Padding = new Padding(16, 10, 16, 8)
            };

            var card = new CardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 12, 16, 12)
            };

            var caption = new Label
            {
                Text = "本次运行的输出明细（每次执行或预览都会覆盖）",
                Dock = DockStyle.Top,
                Height = 24,
                Font = _fontSmall,
                ForeColor = TextDim,
                BackColor = Color.Transparent
            };

            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = _fontMono,
                BorderStyle = BorderStyle.None,
                BackColor = CardColor,
                ForeColor = TextColor
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.Transparent
            };

            var btnClear = MakeButton("清空", FlatButtonKind.Ghost, 72);
            btnClear.Margin = new Padding(8, 6, 0, 0);
            btnClear.Click += delegate
            {
                if (_logBox != null)
                {
                    _logBox.Clear();
                }
            };

            var btnCopy = MakeButton("复制全部", FlatButtonKind.Secondary, 92);
            btnCopy.Margin = new Padding(8, 6, 0, 0);
            btnCopy.Click += OnCopyLog;

            // RightToLeft：先加的在最右
            bar.Controls.Add(btnClear);
            bar.Controls.Add(btnCopy);

            // 停靠顺序：Fill 先加，其余按"从下到上"逆序加
            card.Controls.Add(_logBox);
            card.Controls.Add(bar);
            card.Controls.Add(caption);

            panel.Controls.Add(card);
            return panel;
        }

        /// <summary>开始一次新的记录：清空并写一行抬头。</summary>
        private void BeginLog(string title)
        {
            if (_logBox == null)
            {
                return;
            }

            _logBox.Text = string.Format("==== {0:yyyy-MM-dd HH:mm:ss}  {1} ===={2}",
                DateTime.Now, title, Environment.NewLine);
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        /// <summary>追加一段输出。</summary>
        private void AppendLog(string text)
        {
            if (_logBox == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            _logBox.AppendText(text);

            if (!text.EndsWith("\n", StringComparison.Ordinal))
            {
                _logBox.AppendText(Environment.NewLine);
            }

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private void OnCopyLog(object sender, EventArgs e)
        {
            if (_logBox == null || string.IsNullOrEmpty(_logBox.Text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(_logBox.Text);
                SetStatus("已复制日志到剪贴板", false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

            _btnPreview = MakeButton("预览差异", FlatButtonKind.Secondary, 96);
            _btnPreview.Click += OnPreview;

            _btnPreflight = MakeButton("检查", FlatButtonKind.Ghost, 60);
            _btnPreflight.Click += OnPreflight;

            _btnRollback = MakeButton("回滚", FlatButtonKind.Ghost, 60);
            _btnRollback.Click += OnRollback;

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
            flow.Controls.Add(_btnPreview);
            flow.Controls.Add(_btnPreflight);
            flow.Controls.Add(_btnRollback);
            flow.Controls.Add(_btnNone);
            flow.Controls.Add(_btnAll);

            var hint = new Label
            {
                Text = "勾选规则 → 预览 → 执行",
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

            // 行是新建的，黄点必须重新打一遍：否则每次切页签、每次编辑保存后提示都会消失
            ApplyNameValidation();

            // 配置文件卡片在「执行」页，这里补一行文件名，否则在编辑页看不出在改哪份配置
            string where = string.IsNullOrEmpty(_selectedPath)
                ? "尚未选择配置文件"
                : "配置文件：" + Path.GetFileName(_selectedPath);
            string nameWarn = _nameWarningCount > 0
                ? string.Format("  ·  名称提醒 {0} 条（行右侧黄点）", _nameWarningCount)
                : string.Empty;
            _editStatus.Text = string.Format("{0}  ·  共 {1} 条规则{2}", where, _config.Rules.Count, nameWarn);
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
                // SaveRules 会保留数组外的注释与排版，写盘前自动备份 <配置文件名>.bak
                ConfigSaveResult saved = ConfigSaver.SaveRules(_selectedPath, _config);

                string suffix = saved.UsedFullRewrite ? "（排版特殊，已整体重写）" : string.Empty;
                if (saved.RulesCommentsBackedUp)
                {
                    // 备份名跟着实际文件名走，别写死——配置文件名是可改的
                    suffix += string.Format("（Rules 数组内的注释已备份到 {0}.rules.bak）",
                        Path.GetFileName(_selectedPath));
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
        /// 离线版没有活动文档，所以不提供"到图上点选取名字"——名字直接输入，
        /// 保存时按参考图纸校验（见 <see cref="ApplyNameValidation"/>）。
        /// </summary>
        private void OpenRuleEditor(int index)
        {
            if (_config == null || index < 0 || index >= _config.Rules.Count)
            {
                return;
            }

            RuleConfig rule = _config.Rules[index];
            if (rule == null)
            {
                return;
            }

            RuleEditorDrawer drawer;
            try
            {
                drawer = new RuleEditorDrawer(rule, _nameLookup, _fontBase, _fontBold, _fontSmall, _fontMono);
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

            if (result != DialogResult.OK)
            {
                return; // 用户取消编辑
            }

            _config.Rules[index] = drawer.Result;
            RefreshEditPanel();
            SelectEditRow(index);
            SaveAfterEdit();
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
                    // 备份名跟着实际文件名走，别写死——配置文件名是可改的
                    suffix += string.Format("（Rules 数组内的注释已备份到 {0}.rules.bak）",
                        Path.GetFileName(_selectedPath));
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
        /// 对照参考图纸的符号表，给每条规则打"名称可能写错"的黄色徽标。
        /// 返回提示总数；<see cref="_nameLookup"/> 为 null（还没选参考图纸）时直接跳过。
        ///
        /// 只提示不阻断：批处理的目标是目录里的一批图纸，参考图纸不是判据。
        /// </summary>
        private int ApplyNameValidation()
        {
            if (_nameLookup == null || _config == null || _config.Rules == null)
            {
                _nameWarningCount = 0;
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

                // 「执行」页的行也要打点：那里才是勾选规则的地方，
                // 只标在编辑页等于用户不切页签就看不到提示。
                foreach (RuleRow row in _rows)
                {
                    if (row != null && ReferenceEquals(row.Rule, rule))
                    {
                        row.SetWarning(first);
                        _rowTip.SetToolTip(row, first ?? string.Empty);
                        break;
                    }
                }
            }

            _nameWarningCount = total;
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

            // 离线版只有顺序执行；配置里写了 Isolated/Parallel 也统一按 Sequential 落盘
            _cboMode.SelectedIndex = 0;
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

            // 规则行是重建的，旧行上的黄点也一起没了；选了参考图纸就重新校验一遍
            ApplyNameValidation();
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

                // 这一行只有 18px 高、右边还排着两个辅助按钮，文案必须短；
                // 超长由 AutoEllipsis 兜底（悬停看全文）
                string text = string.Format("共 {0} 条规则 · 勾选 {1} 条。单击行启用，双击行编辑。",
                    _rows.Count, enabled);

                if (_warnings.Count > 0)
                {
                    text += string.Format(" · 配置警告 {0} 条", _warnings.Count);
                }

                if (!string.IsNullOrEmpty(_referenceName))
                {
                    text += _nameWarningCount > 0
                        ? string.Format(" · 参考图纸 {0}（名称提醒 {1} 条）", _referenceName, _nameWarningCount)
                        : string.Format(" · 参考图纸 {0}", _referenceName);
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
            _lblStatus.ForeColor = isError
                ? Color.FromArgb(190, 40, 40)
                : (_warnings.Count > 0 || _nameWarningCount > 0 ? Warn : TextDim);
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

            _config.ExecutionMode = "Sequential";
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

        /// <summary>
        /// 选一张参考图纸，用于"这个名字在图纸里存在吗"的提醒。
        ///
        /// 离线版没有"当前打开的文档"，所以改成显式指定一个文件；读不了就退回不做校验
        /// （<see cref="FileNameLookup.TryCreate"/> 返回 null），绝不把"没读到"说成"名字不存在"。
        /// </summary>
        private void OnChooseReferenceDrawing(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择参考图纸（用于校验图层/块/样式名）",
                Filter = "AutoCAD 图纸 (*.dwg)|*.dwg|所有文件 (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string error;
                FileNameLookup lookup = FileNameLookup.TryCreate(dialog.FileName, out error);

                if (lookup == null)
                {
                    MessageBox.Show(this, error ?? "无法读取该图纸。", "参考图纸",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _nameLookup = lookup;
                _referenceName = lookup.ContextName;

                // 行上的黄点由校验负责打；状态行统一由 UpdateStatus 生成，
                // 这样之后勾选/取消规则时"参考图纸 + 提醒条数"不会被覆盖掉。
                ApplyNameValidation();
                UpdateStatus();
            }
        }

        /// <summary>进入/退出"正在跑"状态：禁用会重入的按钮，显示或隐藏进度条。</summary>
        private void SetBusy(bool busy, string phase)
        {
            _btnRun.Enabled = !busy;

            if (_btnPreview != null) _btnPreview.Enabled = !busy;
            if (_btnPreflight != null) _btnPreflight.Enabled = !busy;
            if (_btnRollback != null) _btnRollback.Enabled = !busy;

            Cursor = busy ? Cursors.AppStarting : Cursors.Default;

            if (_progressHost == null || _progress == null)
            {
                return;
            }

            if (busy)
            {
                _progress.Update(0, 0, (phase ?? "处理") + "：准备中…");
                _progressHost.Visible = true;

                // 注意：这里**不能** BringToFront()。
                // 同级的停靠控件一改索引，Dock 会按新顺序重排——进度条会跑到别处
                // （实测 BringToFront 之后它直接消失了）。层级靠 Controls 的添加顺序保证。
            }
            else
            {
                _progressHost.Visible = false;
            }
        }

        /// <summary>
        /// 进度回调。`Progress&lt;T&gt;` 会封送回 UI 线程，所以这里可以直接碰控件。
        /// </summary>
        private void UpdateProgress(BatchProgress progress)
        {
            // 窗体可能在跑的过程中被关掉：`Progress<T>` 的回调是异步投递的，
            // 到这里控件已经 Dispose 就会抛 ObjectDisposedException
            if (progress == null || _progress == null || _progressHost == null
                || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            _progressHost.Visible = true;
            _progress.Update(progress.Index, progress.Total, progress.Describe());
        }

        private async void OnRun(object sender, EventArgs e)
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

            // 离线执行：按文件夹（或逐张选）→ 输出目录 → 直接跑，全程不需要 AutoCAD
            List<string> files = ResolveInputFiles();
            if (files == null || files.Count == 0)
            {
                return;
            }

            string outputDir = ResolveOutputDirectory(files);
            if (string.IsNullOrEmpty(outputDir))
            {
                return;
            }

            BeginLog(string.Format("执行 · {0} 个文件 · 输出到 {1}", files.Count, outputDir));
            AppendLog("配置文件：" + _selectedPath);

            SetBusy(true, "执行");

            try
            {
                // 放到后台线程跑：进度条要能刷新，界面也不能假死。
                // 文档只在工作线程里被碰，UI 线程不参与，不存在并发改同一个 CadDocument。
                var progress = new Progress<BatchProgress>(UpdateProgress);
                string report = await Task.Run(
                    () => OfflineRunner.Run(config, _selectedPath, files, outputDir, progress));

                AppendLog(report);
                SetStatus(string.Format("已处理 {0} 个文件，输出到 {1}", files.Count, outputDir), false);

                // 结果不再弹窗：切到「日志」页看，明细可复制、可对照着改配置
                SwitchTab(2);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("界面执行失败：" + ex);
                AppendLog("执行失败：" + ex.Message);
                SwitchTab(2);
                MessageBox.Show(this, "执行失败：" + ex.Message, "执行", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>
        /// 差异预览：跑一遍规则但不写盘，把"会改什么"逐条摆出来。
        ///
        /// 与「执行」共用同一段规则代码（<see cref="OfflineRunner.Preview"/>），
        /// 所以预览结果就是执行结果——不存在"预览一套、执行另一套"。
        /// </summary>
        private async void OnPreview(object sender, EventArgs e)
        {
            _selectedPath = _inputPath.Text == null ? string.Empty : _inputPath.Text.Trim();

            BatchConfig config = BuildConfigFromUi();
            if (config == null || !File.Exists(_selectedPath))
            {
                MessageBox.Show(this, "请先选择有效的配置文件。", "无法预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show(this, "当前没有勾选任何规则，预览不会有内容。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> files = ResolveInputFiles();
            if (files == null || files.Count == 0)
            {
                return;
            }

            BeginLog(string.Format("预览 · {0} 个文件（未写盘）", files.Count));
            AppendLog("配置文件：" + _selectedPath);

            SetBusy(true, "预览");

            try
            {
                string logText = null;
                List<ChangeRecord> records = null;
                var progress = new Progress<BatchProgress>(UpdateProgress);

                await Task.Run(delegate
                {
                    records = OfflineRunner.Preview(config, _selectedPath, files, out logText, progress);
                });

                AppendLog(logText);
                AppendLog(string.Format("预览汇总：{0} 行差异（未写盘）。", records.Count));

                SetStatus(string.Format("预览完成：{0} 个文件，{1} 行差异（未写盘）", files.Count, records.Count), false);

                SetBusy(false, null);

                using (var dialog = new PreviewDialog(records))
                {
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("预览失败：" + ex);
                AppendLog("预览失败：" + ex.Message);
                MessageBox.Show(this, "预览失败：" + ex.Message, "预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>前置检查：把能力探测的结果直接摆出来（不是只写日志）。</summary>
        private void OnPreflight(object sender, EventArgs e)
        {
            using (var dialog = new PreflightDialog(null))
            {
                dialog.ShowDialog(this);
            }
        }

        /// <summary>
        /// 回滚上一次运行：按运行清单把输出文件还原成运行前的样子。
        ///
        /// 只动**输出文件**，不碰源图；清单里的"本次新建"会删掉，"本次覆盖"从备份还原。
        /// </summary>
        private void OnRollback(object sender, EventArgs e)
        {
            string runDir = OfflineRunner.LastRunDirectory;

            if (string.IsNullOrEmpty(runDir))
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "选择输出目录（里面应有 .jun-runs）"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    runDir = RunManifestStore.FindLatest(dialog.SelectedPath);
                    if (runDir == null)
                    {
                        MessageBox.Show(this, "该目录下找不到运行清单（.jun-runs）。", "回滚",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
            }

            RunManifest manifest = RunManifestStore.Load(runDir);
            if (manifest == null)
            {
                MessageBox.Show(this, "读不到清单：" + runDir, "回滚", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this,
                    "回滚这次运行？\n\n" + manifest.Describe() + "\n" + runDir
                    + "\n\n本次新建的输出文件会被删除，本次覆盖的会从备份还原。",
                    "回滚", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                List<string> actions = RunManifestStore.Rollback(manifest, false);
                int failed = 0;
                foreach (string action in actions)
                {
                    if (action.StartsWith("失败", StringComparison.Ordinal))
                    {
                        failed++;
                    }
                }

                SetStatus(string.Format("已回滚：{0} 项（失败 {1} 项）", actions.Count - failed, failed), failed > 0);

                var preview = new System.Text.StringBuilder();
                for (int i = 0; i < actions.Count && i < 20; i++)
                {
                    preview.AppendLine(actions[i]);
                }

                if (actions.Count > 20)
                {
                    preview.AppendLine(string.Format("… 另有 {0} 项", actions.Count - 20));
                }

                MessageBox.Show(this, preview.ToString(), "回滚结果", MessageBoxButtons.OK,
                    failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("回滚失败：" + ex);
                MessageBox.Show(this, "回滚失败：" + ex.Message, "回滚", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>复制当前规则（深拷贝 + 名称加后缀），插到原规则之后。</summary>
        private void OnDuplicateRule(object sender, EventArgs e)
        {
            if (_config == null || _config.Rules == null
                || _selectedEditIndex < 0 || _selectedEditIndex >= _config.Rules.Count)
            {
                _editStatus.Text = "请先选中一条规则。";
                _editStatus.ForeColor = Warn;
                return;
            }

            TryApplyJsonToRule(_selectedEditIndex);

            RuleConfig source = _config.Rules[_selectedEditIndex];
            RuleConfig copy;

            try
            {
                string json = RuleJson.Serialize(source);
                copy = JsonConvert.DeserializeObject<RuleConfig>(json);
            }
            catch (Exception ex)
            {
                _editStatus.Text = "复制失败：" + ex.Message;
                _editStatus.ForeColor = Warn;
                return;
            }

            if (copy == null)
            {
                _editStatus.Text = "复制失败：无法还原规则内容。";
                _editStatus.ForeColor = Warn;
                return;
            }

            copy.Name = (string.IsNullOrEmpty(source.Name) ? source.Type : source.Name) + "_副本";

            _config.Rules.Insert(_selectedEditIndex + 1, copy);
            RefreshEditPanel();
            SelectEditRow(_selectedEditIndex + 1);

            _editStatus.Text = "已复制为「" + copy.Name + "」，记得点「保存规则到配置」。";
            _editStatus.ForeColor = TextDim;
        }

        /// <summary>导出当前规则为单个 .rule.json（带 schemaVersion，便于版本控制）。</summary>
        private void OnExportRule(object sender, EventArgs e)
        {
            if (_config == null || _config.Rules == null
                || _selectedEditIndex < 0 || _selectedEditIndex >= _config.Rules.Count)
            {
                _editStatus.Text = "请先选中一条规则。";
                _editStatus.ForeColor = Warn;
                return;
            }

            TryApplyJsonToRule(_selectedEditIndex);
            RuleConfig rule = _config.Rules[_selectedEditIndex];

            using (var dialog = new SaveFileDialog
            {
                Title = "导出规则",
                Filter = "规则文件 (*.rule.json)|*.rule.json|JSON (*.json)|*.json",
                FileName = MakeSafeFileName(string.IsNullOrEmpty(rule.Name) ? rule.Type : rule.Name) + ".rule.json"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    var wrapper = new Dictionary<string, object>
                    {
                        { "schemaVersion", 1 },
                        { "rule", rule }
                    };

                    File.WriteAllText(dialog.FileName,
                        JsonConvert.SerializeObject(wrapper, Formatting.Indented),
                        new System.Text.UTF8Encoding(true));

                    _editStatus.Text = "已导出：" + dialog.FileName;
                    _editStatus.ForeColor = TextDim;
                }
                catch (Exception ex)
                {
                    _editStatus.Text = "导出失败：" + ex.Message;
                    _editStatus.ForeColor = Warn;
                }
            }
        }

        /// <summary>
        /// 导入规则。类型必须已注册——否则拒绝并说明，不能静默丢掉一条"看不懂的规则"。
        /// </summary>
        private void OnImportRule(object sender, EventArgs e)
        {
            if (_config == null)
            {
                _editStatus.Text = "请先加载配置文件。";
                _editStatus.ForeColor = Warn;
                return;
            }

            using (var dialog = new OpenFileDialog
            {
                Title = "导入规则",
                Filter = "规则文件 (*.rule.json;*.json)|*.rule.json;*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                RuleConfig rule;
                try
                {
                    string text = File.ReadAllText(dialog.FileName);
                    var token = Newtonsoft.Json.Linq.JToken.Parse(text);

                    // 兼容两种格式：{ schemaVersion, rule } 或裸规则对象
                    Newtonsoft.Json.Linq.JToken ruleToken = token["rule"] ?? token;
                    rule = ruleToken.ToObject<RuleConfig>();
                }
                catch (Exception ex)
                {
                    _editStatus.Text = "导入失败（JSON 无法解析）：" + ex.Message;
                    _editStatus.ForeColor = Warn;
                    return;
                }

                if (rule == null || string.IsNullOrWhiteSpace(rule.Type))
                {
                    _editStatus.Text = "导入失败：文件里没有规则类型（Type）。";
                    _editStatus.ForeColor = Warn;
                    return;
                }

                if (!RuleFactory.IsRegistered(rule.Type))
                {
                    _editStatus.Text = string.Format(
                        "导入失败：本机没有规则类型「{0}」。已注册：{1}。",
                        rule.Type, string.Join("/", new List<string>(RuleFactory.GetRegisteredTypes()).ToArray()));
                    _editStatus.ForeColor = Warn;
                    return;
                }

                int insertAt = _selectedEditIndex >= 0 && _selectedEditIndex < _config.Rules.Count
                    ? _selectedEditIndex + 1
                    : _config.Rules.Count;

                _config.Rules.Insert(insertAt, rule);
                RefreshEditPanel();
                SelectEditRow(insertAt);

                _editStatus.Text = "已导入规则「" + (string.IsNullOrEmpty(rule.Name) ? rule.Type : rule.Name)
                                   + "」，记得点「保存规则到配置」。";
                _editStatus.ForeColor = TextDim;
            }
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "rule";
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }

        /// <summary>选要处理的 dwg 文件（可多选）。</summary>
        private List<string> AskForDrawings()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择要处理的图纸",
                Filter = "AutoCAD 图纸 (*.dwg)|*.dwg|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return null;
                }

                return new List<string>(dialog.FileNames);
            }
        }

        /// <summary>选输出目录，默认建议放在第一张图纸旁边的 output 子目录。</summary>
        private string AskForOutputDirectory(string firstFile)
        {
            string suggestion;
            try
            {
                suggestion = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(firstFile)) ?? ".", "output");
            }
            catch
            {
                suggestion = string.Empty;
            }

            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择输出目录（原图不会被修改）",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(suggestion) ? suggestion : string.Empty
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
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

                // 徽标放右边缘：规则名长度不定，固定在 Left=180 会被长名字压住
                _enabledLabel = new Label
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
                Controls.Add(_enabledLabel);

                SizeChanged += delegate { PositionEnabledLabel(); };
                PositionEnabledLabel();

                // 点击任意子控件都视为点击本行（Label 默认不吃 Click）
                EventHandler forward = delegate { if (RowClick != null) { RowClick(this, EventArgs.Empty); } };
                EventHandler forwardDouble = delegate { if (RowDoubleClick != null) { RowDoubleClick(this, EventArgs.Empty); } };
                Click += forward;
                DoubleClick += forwardDouble;
                nameLabel.Click += forward;
                nameLabel.DoubleClick += forwardDouble;
                typeLabel.Click += forward;
                typeLabel.DoubleClick += forwardDouble;
                _enabledLabel.Click += forward;
                _enabledLabel.DoubleClick += forwardDouble;
            }

            private Label _enabledLabel;

            private void PositionEnabledLabel()
            {
                if (_enabledLabel == null)
                {
                    return;
                }

                _enabledLabel.Left = Math.Max(200, Width - _enabledLabel.Width - 18);
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
        /// 细进度条：上面一行"执行 3/11  xxx.dwg  27%"，下面一条进度轨道。
        ///
        /// 为什么不用 WinForms 自带的 `ProgressBar`：那个控件在浅色卡片里是一条深绿色块，
        /// 和这里的配色对不上；自绘 20 行就够，还能把文件名和百分比放进同一行。
        /// </summary>
        internal sealed class ProgressStrip : Control
        {
            private readonly Font _font;
            private int _current;
            private int _total;
            private string _text = string.Empty;

            public ProgressStrip(Font font)
            {
                _font = font;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = BgColor;
                Height = 36;
            }

            /// <summary>更新进度。`current/total` 决定填充比例，`text` 是左边那行字。</summary>
            public void Update(int current, int total, string text)
            {
                _current = current;
                _total = total;
                _text = text ?? string.Empty;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BgColor);

                int textHeight = 18;
                int barTop = textHeight + 6;
                int barHeight = 6;

                // 文字行：左对齐文件名，右对齐百分比
                double ratio = _total <= 0 ? 0 : Math.Min(1.0, (double)_current / _total);
                string percent = ((int)Math.Round(ratio * 100)) + "%";

                var textRect = new Rectangle(0, 0, Math.Max(0, Width - 52), textHeight);
                TextRenderer.DrawText(g, _text, _font, textRect, TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                TextRenderer.DrawText(g, percent, _font, new Rectangle(Width - 48, 0, 48, textHeight), TextDim,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                // 轨道 + 填充
                var track = new Rectangle(0, barTop, Math.Max(0, Width), barHeight);
                using (var brush = new SolidBrush(Color.FromArgb(229, 231, 235)))
                {
                    g.FillRectangle(brush, track);
                }

                int filled = (int)Math.Round(track.Width * ratio);
                if (filled > 0)
                {
                    using (var brush = new SolidBrush(Accent))
                    {
                        g.FillRectangle(brush, new Rectangle(track.X, track.Y, filled, barHeight));
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
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);

                // **必须显式关掉** StandardClick：它是 Control 的默认开启项，
                // 只"不在 SetStyle(..., true) 里列出来"是关不掉的（这一点踩过坑：
                // 删掉之后点一次仍然触发两次 Click）。
                //
                // 本类在 OnMouseUp 里自己 RaiseClick（为了做"按下后移出按钮就取消"和
                // DialogResult 两件事）。框架的 StandardClick 会在 WM_LBUTTONUP 之后再发一次
                // Click，于是点一次触发两次——表现就是"浏览"弹出两个文件夹对话框、
                // "执行"把整批图跑两遍。
                SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
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

                // 禁用态：底色也压淡。只把文字改灰的话，蓝色主按钮看上去还是"可点"，
                // 而运行时这几个按钮其实是禁用的（防止重入）。
                if (!Enabled)
                {
                    back = Color.FromArgb(238, 240, 244);
                    border = Color.FromArgb(226, 229, 235);
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

            /// <summary>名称校验提示（空表示没有提示）。只在右侧画一个黄点，详细文案走 ToolTip。</summary>
            private string _warning;

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

            /// <summary>当前名称校验提示（null 表示没有）。</summary>
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
                // 有名称提示时右侧要留出黄点的位置，文字区相应收窄，否则长名称会压到点上
                int textRight = string.IsNullOrEmpty(_warning) ? 20 : 44;
                string name = string.IsNullOrEmpty(Rule.Name) ? "(未命名)" : Rule.Name;
                var nameRect = new Rectangle(70, 9, Math.Max(60, Width - 70 - textRight), 20);
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
                if (chipX + chipWidth < Width - textRight)
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
                var summaryRect = new Rectangle(70, 31, Math.Max(60, Width - 70 - textRight), 18);
                TextRenderer.DrawText(g, summary, _fontSummary, summaryRect, TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // 名称校验的黄色徽标：只提示不阻断，鼠标悬停看具体文案
                if (!string.IsNullOrEmpty(_warning))
                {
                    using (var brush = new SolidBrush(WarnDot))
                    {
                        g.FillEllipse(brush, Width - 20, 15, 8, 8);
                    }
                }
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
