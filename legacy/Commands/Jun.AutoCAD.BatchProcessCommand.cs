using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif
using Jun.AutoCAD.Config;
using Jun.AutoCAD.Core;
#if ZWCAD
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

[assembly: CommandClass(typeof(Jun.AutoCAD.Commands.BatchProcessCommand))]

namespace Jun.AutoCAD.Commands
{
    /// <summary>
    /// 命令行入口。
    ///
    ///   BATCHPROCESS      交互式：提示输入配置路径（回车=默认），支持 /config &lt;路径&gt;
    ///   BATCHPROCESSDEF   无提示：直接用默认配置路径执行（用于排查"提示符没出来"这类问题）
    ///   BATCHDIAG         环境自检：版本、文档、伴随文件、默认路径、规则注册、诊断日志尾部
    ///
    /// 设计约束：**任何失败都必须留下痕迹**。
    /// 命令体整体包在 try/catch 里，异常同时写命令行、诊断日志，必要时弹对话框；
    /// 没有活动文档时不再静默 return，而是弹对话框提示。
    /// </summary>
    public sealed class BatchProcessCommand
    {
        public const string CommandName = "BATCHPROCESS";
        public const string CommandNameDefault = "BATCHPROCESSDEF";
        public const string CommandNameAgain = "BATCHAGAIN";
        public const string CommandNameUi = "BATCHUI";

        /// <summary>非模式窗口必须由静态字段持有，否则会被 GC 回收。</summary>
        private static Jun.AutoCAD.UI.BatchToolForm _uiForm;

        /// <summary>关闭仍开着的界面窗口（插件卸载 / AutoCAD 退出时调用）。</summary>
        public static void CloseUi()
        {
            try
            {
                if (_uiForm != null && !_uiForm.IsDisposed)
                {
                    _uiForm.Close();
                }
            }
            catch
            {
                // 退出阶段忽略异常
            }
            finally
            {
                _uiForm = null;
                UiRunBridge.Clear();
            }
        }
        public const string CommandNameDiag = "BATCHDIAG";
        public const string CommandNamePick = "BATCHPICK";
        public const string ConfigSwitch = "/config";

        // ---------------------------------------------------------------- BATCHPROCESS

        [CommandMethod(CommandName, CommandFlags.Modal)]
        public void BatchProcess()
        {
            DiagnosticLog.Write("BATCHPROCESS 命令进入");

            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    // 以前这里是静默 return，用户看到的就是"执行了但没有任何提示"
                    const string msg = "当前没有打开的图形。请先新建或打开一个 DWG，再执行 BATCHPROCESS。";
                    DiagnosticLog.Write("BATCHPROCESS 中止：MdiActiveDocument 为 null");
                    TryAlert(msg);
                    return;
                }

                ed = doc.Editor;
                WriteBanner(ed);

                string defaultPath = ConfigLoader.ResolveDefaultConfigPath();
                ed.WriteMessage("\n默认配置文件：" + defaultPath);

                PromptStatus status;
                string configPath = PromptConfigPath(ed, defaultPath, out status);
                if (configPath == null)
                {
                    ed.WriteMessage("\n已取消（输入状态：" + status + "）。");
                    ed.WriteMessage("\n如果命令行没有出现输入提示，可直接用 BATCHPROCESSDEF 跳过提示、按默认配置执行。");
                    DiagnosticLog.Write("BATCHPROCESS 未取得路径，状态=" + status);
                    return;
                }

                RunBatch(doc, configPath);
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHPROCESS", ex);
            }
        }

        // ------------------------------------------------------------------- BATCHUI

        /// <summary>
        /// 图形界面入口：弹出"执行 + 设置"窗口（**非模式**）。
        ///
        /// 交互流程：
        ///   1. 窗口打开时自动加载默认配置文件（不弹路径提示），规则列表直接可见；
        ///   2. 在窗口里可以换配置、勾选规则、调执行模式/日志等设置；
        ///   3. 点「执行」→ **窗口不关**，通过 UiRunBridge 把执行排队回命令上下文；
        ///   4. 点「关闭」才真正关闭窗口。
        ///
        /// 非模式窗口的两个要点：
        ///   * 用 Application.ShowModelessDialog 而不是 ShowModalDialog，AutoCAD 会把窗口挂到主窗口下，
        ///     不会阻塞命令行，用户可以一边看图一边点执行；
        ///   * 必须持有静态引用，否则窗口会被 GC 回收；重复执行 BATCHUI 时激活已有窗口而不是再开一个。
        /// </summary>
        [CommandMethod(CommandNameUi, CommandFlags.Modal)]
        public void BatchProcessUi()
        {
            DiagnosticLog.Write("BATCHUI 命令进入");

            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    DiagnosticLog.Write("BATCHUI 中止：MdiActiveDocument 为 null");
                    TryAlert("当前没有打开的图形。请先新建或打开一个 DWG。");
                    return;
                }

                ed = doc.Editor;

                // 已经开着就激活它，不重复创建
                if (_uiForm != null && !_uiForm.IsDisposed)
                {
                    _uiForm.Activate();
                    _uiForm.BringToFront();
                    ed.WriteMessage("\n批量处理窗口已打开，请在该窗口中操作。");
                    DiagnosticLog.Write("BATCHUI 激活已有窗口");
                    return;
                }

                string configPath = ConfigLoader.ResolveDefaultConfigPath();
                DiagnosticLog.Write("BATCHUI 默认配置=" + configPath);

                // 注入名称查询：保存规则时对照当前图纸的符号表提醒"这个名字是不是写错了"。
                // UI 只认 INameLookup 接口，宿主类型只出现在 HostNameLookup 里。
                var form = new Jun.AutoCAD.UI.BatchToolForm(configPath, new HostNameLookup(doc), new HostPickHost(doc));
                form.FormClosed += delegate
                {
                    _uiForm = null;
                    UiRunBridge.Clear();
                    DiagnosticLog.Write("BATCHUI 窗口已关闭");
                };

                _uiForm = form;

                // 非模式：不阻塞命令行，执行由窗口内部排队回 BATCHUIRUN
                AcApp.ShowModelessDialog(form);

                ed.WriteMessage("\n批量处理窗口已打开（非模式）。勾选规则后点「执行」，点「关闭」结束。");
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHUI", ex);
            }
        }

        // ---------------------------------------------------------------- BATCHUIRUN

        /// <summary>
        /// 内部命令：由界面窗口通过 SendStringToExecute 排队触发，不面向用户直接调用。
        ///
        /// 存在的意义：非模式窗口里点「执行」时不在命令上下文，直接开事务会让撤销记录错乱；
        /// 走这个命令，执行时就有正常的文档上下文与撤销边界。
        /// </summary>
        [CommandMethod(UiRunBridge.CommandName, CommandFlags.Modal)]
        public void BatchProcessFromUi()
        {
            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    DiagnosticLog.Write("BATCHUIRUN 中止：MdiActiveDocument 为 null");
                    TryAlert("当前没有打开的图形。");
                    return;
                }

                ed = doc.Editor;

                BatchConfig preset;
                string configPath;
                if (!UiRunBridge.TryTake(out preset, out configPath))
                {
                    // 没有待执行请求：多半是用户手工敲了这个内部命令
                    ed.WriteMessage("\n[提示] 没有待执行的界面任务。请用 " + CommandNameUi + " 打开窗口后点「执行」。");
                    DiagnosticLog.Write("BATCHUIRUN 无待执行请求");
                    return;
                }

                WriteBanner(ed);
                ed.WriteMessage("\n界面执行，配置文件：" + configPath);

                RunBatch(doc, configPath, false, preset);
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHUIRUN", ex);
            }
        }

        // ----------------------------------------------------------- BATCHPROCESSDEF

        /// <summary>
        /// 不提示、直接用默认配置执行。用来区分"提示符没出来"和"批处理本身失败"两类问题。
        /// </summary>
        [CommandMethod(CommandNameDefault, CommandFlags.Modal)]
        public void BatchProcessDefault()
        {
            DiagnosticLog.Write("BATCHPROCESSDEF 命令进入");

            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    DiagnosticLog.Write("BATCHPROCESSDEF 中止：MdiActiveDocument 为 null");
                    TryAlert("当前没有打开的图形。请先新建或打开一个 DWG。");
                    return;
                }

                ed = doc.Editor;
                WriteBanner(ed);

                string configPath = ConfigLoader.ResolveDefaultConfigPath();
                ed.WriteMessage("\n使用默认配置：" + configPath);

                RunBatch(doc, configPath);
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHPROCESSDEF", ex);
            }
        }

        // ----------------------------------------------------------------- BATCHAGAIN

        /// <summary>
        /// 再次执行：复用【上一次】用过的配置文件再跑一遍，不弹任何输入提示。
        ///
        /// 与 BATCHPROCESS 的区别：
        ///   BATCHPROCESS  每次都要选/确认配置路径；
        ///   BATCHAGAIN    直接读上次记录（%APPDATA%\Jun.AutoCAD.BatchTool\last-run.txt），
        ///                 适合"改完参数反复试跑"、"同一套规则跑多张图"的场景。
        ///
        /// 注意：配置文件是【重新读取】的，所以改完 config.json 直接 BATCHAGAIN 就生效，
        ///       不需要重新 NETLOAD，也不需要重新指定路径。
        /// </summary>
        [CommandMethod(CommandNameAgain, CommandFlags.Modal)]
        public void BatchProcessAgain()
        {
            DiagnosticLog.Write("BATCHAGAIN 命令进入");

            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    DiagnosticLog.Write("BATCHAGAIN 中止：MdiActiveDocument 为 null");
                    TryAlert("当前没有打开的图形。请先新建或打开一个 DWG。");
                    return;
                }

                ed = doc.Editor;
                WriteBanner(ed);

                LastRunInfo last = LastRunStore.Load();
                string configPath;

                if (last == null || !last.IsValid)
                {
                    // 没有历史记录：退化为默认配置，并说清楚原因
                    configPath = ConfigLoader.ResolveDefaultConfigPath();
                    ed.WriteMessage("\n没有找到上一次的执行记录，改用默认配置：" + configPath);
                    ed.WriteMessage("\n（首次使用请先执行 BATCHPROCESS 指定配置文件，之后即可用 BATCHAGAIN 重复执行。）");
                    DiagnosticLog.Write("BATCHAGAIN 无历史记录，回退默认配置=" + configPath);
                }
                else
                {
                    configPath = last.ConfigPath;
                    ed.WriteMessage("\n上次执行：" + last.Describe());
                    ed.WriteMessage("\n本次复用配置：" + configPath);
                    ed.WriteMessage("\n（配置文件会重新读取，改完 config.json 直接再跑即可生效）");

                    // 换了图形时提醒一句：同一个配置在不同图纸上跑，结果可能完全不同
                    string current = SafeDrawingName(doc);
                    string previous = string.IsNullOrEmpty(last.Drawing) ? null : Path.GetFileName(last.Drawing);
                    if (previous != null && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                    {
                        ed.WriteMessage(string.Format(
                            "\n[提示] 上次是在「{0}」上执行，当前图形是「{1}」，请确认是否要在这张图上重复执行。",
                            previous, current));
                    }

                    DiagnosticLog.Write("BATCHAGAIN 复用配置=" + configPath);
                }

                RunBatch(doc, configPath, false);
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHAGAIN", ex);
            }
        }

        // ------------------------------------------------------------------ BATCHDIAG

        /// <summary>环境自检：把排查需要的所有信息一次性打出来。</summary>
        [CommandMethod(CommandNameDiag, CommandFlags.Modal)]
        public void BatchDiag()
        {
            DiagnosticLog.Write("BATCHDIAG 命令进入");

            Editor ed = null;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                ed = doc == null ? null : doc.Editor;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("\n============ Jun.AutoCAD.BatchTool 自检 ============");
                sb.AppendLine("宿主版本          : " + SafeVersion());
                sb.AppendLine("当前图形          : " + (doc == null ? "(无)" : doc.Name));
                sb.AppendLine("文档数量          : " + SafeDocumentSummary());
                sb.AppendLine("插件程序集        : " + SafeAssemblyLocation());
                sb.AppendLine("插件版本          : " + SafeAssemblyVersion());
                sb.AppendLine("已注册规则类型    : " + string.Join(", ", RuleFactory.GetRegisteredTypes()));
                sb.AppendLine("诊断日志          : " + DiagnosticLog.LogPath);

                LastRunInfo last = LastRunStore.Load();
                sb.AppendLine("上次执行记录      : " + (last == null ? "(无)" : last.Describe()));
                sb.AppendLine("记录文件          : " + LastRunStore.FilePath);

                // 伴随文件是否齐全：.NET 8 版 AutoCAD 缺一不可；
                // .NET Framework 宿主（AutoCAD 2021~2024 / 中望CAD）只需要 dll + Newtonsoft.Json.dll
                string pluginDir = SafePluginDirectory();
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    sb.AppendLine("伴随文件：");
#if NET
                    string[] companions =
                    {
                        "Jun.AutoCAD.BatchTool.dll",
                        "Jun.AutoCAD.BatchTool.runtimeconfig.json",
                        "Jun.AutoCAD.BatchTool.deps.json",
                        "Newtonsoft.Json.dll"
                    };
#else
                    string[] companions =
                    {
                        "Jun.AutoCAD.BatchTool.dll",
                        "Newtonsoft.Json.dll"
                    };
#endif
                    foreach (string name in companions)
                    {
                        string full = Path.Combine(pluginDir, name);
                        sb.AppendLine(string.Format("  [{0}] {1}", File.Exists(full) ? "有" : "缺", name));
                    }
                }

                sb.AppendLine("默认配置路径候选：");
                int i = 1;
                foreach (string candidate in ConfigLoader.GetDefaultConfigCandidates())
                {
                    sb.AppendLine(string.Format("  {0}. [{1}] {2}", i++, File.Exists(candidate) ? "存在" : "不存在", candidate));
                }

                sb.AppendLine("---- 诊断日志尾部（最近 2000 字符）----");
                sb.Append(DiagnosticLog.ReadTail(2000));
                sb.AppendLine("====================================================");

                string text = sb.ToString();
                if (ed != null)
                {
                    ed.WriteMessage(text);
                }
                else
                {
                    TryAlert(text);
                }

                DiagnosticLog.Write("BATCHDIAG 完成");
            }
            catch (System.Exception ex)
            {
                ReportFatal(ed, "BATCHDIAG", ex);
            }
        }

        // ----------------------------------------------------------------- BATCHPICK

        /// <summary>
        /// 内部命令：由规则编辑抽屉的「拾取」按钮排队触发，让用户在图上点一个对象，
        /// 把它的块名 / 所在图层名回传给界面。
        ///
        /// 必须走命令而不是直接在界面里调 Editor：点按钮时界面在自己的消息循环里，没有命令上下文。
        /// 抽屉在排队之前会先把自己关掉（Hide 会让模态 ShowDialog 立即返回，见 PickBridge 说明），
        /// 所以命令执行时图纸是完全可见可点的。
        /// 用户按 Esc → <see cref="PickBridge.Complete"/> 传 null，界面据此重开抽屉且不改值。
        /// </summary>
        [CommandMethod(CommandNamePick, CommandFlags.Modal)]
        public void BatchPick()
        {
            DiagnosticLog.Write("BATCHPICK 命令进入");

            Editor ed = null;
            int token = 0;
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    PickBridge.CancelPending();
                    return;
                }

                ed = doc.Editor;

                NameKind kind;
                if (!PickBridge.TryTake(out kind, out token))
                {
                    ed.WriteMessage("\n[提示] 没有待拾取的字段。请先在规则编辑抽屉里点「拾取」。");
                    DiagnosticLog.Write("BATCHPICK 无待处理请求");
                    return;
                }

                // 告诉界面"命令真的跑起来了"：界面据此停掉看门狗，用户慢慢点也不会被当成超时
                PickBridge.MarkStarted(token);

                string what = kind == NameKind.Block ? "块" : "图层";
                var options = new PromptEntityOptions("\n选择对象以取其" + what + "名：");
                options.SetRejectMessage("\n请选择一个图元。");

                PromptEntityResult result = ed.GetEntity(options);
                if (result.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n已取消拾取。");
                    PickBridge.Complete(token, null);
                    return;
                }

                string name = null;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var ent = tr.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (ent != null)
                    {
                        if (kind == NameKind.Block)
                        {
                            var br = ent as BlockReference;
                            if (br == null)
                            {
                                ed.WriteMessage("\n所选对象不是块参照，没有取到块名。");
                            }
                            else
                            {
                                name = BlockNameUtil.GetEffectiveName(br, tr);
                            }
                        }
                        else
                        {
                            name = ent.Layer;
                        }
                    }
                }

                ed.WriteMessage(string.IsNullOrEmpty(name) ? "\n没有取到名称。" : "\n拾取到：" + name);
                PickBridge.Complete(token, name);
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Write("BATCHPICK 异常：" + ex);
                try
                {
                    if (ed != null)
                    {
                        ed.WriteMessage("\n拾取失败：" + ex.Message);
                    }
                }
                catch
                {
                    // 编辑器不可用时忽略
                }

                // 无论如何都要回传，否则界面会一直停在"抽屉已关闭"的状态
                if (token > 0)
                {
                    PickBridge.Complete(token, null);
                }
                else
                {
                    PickBridge.CancelPending();
                }
            }
        }

        // ------------------------------------------------------------------ 核心执行

        /// <summary>加载配置并执行批处理；所有异常都会被记录，不会静默失败。</summary>
        private static BatchRunSummary RunBatch(Document doc, string configPath, bool showAgainHint = true, BatchConfig preset = null)
        {
            Editor ed = doc.Editor;

            DiagnosticLog.Write("RunBatch 开始，配置路径=" + configPath);

            if (!File.Exists(configPath))
            {
                ed.WriteMessage("\n[错误] 配置文件不存在：" + configPath);
                ed.WriteMessage("\n可用的默认位置（按优先级）：");
                int i = 1;
                foreach (string candidate in ConfigLoader.GetDefaultConfigCandidates())
                {
                    ed.WriteMessage(string.Format("\n  {0}. {1}", i++, candidate));
                }

                ed.WriteMessage("\n请把 config.json 放到上述任一位置，或用 /config <路径> 指定。");
                DiagnosticLog.Write("RunBatch 中止：配置文件不存在");
                return null;
            }

            // ---- 配置来源：界面传进来的优先（用户在窗口里勾选的启用状态要生效），否则从磁盘读 ----
            BatchConfig config;
            List<string> warnings = new List<string>();
            if (preset != null)
            {
                config = preset;
                DiagnosticLog.Write("RunBatch 使用界面内存配置");
            }
            else
            {
                try
                {
                    config = ConfigLoader.Load(configPath, out warnings);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\n[错误] 配置加载失败：" + ex.Message);
                    DiagnosticLog.Write("RunBatch 配置加载失败：" + ex);
                    return null;
                }
            }

            if (warnings != null)
            {
                foreach (string w in warnings)
                {
                    ed.WriteMessage("\n[配置警告] " + w);
                }
            }

            int enabled = 0;
            foreach (RuleConfig r in config.Rules)
            {
                if (r != null && r.Enabled)
                {
                    enabled++;
                }
            }

            ed.WriteMessage(string.Format("\n配置加载成功：共 {0} 条规则，启用 {1} 条。", config.Rules.Count, enabled));
            DiagnosticLog.Write(string.Format("配置加载成功：{0} 条规则 / 启用 {1} 条", config.Rules.Count, enabled));

            string logPath = ResolveLogPath(config, configPath);

            // ---- 加锁 + 执行 ----
            // 文档锁是必须的：修改数据库期间若用户切换布局/编辑，会触发 eLockViolation。
            BatchRunSummary summary = null;

            using (DocumentLock docLock = doc.LockDocument())
            using (var log = new BatchLogger(ed, logPath, config.Verbose))
            {
                try
                {
                    var runner = new BatchRunner(doc, config, configPath, log);
                    summary = runner.Run();
                    runner.WriteSummary(summary);

                    DiagnosticLog.Write(string.Format(
                        "批处理完成：匹配 {0} / 成功 {1} / 跳过 {2} / 失败 {3}",
                        summary.TotalMatched, summary.TotalChanged, summary.TotalSkipped, summary.TotalFailed));
                }
                catch (System.Exception ex)
                {
                    log.Error("批处理整体异常：" + ex);
                    ed.WriteMessage("\n[错误] 批处理整体异常：" + ex.Message);
                    DiagnosticLog.Write("批处理整体异常：" + ex);
                }
            }

            // 记录本次执行，供 BATCHAGAIN（再次执行）复用
            LastRunStore.Save(configPath, summary, SafeDrawingName(doc));

            try
            {
                ed.Regen();
            }
            catch
            {
                // 重生成失败不影响结果
            }

            ed.WriteMessage("\n批处理结束。");
            if (summary != null && showAgainHint)
            {
                ed.WriteMessage(string.Format(
                    "\n如需对同一份配置再跑一遍，直接输入 {0}。", CommandNameAgain));
            }

            return summary;
        }

        // -------------------------------------------------------------------- 辅助

        private static void WriteBanner(Editor ed)
        {
            ed.WriteMessage("\n============================================================");
            ed.WriteMessage("\n Jun.AutoCAD.BatchTool  —  配置驱动的批量处理  " + PluginInfo.Describe());
            ed.WriteMessage("\n 命名空间：Jun.AutoCAD   宿主：" + HostCompat.Describe);
            ed.WriteMessage("\n 支持版本：AutoCAD 2021 ~ 2025 / 中望CAD 2020");
            ed.WriteMessage("\n 命令：" + PluginInfo.CommandHint);
            ed.WriteMessage("\n============================================================");
        }

        /// <summary>致命异常统一出口：命令行 + 诊断日志 + 对话框，三重留痕。</summary>
        private static void ReportFatal(Editor ed, string stage, System.Exception ex)
        {
            string detail = ex.ToString();
            DiagnosticLog.Write(string.Format("{0} 未处理异常：{1}", stage, detail));

            try
            {
                if (ed != null)
                {
                    ed.WriteMessage("\n[错误] " + stage + " 执行失败：" + ex.Message);
                    ed.WriteMessage("\n" + detail);
                }
            }
            catch
            {
                // 编辑器不可用时忽略
            }

            TryAlert(string.Format("{0} 执行失败：{1}\n\n详细信息已写入：\n{2}",
                stage, ex.Message, DiagnosticLog.LogPath));
        }

        private static void TryAlert(string message)
        {
            try
            {
                AcApp.ShowAlertDialog(message);
            }
            catch
            {
                // 无界面环境（如 accoreconsole）下弹窗不可用
            }
        }

        /// <summary>
        /// 文档数量 + 名称。用于识别一种隐蔽故障：
        /// 若"文档数量 > 0"但 MdiActiveDocument 为 null，说明插件里的 acmgd 与宿主不是同一份
        /// （程序集被重复加载到不同 AssemblyLoadContext，静态成员各自独立）。
        /// </summary>
        private static string SafeDocumentSummary()
        {
            try
            {
                var dm = AcApp.DocumentManager;
                var names = new List<string>();
                foreach (Document d in dm)
                {
                    names.Add(string.IsNullOrEmpty(d.Name) ? "(未命名)" : Path.GetFileName(d.Name));
                }

                return string.Format("{0} 个 [{1}]", dm.Count, string.Join(", ", names));
            }
            catch (System.Exception ex)
            {
                return "(读取失败：" + ex.Message + ")";
            }
        }

        /// <summary>当前图形文件名（未命名图形返回占位文本），用于"上次在哪张图上执行"的提醒。</summary>
        private static string SafeDrawingName(Document doc)
        {
            try
            {
                if (doc == null || string.IsNullOrEmpty(doc.Name))
                {
                    return "(未命名图形)";
                }

                return Path.GetFileName(doc.Name);
            }
            catch
            {
                return "(未命名图形)";
            }
        }

        private static string SafeVersion()
        {
            try
            {
                return HostCompat.Describe;
            }
            catch
            {
                return "(未知)";
            }
        }

        private static string SafeAssemblyLocation()
        {
            try
            {
                return Assembly.GetExecutingAssembly().Location;
            }
            catch
            {
                return "(未知)";
            }
        }

        private static string SafeAssemblyVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch
            {
                return "(未知)";
            }
        }

        private static string SafePluginDirectory()
        {
            try
            {
                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>提示输入配置路径，支持 /config 前缀、引号包裹、直接回车取默认。</summary>
        private static string PromptConfigPath(Editor ed, string defaultPath, out PromptStatus status)
        {
            var options = new PromptStringOptions("\n输入配置文件路径 [回车=默认] 或 /config <路径>: ")
            {
                AllowSpaces = true,
                UseDefaultValue = false
            };

            PromptResult result = ed.GetString(options);
            status = result.Status;
            if (result.Status != PromptStatus.OK)
            {
                DiagnosticLog.Write("PromptConfigPath 状态：" + result.Status);
                return null;
            }

            string input = (result.StringResult ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                return defaultPath;
            }

            // 支持 /config <路径> 形式
            if (input.StartsWith(ConfigSwitch, StringComparison.OrdinalIgnoreCase))
            {
                input = input.Substring(ConfigSwitch.Length).Trim();
                if (input.Length == 0)
                {
                    return defaultPath;
                }
            }

            input = input.Trim('"').Trim();
            return input.Length == 0 ? defaultPath : input;
        }

        /// <summary>
        /// 解析日志文件路径：
        ///   LogFile = "none"      → 不写文件
        ///   LogFile = 相对路径    → 相对配置文件所在目录
        ///   LogFile 为空          → 配置文件同目录 \Logs\batch-yyyyMMdd-HHmmss.log
        /// </summary>
        private static string ResolveLogPath(BatchConfig config, string configPath)
        {
            string configured = config.LogFile == null ? null : config.LogFile.Trim();

            if (!string.IsNullOrEmpty(configured)
                && string.Equals(configured, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;

            if (!string.IsNullOrEmpty(configured))
            {
                return Path.IsPathRooted(configured) ? configured : Path.Combine(configDir, configured);
            }

            string fileName = string.Format("batch-{0:yyyyMMdd-HHmmss}.log", DateTime.Now);
            return Path.Combine(configDir, "Logs", fileName);
        }
    }
}
