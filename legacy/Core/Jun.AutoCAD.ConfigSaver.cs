using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>保存结果，供界面提示用。</summary>
    public sealed class ConfigSaveResult
    {
        /// <summary>文件里原本是否带注释。</summary>
        public bool HadComments { get; set; }

        /// <summary>保存后注释是否仍在。</summary>
        public bool CommentsPreserved { get; set; }

        /// <summary>是否退化成"整体重写"（极端排版下才会发生）。</summary>
        public bool UsedFullRewrite { get; set; }

        /// <summary>
        /// Rules 数组内部的注释是否已被备份到 <c>config.json.rules.bak</c>。
        /// 整段替换数组必然丢掉数组内的注释，写盘前把它们单独存一份，供人工取回。
        /// </summary>
        public bool RulesCommentsBackedUp { get; set; }

        /// <summary>实际写入的路径。</summary>
        public string Path { get; set; }

        /// <summary>备份文件路径。</summary>
        public string BackupPath { get; set; }
    }

    /// <summary>
    /// 把界面上的设置写回 config.json，**只改动界面能编辑的那几个字段**。
    ///
    /// 为什么不用"反序列化 → 序列化"：那会把文件里的 // 注释、字段顺序、用户自定义字段全部抹掉，
    /// 而 config.json 恰恰是给人手改的，注释就是它的说明书。
    ///
    /// 做法：用 Newtonsoft 带行号地解析一遍（LineInfoHandling.Load），拿到每个待改字段所在的**行号**，
    /// 然后只替换/插入那几行文本。写完再整体解析校验一次，解析不过就退化为整体重写（并明确告知用户）。
    /// </summary>
    public static class ConfigSaver
    {
        private static readonly string[] ScalarFields = { "ExecutionMode", "ContinueOnError", "Verbose", "LogFile" };

        public static ConfigSaveResult SavePatched(string path, BatchConfig config)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("配置路径为空。", "path");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("配置文件不存在：" + path, path);
            }

            string original = File.ReadAllText(path);
            string newline = original.Contains("\r\n") ? "\r\n" : "\n";
            bool hadComments = original.Contains("//") || original.Contains("/*");

            var result = new ConfigSaveResult
            {
                Path = path,
                HadComments = hadComments,
                CommentsPreserved = hadComments
            };

            string output = TryPatchInPlace(original, config, newline);
            if (output == null)
            {
                // 排版过于特殊（比如整个对象写在一行），退化为整体重写
                output = JsonConvert.SerializeObject(config, Formatting.Indented);
                result.UsedFullRewrite = true;
                result.CommentsPreserved = false;
            }

            string backup = path + ".bak";
            try
            {
                File.Copy(path, backup, true);
                result.BackupPath = backup;
            }
            catch
            {
                // 备份失败不阻断保存
            }

            File.WriteAllText(path, output, new UTF8Encoding(false));
            return result;
        }

        /// <summary>
        /// 把整个 <c>Rules</c> 数组写回文件，数组之外的文本（含 <c>//</c> 注释）原样保留。
        ///
        /// 与 <see cref="SavePatched"/> 的分工：
        ///   * SavePatched 只改"界面能编辑的那几个标量 + 每条规则的 Enabled"——按行号打补丁就够；
        ///   * 规则编辑页改的是整条规则（名称/过滤条件/操作参数，以及增删和排序），
        ///     按行打补丁表达不了，必须整段替换 Rules 数组。
        /// 替换时保留 "Rules" 行的缩进，数组外的一切内容（注释、字段顺序、自定义字段）不动。
        /// 只有括号配对扫描失败时才退化为整体重写，并在结果里明确标记。
        /// </summary>
        public static ConfigSaveResult SaveRules(string path, BatchConfig config)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("配置路径为空。", "path");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("配置文件不存在：" + path, path);
            }

            string original = File.ReadAllText(path);
            string newline = original.Contains("\r\n") ? "\r\n" : "\n";
            bool hadComments = original.Contains("//") || original.Contains("/*");

            // Rules 数组内部的注释：整段替换必然丢掉，先看清楚有没有，有就单独备份
            string rulesText = ExtractRulesText(original);
            bool rulesHadComments = HasComment(rulesText);

            var result = new ConfigSaveResult
            {
                Path = path,
                HadComments = hadComments,
                // 只有"文件里有注释，且这些注释不在 Rules 数组里"才谈得上保留
                CommentsPreserved = hadComments && !rulesHadComments
            };

            string output = TryReplaceRulesArray(original, config, newline);
            if (output == null)
            {
                output = JsonConvert.SerializeObject(config, Formatting.Indented);
                result.UsedFullRewrite = true;
                result.CommentsPreserved = false;
            }

            // 数组内的注释：备份原文，让人能取回。只在确实有注释时才写，避免多一个无意义文件。
            if (rulesHadComments)
            {
                try
                {
                    File.WriteAllText(path + ".rules.bak", rulesText, new UTF8Encoding(false));
                    result.RulesCommentsBackedUp = true;
                }
                catch
                {
                    // 备份失败不阻断保存
                }
            }

            string backup = path + ".bak";
            try
            {
                File.Copy(path, backup, true);
                result.BackupPath = backup;
            }
            catch
            {
                // 备份失败不阻断保存
            }

            File.WriteAllText(path, output, new UTF8Encoding(false));
            return result;
        }

        /// <summary>整段替换 Rules 数组；无法安全定位时返回 null（调用方走整体重写）。</summary>
        private static string TryReplaceRulesArray(string text, BatchConfig config, string newline)
        {
            int start, end, propLineNumber;
            if (!TryLocateRulesArray(text, out start, out end, out propLineNumber))
            {
                return null;
            }

            // "Rules" 所在行的缩进，替换后按同样的缩进排版
            var lineStarts = LineStarts(text);
            if (propLineNumber < 1 || propLineNumber > lineStarts.Count)
            {
                return null;
            }

            int propLineStart = lineStarts[propLineNumber - 1];
            int propLineEnd = text.IndexOf('\n', propLineStart);
            if (propLineEnd < 0)
            {
                propLineEnd = text.Length;
            }

            string propLine = text.Substring(propLineStart, propLineEnd - propLineStart);
            string indent = propLine.Substring(0, propLine.Length - propLine.TrimStart().Length);

            List<RuleConfig> rules = config.Rules ?? new List<RuleConfig>();
            // 与界面「规则 JSON」用同一套精简设置：null / 空集合不落盘，
            // 写回的 config.json 与界面看到的内容一致（读回语义不变）。
            string serialized = RuleJson.Serialize(rules)
                .Replace("\r\n", "\n");

            var sb = new StringBuilder();
            string[] parts = serialized.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    // 首行顶掉原来的 "["（跟在 "Rules": 后面），其余行按原缩进补齐
                    sb.Append(indent);
                }

                sb.Append(parts[i]);
                if (i < parts.Length - 1)
                {
                    sb.Append(newline);
                }
            }

            string output = text.Substring(0, start) + sb + text.Substring(end + 1);

            // 兜底校验：改完必须是合法 JSON
            try
            {
                JToken.Parse(output);
            }
            catch
            {
                return null;
            }

            return output;
        }

        /// <summary>
        /// 定位 <c>"Rules": [ ... ]</c> 在原文里的字符区间（含方括号）。
        /// 行号信息来自 Newtonsoft 的 <see cref="IJsonLineInfo"/>，括号配对由
        /// <see cref="FindMatchingBracket"/> 完成（会跳过字符串与注释）。
        /// </summary>
        private static bool TryLocateRulesArray(string text, out int start, out int end, out int propLineNumber)
        {
            start = -1;
            end = -1;
            propLineNumber = -1;

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Load
                    });
                }
            }
            catch
            {
                return false;
            }

            JProperty rulesProp = root.Property("Rules");
            JArray rulesArray = rulesProp == null ? null : rulesProp.Value as JArray;
            if (rulesArray == null)
            {
                return false;
            }

            IJsonLineInfo arrayInfo = rulesArray as IJsonLineInfo;
            IJsonLineInfo propInfo = rulesProp as IJsonLineInfo;
            if (arrayInfo == null || propInfo == null ||
                !arrayInfo.HasLineInfo() || !propInfo.HasLineInfo())
            {
                return false;
            }

            var lineStarts = LineStarts(text);
            if (arrayInfo.LineNumber < 1 || arrayInfo.LineNumber > lineStarts.Count)
            {
                return false;
            }

            start = lineStarts[arrayInfo.LineNumber - 1] + arrayInfo.LinePosition - 1;
            if (start < 0 || start >= text.Length || text[start] != '[')
            {
                return false;
            }

            end = FindMatchingBracket(text, start);
            if (end < 0)
            {
                return false;
            }

            propLineNumber = propInfo.LineNumber;
            return true;
        }

        /// <summary>取出原文里 Rules 数组的文本（含方括号）；定位失败返回空串。</summary>
        private static string ExtractRulesText(string text)
        {
            int start, end, propLineNumber;
            if (!TryLocateRulesArray(text, out start, out end, out propLineNumber))
            {
                return string.Empty;
            }

            return text.Substring(start, end - start + 1);
        }

        /// <summary>文本里是否含 JSON 注释（<c>//</c> 或 <c>/* */</c>）。</summary>
        private static bool HasComment(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.Contains("//") || text.Contains("/*");
        }

        /// <summary>行首偏移表；LineNumber 是 1 基，LinePosition 也是 1 基。</summary>
        private static List<int> LineStarts(string text)        {
            var lineStarts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineStarts.Add(i + 1);
                }
            }

            return lineStarts;
        }

        /// <summary>
        /// 从 <paramref name="openIndex"/> 处的 <c>[</c> 找到配对的 <c>]</c>。
        /// 字符串字面量与注释里的括号不参与计数（配置文件里两者都可能出现）。
        /// </summary>
        private static int FindMatchingBracket(string text, int openIndex)
        {
            int depth = 0;
            bool inString = false;

            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        int nl = text.IndexOf('\n', i);
                        if (nl < 0)
                        {
                            return -1;
                        }

                        i = nl;
                        continue;
                    }

                    if (text[i + 1] == '*')
                    {
                        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            return -1;
                        }

                        i = close + 1;
                        continue;
                    }
                }

                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// 按行号打补丁。成功返回新文本；无法安全打补丁时返回 null。
        /// </summary>
        private static string TryPatchInPlace(string text, BatchConfig config, string newline)
        {
            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Load
                    });
                }
            }
            catch
            {
                return null;
            }

            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            var edits = new List<LineEdit>();

            // ---- 顶层标量 ----
            PatchScalar(lines, edits, root, "ExecutionMode", JsonConvert.ToString(config.ExecutionMode ?? "Sequential"));
            PatchScalar(lines, edits, root, "ContinueOnError", config.ContinueOnError ? "true" : "false");
            PatchScalar(lines, edits, root, "Verbose", config.Verbose ? "true" : "false");
            PatchScalar(lines, edits, root, "LogFile", JsonConvert.ToString(config.LogFile ?? string.Empty));

            // ---- 每条规则的 Enabled ----
            var rulesArray = root["Rules"] as JArray;
            if (rulesArray != null)
            {
                int count = Math.Min(rulesArray.Count, config.Rules.Count);
                for (int i = 0; i < count; i++)
                {
                    var item = rulesArray[i] as JObject;
                    if (item == null || config.Rules[i] == null)
                    {
                        continue;
                    }

                    string value = config.Rules[i].Enabled ? "true" : "false";
                    JProperty enabledProp = item.Property("Enabled");

                    if (enabledProp != null)
                    {
                        if (!TryPatchBooleanLine(lines, edits, enabledProp, value))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        // 没有 Enabled 字段：插到 "Type" 那一行后面（沿用同样的缩进）
                        JProperty typeProp = item.Property("Type") ?? item.Property("Name");
                        if (!TryInsertPropertyLine(lines, edits, typeProp, "\"Enabled\": " + value + ","))
                        {
                            return null;
                        }
                    }
                }
            }

            if (edits.Count == 0)
            {
                return text;
            }

            // 从后往前改，避免行号位移
            edits.Sort((a, b) => b.Line.CompareTo(a.Line));
            foreach (LineEdit edit in edits)
            {
                if (edit.InsertAfter)
                {
                    lines.Insert(edit.Line + 1, edit.Text);
                }
                else
                {
                    lines[edit.Line] = edit.Text;
                }
            }

            string output = string.Join(newline, lines.ToArray());

            // 兜底校验：改完必须是合法 JSON，否则让调用方走整体重写
            try
            {
                JToken.Parse(output);
            }
            catch
            {
                return null;
            }

            return output;
        }

        private static void PatchScalar(List<string> lines, List<LineEdit> edits, JObject root, string propertyName, string jsonValue)
        {
            JProperty prop = root.Property(propertyName);
            if (prop == null)
            {
                // 顶层缺这个字段：不主动插入（保持文件结构不变），由整体重写兜底
                return;
            }

            int lineIndex;
            if (!TryGetLineIndex(prop, out lineIndex) || lineIndex < 0 || lineIndex >= lines.Count)
            {
                return;
            }

            string pattern = "^(\\s*\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*)(?:\"(?:[^\"\\\\]|\\\\.)*\"|true|false|null|-?[0-9.eE+-]+)(\\s*,?\\s*(?://.*)?)$";
            var match = Regex.Match(lines[lineIndex], pattern);
            if (!match.Success)
            {
                return;
            }

            edits.Add(new LineEdit
            {
                Line = lineIndex,
                Text = match.Groups[1].Value + jsonValue + match.Groups[2].Value
            });
        }

        private static bool TryPatchBooleanLine(List<string> lines, List<LineEdit> edits, JProperty prop, string jsonValue)
        {
            int lineIndex;
            if (!TryGetLineIndex(prop, out lineIndex) || lineIndex < 0 || lineIndex >= lines.Count)
            {
                return false;
            }

            string pattern = "^(\\s*\"Enabled\"\\s*:\\s*)(true|false)(\\s*,?\\s*(?://.*)?)$";
            var match = Regex.Match(lines[lineIndex], pattern);
            if (!match.Success)
            {
                return false;
            }

            edits.Add(new LineEdit
            {
                Line = lineIndex,
                Text = match.Groups[1].Value + jsonValue + match.Groups[3].Value
            });
            return true;
        }

        private static bool TryInsertPropertyLine(List<string> lines, List<LineEdit> edits, JProperty anchor, string propertyText)
        {
            if (anchor == null)
            {
                return false;
            }

            int lineIndex;
            if (!TryGetLineIndex(anchor, out lineIndex) || lineIndex < 0 || lineIndex >= lines.Count)
            {
                return false;
            }

            string line = lines[lineIndex];
            int indentLength = 0;
            while (indentLength < line.Length && (line[indentLength] == ' ' || line[indentLength] == '\t'))
            {
                indentLength++;
            }

            // 锚点行必须"独占一行"，否则插入会破坏结构
            if (line.IndexOf('{') >= 0 || line.IndexOf('}') >= 0)
            {
                return false;
            }

            edits.Add(new LineEdit
            {
                Line = lineIndex,
                InsertAfter = true,
                Text = line.Substring(0, indentLength) + propertyText
            });
            return true;
        }

        private static bool TryGetLineIndex(JToken token, out int lineIndex)
        {
            lineIndex = -1;
            var info = token as IJsonLineInfo;
            if (info == null || !info.HasLineInfo())
            {
                return false;
            }

            lineIndex = info.LineNumber - 1;
            return lineIndex >= 0;
        }

        private sealed class LineEdit
        {
            public int Line;
            public string Text;
            public bool InsertAfter;
        }
    }
}
