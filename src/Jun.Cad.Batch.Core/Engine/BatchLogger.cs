using System;
using System.IO;
using System.Text;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 统一日志出口：同时写控制台与可选 .log 文件。
    ///
    /// 与 AutoCAD 版唯一的差别：宿主版的 <c>Editor</c> 换成了 <see cref="TextWriter"/>，
    /// 其余（级别、明细抑制、双通道一致）完全照搬，规则层的调用方式不变。
    /// </summary>
    public sealed class BatchLogger : IDisposable
    {
        private readonly TextWriter _console;
        private readonly StreamWriter _writer;
        private readonly bool _verbose;
        private int _consoleDetailCount;
        private bool _detailSuppressed;

        public BatchLogger(TextWriter console, string logFilePath, bool verbose)
        {
            _console = console;
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

        /// <summary>单条规则允许打印到控制台的明细条数上限。</summary>
        public int MaxConsoleDetailLines { get; set; } = 100;

        /// <summary>每开始一条规则时重置明细计数。</summary>
        public void BeginRule()
        {
            _consoleDetailCount = 0;
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

        /// <summary>明细（每条对象的处理结果）。Verbose=false 时只写文件。</summary>
        public void Detail(string message)
        {
            Write("明细", message, _verbose);
        }

        /// <summary>章节标题。</summary>
        public void Section(string message)
        {
            Write("===", message, true);
        }

        private void Write(string level, string message, bool toConsole)
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

            if (!toConsole || _console == null)
            {
                return;
            }

            if (level == "明细")
            {
                if (MaxConsoleDetailLines > 0 && _consoleDetailCount >= MaxConsoleDetailLines)
                {
                    if (!_detailSuppressed)
                    {
                        _detailSuppressed = true;
                        _console.WriteLine(string.Format("  ... 明细超过 {0} 条，后续明细仅写入日志文件。", MaxConsoleDetailLines));
                    }

                    return;
                }

                _consoleDetailCount++;
                _console.WriteLine("  " + message);
                return;
            }

            _console.WriteLine(message);
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
