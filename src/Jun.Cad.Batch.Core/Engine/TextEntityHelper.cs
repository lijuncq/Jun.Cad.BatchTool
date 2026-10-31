using System;
using System.Collections.Generic;
using ACadSharp.Entities;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 文字类实体的统一访问器。
    ///
    /// 工程图纸里"带文字"的对象远不止 TEXT/MTEXT：
    ///   TEXT、MTEXT、ATTDEF、ATTRIB、DIMENSION（标注文字）、ACAD_TABLE（单元格）、
    ///   LEADER（引线注释）、MULTILEADER、TOLERANCE（形位公差）
    /// 各自的文字样式挂载点不同：
    ///   * TextEntity / MText / Attribute*  → 实体自身 Style；
    ///   * Dimension                       → 由 DimensionStyle.Style 控制（实体上没有独立入口）；
    ///   * Table                           → 每个单元格各有一个字样（离线版不支持，见下）；
    ///   * MultiLeader / Tolerance         → 库未建模可写入口，离线版不支持。
    /// </summary>
    public static class TextEntityHelper
    {
        /// <summary>默认的文字类实体类型（配置未显式指定 EntityTypes 时使用）。</summary>
        public static readonly string[] DefaultTextEntityTypes =
        {
            "TEXT", "MTEXT", "ATTDEF", "ATTRIB", "DIMENSION", "ACAD_TABLE", "LEADER", "MULTILEADER", "TOLERANCE"
        };

        /// <summary>实体是否属于"携带文字"的类型。</summary>
        public static bool IsTextCarrier(Entity entity)
        {
            return entity is TextEntity      // 含 AttributeDefinition / AttributeEntity
                   || entity is MText
                   || entity is Dimension
                   || entity is TableEntity
                   || entity is Leader
                   || entity is MultiLeader
                   || entity is Tolerance;
        }

        /// <summary>
        /// 该实体能不能直接改字高 / 字样。
        /// 表格单元格、多重引线、形位公差在离线版里没有可写入口，返回 false 由调用方记为"跳过"。
        /// </summary>
        public static bool SupportsDirectTextProperties(Entity entity)
        {
            return entity is TextEntity || entity is MText;
        }

        /// <summary>解析规则实际使用的实体类型集合（未指定时补上文字类默认集合）。</summary>
        public static List<string> ResolveTypes(RuleFilter filter)
        {
            var types = new List<string>();
            if (filter != null && filter.EntityTypes != null)
            {
                foreach (string t in filter.EntityTypes)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        types.Add(t.Trim());
                    }
                }
            }

            if (types.Count == 0)
            {
                types.AddRange(DefaultTextEntityTypes);
            }

            return types;
        }

        /// <summary>
        /// 把"文字类规则"的默认类型集合补进 Filter。
        ///
        /// 为什么要补：不补的话一条"统一字高"的规则没有任何过滤条件，
        /// 会命中整张图的所有对象（护栏会因此中止规则）。补成"文字类"既符合直觉，
        /// 也让护栏有意义。
        /// </summary>
        public static void PrepareTextFilter(RuleFilter filter)
        {
            if (filter == null)
            {
                return;
            }

            if (filter.EntityTypes == null || filter.EntityTypes.Count == 0)
            {
                filter.EntityTypes = ResolveTypes(filter);
            }
        }

        /// <summary>取实体当前生效的文字样式名（取不到返回空串）。</summary>
        public static string CurrentStyleName(Entity entity)
        {
            return TextStyleService.StyleNameOf(entity);
        }

        /// <summary>取字高；不支持的对象返回 null。</summary>
        public static double? GetHeight(Entity entity)
        {
            var text = entity as TextEntity;
            if (text != null)
            {
                return text.Height;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                return mtext.Height;
            }

            return null;
        }

        /// <summary>设置字高；不支持的对象返回 false。</summary>
        public static bool SetHeight(Entity entity, double height)
        {
            var text = entity as TextEntity;
            if (text != null)
            {
                text.Height = height;
                return true;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                mtext.Height = height;
                return true;
            }

            return false;
        }

        /// <summary>设置文字样式；不支持的对象返回 false。</summary>
        public static bool SetStyle(Entity entity, ACadSharp.Tables.TextStyle style)
        {
            var text = entity as TextEntity;
            if (text != null)
            {
                text.Style = style;
                return true;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                mtext.Style = style;
                return true;
            }

            return false;
        }

        /// <summary>取 MText 内容（非 MText 返回 null）。</summary>
        public static string GetMTextContents(Entity entity)
        {
            var mtext = entity as MText;
            return mtext == null ? null : mtext.Value;
        }

        /// <summary>设置 MText 内容（非 MText 返回 false）。</summary>
        public static bool SetMTextContents(Entity entity, string contents)
        {
            var mtext = entity as MText;
            if (mtext == null)
            {
                return false;
            }

            mtext.Value = contents;
            return true;
        }

        /// <summary>
        /// MText 行距系数（0.25~4.0）。
        /// ACadSharp 把它建模成 <see cref="MText.LineSpacing"/>，语义与 AutoCAD 的
        /// "行距因子"一致（相对字高的倍数）。
        /// </summary>
        public static bool SetLineSpacingFactor(MText mtext, double factor)
        {
            if (mtext == null)
            {
                return false;
            }

            if (factor < 0.25)
            {
                factor = 0.25;
            }
            else if (factor > 4.0)
            {
                factor = 4.0;
            }

            mtext.LineSpacing = factor;
            return true;
        }

        /// <summary>不支持类型的可读说明（写进跳过原因，避免"跑了但什么都没发生"）。</summary>
        public static string UnsupportedReason(Entity entity)
        {
            if (entity is TableEntity)
            {
                return "表格单元格的文字属性在离线模式下不可写";
            }

            if (entity is MultiLeader)
            {
                return "多重引线的文字属性在离线模式下不可写";
            }

            if (entity is Tolerance)
            {
                return "形位公差的文字属性在离线模式下不可写";
            }

            if (entity is Leader)
            {
                return "引线本身不含文字，需改其注释对象";
            }

            return "该类型不支持直接改文字属性";
        }
    }
}
