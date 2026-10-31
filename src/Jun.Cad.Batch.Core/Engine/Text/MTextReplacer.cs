using System;
using System.Collections.Generic;
using System.Text;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// MTEXT 内容替换：**只在纯文本 token 上替换**，格式码原样保留。
    ///
    /// 三个必须遵守的规则：
    ///   1. 匹配不跨 token。跨了就是跨格式码边界（例如要找的文字被 <c>\H2.5x;</c> 劈成两半），
    ///      这时记 <c>Skipped (format boundary)</c>，**不猜也不拼**；
    ///   2. 替换文本要转义（<c>\</c>、<c>{</c>、<c>}</c>），否则写回去会把 MTEXT 结构破坏掉；
    ///   3. 替换后为空默认拒绝（避免批量把文字清空），由调用方显式打开。
    /// </summary>
    public static class MTextReplacer
    {
        public static bool TryReplace(
            string value,
            TextMatcher matcher,
            bool allowEmptyResult,
            out string result,
            out string reason)
        {
            result = value;
            reason = null;

            if (string.IsNullOrEmpty(value) || matcher == null)
            {
                return false;
            }

            List<MTextToken> tokens = MTextTokenizer.Tokenize(value);
            if (tokens.Count == 0)
            {
                return false;
            }

            var parts = new List<string>(tokens.Count);
            bool changed = false;
            int i = 0;

            while (i < tokens.Count)
            {
                if (tokens[i].Kind != MTextTokenKind.Text)
                {
                    parts.Add(tokens[i].Raw);
                    i++;
                    continue;
                }

                // 收集连续的文字 token，合成一段"可匹配文本"
                int start = i;
                var run = new StringBuilder();
                while (i < tokens.Count && tokens[i].Kind == MTextTokenKind.Text)
                {
                    run.Append(tokens[i].Text);
                    i++;
                }

                int end = i;
                string decoded = run.ToString();
                string replaced;

                if (!matcher.TryReplaceAll(decoded, out replaced))
                {
                    for (int k = start; k < end; k++)
                    {
                        parts.Add(tokens[k].Raw);
                    }

                    continue;
                }

                if (!allowEmptyResult && string.IsNullOrWhiteSpace(replaced))
                {
                    reason = "替换后为空，已跳过（Action.AllowEmptyResult=false）";
                    return false;
                }

                // 整段替换成一个文字 token：段内本来就没有格式码，替换不会破坏结构
                parts.Add(MTextTokenizer.EscapeReplacement(replaced));
                changed = true;
            }

            if (!changed)
            {
                // 没改，但整段明文能匹配上 → 说明匹配被格式码劈开了
                string plain = MTextTokenizer.ToPlainText(value);
                if (matcher.Matches(plain))
                {
                    reason = "format boundary：匹配跨越格式码，已跳过";
                }

                return false;
            }

            result = string.Concat(parts);
            return true;
        }
    }
}
