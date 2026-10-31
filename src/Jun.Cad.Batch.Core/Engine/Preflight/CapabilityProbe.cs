using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using CSMath;

namespace Jun.Cad.Batch.Engine.Preflight
{
    /// <summary>前置检查的状态。</summary>
    public enum ProbeStatus
    {
        /// <summary>没测（缺测试图 / 未触发）。</summary>
        NotTested,

        /// <summary>通过。</summary>
        Passed,

        /// <summary>失败，相关规则必须降级。</summary>
        Failed,

        /// <summary>能力本身受限（例如标注遮罩只能静态），不是失败但必须告知。</summary>
        Degraded
    }

    /// <summary>一条前置检查的结果。</summary>
    public sealed class ProbeResult
    {
        public string Key { get; set; }

        public string Title { get; set; }

        public ProbeStatus Status { get; set; }

        public string Detail { get; set; }

        public long ElapsedMs { get; set; }

        /// <summary>受这条检查影响的规则类型。</summary>
        public string Affects { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case ProbeStatus.Passed: return "通过";
                    case ProbeStatus.Failed: return "失败";
                    case ProbeStatus.Degraded: return "降级";
                    default: return "未测";
                }
            }
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}：{2}", StatusText, Title, Detail);
        }
    }

    /// <summary>
    /// 能力探测：在真正改图之前，用**临时文档**把"这条链路到底能不能写出"验一遍。
    ///
    /// 为什么必须探测而不是查文档：
    ///   * Wipeout 的写出链路在 3.8.35 上**实测可用**，但这是"本机 + 这个版本"的结论，
    ///     换版本、换图纸模板都可能变；探测是唯一能给出当前答案的手段；
    ///   * 探测失败必须能降级（Wipeout → 2D Solid），否则整条规则就不能跑；
    ///   * 探测结果要能显示在界面上，而不是埋在日志里——用户在执行前就该知道
    ///     "这次会用 Solid 遮罩"。
    ///
    /// 边界（必须写清楚）：探测通过只证明 **ACadSharp 自己能读回自己写的**，
    /// 不证明 AutoCAD 认可。本项目不做宿主验证，所以结果里不能出现"已验证可用"这种话。
    /// </summary>
    public static class CapabilityProbe
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, ProbeResult> Cache =
            new Dictionary<string, ProbeResult>(StringComparer.OrdinalIgnoreCase);

        /// <summary>清空缓存（切换图纸/版本后重新探测）。</summary>
        public static void Reset()
        {
            lock (SyncRoot)
            {
                Cache.Clear();
            }
        }

        /// <summary>全部检查。表格检查需要一张含 ACAD_TABLE 的样图，没有就给"未测"。</summary>
        public static List<ProbeResult> RunAll(string tableSamplePath = null)
        {
            var list = new List<ProbeResult>();
            list.Add(WipeoutWriter());
            list.Add(TableCellWriter(tableSamplePath));
            list.Add(DimensionMask());
            return list;
        }

        // ------------------------------------------------------------------ Wipeout

        /// <summary>
        /// Wipeout 写出链路：构造最小遮罩 → 写盘 → 回读 → 断言。
        ///
        /// 断言三项：实体还在、裁剪顶点还是 2 个、包围盒与写入值一致。
        /// 任一失败都说明"这条链路在本机当前版本上不可靠"，调用方应降级 2D Solid。
        /// </summary>
        public static ProbeResult WipeoutWriter()
        {
            lock (SyncRoot)
            {
                ProbeResult cached;
                if (Cache.TryGetValue("wipeout-writer", out cached))
                {
                    return cached;
                }

                var watch = Stopwatch.StartNew();
                var result = new ProbeResult
                {
                    Key = "wipeout-writer",
                    Title = "Wipeout 写出链路",
                    Affects = "TextMask"
                };

                string dir = Path.Combine(Path.GetTempPath(), "jun-cad-batch-probe");
                string path = Path.Combine(dir, "wipeout-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".dwg");

                try
                {
                    Directory.CreateDirectory(dir);

                    var doc = new CadDocument();
                    doc.Header.Version = ACadVersion.AC1032;

                    var definition = new ImageDefinition("*JUN_PROBE_WIPEOUT")
                    {
                        Size = new XY(1, 1),
                        DefaultSize = new XY(1, 1),
                        FileName = string.Empty
                    };
                    doc.ImageDefinitions.Add(definition);

                    var wipeout = new Wipeout
                    {
                        InsertPoint = new XYZ(0, 0, 0),
                        Size = new XY(1, 1),
                        Definition = definition
                    };
                    wipeout.ClipBoundaryVertices.Add(new XY(0, 0));
                    wipeout.ClipBoundaryVertices.Add(new XY(100, 50));
                    doc.ModelSpace.Entities.Add(wipeout);

                    var notices = new List<string>();
                    DwgWriter.Write(path, doc, null, (sender, e) =>
                    {
                        if (e != null && e.NotificationType == NotificationType.Error)
                        {
                            notices.Add(e.Message);
                        }
                    });

                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = "写出后文件为空";
                        return Finish(result, watch);
                    }

                    CadDocument back = DwgReader.Read(path);
                    List<Wipeout> read = back.ModelSpace.Entities.OfType<Wipeout>().ToList();

                    if (read.Count != 1)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = string.Format("回读得到 {0} 个 Wipeout（应为 1）", read.Count);
                        return Finish(result, watch);
                    }

                    Wipeout one = read[0];
                    if (one.ClipBoundaryVertices.Count != 2)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = string.Format("裁剪顶点丢失（回读 {0} 个，应为 2）", one.ClipBoundaryVertices.Count);
                        return Finish(result, watch);
                    }

                    var bounds = TextBounds.ForWipeout(one);
                    if (Math.Abs(bounds.Width - 100) > 1e-3 || Math.Abs(bounds.Height - 50) > 1e-3)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = string.Format("包围盒不一致：回读 {0}（应为 100×50）", bounds);
                        return Finish(result, watch);
                    }

                    result.Status = ProbeStatus.Passed;
                    result.Detail = notices.Count == 0
                        ? "构造 → 写出 → 回读一致（裁剪 2 点，包围盒 100×50）"
                        : string.Format("回读一致，但写出时有 {0} 条告警", notices.Count);
                    return Finish(result, watch);
                }
                catch (Exception ex)
                {
                    result.Status = ProbeStatus.Failed;
                    result.Detail = ex.GetType().Name + "：" + ex.Message;
                    return Finish(result, watch);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // 临时文件删不掉不影响结论
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Table

        /// <summary>
        /// 表格单元格写回：需要一张**含 ACAD_TABLE 的样图**。
        /// 没给样图就返回"未测"，而不是假装通过——样本图里没有表格，这是事实。
        /// </summary>
        public static ProbeResult TableCellWriter(string samplePath)
        {
            var result = new ProbeResult
            {
                Key = "table-cell",
                Title = "表格单元格写回",
                Affects = "TextReplace（ACAD_TABLE）"
            };

            if (string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath))
            {
                result.Status = ProbeStatus.NotTested;
                result.Detail = "未提供含 ACAD_TABLE 的样图，表格单元格读写未验证（相关规则会跳过表格并计入跳过）";
                return result;
            }

            lock (SyncRoot)
            {
                ProbeResult cached;
                if (Cache.TryGetValue("table-cell:" + samplePath, out cached))
                {
                    return cached;
                }
            }

            var watch = Stopwatch.StartNew();
            string dir = Path.Combine(Path.GetTempPath(), "jun-cad-batch-probe");
            string path = Path.Combine(dir, "table-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".dwg");

            try
            {
                Directory.CreateDirectory(dir);
                CadDocument doc = DwgReader.Read(samplePath);

                TableEntity table = FindTable(doc);
                if (table == null)
                {
                    result.Status = ProbeStatus.NotTested;
                    result.Detail = "样图里没有 ACAD_TABLE";
                    return Finish(result, watch);
                }

                TableEntity.Cell cell = table.GetCell(0, 0);
                if (cell.Content == null)
                {
                    result.Status = ProbeStatus.Failed;
                    result.Detail = "单元格 [0,0] 没有单一内容（可能是合并单元格或多内容格）";
                    return Finish(result, watch);
                }

                string before = cell.Content.CadValue == null ? null : Convert.ToString(cell.Content.CadValue.Value);
                cell.Content.CadValue.SetValue("JUN-PROBE");

                DwgWriter.Write(path, doc, null, null);
                CadDocument back = DwgReader.Read(path);
                TableEntity backTable = FindTable(back);
                if (backTable == null)
                {
                    result.Status = ProbeStatus.Failed;
                    result.Detail = "回读后找不到表格";
                    return Finish(result, watch);
                }

                TableEntity.Cell backCell = backTable.GetCell(0, 0);
                string after = backCell.Content == null || backCell.Content.CadValue == null
                    ? null
                    : Convert.ToString(backCell.Content.CadValue.Value);

                if (!string.Equals(after, "JUN-PROBE", StringComparison.Ordinal))
                {
                    result.Status = ProbeStatus.Failed;
                    result.Detail = string.Format("写回未生效：写入 JUN-PROBE，回读 [{0}]（原值 [{1}]）", after, before);
                    return Finish(result, watch);
                }

                result.Status = ProbeStatus.Passed;
                result.Detail = string.Format("单元格 [0,0] 写回并回读成功（原值 [{0}]）", before);
                return Finish(result, watch);
            }
            catch (Exception ex)
            {
                result.Status = ProbeStatus.Failed;
                result.Detail = ex.GetType().Name + "：" + ex.Message;
                return Finish(result, watch);
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }

        private static TableEntity FindTable(CadDocument doc)
        {
            foreach (TableEntity table in doc.ModelSpace.Entities.OfType<TableEntity>())
            {
                return table;
            }

            foreach (var block in doc.BlockRecords)
            {
                if (block.Layout == null)
                {
                    continue;
                }

                foreach (TableEntity table in block.Entities.OfType<TableEntity>())
                {
                    return table;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ Dimension

        /// <summary>
        /// 标注文字遮罩：纯文件模式下**只能是静态**（没有反应器，标注一改就错位）。
        /// 这不是失败，是能力边界——状态固定为"降级"，让 UI 直接显示出来。
        /// </summary>
        public static ProbeResult DimensionMask()
        {
            return new ProbeResult
            {
                Key = "dimension-mask",
                Title = "标注文字遮罩",
                Status = ProbeStatus.Degraded,
                Affects = "TextMask（DIMENSION）",
                Detail = "纯文件模式无反应器：遮罩为静态，不随标注联动；标注被编辑后需重跑遮罩规则"
            };
        }

        private static ProbeResult Finish(ProbeResult result, Stopwatch watch)
        {
            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;

            // 只缓存"有确定结论"的结果：未测的结果下次可能因为给了样图而变成通过
            if (result.Status != ProbeStatus.NotTested && result.Key == "wipeout-writer")
            {
                lock (SyncRoot)
                {
                    Cache["wipeout-writer"] = result;
                }
            }

            return result;
        }
    }
}
