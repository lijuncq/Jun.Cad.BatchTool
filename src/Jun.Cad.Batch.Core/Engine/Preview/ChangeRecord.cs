using System;
using System.Collections.Generic;
using System.Text;

namespace Jun.Cad.Batch.Engine.Preview
{
    /// <summary>差异预览里一行的状态。</summary>
    public enum ChangeStatus
    {
        /// <summary>会改动。</summary>
        WillChange,

        /// <summary>命中了但无需改动（已是目标状态）。</summary>
        NoChange,

        /// <summary>被挡住（配置/前置检查/目标不存在）。原因写在 <see cref="ChangeRecord.Note"/>。</summary>
        Blocked
    }

    /// <summary>
    /// 差异预览的一行：文件 / 空间 / 句柄 / 类型 / 操作 / 旧值→新值 / 状态。
    ///
    /// Degradation must be explicit: static dimension-cache masks and skipped table cells
    /// set <see cref="Degraded"/> and explain the limitation in <see cref="Note"/>.
    /// Native mask failures are blocked, never replaced with a solid-color mask.
    /// Preview must describe what execution will actually do.
    /// </summary>
    public sealed class ChangeRecord
    {
        public string File { get; set; }

        public string Space { get; set; }

        public string Handle { get; set; }

        /// <summary>
        /// 表对象（文字样式 / 标注样式 / 图层 / 块定义）用名字标识——它们没有"实体句柄"的语义，
        /// 而且被删掉之后句柄也查不到了。
        /// </summary>
        public string ObjectName { get; set; }

        public string DxfType { get; set; }

        public string RuleName { get; set; }

        public int RuleIndex { get; set; }

        /// <summary>操作类型（改图层 / 改字高 / 加遮罩 / 替换文字 …）。</summary>
        public string Operation { get; set; }

        public string OldValue { get; set; }

        public string NewValue { get; set; }

        public ChangeStatus Status { get; set; }

        public string Note { get; set; }

        /// <summary>Whether capability is limited (static dimension-cache masks, skipped table cells, etc.).</summary>
        public bool Degraded { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case ChangeStatus.WillChange:
                        return "Will change";
                    case ChangeStatus.NoChange:
                        return "No change";
                    default:
                        return "Blocked";
                }
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(Space) ? "-" : Space);
            if (!string.IsNullOrEmpty(Handle))
            {
                sb.Append(" #").Append(Handle);
            }
            else if (!string.IsNullOrEmpty(ObjectName))
            {
                sb.Append(" ").Append(ObjectName);
            }
            if (!string.IsNullOrEmpty(DxfType))
            {
                sb.Append(" ").Append(DxfType);
            }

            if (!string.IsNullOrEmpty(Operation))
            {
                sb.Append(" · ").Append(Operation);
            }

            return sb.ToString();
        }
    }

    /// <summary>预览接收器。规则可以直接往里写记录（新规则），也可以由引擎按快照 diff 生成（老规则）。</summary>
    public interface IPreviewSink
    {
        bool IsPreview { get; }

        void Add(ChangeRecord record);

        /// <summary>该句柄是否已经有记录（用于避免"规则自报"和"快照 diff"重复两行）。</summary>
        bool HasRecordFor(string handle);

        /// <summary>
        /// 开始执行一条规则：清空"本规则已占用句柄"。
        /// 不按规则重置的话，前一条规则记过"跳过"的句柄会把后一条规则的真实改动整行吞掉。
        /// </summary>
        void BeginRule(int index);

        /// <summary>当前正在执行的规则（引擎设置，规则不必自己填）。</summary>
        string CurrentRule { get; set; }

        int CurrentRuleIndex { get; set; }

        string CurrentFile { get; set; }
    }

    /// <summary>预览收集器。</summary>
    public sealed class PreviewSink : IPreviewSink
    {
        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();

        public bool IsPreview
        {
            get { return true; }
        }

        public string CurrentRule { get; set; }

        public int CurrentRuleIndex { get; set; }

        public string CurrentFile { get; set; }

        public IReadOnlyList<ChangeRecord> Records
        {
            get { return _records; }
        }

        private readonly HashSet<string> _handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void BeginRule(int index)
        {
            _handles.Clear();
            CurrentRuleIndex = index;
        }

        public bool HasRecordFor(string handle)
        {
            return !string.IsNullOrEmpty(handle) && _handles.Contains(handle);
        }

        public void Add(ChangeRecord record)
        {
            if (record == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(record.File))
            {
                record.File = CurrentFile;
            }

            if (string.IsNullOrEmpty(record.RuleName))
            {
                record.RuleName = CurrentRule;
            }

            if (record.RuleIndex == 0)
            {
                record.RuleIndex = CurrentRuleIndex;
            }

            _records.Add(record);

            if (!string.IsNullOrEmpty(record.Handle))
            {
                _handles.Add(record.Handle);
            }
        }

        public int CountOf(ChangeStatus status)
        {
            int n = 0;
            foreach (ChangeRecord r in _records)
            {
                if (r.Status == status)
                {
                    n++;
                }
            }

            return n;
        }

        public int DegradedCount
        {
            get
            {
                int n = 0;
                foreach (ChangeRecord r in _records)
                {
                    if (r.Degraded)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public void Clear()
        {
            _records.Clear();
            _handles.Clear();
        }
    }
}
