using System;
using System.Collections.Generic;
using System.Globalization;
using ACadSharp;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 线宽取值的唯一真源（单位 0.01mm，与 DXF 组码 370 一致）。
    ///
    /// 为什么需要这个类：线宽在 DWG 里不是任意整数，而是一组固定取值
    /// （0/5/9/13/15/18/20/25/30/35/40/50/53/60/70/80/90/100/106/120/140/158/200/211）
    /// 加 3 个符号值（-1 随层 / -2 随块 / -3 默认）。
    /// 配置里写一个表外的值（例如 1）时，AutoCAD 版会抛 eInvalidInput 让整条规则中止；
    /// 离线版没有内核校验，**如果不主动拦，脏值会被原样写进 DWG**，
    /// 打开图纸时表现成"线宽莫名变成默认"——更难排查。所以判定必须在写盘前做。
    ///
    /// 本类不引用任何 CAD 类型之外的东西，测试工程可以直接跑。
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

        /// <summary>
        /// 数值 → ACadSharp 的 <see cref="LineWeightType"/>。
        ///
        /// 刻意用显式映射而不是强转：枚举的底层数值在库升级时可能调整，
        /// 强转一旦对不上就会静默写出错误线宽（而且不会报错）。
        /// 非法值返回 false，由调用方决定是回退还是报错。
        /// </summary>
        public static bool TryToLineWeightType(int value, out LineWeightType result)
        {
            switch (value)
            {
                case -3: result = LineWeightType.Default; return true;
                case -2: result = LineWeightType.ByBlock; return true;
                case -1: result = LineWeightType.ByLayer; return true;
                case 0: result = LineWeightType.W0; return true;
                case 5: result = LineWeightType.W5; return true;
                case 9: result = LineWeightType.W9; return true;
                case 13: result = LineWeightType.W13; return true;
                case 15: result = LineWeightType.W15; return true;
                case 18: result = LineWeightType.W18; return true;
                case 20: result = LineWeightType.W20; return true;
                case 25: result = LineWeightType.W25; return true;
                case 30: result = LineWeightType.W30; return true;
                case 35: result = LineWeightType.W35; return true;
                case 40: result = LineWeightType.W40; return true;
                case 50: result = LineWeightType.W50; return true;
                case 53: result = LineWeightType.W53; return true;
                case 60: result = LineWeightType.W60; return true;
                case 70: result = LineWeightType.W70; return true;
                case 80: result = LineWeightType.W80; return true;
                case 90: result = LineWeightType.W90; return true;
                case 100: result = LineWeightType.W100; return true;
                case 106: result = LineWeightType.W106; return true;
                case 120: result = LineWeightType.W120; return true;
                case 140: result = LineWeightType.W140; return true;
                case 158: result = LineWeightType.W158; return true;
                case 200: result = LineWeightType.W200; return true;
                case 211: result = LineWeightType.W211; return true;
                default: result = LineWeightType.Default; return false;
            }
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
