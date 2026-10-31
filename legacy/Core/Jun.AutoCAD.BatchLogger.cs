using System;
using System.IO;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 统一日志出口：同时写命令行与可选 .log 文件。
    /// 所有规则通过该对象输出，保证"命令行 + 文件"双通道一致。
    /// </summary>
    public sealed class BatchLogger : IDisposable
    {
        private readonly Editor _editor;
        private readonly StreamWriter _writer;
        private readonly bool _verbose;
        private int _editorDetailCount;
        private bool _detailSuppressed;

        public BatchLogger(Editor editor, string logFilePath, bool verbose)
        {
            _editor = editor;
            _verbose = verbose;
            LogFilePath = logFilePath;

            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(logFilePath));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    _writer = new StreamWriter(logFilePath, false, new UTF8Encoding(true)) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    _writer = null;
                    Error("无法创建日志文件 " + logFilePath + "：" + ex.Message);
                }
            }
        }

        /// <summary>实际使用的日志文件路径（未启用时为 null）。</summary>
        public string LogFilePath { get; private set; }

        /// <summary>单条规则允许打印到命令行的明细条数上限。</summary>
        public int MaxEditorDetailLines { get; set; } = 100;

        /// <summary>每开始一条规则时重置明细计数。</summary>
        public void BeginRule()
        {
            _editorDetailCount = 0;
            _detailSuppressed = false;
        }

        public void Info(string message)
        {
            Write("信息", message, true);
        }

        public void Warn(string message)
        {
            Write("警告", message, true);
        }

        public void Error(string message)
        {
            Write("错误", message, true);
        }

        /// <summary>明细（每条实体的处理结果）。Verbose=false 时只写文件。</summary>
        public void Detail(string message)
        {
            Write("明细", message, _verbose);
        }

        /// <summary>章节标题（规则开始/结束）。</summary>
        public void Section(string message)
        {
            Write("===", message, true);
        }

        private void Write(string level, string message, bool toEditor)
        {
            string line = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, level, message);

            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 写文件失败不能影响主流程
            }

            if (!toEditor || _editor == null)
            {
                return;
            }

            if (level == "明细")
            {
                if (MaxEditorDetailLines > 0 && _editorDetailCount >= MaxEditorDetailLines)
                {
                    if (!_detailSuppressed)
                    {
                        _detailSuppressed = true;
                        SafeWriteMessage(string.Format("\n  ... 明细超过 {0} 条，后续明细仅写入日志文件。", MaxEditorDetailLines));
                    }

                    return;
                }

                _editorDetailCount++;
                SafeWriteMessage("\n  " + message);
                return;
            }

            SafeWriteMessage("\n" + message);
        }

        private void SafeWriteMessage(string text)
        {
            try
            {
                _editor.WriteMessage(text);
            }
            catch
            {
                // 文档已关闭等情况下忽略
            }
        }

        public void Dispose()
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
