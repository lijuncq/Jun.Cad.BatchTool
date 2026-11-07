using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Rules;

namespace Probe
{
    /// <summary>
    /// 在真实 DWG 上跑一遍 TextMask 规则，然后**以文字为基准**核对：
    /// 对每一个会被加遮罩的文字，规则生成的 Wipeout 是否落在"字体感知后的正确宽度"上，
    /// 而不是退化成旧的 0.75 常量高估。直接验证 2026-11-07「块内/标题栏文字遮罩太宽」返工。
    ///
    /// 做法：先算出每个文字"应当"产生的遮罩 AABB（与规则同口径），再到同块里找中心/尺寸都
    /// 吻合的 Wipeout——规则就是把遮罩建在文字位置上，所以必能精确对上。这样就不会像
    /// "以 Wipeout 找最近文字"那样把遮罩错配到旁边的短文字上。
    /// </summary>
    internal static class MaskVerify
    {
        public static void Run(string dwgPath)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (!File.Exists(dwgPath))
            {
                Console.WriteLine("文件不存在: " + dwgPath);
                return;
            }

            Console.WriteLine("读取: " + dwgPath);
            CadDocument doc;
            try
            {
                doc = DwgReader.Read(dwgPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("读取失败: " + ex.Message);
                return;
            }

            var action = new RuleAction
            {
                MaskMode = "Auto",
                MaskLayer = "_MASK"
            };
            var rule = new RuleConfig
            {
                Name = "遮罩",
                Type = "TextMask",
                Filter = new RuleFilter(),
                Action = action
            };
            var config = new BatchConfig { Rules = new List<RuleConfig> { rule } };

            RuleExecutionResult result;
            using (var log = new BatchLogger(TextWriter.Null, null, false))
            {
                var ctx = new RuleContext(doc, config, config.Rules[0], log, new ResourceLocator());
                result = new RuleExecutionResult(config.Rules[0].Name, config.Rules[0].Type);
                new TextMaskRule().Execute(ctx, result);
            }

            Console.WriteLine("规则结果: 匹配={0} 改动={1} 跳过={2} 失败={3}",
                result.Matched, result.Changed, result.Skipped, result.Failed);

            double padRatio = MaskService.DefaultPaddingRatio; // 0.15

            int matched = 0;          // 找到对应 Wipeout 的文字（被真实加遮罩的）
            int usesNew = 0;          // 遮罩宽 ≈ 新字体感知估算（修复生效）
            int usesOld = 0;          // 遮罩宽 ≈ 旧 0.75 高估（修复未生效，仍过宽）
            int widerThanText = 0;    // 遮罩宽明显 > 新字体感知估算（真·过宽，异常）
            int sampleShown = 0;

            foreach (BlockRecord block in doc.BlockRecords)
            {
                var wipeouts = block.Entities.OfType<Wipeout>().ToList();
                if (wipeouts.Count == 0)
                {
                    continue;
                }

                var texts = block.Entities
                    .Where(e => e is TextEntity || e is MText || e is AttributeEntity)
                    .ToList();

                foreach (Entity t in texts)
                {
                    double h = CharacterHeight(t);
                    double pad = h * padRatio;
                    XY[] v = TextBounds.TextMaskVertices(t, pad);
                    if (v == null)
                    {
                        continue;
                    }

                    Bounds2d expected = TextBounds.AabbOf(v);
                    if (expected.Width <= 0)
                    {
                        continue;
                    }

                    // 在同级块里找中心/尺寸都吻合的 Wipeout（规则把遮罩建在文字位置）
                    Wipeout hitW = null;
                    foreach (Wipeout w in wipeouts)
                    {
                        Bounds2d wb = TextBounds.ForWipeout(w);
                        if (wb.Width <= 0)
                        {
                            continue;
                        }

                        double cxE = (expected.MinX + expected.MaxX) / 2;
                        double cyE = (expected.MinY + expected.MaxY) / 2;
                        double cxW = (wb.MinX + wb.MaxX) / 2;
                        double cyW = (wb.MinY + wb.MaxY) / 2;
                        if (Math.Abs(cxE - cxW) < 0.05 && Math.Abs(cyE - cyW) < 0.05
                            && Math.Abs(wb.Width - expected.Width) < expected.Width * 0.05
                            && Math.Abs(wb.Height - expected.Height) < expected.Height * 0.05)
                        {
                            hitW = w;
                            break;
                        }
                    }

                    if (hitW == null)
                    {
                        continue; // 这个文字没有产生 Wipeout（多行 MTEXT 走原生背景等）
                    }

                    matched++;
                    Bounds2d wbox = TextBounds.ForWipeout(hitW);
                    int n = TextBounds.PlainTextOf(t).Length;
                    double newWidth = expected.Width;
                    double oldWidth = n * 0.75 * h + 2 * (h * padRatio); // 旧常量高估

                    bool nearNew = Math.Abs(wbox.Width - newWidth) <= newWidth * 0.06 + 1e-6;
                    bool nearOld = Math.Abs(wbox.Width - oldWidth) <= oldWidth * 0.06 + 1e-6;

                    if (nearOld && !nearNew)
                    {
                        usesOld++;
                    }
                    else if (nearNew)
                    {
                        usesNew++;
                    }

                    // 真·过宽：遮罩明显宽于"字体感知后的准确文字宽"——这才是用户投诉的现象。
                    // （注意：比"旧0.75"宽不叫过宽，因为旧常量对 W/X/F 等宽字母本就是低估。）
                    if (wbox.Width > newWidth * 1.06 + 1e-6)
                    {
                        widerThanText++;
                    }

                    if (sampleShown < 50)
                    {
                        string font = FontFileOf(t);
                        string plain = Clip(TextBounds.PlainTextOf(t), 26);
                        Console.WriteLine(
                            "  [{0}] 字体={1,-12} 字高={2:0.##} 文字='{3}' | 实际={4:0.##} 新估算={5:0.##} 旧0.75={6:0.##} [{7}]",
                            block.Name, font, h, plain, wbox.Width, newWidth, oldWidth,
                            nearOld && !nearNew ? "仍是旧0.75(过宽!)" : (nearNew ? "字体感知(正确)" : "偏差"));
                        sampleShown++;
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("== 汇总 ==");
            Console.WriteLine("  被真实加遮罩(找到对应Wipeout)的文字数: {0}", matched);
            Console.WriteLine("  其中遮罩宽 ≈ 新字体感知估算（修复生效）: {0}", usesNew);
            Console.WriteLine("  其中遮罩宽 ≈ 旧0.75高估（仍过宽，异常）: {0}", usesOld);
            Console.WriteLine("  其中遮罩明显宽于准确文字宽（真·过宽，异常）: {0}", widerThanText);
            Console.WriteLine(usesOld == 0 && widerThanText == 0
                ? "  => 通过：没有任何遮罩退化成旧常量高估或宽于文字本身，字体感知生效。"
                : "  => 仍有过宽遮罩，需排查。");
        }

        private static double CharacterHeight(Entity e)
        {
            var t = e as TextEntity;
            if (t != null) return t.Height > 0 ? t.Height : 2.5;
            var m = e as MText;
            if (m != null) return m.Height > 0 ? m.Height : 2.5;
            var a = e as AttributeEntity;
            if (a != null) return a.Height > 0 ? a.Height : 2.5;
            return 2.5;
        }

        private static string FontFileOf(Entity e)
        {
            TextStyle s = null;
            var t = e as TextEntity;
            if (t != null) s = t.Style;
            var m = e as MText;
            if (m != null) s = m.Style;
            var a = e as AttributeEntity;
            if (a != null) s = a.Style;
            if (s == null) return "(默认)";
            return string.IsNullOrEmpty(s.Filename) ? s.Name : s.Filename;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
