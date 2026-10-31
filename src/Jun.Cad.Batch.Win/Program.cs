using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Editing;

namespace Jun.Cad.Batch.Win
{
    /// <summary>离线版入口：直接开界面，不需要 AutoCAD。</summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
            }
            catch
            {
                // 老系统上设不了就算了，界面照常能用
            }

            // 自检用：把界面离屏渲染成 PNG。DPI / 停靠 / 控件遮挡这类问题
            // 纯看代码是看不出来的，只能真的画一遍再看图（见 README「界面」一节）。
            //   --screenshot <png>   [config] [refDwg]      只渲染当前页（执行页）
            //   --screenshot-tabs <目录> [config] [refDwg]   三个页签各渲染一张
            // 传 refDwg 时按它做名称校验，用来验"黄色提示点 + 状态行"是否真的画出来了。
            if (args != null && args.Length >= 2
                && string.Equals(args[0], "--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                RenderToFile(args[1], Arg(args, 2), Arg(args, 3));
                return;
            }

            if (args != null && args.Length >= 2
                && string.Equals(args[0], "--screenshot-tabs", StringComparison.OrdinalIgnoreCase))
            {
                RenderAllTabs(args[1], Arg(args, 2), Arg(args, 3));
                return;
            }

            // 自检用：把进度条摆到"执行 3/11"再渲染，验停靠位置与文字不被挤掉
            //   --screenshot-progress <png> [config] [refDwg]
            if (args != null && args.Length >= 2
                && string.Equals(args[0], "--screenshot-progress", StringComparison.OrdinalIgnoreCase))
            {
                RenderWithProgress(args[1], Arg(args, 2), Arg(args, 3));
                return;
            }

            // 自检用：验证一次鼠标点击只触发一次 Click。
            // 曾经 FlatButton 同时开了 StandardClick 又手工 RaiseClick，导致点一次发两次
            // Click（"浏览"弹两个对话框、"执行"跑两遍）。这个入口用 SendMessage 真发
            // WM_LBUTTONDOWN/UP，数一下 Click 次数。
            //   --check-click
            if (args != null && args.Length >= 1
                && string.Equals(args[0], "--check-click", StringComparison.OrdinalIgnoreCase))
            {
                CheckSingleClick();
                return;
            }

            // 自检用：验证主窗口会不会出现在任务栏。
            // ShowInTaskbar=false 会给窗口加 WS_EX_TOOLWINDOW，任务栏直接忽略它——
            // 编译期看不出任何问题，只能开窗口查扩展样式。
            //   --check-taskbar
            if (args != null && args.Length >= 1
                && string.Equals(args[0], "--check-taskbar", StringComparison.OrdinalIgnoreCase))
            {
                CheckTaskbar();
                return;
            }

            // 自检用：走一遍界面里的执行路径（OfflineRunner），报告写到 <输出目录>\ui-run-report.txt。
            // 界面里的「执行」要弹两个文件对话框，自动化点不了，所以用这个入口验同一段代码。
            //   --run <config> <输出目录> <dwg...>
            if (args != null && args.Length >= 4
                && string.Equals(args[0], "--run", StringComparison.OrdinalIgnoreCase))
            {
                RunHeadless(args[1], args[2], args);
                return;
            }

            // 自检用：把「执行结果」窗口按报告原文渲染成 PNG（验证等宽列对齐是否真的成立）。
            //   --screenshot-report <png> <报告txt>
            if (args != null && args.Length >= 3
                && string.Equals(args[0], "--screenshot-report", StringComparison.OrdinalIgnoreCase))
            {
                RenderReport(args[1], args[2]);
                return;
            }

            string configPath = EnsureConfig(args != null && args.Length > 0 ? args[0] : null);

            // 名称校验要等用户指定参考图纸，这里先不注入
            Application.Run(new BatchToolForm(configPath, null));
        }

        private static string Arg(string[] args, int index)
        {
            return args != null && args.Length > index && !string.IsNullOrWhiteSpace(args[index])
                ? args[index]
                : null;
        }

        /// <summary>
        /// 取要用的配置文件：显式传入优先，否则用 **exe 同目录** 的 Jun.Cad.Batch.json；
        /// 文件不存在就按内置模板生成一份（20 种规则各一条示例，全部 Enabled=false，
        /// 所以生成完直接点「执行」不会改任何东西）。
        /// </summary>
        private static string EnsureConfig(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            bool created;
            string error;
            string path = ConfigLoader.EnsureDefaultConfig(out created, out error);

            if (created)
            {
                Console.WriteLine("已生成默认配置：" + path);
            }
            else if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine("生成默认配置失败：" + error);
            }

            return path;
        }

        /// <summary>
        /// 无界面执行一遍（等价于在界面里点「执行」选好文件与输出目录）。
        /// WinExe 没有控制台，结果只能落文件：正常写 ui-run-report.txt，异常写 ui-run-error.txt。
        /// </summary>
        /// <summary>把进度直接打到控制台（自检用；界面里走的是 Progress&lt;T&gt; + 进度条）。</summary>
        private sealed class PrintingProgress : IProgress<BatchProgress>
        {
            public void Report(BatchProgress value)
            {
                if (value != null)
                {
                    Console.WriteLine(value.Describe());
                }
            }
        }

        private static void RunHeadless(string configPath, string outputDir, string[] args)
        {
            var files = new System.Collections.Generic.List<string>();
            for (int i = 3; i < args.Length; i++)
            {
                files.Add(args[i]);
            }

            try
            {
                System.IO.Directory.CreateDirectory(outputDir);

                System.Collections.Generic.List<string> warnings;
                BatchConfig config = ConfigLoader.Load(configPath, out warnings);

                // 带上进度回调：这个入口本来就是为了"验界面走的那段代码"，
                // 进度条现在也在那段代码里，不打出来就等于没验。
                string report = OfflineRunner.Run(config, configPath, files, outputDir, new PrintingProgress());
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(outputDir, "ui-run-report.txt"), report);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(outputDir, "ui-run-error.txt"), ex.ToString());
            }
        }

        /// <summary>截图模式下的参考图纸：读不了就返回 null（等于不校验），不弹窗打扰。</summary>
        private static INameLookup CreateLookup(string referencePath)
        {
            if (string.IsNullOrEmpty(referencePath))
            {
                return null;
            }

            string error;
            return FileNameLookup.TryCreate(referencePath, out error);
        }

        /// <summary>
        /// 把「执行结果」窗口按报告原文渲染成 PNG。
        /// 验的是"等宽列对齐"：汉字宽度必须等于两个西文宽度，否则汇总表的数字列会参差不齐。
        /// </summary>
        private static void RenderReport(string outputPath, string reportPath)
        {
            string report = System.IO.File.ReadAllText(reportPath);

            using (var dialog = new RunResultDialog(report))
            {
                dialog.Show();
                Application.DoEvents();

                using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
                    bitmap.Save(outputPath, ImageFormat.Png);
                }

                dialog.Close();
            }
        }

        private static void RenderWithProgress(string outputPath, string configPath, string referencePath)
        {
            using (var form = new BatchToolForm(EnsureConfig(configPath),
                       CreateLookup(referencePath)))
            {
                form.Show();
                Application.DoEvents();

                form.SelectTab(0);
                form.ShowProgressSample(3, 11, "执行 3/11  KLD-ZHJT-PIP-LYT-2000ZZ-30110_B0_F10 FLARE TOWER.dwg");
                Application.DoEvents();

                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(outputPath, ImageFormat.Png);
                }

                form.Close();
            }
        }

        private static void RenderToFile(string outputPath, string configPath, string referencePath)
        {
            using (var form = new BatchToolForm(EnsureConfig(configPath),
                       CreateLookup(referencePath)))
            {
                // 必须先 Show 出句柄：布局与 DPI 缩放要到显示阶段才落定
                form.Show();
                Application.DoEvents();

                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(outputPath, ImageFormat.Png);
                }

                form.Close();
            }
        }

        /// <summary>
        /// 两个页签各渲一张。页签切换会重排控件（规则列表宽度、JSON 框尺寸都按容器算），
        /// 只渲当前页等于只验一半布局，所以这里两页都过一遍。
        /// </summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private const int MkLeftButton = 0x0001;

        /// <summary>
        /// 离屏点一次按钮，数 Click 次数。
        ///
        /// 为什么必须真发消息：`PerformClick()` 走的是我们自己的 RaiseClick，绕过了框架，
        /// 数不出"框架也发了一次"这个 bug。只有 WM_LBUTTONUP 走完整消息链才看得出来。
        /// </summary>
        private static void CheckSingleClick()
        {
            using (var form = new Form
            {
                Text = "click-check",
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                Size = new Size(240, 120),
                ShowInTaskbar = false
            })
            {
                int clicks = 0;
                int downs = 0;
                int ups = 0;

                var button = new BatchToolForm.FlatButton
                {
                    Text = "测试",
                    Kind = BatchToolForm.FlatButtonKind.Secondary,
                    Left = 20,
                    Top = 20,
                    Width = 100,
                    Height = 34
                };
                button.Click += delegate { clicks++; };
                button.MouseDown += delegate { downs++; };
                button.MouseUp += delegate { ups++; };

                form.Controls.Add(button);
                form.Show();
                Application.DoEvents();

                IntPtr handle = button.Handle;
                IntPtr point = (IntPtr)((30 << 16) | 30);   // 客户区内一点

                SendMessage(handle, WmLeftButtonDown, (IntPtr)MkLeftButton, point);
                SendMessage(handle, WmLeftButtonUp, IntPtr.Zero, point);
                Application.DoEvents();

                Console.WriteLine(string.Format("MouseDown={0} MouseUp={1} Click={2}{3}",
                    downs, ups, clicks, clicks == 1 ? "（正常）" : "（异常）"));

                form.Close();
                Environment.ExitCode = clicks == 1 ? 0 : 1;
            }
        }

        private const int GwlExStyle = -20;
        private const int GwlStyle = -16;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExAppWindow = 0x00040000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /// <summary>
        /// 检查主窗口会不会出现在任务栏。
        ///
        /// Win32 的规则：顶层窗口要进任务栏，必须**没有** <c>WS_EX_TOOLWINDOW</c>，
        /// 并且要么有 <c>WS_EX_APPWINDOW</c>、要么没有 owner。
        /// <c>Form.ShowInTaskbar = false</c> 做的正是给窗口加 <c>WS_EX_TOOLWINDOW</c>，
        /// 于是任务栏彻底忽略它——窗口被别的程序盖住后只能靠 Alt+Tab 找回来。
        ///
        /// 这个属性写错了编译照样通过、截图也看不出来（截图只画客户区），
        /// 只能真的开一个窗口去查扩展样式，所以留了这个入口做回归。
        /// </summary>
        private static void CheckTaskbar()
        {
            using (var form = new BatchToolForm(EnsureConfig(null), null))
            {
                form.Show();
                Application.DoEvents();

                IntPtr handle = form.Handle;
                int exStyle = GetWindowLong(handle, GwlExStyle);
                int style = GetWindowLong(handle, GwlStyle);
                bool toolWindow = (exStyle & WsExToolWindow) != 0;
                bool appWindow = (exStyle & WsExAppWindow) != 0;

                bool ok = form.ShowInTaskbar && !toolWindow;

                // 故意只用 ASCII：这个入口的结果常被重定向到文件/管道，
                // 中文在 OEM 代码页里会变成乱码，反而看不出结论。
                Console.WriteLine(string.Format(
                    "ShowInTaskbar={0}  WS_EX_TOOLWINDOW={1}  WS_EX_APPWINDOW={2}  => {3}",
                    form.ShowInTaskbar ? "true" : "false",
                    toolWindow ? "YES(taskbar ignores it)" : "no",
                    appWindow ? "yes" : "no",
                    ok ? "OK: shows in taskbar" : "FAIL: missing from taskbar"));

                // 顺带把标题栏按钮的状态打出来：布局是绝对坐标 + 定宽的，
                // 一旦 WS_MAXIMIZEBOX 被打开，用户拉大窗口就会把控件挤乱。
                Console.WriteLine(string.Format(
                    "MinimizeBox={0}  MaximizeBox={1}  WS_MINIMIZEBOX={2}  WS_MAXIMIZEBOX={3}",
                    form.MinimizeBox ? "true" : "false",
                    form.MaximizeBox ? "true" : "false",
                    (style & WsMinimizeBox) != 0 ? "yes" : "no",
                    (style & WsMaximizeBox) != 0 ? "yes" : "no"));

                form.Close();
                Environment.ExitCode = ok ? 0 : 1;
            }
        }

        private static void RenderAllTabs(string directory, string configPath, string referencePath)
        {
            System.IO.Directory.CreateDirectory(directory);

            INameLookup lookup = CreateLookup(referencePath);
            string[] names = { "ui-run.png", "ui-edit.png", "ui-log.png" };
            for (int tab = 0; tab < names.Length; tab++)
            {
                using (var form = new BatchToolForm(EnsureConfig(configPath), lookup))
                {
                    form.Show();
                    Application.DoEvents();

                    form.SelectTab(tab);
                    Application.DoEvents();

                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(System.IO.Path.Combine(directory, names[tab]), ImageFormat.Png);
                    }

                    form.Close();
                }
            }
        }
    }
}
