using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
using Jun.AutoCAD.Config;
#if ZWCAD
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 界面 → 命令上下文的执行桥。
    ///
    /// 为什么需要它：BATCHUI 改成**非模式窗口**后，用户点「执行」时不在任何命令上下文里。
    /// 此时如果直接开事务改数据库，撤销记录会错乱（AutoCAD 的 undo 是按命令组织的），
    /// 轻则 Ctrl+Z 失效，重则抛异常。
    ///
    /// 做法：窗口把"要执行的配置"暂存到这里，再用 SendStringToExecute 把
    /// BATCHUIRUN 命令排队回 AutoCAD 的命令循环——命令真正执行时就有了正常的文档/撤销上下文。
    /// </summary>
    public static class UiRunBridge
    {
        /// <summary>内部命令名：由界面排队触发，不面向用户。</summary>
        public const string CommandName = "BATCHUIRUN";

        private static readonly object SyncRoot = new object();
        private static BatchConfig _pendingConfig;
        private static string _pendingPath;

        /// <summary>是否有等待执行的界面请求。</summary>
        public static bool HasPending
        {
            get
            {
                lock (SyncRoot)
                {
                    return _pendingConfig != null;
                }
            }
        }

        /// <summary>
        /// 由窗口的「执行」按钮调用：暂存配置并把执行排队回命令上下文。
        /// </summary>
        public static void RequestRun(BatchConfig config, string configPath)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new ArgumentException("配置路径为空。", "configPath");
            }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                throw new InvalidOperationException("当前没有打开的图形。");
            }

            lock (SyncRoot)
            {
                _pendingConfig = config;
                _pendingPath = configPath;
            }

            DiagnosticLog.Write("UiRunBridge 提交执行：" + configPath);
            doc.SendStringToExecute(CommandName + " ", true, false, false);
        }

        /// <summary>命令侧取走待执行配置（取一次即清空，避免重复执行）。</summary>
        public static bool TryTake(out BatchConfig config, out string configPath)
        {
            lock (SyncRoot)
            {
                config = _pendingConfig;
                configPath = _pendingPath;
                _pendingConfig = null;
                _pendingPath = null;
            }

            return config != null;
        }

        /// <summary>放弃待执行请求（例如窗口被关闭）。</summary>
        public static void Clear()
        {
            lock (SyncRoot)
            {
                _pendingConfig = null;
                _pendingPath = null;
            }
        }
    }
}
