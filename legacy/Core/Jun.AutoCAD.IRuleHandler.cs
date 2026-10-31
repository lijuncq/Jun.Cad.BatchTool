using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 规则处理器契约。
    ///
    /// 扩展方式（无需改动主流程）：
    ///   1. 新建类实现 IRuleHandler；
    ///   2. 在 PluginEntry 或自定义初始化代码里调用
    ///      RuleFactory.Register("MyRuleType", () => new MyRuleHandler());
    ///   3. 配置文件里写 "Type": "MyRuleType" 即可生效。
    ///
    /// 处理器只负责"把 Filter 选中、把 Action 施加"，事务、日志、异常兜底由 BatchRunner 统一处理。
    /// </summary>
    public interface IRuleHandler
    {
        /// <summary>规则类型标识（与配置中的 Type 对应，大小写不敏感）。</summary>
        string RuleType { get; }

        /// <summary>
        /// 执行规则。
        /// 重要约定：本方法【不得】自行 Commit 事务，也不得关闭事务；
        /// 所有数据库修改都必须在 context.Transaction 内完成。
        /// </summary>
        void Execute(RuleExecutionContext context, RuleExecutionResult result);
    }

    /// <summary>
    /// 规则校验失败（配置缺字段、目标对象不存在等）——属于"可预期"的失败，
    /// 由 BatchRunner 记录为规则中止而不是崩溃。
    /// </summary>
    public sealed class RuleValidationException : Exception
    {
        public RuleValidationException(string message) : base(message)
        {
        }

        public RuleValidationException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// 规则执行上下文：把"事务 + 文档 + 配置 + 日志"打包传给处理器。
    /// </summary>
    public sealed class RuleExecutionContext
    {
        public RuleExecutionContext(Document document, Transaction transaction, BatchConfig config, RuleConfig rule, BatchLogger log)
        {
            Document = document;
            Transaction = transaction;
            Config = config;
            Rule = rule;
            Log = log;
        }

        public Document Document { get; private set; }

        public Database Database
        {
            get { return Document.Database; }
        }

        public Editor Editor
        {
            get { return Document.Editor; }
        }

        /// <summary>当前规则使用的环境事务（Sequential 模式下所有规则共用同一个）。</summary>
        public Transaction Transaction { get; private set; }

        public BatchConfig Config { get; private set; }

        public RuleConfig Rule { get; private set; }

        public BatchLogger Log { get; private set; }

        public RuleFilter Filter
        {
            get { return Rule.Filter ?? (Rule.Filter = new RuleFilter()); }
        }

        public RuleAction Action
        {
            get { return Rule.Action ?? (Rule.Action = new RuleAction()); }
        }

        public bool IncludeNestedBlocks
        {
            get { return Filter.IncludeNestedBlocks ?? true; }
        }

        public int MaxNestingDepth
        {
            get
            {
                int d = Filter.MaxNestingDepth ?? 8;
                return d < 1 ? 1 : d;
            }
        }

        public bool SkipLockedLayers
        {
            get { return Filter.SkipLockedLayers ?? true; }
        }

        /// <summary>读取布尔型 Action 字段的默认值。</summary>
        public static bool Bool(bool? value, bool defaultValue)
        {
            return ConfigValues.Bool(value, defaultValue);
        }

        public static double Double(double? value, double defaultValue)
        {
            return ConfigValues.Double(value, defaultValue);
        }

        public static int Int(int? value, int defaultValue)
        {
            return ConfigValues.Int(value, defaultValue);
        }
    }
}
