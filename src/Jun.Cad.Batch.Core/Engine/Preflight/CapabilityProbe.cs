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

        /// <summary>Probe failed; callers must report the failure, not assume a mask fallback.</summary>
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
    /// 能力探测：在真正改图之前，用**临时文档**检查 Wipeout 的序列化和几何往返。
    ///
    /// 当前项目引用本地修复版 ACadSharp：探测检查多边形顶点、裁剪开关、零影像引用和 WCS 包围盒。
    /// 这可以捕获 API/序列化回归，但本身不代表出图渲染验证。AutoCAD 2025 Core Console
    /// 已另行确认修复版输出被识别为 WIPEOUT；视觉遮罩效果仍应以宿主出图验收为准。
    /// 本探测仍非宿主渲染验收；生产路径的逐实体自检在 MaskRuntime.CreateWipeout。
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
        /// Wipeout 写出链路：构造矩形掩膜 → 写盘 → 回读 → 检查剪裁几何和 WCS 包围盒。
        /// 通过表示当前 ACadSharp writer round-trip 一致；宿主渲染仍以 AutoCAD 出图验收为准。
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

                    Wipeout wipeout = MaskService.CreateWipeout(new Bounds2d(0, 0, 100, 50));
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
                    if (!one.ClippingState || one.ClipBoundaryVertices.Count != 4)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = string.Format("剪裁状态或多边形顶点错误（clipping={0}, vertices={1}; 应为开启、4 点）",
                            one.ClippingState, one.ClipBoundaryVertices.Count);
                        return Finish(result, watch);
                    }

                    if (one.Definition != null)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = "WIPEOUT 错误地关联了 IMAGEDEF；其影像定义句柄必须为 0";
                        return Finish(result, watch);
                    }

                    var bounds = TextBounds.ForWipeout(one);
                    if (Math.Abs(bounds.MinX) > 1e-3 || Math.Abs(bounds.MinY) > 1e-3
                        || Math.Abs(bounds.Width - 100) > 1e-3 || Math.Abs(bounds.Height - 50) > 1e-3)
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = string.Format("WCS 包围盒不一致：回读 {0}（应为 (0,0)-(100,50)）", bounds);
                        return Finish(result, watch);
                    }

                    // 与生产路径共用同一个判据：MaskRuntime.CreateWipeout 对每个 Wipeout
                    // 都调 IsWipeoutEncoded。probe 额外的轴对齐/包围盒断言不代表生产保证。
                    string reason;
                    if (!MaskService.IsWipeoutEncoded(one, out reason))
                    {
                        result.Status = ProbeStatus.Failed;
                        result.Detail = "Wipeout 编码自检失败：" + reason;
                        return Finish(result, watch);
                    }

                    result.Status = ProbeStatus.Passed;
                    result.Detail = notices.Count == 0
                        ? "DWG 往返一致（4 点多边形、clipping on、无 IMAGEDEF、WCS 包围盒 100×50）；宿主渲染另行验收"
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
        /// Dimension masks are DIMTFILL=1 style overrides (DSTYLE XData): native, no new entities,
        /// and they survive dimension regeneration. The remaining limitation is informational —
        /// geometry drawn above the dimension can still cover the text background.
        /// </summary>
        public static ProbeResult DimensionMask()
        {
            return new ProbeResult
            {
                Key = "dimension-mask",
                Title = "标注文字遮罩",
                Status = ProbeStatus.Degraded,
                Affects = "TextMask（DIMENSION）",
                Detail = "DIMTFILL=1 样式覆盖（图形背景色），随标注重生成保留；但画在标注之上的几何仍会盖住文字背景"
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
