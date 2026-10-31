using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>文字匹配模式。</summary>
    public enum TextMatchMode
    {
        Exact,
        Contains,
        WholeWord,
        Regex
    }

    /// <summary>
    /// 文字匹配/替换的唯一实现（TEXT、MTEXT 纯文本 token、属性、表格单元格共用）。
    ///
    /// 为什么不用 <c>string.Replace</c> 一把梭：
    ///   * 需要大小写开关、词边界、正则三种语义；
    ///   * MTEXT 的替换必须在 token 上做（见 <see cref="MTextTokenizer"/>），
    ///     匹配逻辑要能被"单个纯文本片段"复用；
    ///   * 中文的"全词"没有意义——<c>\b</c> 在中日韩字符之间永远不成立，
    ///     所以检测到 CJK 时**降级为 Contains 并告警**，而不是悄悄一个都匹配不上。
    /// </summary>
    public sealed class TextMatcher
    {
        private readonly string _pattern;
        private readonly string _replacement;
        private readonly TextMatchMode _mode;
        private readonly bool _caseSensitive;
        private readonly Regex _regex;

        private TextMatcher(string pattern, string replacement, TextMatchMode mode, bool caseSensitive, Regex regex)
        {
            _pattern = pattern;
            _replacement = replacement ?? string.Empty;
            _mode = mode;
            _caseSensitive = caseSensitive;
            _regex = regex;
        }

        public TextMatchMode Mode
        {
            get { return _mode; }
        }

        /// <summary>WholeWord 因模式里含中文而降级成了 Contains。</summary>
        public bool WholeWordDegraded { get; private set; }

        public static TextMatcher Create(string modeText, string pattern, string replacement, bool caseSensitive, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(pattern))
            {
                error = "查找内容（Action.SearchText）不能为空。";
                return null;
            }

            TextMatchMode mode = ParseMode(modeText);
            Regex regex = null;

            if (mode == TextMatchMode.Regex)
            {
                try
                {
                    var options = RegexOptions.CultureInvariant;
                    if (!caseSensitive)
                    {
                        options |= RegexOptions.IgnoreCase;
                    }

                    // 超时是必须的：批量图里出现 (a+)+ 这类模式会把整批任务挂死
                    regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException ex)
                {
                    error = "正则表达式无效：" + ex.Message;
                    return null;
                }
            }

            var matcher = new TextMatcher(pattern, replacement, mode, caseSensitive, regex);

            if (mode == TextMatchMode.WholeWord && ContainsCjk(pattern))
            {
                matcher.WholeWordDegraded = true;
            }

            return matcher;
        }

        public static TextMatchMode ParseMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return TextMatchMode.Contains;
            }

            switch (text.Trim().ToUpperInvariant())
            {
                case "EXACT":
                case "EQUALS":
                    return TextMatchMode.Exact;
                case "WHOLEWORD":
                case "WHOLE_WORD":
                case "WORD":
                    return TextMatchMode.WholeWord;
                case "REGEX":
                case "RE":
                    return TextMatchMode.Regex;
                default:
                    return TextMatchMode.Contains;
            }
        }

        /// <summary>是否至少能匹配一次（用于"跨格式码边界"的判定）。</summary>
        public bool Matches(string input)
        {
            string ignored;
            return TryReplaceAll(input, out ignored);
        }

        /// <summary>替换所有出现。返回是否真的变了。</summary>
        public bool TryReplaceAll(string input, out string output)
        {
            output = input;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(_pattern))
            {
                return false;
            }

            switch (_mode)
            {
                case TextMatchMode.Exact:
                    if (string.Equals(input, _pattern, Comparison))
                    {
                        output = _replacement;
                        return true;
                    }

                    return false;

                case TextMatchMode.Regex:
                    try
                    {
                        if (!_regex.IsMatch(input))
                        {
                            return false;
                        }

                        output = _regex.Replace(input, _replacement);
                        return true;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }

                case TextMatchMode.WholeWord:
                    return ReplaceByScan(input, out output, !WholeWordDegraded);

                default:
                    return ReplaceByScan(input, out output, false);
            }
        }

        private StringComparison Comparison
        {
            get { return _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
        }

        private bool ReplaceByScan(string input, out string output, bool requireWordBoundary)
        {
            output = input;

            var sb = new StringBuilder(input.Length);
            int i = 0;
            bool changed = false;

            while (i <= input.Length - _pattern.Length)
            {
                if (string.Compare(input, i, _pattern, 0, _pattern.Length, Comparison) == 0
                    && (!requireWordBoundary || IsBoundary(input, i, i + _pattern.Length)))
                {
                    sb.Append(_replacement);
                    i += _pattern.Length;
                    changed = true;
                    continue;
                }

                sb.Append(input[i]);
                i++;
            }

            if (i < input.Length)
            {
                sb.Append(input.Substring(i));
            }

            if (changed)
            {
                output = sb.ToString();
            }

            return changed;
        }

        private static bool IsBoundary(string input, int start, int end)
        {
            bool leftOk = start == 0 || !IsWordChar(input[start - 1]);
            bool rightOk = end >= input.Length || !IsWordChar(input[end]);
            return leftOk && rightOk;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool ContainsCjk(string text)
        {
            foreach (char c in text)
            {
                if (TextBounds.IsWide(c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
