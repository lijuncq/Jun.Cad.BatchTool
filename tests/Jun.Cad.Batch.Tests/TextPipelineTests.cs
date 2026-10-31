using System;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// MTEXT 分词器。
    ///
    /// 为什么必须自己写：`MText.PlainText` 会把格式码碎片留在结果里
    /// （`{\l\f...;\T1;CONNECT TO}` → `lT1;CONNECT TO`），拿它当匹配源会替换到错误位置。
    /// 下面第一条用例就是这个现象的回归。
    /// </summary>
    public class MTextTokenizerTests
    {
        private const string Sample = @"{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P\l\fISOCPEUR|b0|i0;\T1;P30 MODULE}";

        [Fact]
        public void 明文里不残留格式码碎片()
        {
            string plain = MTextTokenizer.ToPlainText(Sample);

            Assert.DoesNotContain("ISOCPEUR", plain);
            Assert.DoesNotContain("T1;", plain);
            Assert.Contains("CONNECT TO", plain);
            Assert.Contains("P30 MODULE", plain);
        }

        [Fact]
        public void 段落符切成两行()
        {
            var lines = MTextTokenizer.ToLines(Sample);

            Assert.Equal(2, lines.Count);
            Assert.Equal("CONNECT TO", lines[0]);
            Assert.Equal("P30 MODULE", lines[1]);
        }

        [Fact]
        public void 格式码被识别为Format而不是文字()
        {
            var tokens = MTextTokenizer.Tokenize(@"\H2.5x;AB");

            Assert.Equal(MTextTokenKind.Format, tokens[0].Kind);
            Assert.Equal(@"\H2.5x;", tokens[0].Raw);
            Assert.Equal(MTextTokenKind.Text, tokens[1].Kind);
            Assert.Equal("AB", tokens[1].Text);
        }

        [Fact]
        public void 对齐码与字距码都算格式码()
        {
            var tokens = MTextTokenizer.Tokenize(@"\A1;4890");

            Assert.Equal(MTextTokenKind.Format, tokens[0].Kind);
            Assert.Equal(@"\A1;", tokens[0].Raw);
            Assert.Equal("4890", tokens[1].Text);
        }

        [Fact]
        public void 转义的花括号与反斜杠算文字()
        {
            var tokens = MTextTokenizer.Tokenize(@"a\{b\}c\\d");

            string plain = MTextTokenizer.ToPlainText(@"a\{b\}c\\d");
            Assert.Equal("a{b}c\\d", plain);
            Assert.All(tokens, t => Assert.Equal(MTextTokenKind.Text, t.Kind));
        }

        [Fact]
        public void 不换行空格解码成空格但保留原文()
        {
            var tokens = MTextTokenizer.Tokenize(@"A\~B");

            Assert.Equal(3, tokens.Count);
            Assert.Equal(@"\~", tokens[1].Raw);
            Assert.Equal(" ", tokens[1].Text);
            Assert.Equal("A B", MTextTokenizer.ToPlainText(@"A\~B"));
        }

        [Fact]
        public void 替换文本里的特殊字符会被转义()
        {
            Assert.Equal(@"a\\b\{c\}", MTextTokenizer.EscapeReplacement(@"a\b{c}"));
        }

        [Fact]
        public void 空内容不炸()
        {
            Assert.Empty(MTextTokenizer.Tokenize(null));
            Assert.Empty(MTextTokenizer.Tokenize(""));
            Assert.Equal(string.Empty, MTextTokenizer.ToPlainText(null));
        }
    }

    /// <summary>文字匹配：四种模式 + 大小写 + 中文降级。</summary>
    public class TextMatcherTests
    {
        private static TextMatcher Create(string mode, string pattern, string replacement, bool caseSensitive = false)
        {
            string error;
            TextMatcher matcher = TextMatcher.Create(mode, pattern, replacement, caseSensitive, out error);
            Assert.Null(error);
            return matcher;
        }

        [Fact]
        public void 包含模式替换所有出现()
        {
            string output;
            Assert.True(Create("Contains", "P30", "P40").TryReplaceAll("P30 A P30", out output));
            Assert.Equal("P40 A P40", output);
        }

        [Fact]
        public void 默认不区分大小写()
        {
            string output;
            Assert.True(Create("Contains", "abc", "X").TryReplaceAll("ABC", out output));
            Assert.Equal("X", output);
        }

        [Fact]
        public void 区分大小写时匹配不到()
        {
            string output;
            Assert.False(Create("Contains", "abc", "X", true).TryReplaceAll("ABC", out output));
            Assert.Equal("ABC", output);
        }

        [Fact]
        public void 整段相等模式()
        {
            string output;
            Assert.True(Create("Exact", "AB", "X").TryReplaceAll("AB", out output));
            Assert.Equal("X", output);

            Assert.False(Create("Exact", "AB", "X").TryReplaceAll("ABC", out output));
        }

        [Fact]
        public void 全词模式不匹配词内片段()
        {
            string output;
            Assert.False(Create("WholeWord", "P30", "X").TryReplaceAll("P30A", out output));
            Assert.True(Create("WholeWord", "P30", "X").TryReplaceAll("A P30 B", out output));
        }

        /// <summary>
        /// 中文没有空格分词，`\b` 在汉字之间永远不成立——必须降级成"包含"并告警，
        /// 否则用户配了全词会一个都匹配不上。
        /// </summary>
        [Fact]
        public void 全词模式遇到中文降级为包含()
        {
            TextMatcher matcher = Create("WholeWord", "管道", "管线");

            Assert.True(matcher.WholeWordDegraded);

            string output;
            Assert.True(matcher.TryReplaceAll("主管道布置", out output));
            Assert.Equal("主管线布置", output);
        }

        [Fact]
        public void 正则模式支持捕获组()
        {
            string output;
            Assert.True(Create("Regex", @"P(\d+)", "P4$1").TryReplaceAll("P30", out output));
            Assert.Equal("P430", output);
        }

        [Fact]
        public void 非法正则返回错误而不是抛异常()
        {
            string error;
            Assert.Null(TextMatcher.Create("Regex", "(", "x", false, out error));
            Assert.Contains("正则表达式无效", error);
        }

        [Fact]
        public void 空查找内容返回错误()
        {
            string error;
            Assert.Null(TextMatcher.Create("Contains", "", "x", false, out error));
            Assert.NotNull(error);
        }
    }

    /// <summary>
    /// MTEXT 替换：格式码不能被动，跨格式码的匹配要记"跳过"而不是硬拼。
    /// </summary>
    public class MTextReplacerTests
    {
        private const string Sample = @"{\l\fISOCPEUR|b0|i0;\T1;CONNECT TO\P\l\fISOCPEUR|b0|i0;\T1;P30 MODULE}";

        private static TextMatcher Matcher(string pattern, string replacement)
        {
            string error;
            return TextMatcher.Create("Contains", pattern, replacement, false, out error);
        }

        [Fact]
        public void 只换纯文本格式码一个不动()
        {
            string result;
            string reason;

            Assert.True(MTextReplacer.TryReplace(Sample, Matcher("P30", "P40"), false, out result, out reason));

            Assert.Contains(@"\fISOCPEUR|b0|i0;", result);
            Assert.Contains(@"\T1;", result);
            Assert.Contains(@"\P", result);
            Assert.Contains("P40 MODULE", result);
            Assert.DoesNotContain("P30", result);
        }

        [Fact]
        public void 替换后为空默认拒绝()
        {
            string result;
            string reason;

            Assert.False(MTextReplacer.TryReplace(Sample, Matcher("CONNECT TO", ""), false, out result, out reason));
            Assert.Contains("替换后为空", reason);
        }

        [Fact]
        public void 允许为空时才真的清空()
        {
            string result;
            string reason;

            Assert.True(MTextReplacer.TryReplace("AB", Matcher("AB", ""), true, out result, out reason));
            Assert.Equal(string.Empty, result);
        }

        /// <summary>
        /// 要替换的文字被格式码劈成两半时，必须记 format boundary，不能拼起来硬换。
        /// </summary>
        [Fact]
        public void 跨格式码边界的匹配记为跳过()
        {
            // 明文是 "ABCD"，但 A 与 BCD 之间插了一个字体码
            string value = @"A\H2.5x;BCD";

            string result;
            string reason;

            Assert.False(MTextReplacer.TryReplace(value, Matcher("ABCD", "X"), false, out result, out reason));
            Assert.Contains("format boundary", reason);
        }

        [Fact]
        public void 没有匹配时不改内容()
        {
            string result;
            string reason;

            Assert.False(MTextReplacer.TryReplace(Sample, Matcher("不存在的字", "X"), false, out result, out reason));
            Assert.Equal(Sample, result);
        }
    }
}
