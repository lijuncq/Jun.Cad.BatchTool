using System;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 标注样式服务。
    ///
    /// 容易踩的边界（与 AutoCAD 版同一条）：**标注文字的文字样式不由 Dimension 实体控制**，
    /// 而是由它引用的 <see cref="DimensionStyle.Style"/> 决定。
    /// 所以"改标注文字字体"必须落到标注样式上，单改实体上的字样对标注无效。
    /// </summary>
    public static class DimStyleService
    {
        /// <summary>
        /// 确保标注样式存在；不存在时以 Standard（或表中第一个）为模板克隆一个。
        /// </summary>
        /// <param name="created">是否本次新建（用于日志/统计）。</param>
        public static DimensionStyle EnsureDimStyle(
            CadDocument doc,
            string styleName,
            RuleAction action,
            BatchLogger log,
            out bool created)
        {
            created = false;
            if (doc == null || string.IsNullOrWhiteSpace(styleName))
            {
                return null;
            }

            string name = styleName.Trim();

            DimensionStyle existing;
            if (doc.DimensionStyles.TryGetValue(name, out existing))
            {
                ApplyOverrides(existing, action, log);
                return existing;
            }

            bool createIfMissing = action == null || !action.CreateIfMissing.HasValue || action.CreateIfMissing.Value;
            if (!createIfMissing)
            {
                if (log != null)
                {
                    log.Warn(string.Format("标注样式「{0}」不存在，且 CreateIfMissing=false，已跳过。", name));
                }

                return null;
            }

            // 选模板：优先 Standard，否则取表中第一个
            DimensionStyle template = null;
            doc.DimensionStyles.TryGetValue("Standard", out template);
            if (template == null)
            {
                foreach (DimensionStyle s in doc.DimensionStyles)
                {
                    template = s;
                    break;
                }
            }

            if (template == null)
            {
                if (log != null)
                {
                    log.Warn("标注样式表为空，无法创建新标注样式。");
                }

                return null;
            }

            DimensionStyle clone;
            try
            {
                clone = template.Clone() as DimensionStyle;
            }
            catch (Exception ex)
            {
                throw new RuleValidationException(string.Format(
                    "以「{0}」为模板克隆标注样式「{1}」失败：{2}", template.Name, name, ex.Message), ex);
            }

            if (clone == null)
            {
                throw new RuleValidationException(string.Format(
                    "以「{0}」为模板克隆标注样式「{1}」失败：克隆结果类型不符。", template.Name, name));
            }

            clone.Name = name;
            ApplyOverrides(clone, action, log);

            try
            {
                doc.DimensionStyles.Add(clone);
            }
            catch (Exception ex)
            {
                throw new RuleValidationException(string.Format("创建标注样式「{0}」失败：{1}", name, ex.Message), ex);
            }

            created = true;
            if (log != null)
            {
                log.Info(string.Format("已基于「{0}」创建标注样式「{1}」。", template.Name, name));
            }

            return clone;
        }

        /// <summary>同步箭头大小、文字高度等常用参数。</summary>
        public static void ApplyOverrides(DimensionStyle style, RuleAction action, BatchLogger log)
        {
            if (style == null || action == null)
            {
                return;
            }

            if (action.ArrowSize.HasValue && action.ArrowSize.Value > 0)
            {
                style.ArrowSize = action.ArrowSize.Value;
            }

            if (action.TextHeight.HasValue && action.TextHeight.Value > 0)
            {
                style.TextHeight = action.TextHeight.Value;
            }
        }

        /// <summary>把标注样式的文字样式指向目标文字样式（标注文字字体的唯一正确入口）。</summary>
        public static bool SetTextStyle(DimensionStyle style, TextStyle textStyle, BatchLogger log)
        {
            if (style == null || textStyle == null)
            {
                return false;
            }

            if (ReferenceEquals(style.Style, textStyle))
            {
                return false;
            }

            style.Style = textStyle;
            return true;
        }

        /// <summary>取标注样式名（失败返回空串）。</summary>
        public static string NameOf(DimensionStyle style)
        {
            return style == null ? string.Empty : style.Name;
        }

        /// <summary>实体引用的标注样式（非标注返回 null）。</summary>
        public static DimensionStyle StyleOf(Entity entity)
        {
            var dim = entity as Dimension;
            return dim == null ? null : dim.Style;
        }
    }
}
