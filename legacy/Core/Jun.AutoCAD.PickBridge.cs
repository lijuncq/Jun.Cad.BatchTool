using System;
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 拾取结果的回传桥。
    ///
    /// 为什么不能复用 <see cref="UiRunBridge"/>：那条桥是**单向**的（界面放请求、命令取一次就清空），
    /// 而拾取需要把结果**回传**给界面，还要能区分"用户按了 Esc"和"命令根本没跑起来"。
    ///
    /// 时序（2026-10-29 修正，见下方"为什么抽屉必须先关掉"）：
    ///   UI 点「拾取」→ 抽屉记下要继续编辑的副本并**关闭自己**（不是 Hide）
    ///   → <see cref="Request"/> 登记回调 → 主窗口排队 BATCHPICK
    ///   → 命令 <see cref="TryTake"/> 取到种类 → <see cref="MarkStarted"/> → 让用户点一个对象
    ///   → <see cref="Complete"/> 回传名称 → 界面重开抽屉并把名字填回那个字段。
    ///
    /// **为什么抽屉必须先关掉**：`Form.Hide()` 会让模态窗口的 `ShowDialog` 立即返回
    /// （实测 .NET 8：Hide 后 13ms ShowDialog 就返回 DialogResult.Cancel），
    /// 于是调用方的 `using` 把抽屉 Dispose 掉、窗口凭空消失，拾取永远填不回去。
    /// 只有先关掉抽屉，命令队列才会真正推进，拾取结果也才有地方落。
    ///
    /// **为什么每个请求带一个号**：看门狗、超时、关窗都可能取消请求，
    /// 旧请求的取消不能误伤新请求（否则"点第二次拾取"会被上一次的超时打掉）。
    ///
    /// 所有状态变化都写诊断日志：这条链路出问题时，现场只能靠日志判断卡在哪一步。
    /// </summary>
    public static class PickBridge
    {
        private static readonly object Sync = new object();

        private static bool _pending;
        private static bool _started;
        private static NameKind _kind;
        private static int _token;
        private static int _nextToken;
        private static Action<string> _callback;

        /// <summary>
        /// 登记一次拾取请求。返回请求号（&gt;0），已有请求在途时返回 0。
        /// 回调参数为拾取到的名称；null 表示取消（Esc / 超时 / 命令没跑起来）。
        /// </summary>
        public static int Request(NameKind kind, Action<string> callback)
        {
            if (callback == null)
            {
                return 0;
            }

            int token;
            lock (Sync)
            {
                if (_pending)
                {
                    return 0;
                }

                _pending = true;
                _started = false;
                _kind = kind;
                _callback = callback;
                token = ++_nextToken;
                _token = token;
            }

            DiagnosticLog.Write(string.Format("PickBridge 登记拾取请求 #{0}：{1}", token, kind));
            return token;
        }

        /// <summary>命令侧取请求（不清空，等 <see cref="Complete"/> 收尾）。</summary>
        public static bool TryTake(out NameKind kind, out int token)
        {
            lock (Sync)
            {
                kind = _kind;
                token = _token;
                return _pending;
            }
        }

        /// <summary>
        /// 命令侧标记"已经开始等用户点选"。
        /// 界面据此停止看门狗——否则用户多点几秒就被当成"命令没跑起来"取消掉。
        /// </summary>
        public static void MarkStarted(int token)
        {
            lock (Sync)
            {
                if (!_pending || token != _token)
                {
                    return;
                }

                _started = true;
            }

            DiagnosticLog.Write(string.Format("PickBridge 请求 #{0} 已进入拾取命令", token));
        }

        /// <summary>命令侧回传结果；<paramref name="name"/> 为 null 表示用户取消。请求号不匹配时忽略。</summary>
        public static void Complete(int token, string name)
        {
            Action<string> callback;
            lock (Sync)
            {
                if (!_pending || token != _token)
                {
                    return;
                }

                callback = _callback;
                _pending = false;
                _started = false;
                _callback = null;
            }

            DiagnosticLog.Write(string.Format("PickBridge 完成 #{0}：{1}",
                token, string.IsNullOrEmpty(name) ? "(取消)" : name));

            if (callback == null)
            {
                return;
            }

            try
            {
                callback(name);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("PickBridge 回调异常：" + ex);
            }
        }

        /// <summary>取消指定请求（只取消还挂着的那一个）。返回是否真的取消了。</summary>
        public static bool Cancel(int token)
        {
            lock (Sync)
            {
                if (!_pending || token != _token)
                {
                    return false;
                }
            }

            Complete(token, null);
            return true;
        }

        /// <summary>界面侧取消当前请求（关窗、超时）：等价于用户按 Esc。</summary>
        public static void CancelPending()
        {
            int token;
            lock (Sync)
            {
                token = _token;
                if (!_pending)
                {
                    return;
                }
            }

            Complete(token, null);
        }

        public static bool HasPending
        {
            get
            {
                lock (Sync)
                {
                    return _pending;
                }
            }
        }

        /// <summary>这个请求号是否还挂着（没完成也没被取消）。</summary>
        public static bool IsPending(int token)
        {
            lock (Sync)
            {
                return _pending && token == _token;
            }
        }

        /// <summary>这个请求是否已经进入命令（开始等用户点选）。</summary>
        public static bool HasStarted(int token)
        {
            lock (Sync)
            {
                return _pending && _started && token == _token;
            }
        }
    }

    /// <summary>
    /// 一次"收起抽屉去图上拾取"的上下文。
    ///
    /// 抽屉为了不挡住图纸必须整个关掉（见 <see cref="PickBridge"/> 的说明），
    /// 关掉之后界面上就没有任何控件可以承载"刚才编辑到一半的规则"了，
    /// 所以把副本、目标字段、拾取结果一起装在这里，等结果回来后交给新的抽屉实例继续编辑。
    /// </summary>
    public sealed class PickContinuation
    {
        public PickContinuation(RuleConfig working, string fieldPath, NameKind kind)
        {
            Working = working;
            FieldPath = fieldPath;
            Kind = kind;
        }

        /// <summary>抽屉里编辑到一半的规则副本（拾取回来后继续在这上面改）。</summary>
        public RuleConfig Working { get; private set; }

        /// <summary>点「拾取」的那个字段路径，例如 <c>Filter.SourceBlockName</c>。</summary>
        public string FieldPath { get; private set; }

        /// <summary>要取块名还是图层名。</summary>
        public NameKind Kind { get; private set; }

        /// <summary>拾取结果；null 表示取消（Esc / 超时 / 命令没跑起来）。</summary>
        public string Value { get; set; }

        /// <summary>取消时显示在状态栏的原因（为空则用默认文案）。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 由宿主层排一个拾取命令。UI 不能直接调 <c>SendStringToExecute</c>（那是宿主 API），
    /// 所以这里留一个接口，实现放在 <see cref="HostPickHost"/>。
    /// </summary>
    public interface IPickHost
    {
        /// <summary>排队执行 BATCHPICK；失败返回 false（界面据此恢复窗口）。</summary>
        bool StartPick();
    }
}
