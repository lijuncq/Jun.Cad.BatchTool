using System;
using System.IO;
using System.Reflection;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 插件自身信息：版本号、构建时间、命令清单。
    ///
    /// 用途：命令行输出里带上"构建时间"，能立刻区分"你加载的是不是我刚编译的那个 DLL"——
    /// 排查"改了没生效"类问题时这一步最省时间。
    /// </summary>
    public static class PluginInfo
    {
        public const string ToolName = "Jun.AutoCAD.BatchTool";

        public const string CommandHint = "BATCHUI（图形界面）/ BATCHPROCESS（命令行交互）/ BATCHAGAIN（重复上次）/ BATCHPROCESSDEF（免提示）/ BATCHDIAG（自检）/ BATCHUIRUN（内部）";

        /// <summary>程序集版本号。</summary>
        public static string Version
        {
            get
            {
                try
                {
                    return Assembly.GetExecutingAssembly().GetName().Version.ToString();
                }
                catch
                {
                    return "?";
                }
            }
        }

        /// <summary>DLL 的编译时间（取文件最后写入时间），用于确认加载的是最新构建。</summary>
        public static string BuildStamp
        {
            get
            {
                try
                {
                    string path = Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        return "(未知构建)";
                    }

                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
                }
                catch
                {
                    return "(未知构建)";
                }
            }
        }

        /// <summary>形如 "v1.0.0.0 (构建 2026-10-23 14:36)"。</summary>
        public static string Describe()
        {
            return string.Format("v{0} (构建 {1})", Version, BuildStamp);
        }

        /// <summary>程序集所在目录。</summary>
        public static string Directory
        {
            get
            {
                try
                {
                    string path = Assembly.GetExecutingAssembly().Location;
                    return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
