using System;
using System.Collections.Generic;
using System.Text;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>MTEXT 里的一个片段。</summary>
    public enum MTextTokenKind
    {
        /// <summary>可显示文字（已经解码：<c>\\</c> → <c>\</c>、<c>\~</c> → 空格）。</summary>
        Text,

        /// <summary>格式码（<c>\P</c>、<c>\H2.5x;</c>、<c>\fArial|b0|i0;</c>、<c>\A1;</c> …）。</summary>
        Format,

        GroupStart,

        GroupEnd
    }

    /// <summary>
    /// MTEXT 的一个 token。
    ///
    /// <see cref="Raw"/> 是原文切片，写回时原样输出；<see cref="Text"/> 只对
    /// <see cref="MTextTokenKind.Text"/> 有意义。
    /// </summary>
    public sealed class MTextToken
    {
        public MTextToken(MTextTokenKind kind, string raw, string text)
        {
            Kind = kind;
            Raw = raw ?? string.Empty;
            Text = text;
        }

        public MTextTokenKind Kind { get; private set; }

        public string Raw { get; private set; }

        public string Text { get; private set; }

        public override string ToString()
        {
            return Kind + ":" + Raw;
        }
    }

    /// <summary>
    /// MTEXT 内容的分词器。
    ///
    /// 为什么必须自己写：
    ///   * <c>MText.PlainText</c> 不能用。实测
    ///     <c>{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P...}</c> 的 <c>PlainText</c> 是
    ///     <c>lT1;CONNECT TO</c>——<c>\l</c> 的 <c>l</c> 和 <c>\T1;</c> 的 <c>T1;</c> 都留在结果里。
    ///     拿它做"查找替换"的匹配源，会替换到错误位置，而且写回时无处安放；
    ///   * 库没有格式码解析器/生成器，<c>Value</c> 只是"带码的原始串"。
    ///
    /// 因此本类只做一件事：把原始串切成 [文字] / [格式码] / [分组] 三类 token，
    /// 文字替换只允许落在 <see cref="MTextTokenKind.Text"/> 上。
    /// </summary>
    public static class MTextTokenizer
    {
        /// <summary>
        /// 需要读到 <c>;</c> 才结束的格式码首字母。
        ///
        /// 小写 <c>f</c>（内联字体 <c>\fArial|b0|i0;</c>）与 <c>p</c>（段落属性 <c>\pxi-3.5,l0;</c>）
        /// 必须在这里——漏掉的话 <c>\f</c> 会被当成单字符格式码，后面的
        /// <c>ISOCPEUR|b0|i0;</c> 全部变成"文字"，替换时就会被误改。
        /// </summary>
        private const string SemicolonCodes = "ACFHQSTWXachqstwfp";

        /// <summary>不需要参数的单字符格式码。</summary>
        private const string SingleCodes = "LlOoKkNn";

        public static List<MTextToken> Tokenize(string value)
        {
            var tokens = new List<MTextToken>();
            if (string.IsNullOrEmpty(value))
            {
                return tokens;
            }

            var buffer = new StringBuilder();
            int i = 0;

            while (i < value.Length)
            {
                char c = value[i];

                if (c == '{')
                {
                    Flush(tokens, buffer);
                    tokens.Add(new MTextToken(MTextTokenKind.GroupStart, "{", null));
                    i++;
                    continue;
                }

                if (c == '}')
                {
                    Flush(tokens, buffer);
                    tokens.Add(new MTextToken(MTextTokenKind.GroupEnd, "}", null));
                    i++;
                    continue;
                }

                if (c != '\\')
                {
                    buffer.Append(c);
                    i++;
                    continue;
                }

                // ---- 反斜杠转义 ----
                if (i + 1 >= value.Length)
                {
                    buffer.Append(c);
                    i++;
                    continue;
                }

                char next = value[i + 1];

                switch (next)
                {
                    case '\\':
                        buffer.Append('\\');
                        i += 2;
                        continue;
                    case '{':
                        buffer.Append('{');
                        i += 2;
                        continue;
                    case '}':
                        buffer.Append('}');
                        i += 2;
                        continue;
                    case '~':
                        // 不换行空格：解码成空格，但保留原文（Raw），未替换时原样写回
                        Flush(tokens, buffer);
                        tokens.Add(new MTextToken(MTextTokenKind.Text, "\\~", " "));
                        i += 2;
                        continue;
                    case 'P':
                        Flush(tokens, buffer);
                        tokens.Add(new MTextToken(MTextTokenKind.Format, "\\P", null));
                        i += 2;
                        continue;
                }

                if (SemicolonCodes.IndexOf(next) >= 0)
                {
                    // \A1;  \H2.5x;  \fArial|b0|i0;  \S1/2;  —— 读到分号
                    int end = value.IndexOf(';', i + 2);
                    if (end < 0)
                    {
                        // 没有分号：按"读到结尾"处理，宁可整体当格式码也不要把半截当文字
                        end = value.Length - 1;
                    }

                    Flush(tokens, buffer);
                    tokens.Add(new MTextToken(MTextTokenKind.Format, value.Substring(i, end - i + 1), null));
                    i = end + 1;
                    continue;
                }

                if (SingleCodes.IndexOf(next) >= 0)
                {
                    Flush(tokens, buffer);
                    tokens.Add(new MTextToken(MTextTokenKind.Format, value.Substring(i, 2), null));
                    i += 2;
                    continue;
                }

                // 其它（\U+XXXX 这类）：整段当格式码，避免把它当文字参与替换
                Flush(tokens, buffer);
                tokens.Add(new MTextToken(MTextTokenKind.Format, value.Substring(i, 2), null));
                i += 2;
            }

            Flush(tokens, buffer);
            return tokens;
        }

        private static void Flush(List<MTextToken> tokens, StringBuilder buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            string raw = buffer.ToString();
            tokens.Add(new MTextToken(MTextTokenKind.Text, raw, raw));
            buffer.Length = 0;
        }

        /// <summary>解码成可显示文本：格式码丢弃，<c>\P</c> → 换行。</summary>
        public static string ToPlainText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (MTextToken token in Tokenize(value))
            {
                switch (token.Kind)
                {
                    case MTextTokenKind.Text:
                        sb.Append(token.Text);
                        break;
                    case MTextTokenKind.Format:
                        if (token.Raw == "\\P")
                        {
                            sb.Append('\n');
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>按 <c>\P</c> 切行（用于宽度估算与"整行替换"）。</summary>
        public static List<string> ToLines(string value)
        {
            var lines = new List<string>();
            var current = new StringBuilder();

            foreach (MTextToken token in Tokenize(value))
            {
                if (token.Kind == MTextTokenKind.Text)
                {
                    current.Append(token.Text);
                }
                else if (token.Kind == MTextTokenKind.Format && token.Raw == "\\P")
                {
                    lines.Add(current.ToString());
                    current.Length = 0;
                }
            }

            lines.Add(current.ToString());
            return lines;
        }

        /// <summary>把替换文本里的 MTEXT 特殊字符转义，保证写回去不会破坏结构。</summary>
        public static string EscapeReplacement(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                if (c == '\\' || c == '{' || c == '}')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
