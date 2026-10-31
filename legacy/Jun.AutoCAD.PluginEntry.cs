using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif
using Jun.AutoCAD.Core;
#if ZWCAD
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

[assembly: ExtensionApplication(typeof(Jun.AutoCAD.PluginEntry))]

namespace Jun.AutoCAD
{
    /// <summary>
    /// 插件入口。NETLOAD 加载本 DLL 时由 AutoCAD 调用 Initialize。
    ///
    /// 这里只做两件事：
    ///   1. 注册内置规则处理器（RuleFactory）——这是"可扩展性"的注册点，
    ///      第三方插件可在自己的 Initialize 里追加自定义规则类型；
    ///   2. 在命令行打印就绪提示。
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                RuleFactory.RegisterBuiltIns();
                DiagnosticLog.Write("PluginEntry.Initialize 完成，已注册规则 " +
                                    string.Join(", ", RuleFactory.GetRegisteredTypes()));

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage(string.Format(
                        "\n[{0}] 已加载 {1}。命令：{2}", PluginInfo.ToolName, PluginInfo.Describe(), PluginInfo.CommandHint));
                    doc.Editor.WriteMessage(string.Format(
                        "\n[{0}] 已注册规则：{1}", PluginInfo.ToolName, string.Join(", ", RuleFactory.GetRegisteredTypes())));
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Write("PluginEntry.Initialize 异常：" + ex);
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n[Jun.AutoCAD.BatchTool] 初始化失败：" + ex.Message);
                }
            }
        }

        public void Terminate()
        {
            // 关闭可能还开着的非模式窗口，避免 AutoCAD 退出时残留窗口
            Commands.BatchProcessCommand.CloseUi();
        }
    }
}
