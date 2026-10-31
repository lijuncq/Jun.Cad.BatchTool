using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace Jun.Cad.Batch.Win
{
    /// <summary>产品名与版本号。</summary>
    internal static class AppInfo
    {
        /// <summary>嵌入资源名（与 csproj 里的 LogicalName 对应）。</summary>
        private const string IconResource = "Jun.Cad.Batch.app.ico";

        private static Icon _appIcon;
        private static bool _iconTried;

        /// <summary>
        /// 窗口图标。
        ///
        /// 为什么必须显式设：`ApplicationIcon` 只把图标写进 exe 的 Win32 资源，
        /// 而窗口类注册时 hIcon 是 NULL，系统会给它挂上"默认应用程序图标"——
        /// 于是 exe 在资源管理器里是对的，标题栏和任务栏却是两个方块的系统图（实测）。
        /// 这里从嵌入资源读回来设给 <c>Form.Icon</c>，两处才一致。
        /// </summary>
        public static Icon AppIcon
        {
            get
            {
                if (_iconTried)
                {
                    return _appIcon;
                }

                _iconTried = true;

                try
                {
                    using (Stream stream = typeof(AppInfo).Assembly.GetManifestResourceStream(IconResource))
                    {
                        if (stream != null)
                        {
                            _appIcon = new Icon(stream);
                        }
                    }
                }
                catch
                {
                    // 图标取不到不该影响启动，退回系统默认图标
                    _appIcon = null;
                }

                return _appIcon;
            }
        }

        public static string Version
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "1.0.0" : v.ToString(3);
                }
                catch
                {
                    return "1.0.0";
                }
            }
        }

        /// <summary>
        /// 标题栏副标题：只留版本号。
        /// 以前带"离线版"后缀，那是从 AutoCAD 插件平移过来的历史包袱——
        /// 现在它就是一个独立的桌面程序，没有"在线版"与之对照，说"离线"反而让人困惑。
        /// </summary>
        public static string Describe()
        {
            return "v" + Version;
        }
    }
}
