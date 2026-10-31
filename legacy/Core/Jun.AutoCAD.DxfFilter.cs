using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if ZWCAD
using ZwSoft.ZwCAD.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif
using Jun.AutoCAD.Config;

namespace Jun.AutoCAD.Core
{
    /// <summary>
    /// 把 RuleFilter 翻译成 DXF 组码组合，并提供两条等价通道：
    ///
    ///   1) <see cref="ToSelectionFilter"/> —— 生成真正的 SelectionFilter（TypedValue[]），
    ///      用于 Editor.SelectAll / SelectImplied 这类"当前空间快速路径"（Scope = Selection）。
    ///   2) <see cref="Matches"/> —— 对同一份 TypedValue[] 做托管解释，
    ///      用于跨布局、跨嵌套块的全库遍历（SelectionFilter 无法作用于任意 BlockTableRecord）。
    ///
    /// 两条通道共用同一份组码数据，避免"选择集过滤"与"遍历过滤"语义漂移。
    ///
    /// 用到的组码：
    ///   0   (DxfCode.Start)     实体类型，多个类型用逗号连接，如 "LINE,LWPOLYLINE,CIRCLE"
    ///   62  (DxfCode.Color)     颜色索引号（1~255 ACI，256=随层，0=随块）
    ///   8   (DxfCode.LayerName) 图层名
    ///   2   (DxfCode.BlockName) 块名（仅 INSERT 有效）
    ///   -4  过滤运算符（"&lt;OR" / "OR&gt;"），用于多颜色、多图层这类"或"关系
    /// </summary>
    public sealed class DxfFilter
    {
        private readonly List<TypedValue> _values = new List<TypedValue>();
        private readonly HashSet<string> _typeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _colorSet = new HashSet<int>();
        private readonly HashSet<string> _layerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _blockSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private System.Drawing.Color? _trueColor;
        private Transaction _transaction;

        private DxfFilter()
        {
        }

        /// <summary>原始 DXF 组码列表（只读），用于日志与调试。</summary>
        public IReadOnlyList<TypedValue> Values
        {
            get { return _values; }
        }

        public int ColorCount
        {
            get { return _colorSet.Count; }
        }

        /// <summary>
        /// 由规则过滤器构建 DXF 组码集合。
        /// </summary>
        /// <param name="filter">配置中的 Filter 段。</param>
        /// <param name="defaultBlockName">BlockReplace 规则的源块名（会作为组码 2 参与过滤）。</param>
        /// <param name="tr">用于解析动态块有效块名的事务（可为 null）。</param>
        public static DxfFilter Build(RuleFilter filter, string defaultBlockName = null, Transaction tr = null)
        {
            var f = new DxfFilter { _transaction = tr };
            filter = filter ?? new RuleFilter();

            // ---- 组码 0：实体类型（多类型用逗号连接，SelectionFilter 原生支持） ----
            if (filter.EntityTypes != null)
            {
                foreach (string t in filter.EntityTypes.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    f._typeSet.Add(t.Trim());
                }
            }

            if (f._typeSet.Count > 0)
            {
                string joined = string.Join(",", f._typeSet.Select(x => x.ToUpperInvariant()));
                f._values.Add(new TypedValue((int)DxfCode.Start, joined));
            }

            // ---- 组码 62：颜色索引（多值用 <OR ... OR> 组合） ----
            if (filter.ColorIndex.HasValue)
            {
                f._colorSet.Add(filter.ColorIndex.Value);
            }

            if (filter.ColorIndexes != null)
            {
                foreach (int c in filter.ColorIndexes)
                {
                    f._colorSet.Add(c);
                }
            }

            if (f._colorSet.Count == 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.Color, f._colorSet.First()));
            }
            else if (f._colorSet.Count > 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.Operator, "<OR"));
                foreach (int c in f._colorSet)
                {
                    f._values.Add(new TypedValue((int)DxfCode.Color, c));
                }

                f._values.Add(new TypedValue((int)DxfCode.Operator, "OR>"));
            }

            // ---- 组码 8：图层名（多值同样用 OR 组合） ----
            if (!string.IsNullOrWhiteSpace(filter.LayerName))
            {
                f._layerSet.Add(filter.LayerName.Trim());
            }

            if (filter.LayerNames != null)
            {
                foreach (string l in filter.LayerNames.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    f._layerSet.Add(l.Trim());
                }
            }

            if (f._layerSet.Count == 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.LayerName, f._layerSet.First()));
            }
            else if (f._layerSet.Count > 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.Operator, "<OR"));
                foreach (string l in f._layerSet)
                {
                    f._values.Add(new TypedValue((int)DxfCode.LayerName, l));
                }

                f._values.Add(new TypedValue((int)DxfCode.Operator, "OR>"));
            }

            // ---- 组码 2：块名（多个块名同样用 OR 组合，供"一次归并好几种块"使用） ----
            string blockName = !string.IsNullOrWhiteSpace(filter.SourceBlockName) ? filter.SourceBlockName : defaultBlockName;
            if (!string.IsNullOrWhiteSpace(blockName))
            {
                f._blockSet.Add(blockName.Trim());
            }

            if (filter.SourceBlockNames != null)
            {
                foreach (string b in filter.SourceBlockNames.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    f._blockSet.Add(b.Trim());
                }
            }

            if (f._blockSet.Count == 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.BlockName, f._blockSet.First()));
            }
            else if (f._blockSet.Count > 1)
            {
                f._values.Add(new TypedValue((int)DxfCode.Operator, "<OR"));
                foreach (string b in f._blockSet)
                {
                    f._values.Add(new TypedValue((int)DxfCode.BlockName, b));
                }

                f._values.Add(new TypedValue((int)DxfCode.Operator, "OR>"));
            }

            // ---- 真彩色：DXF 组码 62 只存 ACI 索引，无法表达真彩色，只能遍历后判定 ----
            if (!string.IsNullOrWhiteSpace(filter.TrueColor))
            {
                f._trueColor = ParseTrueColor(filter.TrueColor);
            }

            return f;
        }

        /// <summary>生成 SelectionFilter（供 Editor.SelectAll 使用）。无任何条件时返回 null。</summary>
        public SelectionFilter ToSelectionFilter()
        {
            if (_values.Count == 0)
            {
                return null;
            }

            return new SelectionFilter(_values.ToArray());
        }

        /// <summary>是否使用了 DXF 组码过滤（false 表示"全选"）。</summary>
        public bool HasCondition
        {
            get
            {
                return _values.Count > 0 || _trueColor.HasValue;
            }
        }

        /// <summary>
        /// 托管侧判定，语义与 <see cref="ToSelectionFilter"/> 一致。
        /// 注意：组码 62 记录的是"实体自身颜色"，随层实体在 DXF 中不写 62 组码，
        /// 因此这里用 Entity.Color.ColorIndex 判定（256 表示随层），比 SelectionFilter 更可靠。
        /// </summary>
        public bool Matches(Entity ent)
        {
            if (ent == null)
            {
                return false;
            }

            // 类型
            if (_typeSet.Count > 0 && !_typeSet.Any(t => MatchesType(ent, t)))
            {
                return false;
            }

            // 颜色（ACI）
            if (_colorSet.Count > 0)
            {
                int aci;
                try
                {
                    aci = ent.Color.ColorIndex;
                }
                catch
                {
                    return false;
                }

                if (!_colorSet.Contains(aci))
                {
                    return false;
                }
            }

            // 真彩色（遍历后判定）
            if (_trueColor.HasValue)
            {
                try
                {
                    if (!ent.Color.IsByColor || !SameColor(ent.Color.ColorValue, _trueColor.Value))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            // 图层
            if (_layerSet.Count > 0 && !_layerSet.Contains(ent.Layer ?? string.Empty))
            {
                return false;
            }

            // 块名（动态块按"有效块名"比较）
            if (_blockSet.Count > 0)
            {
                var br = ent as BlockReference;
                if (br == null)
                {
                    return false;
                }

                if (!_blockSet.Any(name => BlockNameUtil.Matches(br, name, _transaction)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把组码列表格式化成可读文本，例如：(0 . "LINE,LWPOLYLINE") (62 . 1) (8 . "EQUIPMENT")</summary>
        public string Describe()
        {
            if (_values.Count == 0 && !_trueColor.HasValue)
            {
                return "(无过滤条件)";
            }

            var parts = _values.Select(v => string.Format(CultureInfo.InvariantCulture, "({0} . {1})", v.TypeCode, FormatValue(v.Value)));

            string text = string.Join(" ", parts);
            if (_trueColor.HasValue)
            {
                text += string.Format(" (真彩色 {0})", ToHex(_trueColor.Value));
            }

            return text;
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var s = value as string;
            return s != null ? "\"" + s + "\"" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>DXF 类型名 → 托管类型判定。使用 DXF 名称而非 .NET 类名，与配置语义一致。</summary>
        public static bool MatchesType(Entity ent, string dxfName)
        {
            if (ent == null || string.IsNullOrWhiteSpace(dxfName))
            {
                return false;
            }

            switch (dxfName.Trim().ToUpperInvariant())
            {
                case "INSERT":
                case "BLOCKREFERENCE":
                    return ent is BlockReference;

                case "DIMENSION":
                    // 覆盖所有标注子类：RotatedDimension / AlignedDimension / RadialDimension ...
                    return ent is Dimension;

                case "TEXT":
                    return ent is DBText;

                case "MTEXT":
                    return ent is MText;

                case "ATTRIB":
                    return ent is AttributeReference;

                case "ATTDEF":
                    return ent is AttributeDefinition;

                case "LWPOLYLINE":
                    return ent is Polyline;

                case "POLYLINE":
                    return ent is Polyline2d || ent is Polyline3d;

                case "POINT":
                    return ent is DBPoint;

                case "TABLE":
                case "ACAD_TABLE":
                    return ent is Table;

                case "MULTILEADER":
                case "MLEADER":
                    return ent is MLeader;

                case "LEADER":
                    return ent is Leader;

                case "TOLERANCE":
                    return ent is FeatureControlFrame;

                case "3DFACE":
                    return ent is Face;

                case "IMAGE":
                    return ent is RasterImage;

                case "MLINE":
                    return ent is Mline;

                case "REGION":
                    return ent is Region;

                case "3DSOLID":
                    return ent is Solid3d;

                case "WIPEOUT":
                    return ent is Wipeout;

                case "XLINE":
                    return ent is Xline;

                case "RAY":
                    return ent is Ray;

                case "ELLIPSE":
                    return ent is Ellipse;

                case "SPLINE":
                    return ent is Spline;

                case "HATCH":
                    return ent is Hatch;

                case "SOLID":
                    return ent is Solid;

                case "ARC":
                    return ent is Arc;

                case "CIRCLE":
                    return ent is Circle;

                case "LINE":
                    return ent is Line;

                case "VIEWPORT":
                    return ent is Viewport;

                default:
                    // 兜底：直接比较 .NET 类名（如 "HATCH" → Hatch、"WIPEOUT" → Wipeout 已在上面显式处理）
                    return string.Equals(ent.GetType().Name, dxfName.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>取实体的 DXF 类型名（用于日志与反向匹配）。</summary>
        public static string DxfNameOf(Entity ent)
        {
            if (ent == null)
            {
                return string.Empty;
            }

            if (ent is BlockReference) return "INSERT";
            if (ent is Dimension) return "DIMENSION";
            if (ent is DBText) return "TEXT";
            if (ent is MText) return "MTEXT";
            if (ent is AttributeReference) return "ATTRIB";
            if (ent is AttributeDefinition) return "ATTDEF";
            if (ent is Polyline) return "LWPOLYLINE";
            if (ent is Polyline2d || ent is Polyline3d) return "POLYLINE";
            if (ent is DBPoint) return "POINT";
            if (ent is Table) return "ACAD_TABLE";
            if (ent is MLeader) return "MULTILEADER";
            if (ent is Leader) return "LEADER";
            if (ent is FeatureControlFrame) return "TOLERANCE";
            if (ent is Face) return "3DFACE";
            if (ent is RasterImage) return "IMAGE";
            if (ent is Mline) return "MLINE";
            if (ent is Region) return "REGION";
            if (ent is Solid3d) return "3DSOLID";
            if (ent is Wipeout) return "WIPEOUT";

            return ent.GetType().Name.ToUpperInvariant();
        }

        private static System.Drawing.Color? ParseTrueColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string hex = text.Trim().TrimStart('#');
            int rgb;
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                return System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            }

            return null;
        }

        private static bool SameColor(System.Drawing.Color a, System.Drawing.Color b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B;
        }

        private static string ToHex(System.Drawing.Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }
}
