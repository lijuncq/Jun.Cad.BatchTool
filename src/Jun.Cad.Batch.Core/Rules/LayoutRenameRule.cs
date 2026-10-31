using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;

namespace Jun.Cad.Batch.Rules
{
    /// <summary>
    /// 布局改名：按映射表把 `Layout1`、`布局1` 这类名字改成规范名（`V-01`、`A1-平面`）。
    ///
    /// 可行性已实测：<c>Layout.Name</c> 写进 DWG 后能稳定往返（连写两次都不丢）。
    /// 但有两个坑必须挡住：
    ///   * **模型空间也是"布局"**——`*Model_Space` 的 `Layout.Name` 就是 `"Model"`，
    ///     改名列表里必须排除它，否则整张图的模型空间会被改名；
    ///   * **重名**——AutoCAD 不允许两个布局同名，遇到重名要拒绝而不是覆盖。
    ///
    /// 新名支持两个占位符：`{name}` 原名、`{n}` 两位序号（`V-{n}` → `V-01`）。
    /// </summary>
    public sealed class LayoutRenameRule : IRuleHandler
    {
        /// <summary>AutoCAD 布局名不允许的字符。</summary>
        private const string InvalidChars = "<>/\\\":;?*|,=";

        public string RuleType
        {
            get { return "LayoutRename"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;
            CadDocument doc = context.Document;

            List<KeyValuePair<string, string>> mappings = ParseMap(action.LayoutRenameMap);
            if (mappings.Count == 0)
            {
                throw new RuleValidationException(
                    "Action.LayoutRenameMap 为空。写法：\"旧名模式=新名\"，例如 \"Layout*=V-{n}\"。");
            }

            // 现有布局名（用于重名检测），模型空间单独排除
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new List<BlockRecord>();

            foreach (BlockRecord block in doc.BlockRecords)
            {
                if (block == null || block.Layout == null)
                {
                    continue;
                }

                if (ReferenceEquals(block, doc.ModelSpace)
                    || string.Equals(block.Layout.Name, "Model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                taken.Add(block.Layout.Name);
                targets.Add(block);
            }

            if (targets.Count == 0)
            {
                result.AddNote("这张图没有图纸空间布局，无需改名。");
                return;
            }

            string syncTag = action.LayoutRenameSyncTag;
            int index = 0;
            int renamed = 0;

            // {n} 按"每条映射各自编号"：V-{n} 与 S-{n} 各自从 01 开始，
            // 而不是全图共用一个计数器（否则 S 组会从 V 组停下的号继续）
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (BlockRecord block in targets)
            {
                index++;
                string current = block.Layout.Name;
                string template = null;

                string matchedKey = null;
                foreach (KeyValuePair<string, string> mapping in mappings)
                {
                    if (Wildcard.IsMatch(current, mapping.Key))
                    {
                        template = mapping.Value;
                        matchedKey = mapping.Key;
                        break;
                    }
                }

                result.Hit();

                if (template == null)
                {
                    result.MarkSkipped("不匹配任何映射");
                    continue;
                }

                int counter;
                counters.TryGetValue(matchedKey, out counter);
                counter++;
                counters[matchedKey] = counter;

                string desired = Format(template, current, counter);

                if (string.Equals(desired, current, StringComparison.Ordinal))
                {
                    result.MarkSkipped("已是目标名");
                    continue;
                }

                string problem = ValidateName(desired);
                if (problem != null)
                {
                    result.MarkSkipped(problem);
                    context.RecordBlocked(block.BlockEntity, "布局改名",
                        string.Format("「{0}」→「{1}」被拒绝：{2}", current, desired, problem));
                    continue;
                }

                // 重名检测：去掉自己之后再判断
                taken.Remove(current);
                if (taken.Contains(desired))
                {
                    taken.Add(current);
                    result.MarkSkipped("目标名已被占用");
                    context.RecordBlocked(block.BlockEntity, "布局改名",
                        string.Format("「{0}」→「{1}」被拒绝：已有同名布局", current, desired));
                    continue;
                }

                block.Layout.Name = desired;
                taken.Add(desired);
                renamed++;

                result.MarkChanged("布局 " + current + " → " + desired);
                context.RecordChange(block.BlockEntity, "布局改名", current, desired, false,
                    "Layout.Name 写入（实测可往返）");

                if (!string.IsNullOrWhiteSpace(syncTag))
                {
                    SyncTitleBlock(context, result, block, syncTag.Trim(), desired);
                }
            }

            result.AddNote(string.Format("布局改名：扫描 {0} 个布局，改名 {1} 个。", targets.Count, renamed));
        }

        /// <summary>把新布局名写进该布局里图框块的指定属性（可选）。</summary>
        private static void SyncTitleBlock(
            RuleContext context, RuleExecutionResult result, BlockRecord block, string tag, string value)
        {
            foreach (Entity entity in block.Entities)
            {
                var insert = entity as Insert;
                if (insert == null || insert is TableEntity || insert.Attributes == null)
                {
                    continue;
                }

                foreach (AttributeEntity attribute in insert.Attributes)
                {
                    if (attribute == null
                        || !string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(attribute.Value, value, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string before = attribute.Value;
                    attribute.Value = value;
                    result.MarkChanged("属性 " + tag);
                    context.RecordChange(attribute, "同步图框属性",
                        tag + "=" + (before ?? string.Empty), tag + "=" + value);
                }
            }
        }

        /// <summary>展开 <c>{name}</c> / <c>{n}</c> / <c>{N}</c>。</summary>
        public static string Format(string template, string originalName, int index)
        {
            if (string.IsNullOrEmpty(template))
            {
                return originalName;
            }

            return template
                .Replace("{name}", originalName)
                .Replace("{n}", index.ToString("00"))
                .Replace("{N}", index.ToString());
        }

        /// <summary>返回 null 表示合法，否则返回原因。</summary>
        public static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "名字为空";
            }

            if (name.Length > 255)
            {
                return "名字超过 255 字符";
            }

            foreach (char c in name)
            {
                if (InvalidChars.IndexOf(c) >= 0)
                {
                    return "含非法字符「" + c + "」";
                }
            }

            return null;
        }

        public static List<KeyValuePair<string, string>> ParseMap(List<string> raw)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (raw == null)
            {
                return list;
            }

            foreach (string item in raw)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                foreach (string part in item.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int index = part.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    string source = part.Substring(0, index).Trim();
                    string target = part.Substring(index + 1).Trim();
                    if (source.Length > 0 && target.Length > 0)
                    {
                        list.Add(new KeyValuePair<string, string>(source, target));
                    }
                }
            }

            return list;
        }
    }
}
