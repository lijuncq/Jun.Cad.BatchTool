using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>图形测量单位（对应 AutoCAD 的 <c>$MEASUREMENT</c>）策略。</summary>
    public enum MeasurementPolicy
    {
        /// <summary>只在"写出去的图案名在英制图案库里不存在"时才改成公制（默认）。</summary>
        Auto,

        /// <summary>不动。</summary>
        Keep,

        /// <summary>强制公制。</summary>
        Metric,

        /// <summary>强制英制。</summary>
        English
    }

    /// <summary>
    /// 保证"写进 DWG 的填充图案名，AutoCAD 打开时认得出"。
    ///
    /// 问题现象（用户反馈）：转换后的图打开后，属性面板的预定义图案列表里找不到
    /// <c>JIS_LC_8A</c>，虽然填充本身画得出来。
    ///
    /// 根因（AutoCAD 2025 内核 accoreconsole 实测，不是推测）：
    ///   * 图形的 <c>$MEASUREMENT</c> 决定按哪份图案库解析名字：
    ///     0 = <c>acad.pat</c>（英制），1 = <c>acadiso.pat</c>（公制）；
    ///   * 样例图纸的 <c>$MEASUREMENT</c> 是 0，而 <c>JIS_LC_8A</c> 只在 acadiso.pat 里；
    ///   * 于是 <c>(setvar "HPNAME" "JIS_LC_8A")</c> 报"无效的图案名称"，
    ///     改成 1 之后同一句立刻成功。
    ///
    /// 为什么改 <c>$MEASUREMENT</c> 而不是改 AutoCAD 的 .pat 文件：
    ///   * 本工具用的图案库就是 acadiso.pat（Resources 里那份），保证"我们写出去的名字
    ///     都能被解析"的唯一自洽前提就是图形也用 acadiso.pat；
    ///   * 改图内标志随文件走，换机器、换 AutoCAD 版本、发给别人都不会失效；
    ///     往 <c>acad.pat</c> 里追加内容只在当前这台机器当前版本有效，还要写系统目录。
    ///
    /// 只改"填充图案解析"相关的这个标志，不动 <c>INSUNITS</c>、不动已有实体的任何几何。
    /// </summary>
    public static class DrawingUnits
    {
        /// <summary>解析配置里的策略字符串（大小写不敏感，认中文）。</summary>
        public static MeasurementPolicy ParsePolicy(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return MeasurementPolicy.Auto;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "KEEP":
                case "NONE":
                case "保持":
                case "不改":
                    return MeasurementPolicy.Keep;

                case "METRIC":
                case "ISO":
                case "公制":
                    return MeasurementPolicy.Metric;

                case "ENGLISH":
                case "IMPERIAL":
                case "英制":
                    return MeasurementPolicy.English;

                default:
                    return MeasurementPolicy.Auto;
            }
        }

        public static string DescribePolicy(MeasurementPolicy policy)
        {
            switch (policy)
            {
                case MeasurementPolicy.Keep: return "保持（不改 $MEASUREMENT）";
                case MeasurementPolicy.Metric: return "强制公制（acadiso.pat）";
                case MeasurementPolicy.English: return "强制英制（acad.pat）";
                default: return "自动（图案名在英制库里不存在时才改公制）";
            }
        }

        /// <summary>当前图形是不是公制图案库（<c>$MEASUREMENT == 1</c>）。</summary>
        public static bool IsMetric(CadDocument doc)
        {
            return doc != null && doc.Header.MeasurementUnits == MeasurementUnits.Metric;
        }

        /// <summary>
        /// 找出图形里"只有公制图案库认得"的图案名（去重、升序）。
        /// 覆盖模型空间、全部布局、块定义内部——块里的填充一样要能解析。
        /// </summary>
        public static List<string> FindMetricOnlyPatterns(CadDocument doc, ResourceLocator resources)
        {
            var names = new List<string>();
            if (doc == null || resources == null)
            {
                return names;
            }

            List<ScanHit> hits = DatabaseWalker.Collect(
                doc, ScanScope.ModelAndLayouts, 32, true, e => e is Hatch);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScanHit hit in hits)
            {
                var hatch = hit.Entity as Hatch;
                if (hatch == null || hatch.IsSolid || hatch.Pattern == null)
                {
                    continue;
                }

                if (hatch.GradientColor != null && hatch.GradientColor.Enabled)
                {
                    continue;
                }

                string name = hatch.Pattern.Name;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name.Trim()))
                {
                    continue;
                }

                if (resources.RequiresMetric(name))
                {
                    names.Add(name.Trim());
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// 按策略修正 <c>$MEASUREMENT</c>。返回给用户看的一句话（没有动作时返回空串）。
        /// </summary>
        /// <param name="isPreview">
        /// 预演模式：不写盘，措辞要改成"将会……"，不能说得像已经落盘了。
        /// </param>
        /// <param name="checkPatterns">
        /// 配置里是否有填充图案规则。为 false 时 Auto 模式直接跳过——
        /// 这次运行没碰图案，就没有"我们写出去的名字"需要保证可解析，
        /// 也就不该为了别人的图纸白跑一遍全库遍历。
        /// Metric / English 是显式意图，不受这个开关影响。
        /// </param>
        public static string Apply(
            CadDocument doc, ResourceLocator resources, string policyText, bool isPreview, BatchLogger log,
            bool checkPatterns = true)
        {
            if (doc == null)
            {
                return string.Empty;
            }

            MeasurementPolicy policy = ParsePolicy(policyText);
            bool metric = IsMetric(doc);

            if (policy == MeasurementPolicy.Keep)
            {
                // 用户明确不让改：如果确实有图案会解析不出来，至少要说清楚后果
                if (!metric && checkPatterns)
                {
                    List<string> risky = FindMetricOnlyPatterns(doc, resources);
                    if (risky.Count > 0 && log != null)
                    {
                        log.Warn(string.Format(
                            "图案「{0}」只在 acadiso.pat 里存在，而本图 $MEASUREMENT=0（英制图案库）。"
                            + "AutoCAD 打开后填充画得出来，但图案名不在预定义列表里（属性面板改不回这个图案）。"
                            + "把配置 MeasurementUnits 设为 Auto 或 Metric 可自动修正。",
                            string.Join("、", risky.ToArray())));
                    }
                }

                return string.Empty;
            }

            if (policy == MeasurementPolicy.English)
            {
                if (doc.Header.MeasurementUnits == MeasurementUnits.English)
                {
                    return string.Empty;
                }

                doc.Header.MeasurementUnits = MeasurementUnits.English;
                string englishNote = string.Format(
                    "已把 $MEASUREMENT 设为 0（英制图案库 acad.pat）{0}。", isPreview ? "（预演，未写盘）" : string.Empty);

                if (log != null)
                {
                    log.Info(englishNote);
                }

                return englishNote;
            }

            // Metric / Auto 共用这段：先看需不需要动
            if (policy == MeasurementPolicy.Auto && !checkPatterns)
            {
                return string.Empty;
            }

            List<string> metricOnly = metric ? new List<string>() : FindMetricOnlyPatterns(doc, resources);

            if (policy == MeasurementPolicy.Auto && metricOnly.Count == 0)
            {
                return string.Empty;
            }

            if (metric)
            {
                return string.Empty;
            }

            doc.Header.MeasurementUnits = MeasurementUnits.Metric;

            string reason = metricOnly.Count > 0
                ? string.Format("图案「{0}」只存在于 acadiso.pat，而本图 $MEASUREMENT=0 会去 acad.pat 里找，"
                                + "AutoCAD 因此认不出这个图案名（填充能画出来，但属性面板的预定义列表里没有它）",
                                string.Join("、", metricOnly.ToArray()))
                : "按配置强制使用公制图案库";

            string note = string.Format(
                "已把 $MEASUREMENT 设为 1（公制图案库 acadiso.pat）{0}：{1}。",
                isPreview ? "（预演，未写盘）" : string.Empty,
                reason);

            if (log != null)
            {
                log.Info(note);
            }

            return note;
        }
    }
}
