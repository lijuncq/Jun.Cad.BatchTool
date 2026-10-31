using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 线宽取值的唯一真源（单位 0.01mm，与 DXF 组码 370 一致）。
    ///
    /// 为什么需要这个类：AutoCAD 的 `LineWeight` 是**枚举**，只有 24 个具体线宽
    /// （0/5/9/13/15/18/20/25/30/35/40/50/53/60/70/80/90/100/106/120/140/158/200/211）
    /// 加 4 个符号值（-1 随层 / -2 随块 / -3 默认 / -4 按 DIPs）。
    /// 直接把任意整数强转成 `LineWeight` 再赋值，宿主会抛 `eInvalidInput`
    /// （2026-10-29 实测：配置里写 `"LayerLineWeight": 1` 导致整条规则以 eInvalidInput 中止）。
    /// 所以"哪些值合法"必须由这里统一判定，配置校验、图层创建、实体线宽三处共用。
    ///
    /// 本类**不引用任何宿主类型**，可以在没有 AutoCAD 的测试进程里跑。
    /// </summary>
    public static class LineWeightUtil
    {
        /// <summary>实体可用的线宽值：三个符号值 + 24 个具体线宽。</summary>
        public static readonly int[] EntityValues =
        {
            -3, -2, -1, 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211
        };

        /// <summary>图层可用的线宽值：只能是具体线宽，不能用随层/随块/默认。</summary>
        public static readonly int[] LayerValues =
        {
            0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211
        };

        /// <summary>实体线宽是否合法（含 -1 随层 / -2 随块 / -3 默认）。</summary>
        public static bool IsValidEntityValue(int value)
        {
            return Array.IndexOf(EntityValues, value) >= 0;
        }

        /// <summary>图层线宽是否合法（必须是具体线宽）。</summary>
        public static bool IsValidLayerValue(int value)
        {
            return Array.IndexOf(LayerValues, value) >= 0;
        }

        /// <summary>
        /// 找最接近的合法图层线宽，用于把"你写的值不合法"翻译成"你大概想要哪个"。
        /// 空集合时返回 25（AutoCAD 图层默认 0.25mm）。
        /// </summary>
        public static int NearestLayerValue(int value)
        {
            int best = 25;
            int bestDistance = int.MaxValue;
            foreach (int candidate in LayerValues)
            {
                int distance = Math.Abs(candidate - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>把线宽值翻译成界面/日志里的可读文字，例如 <c>25 → "0.25mm"</c>。</summary>
        public static string Describe(int value)
        {
            switch (value)
            {
                case -1:
                    return "随层(ByLayer)";
                case -2:
                    return "随块(ByBlock)";
                case -3:
                    return "默认(Default)";
                case -4:
                    return "按DIPs(ByDIPs)";
                default:
                    return string.Format(CultureInfo.InvariantCulture, "{0:0.00}mm", value / 100.0);
            }
        }

        /// <summary>实体合法值清单（写进错误提示）。</summary>
        public static string EntityValuesText
        {
            get { return "-1/-2/-3（随层/随块/默认）、" + LayerValuesText; }
        }

        /// <summary>图层合法值清单（写进错误提示）。</summary>
        public static string LayerValuesText
        {
            get { return string.Join("/", Array.ConvertAll(LayerValues, v => v.ToString(CultureInfo.InvariantCulture))); }
        }

        /// <summary>生成 <c>RuleFieldAttribute.Values</c> 用的候选串（值=显示名）。</summary>
        public static string BuildOptionList(bool includeSymbolic)
        {
            var parts = new List<string>();
            if (includeSymbolic)
            {
                parts.Add("-1=随层 (ByLayer)");
                parts.Add("-2=随块 (ByBlock)");
                parts.Add("-3=默认 (Default)");
            }

            foreach (int value in LayerValues)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1:0.00}mm", value, value / 100.0));
            }

            return string.Join(";", parts.ToArray());
        }
    }
}
