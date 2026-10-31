using System;
using System.IO;
using System.Text;

namespace Jun.Cad.Batch.Editing
{
    /// <summary>
    /// 诊断日志：写到 %TEMP%\Jun.Cad.Batch.diag.log，独立于业务日志。
    ///
    /// 存在的意义：AutoCAD 的命令方法如果抛异常，命令行有时什么都不打印（或只弹一次对话框），
    /// 用户会看到"执行了但没有任何提示"。这里在命令入口/关键节点落盘一行，
    /// 即使编辑器输出被吞掉，也能从文件确认命令到底有没有进去、卡在哪一步。
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly object SyncRoot = new object();

        public static string LogPath
        {
            get
            {
                try
                {
                    return Path.Combine(Path.GetTempPath(), "Jun.Cad.Batch.diag.log");
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>追加一行诊断信息（任何异常都被吞掉，绝不影响主流程）。</summary>
        public static void Write(string message)
        {
            try
            {
                string path = LogPath;
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                lock (SyncRoot)
                {
                    File.AppendAllText(
                        path,
                        string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}", DateTime.Now, message, Environment.NewLine),
                        new UTF8Encoding(true));
                }
            }
            catch
            {
                // 诊断日志本身不允许抛异常
            }
        }

        /// <summary>读取日志尾部（用于 BATCHDIAG 展示）。</summary>
        public static string ReadTail(int maxChars)
        {
            try
            {
                string path = LogPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return "(暂无诊断日志)";
                }

                string text = File.ReadAllText(path);
                if (maxChars > 0 && text.Length > maxChars)
                {
                    text = text.Substring(text.Length - maxChars);
                }

                return text;
            }
            catch (Exception ex)
            {
                return "(读取诊断日志失败：" + ex.Message + ")";
            }
        }
    }
}
