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
    /// 图层标准映射：把一批"旧图层名"按映射表并到标准层上。
    ///
    /// 与 `EntityLayerChange` 的分工：那条规则一次只能把一个目标层写死；
    /// 整改图层标准时往往是"十几条旧层名 → 三四个标准层"，用一条规则一次搞定。
    ///
    /// 映射写法：`旧名模式=新层名`，旧名支持通配符（`*` `?`）。
    ///   `"0-PIPE*=PIPE"`、`"TEXT-*="`（目标为空表示沿用匹配到的名字，用于只做统计）。
    ///
    /// 安全默认：
    ///   * `0` / `Defpoints` / 当前层 永远不动；
    ///   * `LayerMapDeleteEmpty` 默认 **false**——只并层、不删层。删层是不可逆的，
    ///     要删得显式打开，而且只删"并完之后真的没人用"的层。
    /// </summary>
    public sealed class LayerMapRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "LayerMap"; }
        }

        public void Execute(RuleContext context, RuleExecutionResult result)
        {
            RuleAction action = context.Action;
            CadDocument doc = context.Document;

            List<KeyValuePair<string, string>> mappings = ParseMappings(action.LayerMappings);
            if (mappings.Count == 0)
            {
                throw new RuleValidationException(
                    "Action.LayerMappings 为空。写法：\"旧层名模式=新层名\"，多条用分号或列表分隔。");
            }

            // 1) 先按图层表算出"旧层 → 新层"的映射（同名/受保护层跳过）
            var layerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();

            foreach (KeyValuePair<string, string> mapping in mappings)
            {
                int matched = 0;

                foreach (Layer layer in doc.Layers)
                {
                    if (layer == null || string.IsNullOrEmpty(layer.Name))
                    {
                        continue;
                    }

                    if (!Wildcard.IsMatch(layer.Name, mapping.Key))
                    {
                        continue;
                    }

                    if (IsProtected(layer.Name))
                    {
                        continue;
                    }

                    if (string.Equals(layer.Name, mapping.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    layerMap[layer.Name] = mapping.Value;
                    matched++;
                }

                if (matched == 0)
                {
                    notes.Add(string.Format("映射「{0}」没有匹配到任何图层。", mapping.Key));
                }
            }

            if (layerMap.Count == 0)
            {
                result.AddNote("没有任何图层命中映射表（检查旧层名模式是否写对）。");
                foreach (string note in notes)
                {
                    result.AddNote(note);
                }

                return;
            }

            // 2) 目标图层按需创建
            var targets = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in layerMap)
            {
                if (targets.ContainsKey(pair.Value))
                {
                    continue;
                }

                Layer target = LayerService.EnsureLayer(doc, pair.Value, action, context.Log, context.Resources);
                if (target == null)
                {
                    throw new RuleValidationException("目标图层「" + pair.Value + "」不存在且无法创建。");
                }

                targets[pair.Value] = target;
            }

            // 3) 一次遍历改完所有实体（模型 + 布局 + 块定义内部）
            //    块内实体自带图层，不跟着块参照走，所以必须进去改。
            int changed = 0;
            var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (ScanHit hit in DatabaseWalker.Collect(
                doc, ScanScope.ModelAndLayouts, context.MaxNestingDepth, true, null))
            {
                Entity entity = hit.Entity;
                if (entity == null || entity.Layer == null)
                {
                    continue;
                }

                string current = entity.Layer.Name;

                int count;
                remaining.TryGetValue(current, out count);
                remaining[current] = count + 1;

                string targetName;
                if (!layerMap.TryGetValue(current, out targetName))
                {
                    continue;
                }

                result.Hit();
                entity.Layer = targets[targetName];
                changed++;
                result.MarkChanged("图层 " + current + " → " + targetName);

                context.RecordChange(entity, "图层映射",
                    current, targetName, false,
                    string.Format("按映射「{0}」并入", current),
                    EntityScanner.SpaceNameOf(hit));
            }

            // 4) 可选：删掉已经没人用的源图层
            bool deleteEmpty = RuleContext.Bool(action.LayerMapDeleteEmpty, false);
            int deleted = 0;

            if (deleteEmpty)
            {
                var sources = new HashSet<string>(layerMap.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (string source in sources)
                {
                    int used;
                    remaining.TryGetValue(source, out used);
                    if (used > 0 || IsProtected(source))
                    {
                        continue;
                    }

                    try
                    {
                        // Layers.Remove 返回被删掉的 Layer（不是 bool），null 表示没删到
                        if (doc.Layers.Remove(source) != null)
                        {
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add(string.Format("删除图层「{0}」失败：{1}", source, ex.Message));
                    }
                }
            }

            result.AddNote(string.Format(
                "图层映射：{0} 条规则，改动 {1} 个实体，删除空源层 {2} 个。",
                layerMap.Count, changed, deleted));

            foreach (string note in notes)
            {
                result.AddNote(note);
            }

            if (!deleteEmpty)
            {
                result.AddNote("LayerMapDeleteEmpty=false：只并层、不删层（源图层留在图层表里）。");
            }
        }

        /// <summary>解析 <c>"旧=新"</c> 列表（每项一条，也允许用分号塞多条）。</summary>
        public static List<KeyValuePair<string, string>> ParseMappings(List<string> raw)
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

        private static bool IsProtected(string layerName)
        {
            return string.Equals(layerName, "0", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(layerName, "Defpoints", StringComparison.OrdinalIgnoreCase);
        }
    }
}
