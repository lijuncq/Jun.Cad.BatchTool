using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>轴对齐二维包围盒（世界坐标）。</summary>
    public struct Bounds2d
    {
        public Bounds2d(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;

        public bool IsValid
        {
            get { return MaxX >= MinX && MaxY >= MinY && !double.IsNaN(MinX) && !double.IsNaN(MaxY); }
        }

        public double Width
        {
            get { return MaxX - MinX; }
        }

        public double Height
        {
            get { return MaxY - MinY; }
        }

        public double CenterX
        {
            get { return (MinX + MaxX) * 0.5; }
        }

        public double CenterY
        {
            get { return (MinY + MaxY) * 0.5; }
        }

        public double Area
        {
            get { return Math.Max(0, Width) * Math.Max(0, Height); }
        }

        public Bounds2d Inflate(double padding)
        {
            return new Bounds2d(MinX - padding, MinY - padding, MaxX + padding, MaxY + padding);
        }

        public Bounds2d Union(Bounds2d other)
        {
            if (!IsValid)
            {
                return other;
            }

            if (!other.IsValid)
            {
                return this;
            }

            return new Bounds2d(
                Math.Min(MinX, other.MinX),
                Math.Min(MinY, other.MinY),
                Math.Max(MaxX, other.MaxX),
                Math.Max(MaxY, other.MaxY));
        }

        public bool Intersects(Bounds2d other)
        {
            return MinX <= other.MaxX && MaxX >= other.MinX
                   && MinY <= other.MaxY && MaxY >= other.MinY;
        }

        public double IntersectionArea(Bounds2d other)
        {
            double w = Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX);
            double h = Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY);
            return w <= 0 || h <= 0 ? 0 : w * h;
        }

        public bool Contains(double x, double y)
        {
            return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        }

        public override string ToString()
        {
            return string.Format("({0:0.###},{1:0.###})-({2:0.###},{3:0.###})", MinX, MinY, MaxX, MaxY);
        }
    }

    /// <summary>
    /// 实体包围盒的**唯一入口**。
    ///
    /// 为什么不能直接用 <c>Entity.GetBoundingBox()</c>（3.8.35 实测）：
    ///   * <c>TextEntity</c> / <c>MText</c> 返回**退化矩形**（min == max），读入实体与新建实体都一样。
    ///     文字遮罩尺寸、遮挡判断、"中心点是否在区域内"全都依赖这个盒子，所以必须自己估；
    ///   * <c>Dimension</c> 返回零宽矩形，真实范围只能取 <c>Dimension.Block</c> 内实体的并集；
    ///   * <c>TableEntity</c> 源码直接 <c>return BoundingBox.Null</c>，表格没有盒子；
    ///   * <c>Wipeout</c> 的盒子忽略 <c>UVector/VVector</c>，旋转后是错的。
    ///
    /// 本类对上述四类各写了一条路径，其余实体仍然走库的 <c>GetBoundingBox()</c>。
    /// 文字宽度是**估算**（见 <see cref="MeasureWidth"/>），误差在 ±10% 量级，
    /// 调用方在需要精确的地方应留 padding。
    /// </summary>
    public static class TextBounds
    {
        /// <summary>西文平均字宽系数（相对字高）。SHX 字体实测在 0.55~0.75 之间。</summary>
        public const double LatinWidthFactor = 0.62;

        /// <summary>中日韩全角字符宽度系数。</summary>
        public const double WideWidthFactor = 1.0;

        /// <summary>
        /// 取实体包围盒。文字类走估算，表格返回 false（没有盒子）。
        /// </summary>
        public static bool TryGet(Entity entity, out Bounds2d bounds)
        {
            bounds = default(Bounds2d);
            if (entity == null)
            {
                return false;
            }

            // 顺序重要：TableEntity 继承 Insert，Wipeout 继承 RasterImage，AttributeEntity 继承 TextEntity
            if (entity is TableEntity)
            {
                return false;
            }

            var wipeout = entity as Wipeout;
            if (wipeout != null)
            {
                bounds = ForWipeout(wipeout);
                return bounds.IsValid;
            }

            var mtext = entity as MText;
            if (mtext != null)
            {
                bounds = ForMText(mtext);
                return bounds.IsValid;
            }

            var text = entity as TextEntity;
            if (text != null)
            {
                bounds = ForText(text);
                return bounds.IsValid;
            }

            var dim = entity as Dimension;
            if (dim != null)
            {
                bounds = ForDimension(dim);
                return bounds.IsValid;
            }

            return TryGetFromLibrary(entity, out bounds);
        }

        /// <summary>库的 <c>GetBoundingBox()</c> 兜底（对 Line/Polyline/Insert/Hatch 等有效）。</summary>
        public static bool TryGetFromLibrary(Entity entity, out Bounds2d bounds)
        {
            bounds = default(Bounds2d);
            if (entity == null)
            {
                return false;
            }

            BoundingBox box;
            try
            {
                box = entity.GetBoundingBox();
            }
            catch
            {
                return false;
            }

            XYZ min = box.Min;
            XYZ max = box.Max;
            if (double.IsNaN(min.X) || double.IsNaN(max.X))
            {
                return false;
            }

            bounds = new Bounds2d(min.X, min.Y, max.X, max.Y);
            return bounds.IsValid;
        }

        // ------------------------------------------------------------------ TEXT

        public static Bounds2d ForText(TextEntity text)
        {
            double height = text.Height > 0 ? text.Height : 2.5;
            double widthFactor = text.WidthFactor > 0 ? text.WidthFactor : 1.0;
            string value = text.Value ?? string.Empty;

            double width = MeasureWidth(value, height * widthFactor);
            double boxHeight = height;

            XYZ anchor = text.InsertPoint;

            // 对齐方式不是"左下"时，AutoCAD 用的是 AlignmentPoint
            bool usesAlignmentPoint =
                text.HorizontalAlignment != TextHorizontalAlignment.Left
                || text.VerticalAlignment != TextVerticalAlignmentType.Baseline;

            if (usesAlignmentPoint)
            {
                XYZ ap = text.AlignmentPoint;
                if (Math.Abs(ap.X) > 1e-9 || Math.Abs(ap.Y) > 1e-9)
                {
                    anchor = ap;
                }
            }

            double dx;
            switch (text.HorizontalAlignment)
            {
                case TextHorizontalAlignment.Center:
                case TextHorizontalAlignment.Middle:
                case TextHorizontalAlignment.Aligned:
                case TextHorizontalAlignment.Fit:
                    dx = -width * 0.5;
                    break;
                case TextHorizontalAlignment.Right:
                    dx = -width;
                    break;
                default:
                    dx = 0;
                    break;
            }

            double dy;
            switch (text.VerticalAlignment)
            {
                case TextVerticalAlignmentType.Middle:
                    dy = -boxHeight * 0.5;
                    break;
                case TextVerticalAlignmentType.Top:
                    dy = -boxHeight;
                    break;
                default:
                    dy = 0;
                    break;
            }

            return RotatedRect(anchor, dx, dy, width, boxHeight, text.Rotation);
        }

        // ------------------------------------------------------------------ MTEXT

        public static Bounds2d ForMText(MText mtext)
        {
            double height = mtext.Height > 0 ? mtext.Height : 2.5;
            double spacing = mtext.LineSpacing > 0 ? mtext.LineSpacing : 1.0;

            List<string> lines = MTextTokenizer.ToLines(mtext.Value);
            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            double maxLine = 0;
            foreach (string line in lines)
            {
                maxLine = Math.Max(maxLine, MeasureWidth(line, height));
            }

            double width = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : maxLine;
            double boxHeight = height + (lines.Count - 1) * height * spacing;

            double dx;
            switch (mtext.AttachmentPoint)
            {
                case AttachmentPointType.TopCenter:
                case AttachmentPointType.MiddleCenter:
                case AttachmentPointType.BottomCenter:
                    dx = -width * 0.5;
                    break;
                case AttachmentPointType.TopRight:
                case AttachmentPointType.MiddleRight:
                case AttachmentPointType.BottomRight:
                    dx = -width;
                    break;
                default:
                    dx = 0;
                    break;
            }

            double dy;
            switch (mtext.AttachmentPoint)
            {
                case AttachmentPointType.MiddleLeft:
                case AttachmentPointType.MiddleCenter:
                case AttachmentPointType.MiddleRight:
                    dy = -boxHeight * 0.5;
                    break;
                case AttachmentPointType.BottomLeft:
                case AttachmentPointType.BottomCenter:
                case AttachmentPointType.BottomRight:
                    dy = -boxHeight;
                    break;
                default:
                    dy = 0;
                    break;
            }

            return RotatedRect(mtext.InsertPoint, dx, dy, width, boxHeight, mtext.Rotation);
        }

        // ------------------------------------------------------------------ DIMENSION

        /// <summary>
        /// 标注的真实范围 = <c>Dimension.Block</c>（<c>*D###</c> 匿名块）内实体的并集。
        ///
        /// 实测：一个 <c>DimensionLinear</c> 的块里有 2 条线 + 2 个箭头 + 1 个 MText + 3 个定义点；
        /// 而 <c>Dimension.GetBoundingBox()</c> 返回零宽矩形，完全不能用。
        /// 块不存在时退化为"定义点 + 插入点"的退化盒，并在 <see cref="Describe"/> 里标注 approx。
        /// </summary>
        public static Bounds2d ForDimension(Dimension dim)
        {
            var result = default(Bounds2d);
            bool any = false;

            if (dim.Block != null)
            {
                foreach (Entity child in dim.Block.Entities)
                {
                    Bounds2d childBounds;
                    if (!TryGet(child, out childBounds))
                    {
                        continue;
                    }

                    result = any ? result.Union(childBounds) : childBounds;
                    any = true;
                }
            }

            if (!any)
            {
                XYZ p1 = dim.DefinitionPoint;
                XYZ p2 = dim.InsertionPoint;
                double minX = Math.Min(p1.X, p2.X);
                double maxX = Math.Max(p1.X, p2.X);
                double minY = Math.Min(p1.Y, p2.Y);
                double maxY = Math.Max(p1.Y, p2.Y);
                return new Bounds2d(minX, minY, maxX, maxY);
            }

            return result;
        }

        /// <summary>标注文字的显示位置：优先取 <c>*D</c> 块里的 MText/TEXT，取不到退回 <c>TextMiddlePoint</c>。</summary>
        public static bool TryGetDimensionTextAnchor(Dimension dim, out XYZ anchor, out double height, out double rotation)
        {
            anchor = default(XYZ);
            height = 0;
            rotation = 0;

            if (dim == null)
            {
                return false;
            }

            if (dim.Block != null)
            {
                foreach (Entity child in dim.Block.Entities)
                {
                    var mt = child as MText;
                    if (mt != null)
                    {
                        anchor = mt.InsertPoint;
                        height = mt.Height;
                        rotation = mt.Rotation;
                        return true;
                    }

                    var tx = child as TextEntity;
                    if (tx != null)
                    {
                        anchor = tx.InsertPoint;
                        height = tx.Height;
                        rotation = tx.Rotation;
                        return true;
                    }
                }
            }

            XYZ middle = dim.TextMiddlePoint;
            if (Math.Abs(middle.X) > 1e-9 || Math.Abs(middle.Y) > 1e-9)
            {
                anchor = middle;
                height = dim.Style == null ? 2.5 : dim.Style.TextHeight;
                rotation = dim.TextRotation;
                return height > 0;
            }

            return false;
        }

        // ------------------------------------------------------------------ WIPEOUT

        /// <summary>
        /// Wipeout 的盒子必须自己算：库的实现是"裁剪顶点 + InsertPoint"，**不经过 U/V 向量**。
        /// 实测旋转 90° 的遮罩（U=+Y, V=-X，裁剪 10×4）库返回 (0,0)-(10,4)，
        /// 真实范围是 X:-4..0, Y:0..10。
        /// </summary>
        public static Bounds2d ForWipeout(CadWipeoutBase wipeout)
        {
            var result = default(Bounds2d);
            if (wipeout == null || wipeout.ClipBoundaryVertices == null || wipeout.ClipBoundaryVertices.Count == 0)
            {
                return result;
            }

            XYZ origin = wipeout.InsertPoint;
            XYZ u = wipeout.UVector;
            XYZ v = wipeout.VVector;
            bool any = false;

            foreach (XY corner in wipeout.ClipBoundaryVertices)
            {
                double x = origin.X + corner.X * u.X + corner.Y * v.X;
                double y = origin.Y + corner.X * u.Y + corner.Y * v.Y;

                if (!any)
                {
                    result = new Bounds2d(x, y, x, y);
                    any = true;
                }
                else
                {
                    result = new Bounds2d(
                        Math.Min(result.MinX, x), Math.Min(result.MinY, y),
                        Math.Max(result.MaxX, x), Math.Max(result.MaxY, y));
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ 通用

        /// <summary>把一个局部矩形按锚点 + 旋转展开成世界坐标 AABB。</summary>
        public static Bounds2d RotatedRect(XYZ anchor, double dx, double dy, double width, double height, double rotation)
        {
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);

            var corners = new[]
            {
                new[] { dx, dy },
                new[] { dx + width, dy },
                new[] { dx + width, dy + height },
                new[] { dx, dy + height }
            };

            var result = default(Bounds2d);
            bool any = false;

            foreach (double[] c in corners)
            {
                double x = anchor.X + c[0] * cos - c[1] * sin;
                double y = anchor.Y + c[0] * sin + c[1] * cos;

                if (!any)
                {
                    result = new Bounds2d(x, y, x, y);
                    any = true;
                }
                else
                {
                    result = new Bounds2d(
                        Math.Min(result.MinX, x), Math.Min(result.MinY, y),
                        Math.Max(result.MaxX, x), Math.Max(result.MaxY, y));
                }
            }

            return result;
        }

        /// <summary>
        /// 估算一行文字的宽度。汉字按 1.0 倍字高、西文按 0.62 倍字高累加。
        /// 这是估算——库没有字形度量，任何"精确宽度"的承诺都是假的。
        /// </summary>
        public static double MeasureWidth(string text, double height)
        {
            if (string.IsNullOrEmpty(text) || height <= 0)
            {
                return 0;
            }

            double width = 0;
            foreach (char c in text)
            {
                width += IsWide(c) ? WideWidthFactor : LatinWidthFactor;
            }

            return width * height;
        }

        /// <summary>全角字符（CJK / 全角标点 / 全角字母）。</summary>
        public static bool IsWide(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)      // CJK 部首、假名、汉字
                   || (c >= 0xF900 && c <= 0xFAFF)   // 兼容汉字
                   || (c >= 0xFF00 && c <= 0xFF60)   // 全角 ASCII
                   || (c >= 0xFFE0 && c <= 0xFFE6)   // 全角符号
                   || (c >= 0x3000 && c <= 0x303F);  // CJK 标点
        }

        /// <summary>实体的"可显示文字"（TEXT 原值 / MTEXT 去码 / 属性值 / 标注显式文字）。</summary>
        public static string PlainTextOf(Entity entity)
        {
            var mtext = entity as MText;
            if (mtext != null)
            {
                return MTextTokenizer.ToPlainText(mtext.Value);
            }

            var text = entity as TextEntity;
            if (text != null)
            {
                return text.Value ?? string.Empty;
            }

            var dim = entity as Dimension;
            if (dim != null)
            {
                if (!string.IsNullOrEmpty(dim.Text) && dim.Text != "<>")
                {
                    return MTextTokenizer.ToPlainText(dim.Text);
                }

                XYZ anchor;
                double height;
                double rotation;
                if (TryGetDimensionTextAnchor(dim, out anchor, out height, out rotation))
                {
                    foreach (Entity child in dim.Block.Entities)
                    {
                        var childText = child as TextEntity;
                        if (childText != null)
                        {
                            return childText.Value ?? string.Empty;
                        }

                        var childMText = child as MText;
                        if (childMText != null)
                        {
                            return MTextTokenizer.ToPlainText(childMText.Value);
                        }
                    }
                }

                return dim.Text ?? string.Empty;
            }

            return string.Empty;
        }
    }
}
