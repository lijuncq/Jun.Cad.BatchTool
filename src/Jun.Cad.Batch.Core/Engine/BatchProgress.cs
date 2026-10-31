using System;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 批处理的进度快照。
    ///
    /// 只描述"正在处理第几个文件"，不掺任何界面类型——命令行将来要用同一份也不用改。
    /// 界面通过 <see cref="IProgress{T}"/> 接收，跨线程的封送由 `Progress&lt;T&gt;` 负责。
    /// </summary>
    public sealed class BatchProgress
    {
        /// <summary>当前文件序号（从 1 开始）。</summary>
        public int Index { get; set; }

        public int Total { get; set; }

        /// <summary>当前文件名（不含路径，太长会把进度条文字挤爆）。</summary>
        public string FileName { get; set; }

        /// <summary>阶段名：执行 / 预览。</summary>
        public string Phase { get; set; }

        /// <summary>整批已经结束（用于把进度条推到 100%）。</summary>
        public bool Finished { get; set; }

        public string Describe()
        {
            if (Finished)
            {
                return string.Format("{0}完成：共 {1} 个文件", Phase ?? string.Empty, Total);
            }

            return string.Format("{0} {1}/{2}  {3}", Phase ?? string.Empty, Index, Total, FileName ?? string.Empty);
        }
    }
}
