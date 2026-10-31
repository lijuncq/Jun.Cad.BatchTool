using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 文字类实体的统一访问器。
    ///
    /// 工程图纸里"带文字"的对象远不止 TEXT/MTEXT：
    ///   TEXT、MTEXT、ATTDEF、ATTRIB、DIMENSION（标注文字）、ACAD_TABLE（单元格）、
    ///   LEADER（引线注释）、MULTILEADER、TOLERANCE（形位公差）
    /// 各自的文字样式挂载点不同：
    ///   * DBText / MText / Attribute* → 实体自身 TextStyleId；
    ///   * Dimension                   → 由 DimensionStyle.TextStyleId 控制（实体上没有独立入口）；
    ///   * Table                       → 每个 Cell 各有一个 TextStyleId；
    ///   * Leader                      → 取决于它的 Annotation（MText / Tolerance / 块）；
    ///   * MLeader / Tolerance         → 有 TextStyleId，但跨版本存在差异，走反射兜底。
    /// </summary>
    public static class TextEntityHelper
    {
        /// <summary>默认的文字类实体类型（配置未显式指定 EntityTypes 时使用）。</summary>
        public static readonly string[] DefaultTextEntityTypes =
        {
            "TEXT", "MTEXT", "ATTDEF", "ATTRIB", "DIMENSION", "ACAD_TABLE", "LEADER", "MULTILEADER", "TOLERANCE"
        };

        /// <summary>实体是否属于"携带文字"的类型。</summary>
        public static bool IsTextCarrier(Entity ent)
        {
            return ent is DBText
                   || ent is MText
                   || ent is AttributeDefinition
                   || ent is AttributeReference
                   || ent is Dimension
                   || ent is Table
                   || ent is Leader
                   || ent is MLeader
                   || ent is FeatureControlFrame;
        }

        /// <summary>
        /// 取实体当前生效的文字样式 ObjectId（尽力而为，取不到返回 ObjectId.Null）。
        /// </summary>
        public static ObjectId GetTextStyleId(Transaction tr, Entity ent)
        {
            try
            {
                var dbText = ent as DBText;
                if (dbText != null)
                {
                    return dbText.TextStyleId;
                }

                var mtext = ent as MText;
                if (mtext != null)
                {
                    return mtext.TextStyleId;
                }

                var attDef = ent as AttributeDefinition;
                if (attDef != null)
                {
                    return attDef.TextStyleId;
                }

                var attRef = ent as AttributeReference;
                if (attRef != null)
                {
                    return attRef.TextStyleId;
                }

                var dim = ent as Dimension;
                if (dim != null)
                {
                    // 标注文字样式来自标注样式
                    var dimStyle = tr.GetObject(dim.DimensionStyle, OpenMode.ForRead, false) as DimStyleTableRecord;
                    return dimStyle == null ? ObjectId.Null : dimStyle.Dimtxsty;
                }

                var table = ent as Table;
                if (table != null)
                {
                    try
                    {
                        if (table.Rows.Count > 0 && table.Columns.Count > 0)
                        {
                            var cell = table.Cells[0, 0];
                            if (cell != null && cell.TextStyleId.HasValue && !cell.TextStyleId.Value.IsNull)
                            {
                                return cell.TextStyleId.Value;
                            }
                        }
                    }
                    catch
                    {
                        // 空表/合并单元格异常，忽略
                    }

                    return ObjectId.Null;
                }

                var leader = ent as Leader;
                if (leader != null)
                {
                    return GetLeaderAnnotationStyleId(tr, leader);
                }

                // MLeader / Tolerance：属性名存在但版本差异较大，走反射
                return GetStyleIdViaReflection(ent);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// 给实体重新指定文字样式。返回是否成功，失败原因写入 reason。
        /// 说明：Dimension 不在本方法处理范围内（必须改标注样式，见 TextFontChangeRule）。
        /// </summary>
        public static bool SetTextStyleId(Transaction tr, Entity ent, ObjectId styleId, out string reason)
        {
            reason = null;
            if (ent == null || styleId.IsNull)
            {
                reason = "实体或样式为空";
                return false;
            }

            try
            {
                var dbText = ent as DBText;
                if (dbText != null)
                {
                    if (dbText.TextStyleId == styleId)
                    {
                        return false;
                    }

                    dbText.UpgradeOpen();
                    dbText.TextStyleId = styleId;
                    return true;
                }

                var mtext = ent as MText;
                if (mtext != null)
                {
                    if (mtext.TextStyleId == styleId)
                    {
                        return false;
                    }

                    mtext.UpgradeOpen();
                    mtext.TextStyleId = styleId;
                    return true;
                }

                var attDef = ent as AttributeDefinition;
                if (attDef != null)
                {
                    if (attDef.TextStyleId == styleId)
                    {
                        return false;
                    }

                    attDef.UpgradeOpen();
                    attDef.TextStyleId = styleId;
                    return true;
                }

                var attRef = ent as AttributeReference;
                if (attRef != null)
                {
                    if (attRef.TextStyleId == styleId)
                    {
                        return false;
                    }

                    attRef.UpgradeOpen();
                    attRef.TextStyleId = styleId;
                    return true;
                }

                var table = ent as Table;
                if (table != null)
                {
                    table.UpgradeOpen();
                    string error;
                    int cells = TextStyleService.ApplyToTableCells(table, styleId, out error);
                    if (cells == 0 && error != null)
                    {
                        reason = error;
                        return false;
                    }

                    return cells > 0;
                }

                var leader = ent as Leader;
                if (leader != null)
                {
                    // 引线本身没有文字，改它的注释对象
                    Entity annotation = GetLeaderAnnotation(tr, leader);
                    if (annotation == null)
                    {
                        reason = "引线没有可修改的文字注释";
                        return false;
                    }

                    return SetTextStyleId(tr, annotation, styleId, out reason);
                }

                // MLeader / Tolerance
                if (TextStyleService.TrySetTextStyleIdViaReflection(ent, styleId, out reason))
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>取 Leader 的注释实体（MText / Tolerance / BlockReference），没有则返回 null。</summary>
        public static Entity GetLeaderAnnotation(Transaction tr, Leader leader)
        {
            try
            {
                if (leader.Annotation.IsNull)
                {
                    return null;
                }

                return tr.GetObject(leader.Annotation, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                return null;
            }
        }

        private static ObjectId GetLeaderAnnotationStyleId(Transaction tr, Leader leader)
        {
            Entity annotation = GetLeaderAnnotation(tr, leader);
            if (annotation == null)
            {
                return ObjectId.Null;
            }

            var mtext = annotation as MText;
            if (mtext != null)
            {
                return mtext.TextStyleId;
            }

            var tolerance = annotation as FeatureControlFrame;
            if (tolerance != null)
            {
                return GetStyleIdViaReflection(tolerance);
            }

            return ObjectId.Null;
        }

        private static ObjectId GetStyleIdViaReflection(DBObject obj)
        {
            try
            {
                var prop = obj.GetType().GetProperty("TextStyleId");
                if (prop == null || prop.PropertyType != typeof(ObjectId))
                {
                    return ObjectId.Null;
                }

                return (ObjectId)prop.GetValue(obj, null);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        /// <summary>把配置里的 EntityTypes 规范成"文字类"类型列表（为空时给默认集合）。</summary>
        public static List<string> ResolveTypes(Config.RuleFilter filter)
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
    }
}
