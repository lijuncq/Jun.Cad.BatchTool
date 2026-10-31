using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Jun.Cad.Batch.Config;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 把 <see cref="RuleFilter"/> 翻译成托管判定。
    ///
    /// AutoCAD 版有两条通道（SelectionFilter 组码 + 托管判定），离线版只剩托管判定这一条——
    /// 因为不存在"内核帮我筛"这回事。好处是语义只有一份，不会再出现两条通道漂移。
    ///
    /// 判定维度与配置字段一一对应：实体类型 / 颜色索引 / 真彩色 / 图层 / 块名。
    /// </summary>
    public sealed class EntityFilter
    {
        private readonly HashSet<string> _typeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<short> _colorSet = new HashSet<short>();
        private readonly HashSet<string> _layerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _blockSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private (byte R, byte G, byte B)? _trueColor;

        /// <summary>配置了空间/图框/区域条件（它们由 SpatialFilter 执行，但同样算"有过滤条件"）。</summary>
        private bool _hasSpatial;

        private EntityFilter()
        {
        }

        /// <summary>是否配置了任何条件（false 表示"全选"）。</summary>
        public bool HasCondition
        {
            get
            {
                return _typeSet.Count > 0 || _colorSet.Count > 0 || _layerSet.Count > 0
                       || _blockSet.Count > 0 || _trueColor.HasValue || _hasSpatial;
            }
        }

        /// <summary>
        /// 由规则过滤器构建。
        /// </summary>
        /// <param name="filter">配置中的 Filter 段。</param>
        /// <param name="defaultBlockName">BlockReplace 的源块名（等价于组码 2）。</param>
        public static EntityFilter Build(RuleFilter filter, string defaultBlockName = null)
        {
            var f = new EntityFilter();
            filter = filter ?? new RuleFilter();

            if (filter.EntityTypes != null)
            {
                foreach (string t in filter.EntityTypes.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    f._typeSet.Add(t.Trim());
                }
            }

            if (filter.ColorIndex.HasValue)
            {
                f._colorSet.Add((short)filter.ColorIndex.Value);
            }

            if (filter.ColorIndexes != null)
            {
                foreach (int c in filter.ColorIndexes)
                {
                    f._colorSet.Add((short)c);
                }
            }

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

            if (!string.IsNullOrWhiteSpace(filter.TrueColor))
            {
                f._trueColor = ParseTrueColor(filter.TrueColor);
            }

            // 空间/图框/区域是比"实体类型"更强的限定，必须计入"有没有过滤条件"。
            // 不计入的话，"只处理 TB-2025 图框里的对象"会被护栏判成"没有任何过滤条件"并中止。
            f._hasSpatial =
                (filter.LayoutPatterns != null && filter.LayoutPatterns.Exists(x => !string.IsNullOrWhiteSpace(x)))
                || !string.IsNullOrWhiteSpace(filter.ContainerBlockPattern)
                || (!string.IsNullOrWhiteSpace(filter.RegionMode)
                    && filter.RegionPoints != null && filter.RegionPoints.Count > 0);

            return f;
        }

        /// <summary>托管侧判定。</summary>
        public bool Matches(Entity ent)
        {
            if (ent == null)
            {
                return false;
            }

            if (_typeSet.Count > 0 && !_typeSet.Any(t => MatchesType(ent, t)))
            {
                return false;
            }

            if (_colorSet.Count > 0 && !_colorSet.Contains(ent.Color.Index))
            {
                return false;
            }

            if (_trueColor.HasValue)
            {
                if (!ent.Color.IsTrueColor)
                {
                    return false;
                }

                var c = ent.Color;
                if (c.R != _trueColor.Value.R || c.G != _trueColor.Value.G || c.B != _trueColor.Value.B)
                {
                    return false;
                }
            }

            if (_layerSet.Count > 0)
            {
                string layer = ent.Layer == null ? string.Empty : ent.Layer.Name;
                if (!_layerSet.Contains(layer))
                {
                    return false;
                }
            }

            if (_blockSet.Count > 0)
            {
                var ins = ent as Insert;
                if (ins == null)
                {
                    return false;
                }

                string effective = EffectiveBlockName(ins.Block);
                if (effective == null || !_blockSet.Contains(effective))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 块参照的"有效块名"：动态块的实例指向匿名块（*U5），
        /// 通过 <see cref="BlockRecord.Source"/> 回溯到原始动态块定义名。
        /// 这与 AutoCAD 里用户看到的块名一致。
        /// </summary>
        public static string EffectiveBlockName(BlockRecord block)
        {
            if (block == null)
            {
                return null;
            }

            if (block.IsAnonymous && block.Source != null)
            {
                return block.Source.Name;
            }

            return block.Name;
        }

        public string Describe()
        {
            if (!HasCondition)
            {
                return "(无过滤条件)";
            }

            var parts = new List<string>();
            if (_typeSet.Count > 0)
            {
                parts.Add("类型=" + string.Join(",", _typeSet));
            }

            if (_colorSet.Count > 0)
            {
                parts.Add("颜色=" + string.Join(",", _colorSet.Select(c => c.ToString(CultureInfo.InvariantCulture))));
            }

            if (_layerSet.Count > 0)
            {
                parts.Add("图层=" + string.Join(",", _layerSet));
            }

            if (_blockSet.Count > 0)
            {
                parts.Add("块名=" + string.Join(",", _blockSet));
            }

            if (_trueColor.HasValue)
            {
                parts.Add(string.Format("真彩色=#{0:X2}{1:X2}{2:X2}", _trueColor.Value.R, _trueColor.Value.G, _trueColor.Value.B));
            }

            return string.Join(" ", parts);
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
                    // TableEntity 继承自 Insert，但它自己的 DXF 名是 ACAD_TABLE
                    return ent is Insert && !(ent is TableEntity);

                case "DIMENSION":
                    return ent is Dimension;

                case "TEXT":
                    return ent is TextEntity && !(ent is AttributeBase);

                case "MTEXT":
                    return ent is MText;

                case "ATTRIB":
                    return ent is AttributeEntity;

                case "ATTDEF":
                    return ent is AttributeDefinition;

                case "LWPOLYLINE":
                    return ent is LwPolyline;

                case "POLYLINE":
                    return ent is Polyline2D || ent is Polyline3D || ent is PolyfaceMesh || ent is PolygonMesh;

                case "POINT":
                    return ent is Point;

                case "TABLE":
                case "ACAD_TABLE":
                    return ent is TableEntity;

                case "MULTILEADER":
                case "MLEADER":
                    return ent is MultiLeader;

                case "LEADER":
                    return ent is Leader;

                case "TOLERANCE":
                    return ent is Tolerance;

                case "3DFACE":
                    return ent is Face3D;

                case "IMAGE":
                    // Wipeout 在库里继承自 RasterImage，但它的 DXF 名是 WIPEOUT
                    return ent is RasterImage && !(ent is Wipeout);

                case "MLINE":
                    return ent is MLine;

                case "REGION":
                    return ent is Region;

                case "3DSOLID":
                    return ent is Solid3D;

                case "WIPEOUT":
                    return ent is Wipeout;

                case "XLINE":
                    return ent is XLine;

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
                    // ACadSharp 里 Arc 继承自 Circle（圆弧 = 带起止角的圆），
                    // 直接写 ent is Circle 会把圆弧一起捞进来——实测一张 2568KB 的图里
                    // CIRCLE 被误判成 1450 个（= 33 个圆 + 1417 个圆弧）。
                    return ent is Circle && !(ent is Arc);

                case "LINE":
                    return ent is Line;

                case "VIEWPORT":
                    return ent is Viewport;

                default:
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

            if (ent is TableEntity) return "ACAD_TABLE";
            if (ent is AttributeEntity) return "ATTRIB";
            if (ent is AttributeDefinition) return "ATTDEF";
            if (ent is Insert) return "INSERT";
            if (ent is Dimension) return "DIMENSION";
            if (ent is TextEntity) return "TEXT";
            if (ent is MText) return "MTEXT";
            if (ent is LwPolyline) return "LWPOLYLINE";
            if (ent is Polyline2D || ent is Polyline3D) return "POLYLINE";
            if (ent is PolyfaceMesh) return "POLYLINE";
            if (ent is Point) return "POINT";
            if (ent is MultiLeader) return "MULTILEADER";
            if (ent is Leader) return "LEADER";
            if (ent is Tolerance) return "TOLERANCE";
            if (ent is Face3D) return "3DFACE";

            // Wipeout 必须排在 RasterImage 前面：它继承自 RasterImage，
            // 顺序反了会把遮挡框报成 IMAGE，用户按类型排查时对不上。
            if (ent is Wipeout) return "WIPEOUT";
            if (ent is RasterImage) return "IMAGE";

            if (ent is MLine) return "MLINE";
            if (ent is Region) return "REGION";
            if (ent is Solid3D) return "3DSOLID";

            return ent.GetType().Name.ToUpperInvariant();
        }

        private static (byte, byte, byte)? ParseTrueColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string hex = text.Trim().TrimStart('#');
            int rgb;
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                return ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            }

            return null;
        }
    }
}
