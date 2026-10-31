using System.Text;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 等宽文本的列对齐工具。
    ///
    /// 为什么需要它：<c>string.Format("{0,-28}", …)</c> 按**字符数**补空格，而控制台/等宽字体里
    /// 一个汉字占两列。规则名几乎都是中文，于是"规则 / 匹配 / 成功…"那张汇总表在中文行上整体右移，
    /// 看上去像没对齐——不是字体问题，是补空格的算法错了。
    ///
    /// 这里按**显示列宽**补空格：全角（CJK、全角标点、假名、谚文）算 2 列，其余算 1 列。
    /// 只处理 BMP 内的常见全角区间，够用且不引入 System.Globalization 的 EastAsianWidth 依赖
    /// （.NET 没有公开该 API）。
    /// </summary>
    public static class TextColumns
    {
        /// <summary>字符串在等宽字体下占用的列数。</summary>
        public static int Width(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int width = 0;
            foreach (char c in text)
            {
                width += IsWide(c) ? 2 : 1;
            }

            return width;
        }

        /// <summary>
        /// 截断到指定显示列宽。超宽时保留末尾的省略号（…），避免把汉字切成半个。
        /// </summary>
        public static string Truncate(string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return string.Empty;
            }

            if (Width(text) <= maxWidth)
            {
                return text;
            }

            var sb = new StringBuilder();
            int used = 0;
            foreach (char c in text)
            {
                int w = IsWide(c) ? 2 : 1;

                // 留 1 列给省略号
                if (used + w > maxWidth - 1)
                {
                    break;
                }

                sb.Append(c);
                used += w;
            }

            sb.Append('…');
            return sb.ToString();
        }

        /// <summary>按显示列宽左/右对齐。超宽内容先截断再补空格，保证列不会错位。</summary>
        public static string Pad(string text, int width, bool alignRight)
        {
            string body = Truncate(text ?? string.Empty, width);
            int pad = width - Width(body);
            if (pad <= 0)
            {
                return body;
            }

            string spaces = new string(' ', pad);
            return alignRight ? spaces + body : body + spaces;
        }

        /// <summary>全角判定：CJK 汉字、假名、谚文、全角标点与全角字母数字。</summary>
        private static bool IsWide(char c)
        {
            return (c >= 0x1100 && c <= 0x115F)   // 谚文字母
                || (c >= 0x2E80 && c <= 0x303E)   // CJK 部首、标点
                || (c >= 0x3041 && c <= 0x33FF)   // 假名、注音、CJK 兼容
                || (c >= 0x3400 && c <= 0x4DBF)   // CJK 扩展 A
                || (c >= 0x4E00 && c <= 0x9FFF)   // CJK 基本区
                || (c >= 0xA000 && c <= 0xA4CF)   // 彝文
                || (c >= 0xAC00 && c <= 0xD7A3)   // 谚文音节
                || (c >= 0xF900 && c <= 0xFAFF)   // CJK 兼容汉字
                || (c >= 0xFE30 && c <= 0xFE6F)   // CJK 兼容形式
                || (c >= 0xFF00 && c <= 0xFF60)   // 全角 ASCII
                || (c >= 0xFFE0 && c <= 0xFFE6);  // 全角符号
        }
    }
}
