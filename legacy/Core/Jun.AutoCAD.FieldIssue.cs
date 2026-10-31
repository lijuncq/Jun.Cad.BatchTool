using System;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 校验问题的级别。
    ///
    ///   Error   —— 阻断保存。只用于"保存下去一定是错的"：必填缺失、数值越界、类型级前置条件不满足。
    ///   Warning —— 仅提醒。用于"可能不对但取决于图纸"的情况，例如名称在当前图纸里找不到
    ///              （批处理的目标是目录里的一批图纸，当前这张不是判据）。
    /// </summary>
    public enum IssueSeverity
    {
        /// <summary>阻断保存。</summary>
        Error,

        /// <summary>仅提醒，不阻断。</summary>
        Warning
    }

    /// <summary>
    /// 一条字段级校验问题。
    ///
    /// Path 是相对 <c>RuleConfig</c> 的点路径，例如 <c>Filter.LayerName</c>、<c>Action.TargetLayer</c>；
    /// 空字符串表示问题属于整条规则（例如"类型未注册"）。
    /// 表单据此把错误标到具体控件上，列表据此显示行内徽标。
    /// </summary>
    public sealed class FieldIssue
    {
        public FieldIssue(string path, IssueSeverity severity, string message)
        {
            Path = path ?? string.Empty;
            Severity = severity;
            Message = message ?? string.Empty;
        }

        /// <summary>相对 RuleConfig 的字段路径；空串表示规则级问题。</summary>
        public string Path { get; private set; }

        public IssueSeverity Severity { get; private set; }

        public string Message { get; private set; }

        public bool IsError
        {
            get { return Severity == IssueSeverity.Error; }
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Path)
                ? Message
                : Path + "：" + Message;
        }
    }
}
