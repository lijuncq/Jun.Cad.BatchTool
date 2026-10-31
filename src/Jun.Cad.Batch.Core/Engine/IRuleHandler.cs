using System;
using ACadSharp;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine.Preview;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 规则处理器契约。
    ///
    /// 与 AutoCAD 版的差别：没有事务。ACadSharp 的对象直接改内存模型，
    /// 回滚由 <see cref="CadFileSession"/> 在"文件级"完成（规则失败则丢弃整个文档重新读入）。
    /// 因此处理器只需要"选中、改掉"，不需要关心提交。
    /// </summary>
    public interface IRuleHandler
    {
        /// <summary>规则类型标识（与配置中的 Type 对应，大小写不敏感）。</summary>
        string RuleType { get; }

        /// <summary>执行规则。不得自行写盘，写盘由 <see cref="BatchRunner"/> 统一负责。</summary>
        void Execute(RuleContext context, RuleExecutionResult result);

        /// <summary>
        /// 规则是否自己上报了全部改动。
        ///
        /// 引擎默认用"实体快照 diff"兜底，但快照只覆盖**实体自身属性**——
        /// 改文字样式定义、改标注样式、删表项这三类改动快照看不见，
        /// 于是 diff 为空、引擎就会写一行"均已是目标状态"，把"改了很多"说成"什么都没改"。
        ///
        /// 自报的规则要把它改的每一处都写进 <see cref="RuleContext.RecordChange"/>；
        /// 为 true 时引擎不再输出那句"均已是目标状态"的兜底结论。
        /// </summary>
        bool ReportsOwnChanges => false;
    }

    /// <summary>
    /// 规则校验失败（配置缺字段、目标对象不存在等）——属于"可预期"的失败，
    /// 由 <see cref="BatchRunner"/> 记录为规则中止而不是崩溃。
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
    /// 规则执行上下文：文档 + 配置 + 日志 + 资源库。
    /// </summary>
    public sealed class RuleContext
    {
        private SpatialFilter _spatial;
        private bool _spatialResolved;

        public RuleContext(CadDocument document, BatchConfig config, RuleConfig rule, BatchLogger log, ResourceLocator resources)
        {
            Document = document;
            Config = config;
            Rule = rule;
            Log = log;
            Resources = resources ?? new ResourceLocator();
        }

        public CadDocument Document { get; private set; }

        /// <summary>当前处理的文件名（由引擎填，规则里做 {filename} 这类占位符时用）。</summary>
        public string FileLabel { get; set; }

        /// <summary>图案库 / 线型库 / 字体搜索路径（离线工具必须自带，不能依赖本机 AutoCAD）。</summary>
        public ResourceLocator Resources { get; private set; }

        public BatchConfig Config { get; private set; }

        public RuleConfig Rule { get; private set; }

        public BatchLogger Log { get; private set; }

        /// <summary>
        /// 差异预览接收器。非空表示"这次是预演"——规则**不要写盘**（本来也不写），
        /// 但应把"要改什么"记进来，尤其是降级行为。
        /// </summary>
        public IPreviewSink Preview { get; set; }

        /// <summary>
        /// 空间/容器/区域过滤（懒解析）。没有任何相关配置时为 null，调用方按原逻辑走。
        /// 懒解析的原因：解析要遍历布局与图框，不是每条规则都需要。
        /// </summary>
        public SpatialFilter Spatial
        {
            get
            {
                if (!_spatialResolved)
                {
                    _spatialResolved = true;
                    _spatial = SpatialFilter.Build(this, EntityScanner.ResolveScope(this));

                    if (_spatial != null && Log != null)
                    {
                        foreach (string note in _spatial.Notes)
                        {
                            Log.Detail("  空间过滤：" + note);
                        }
                    }
                }

                return _spatial;
            }
        }

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
            get { return ConfigValues.Bool(Filter.IncludeNestedBlocks, true); }
        }

        public int MaxNestingDepth
        {
            get
            {
                int d = ConfigValues.Int(Filter.MaxNestingDepth, 8);
                return d < 1 ? 1 : d;
            }
        }

        public bool SkipLockedLayers
        {
            get { return ConfigValues.Bool(Filter.SkipLockedLayers, true); }
        }

        /// <summary>
        /// 往差异预览里记一行（预览模式下才生效）。
        ///
        /// 规则自己上报的价值：能给出**引擎快照看不出来的信息**——
        /// 比如"这条文字本来要加 Wipeout，因为 writer 探测失败改成了 Solid"。
        /// 快照 diff 只能看到"多了个实体"，说不出为什么。
        /// </summary>
        public void RecordChange(
            ACadSharp.CadObject entity,
            string operation,
            string oldValue,
            string newValue,
            bool degraded = false,
            string note = null,
            string space = null)
        {
            if (Preview == null)
            {
                return;
            }

            Preview.Add(new ChangeRecord
            {
                Space = space,
                Handle = entity == null ? string.Empty : entity.Handle.ToString("X"),
                DxfType = KindOf(entity),
                Operation = operation,
                OldValue = oldValue,
                NewValue = newValue,
                Status = ChangeStatus.WillChange,
                Degraded = degraded,
                Note = note
            });
        }

        /// <summary>
        /// 记一条**表对象**的改动（文字样式 / 标注样式 / 图层 / 块定义）。
        /// 这些对象被删掉之后句柄就查不到了，所以用名字标识。
        /// </summary>
        public void RecordTableChange(
            string objectName, string kind, string operation, string oldValue, string newValue,
            bool degraded = false, string note = null)
        {
            if (Preview == null)
            {
                return;
            }

            Preview.Add(new ChangeRecord
            {
                ObjectName = objectName,
                DxfType = kind,
                Operation = operation,
                OldValue = oldValue,
                NewValue = newValue,
                Status = ChangeStatus.WillChange,
                Degraded = degraded,
                Note = note
            });
        }

        private static string KindOf(ACadSharp.CadObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            var entity = obj as ACadSharp.Entities.Entity;
            return entity != null ? EntityFilter.DxfNameOf(entity) : obj.GetType().Name.ToUpperInvariant();
        }

        /// <summary>记一条"命中但不改"（例如"已有遮罩，跳过"）。</summary>
        public void RecordSkip(ACadSharp.CadObject entity, string operation, string reason, bool degraded = false, string space = null)
        {
            if (Preview == null)
            {
                return;
            }

            Preview.Add(new ChangeRecord
            {
                Space = space,
                Handle = entity == null ? string.Empty : entity.Handle.ToString("X"),
                DxfType = KindOf(entity),
                Operation = operation,
                Status = ChangeStatus.NoChange,
                Note = reason,
                Degraded = degraded
            });
        }

        /// <summary>记一条"被挡住"（例如目标图层被锁定、表格单元格无法写）。</summary>
        public void RecordBlocked(ACadSharp.CadObject entity, string operation, string reason, string space = null)
        {
            if (Preview == null)
            {
                return;
            }

            Preview.Add(new ChangeRecord
            {
                Space = space,
                Handle = entity == null ? string.Empty : entity.Handle.ToString("X"),
                DxfType = KindOf(entity),
                Operation = operation,
                Status = ChangeStatus.Blocked,
                Note = reason
            });
        }

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
