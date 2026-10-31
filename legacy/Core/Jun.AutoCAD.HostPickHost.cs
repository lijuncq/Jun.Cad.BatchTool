using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// <see cref="IPickHost"/> 的宿主实现：把 BATCHPICK 排进命令队列。
    ///
    /// 为什么必须排队而不是直接调 Editor：点「拾取」时界面在自己的消息循环里（抽屉是模态的），
    /// 没有命令上下文，直接调 GetEntity 会失败。SendStringToExecute 把命令排到队列，
    /// 等消息循环空闲时由宿主在正常命令上下文里执行。
    /// </summary>
    public sealed class HostPickHost : IPickHost
    {
        private readonly Document _document;

        public HostPickHost(Document document)
        {
            _document = document;
        }

        public bool StartPick()
        {
            if (_document == null)
            {
                return false;
            }

            try
            {
                _document.SendStringToExecute("BATCHPICK ", true, false, false);
                DiagnosticLog.Write("HostPickHost 已排队 BATCHPICK");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("HostPickHost 排队失败：" + ex);
                return false;
            }
        }
    }
}
