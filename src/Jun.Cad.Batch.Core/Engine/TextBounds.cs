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
        /// 按 WIPEOUT 的图像像素坐标变换计算 WCS 包围盒。
        /// 裁剪顶点是归一化像素坐标，需经 Size、UVector 和 VVector 变换；
        /// 直接把裁剪点加到 InsertPoint 会使轴对齐/旋转遮罩都算错。
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
            double imageHeight = wipeout.Size.Y;
            bool any = false;

            foreach (XY corner in wipeout.ClipBoundaryVertices)
            {
                double pixelX = corner.X + 0.5;
                double pixelY = imageHeight - corner.Y - 0.5;
                double x = origin.X + pixelX * u.X + pixelY * v.X;
                double y = origin.Y + pixelX * u.Y + pixelY * v.Y;

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

        /// <summary>把局部矩形变换到 WCS，保留四角方向（用于真实的旋转 Wipeout 裁剪多边形）。</summary>
        public static XY[] RotatedRectVertices(
            XYZ anchor, double dx, double dy, double width, double height, double rotation, double padding = 0)
        {
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);
            double left = dx - padding;
            double bottom = dy - padding;
            double right = dx + width + padding;
            double top = dy + height + padding;

            var local = new[]
            {
                new XY(left, bottom),
                new XY(right, bottom),
                new XY(right, top),
                new XY(left, top)
            };

            var result = new XY[local.Length];
            for (int i = 0; i < local.Length; i++)
            {
                result[i] = new XY(
                    anchor.X + local[i].X * cos - local[i].Y * sin,
                    anchor.Y + local[i].X * sin + local[i].Y * cos);
            }

            return result;
        }

        /// <summary>把一个局部矩形按锚点 + 旋转展开成世界坐标 AABB。</summary>
        public static Bounds2d RotatedRect(XYZ anchor, double dx, double dy, double width, double height, double rotation)
        {
            XY[] corners = RotatedRectVertices(anchor, dx, dy, width, height, rotation);
            return AabbOf(corners);
        }

        /// <summary>一组顶点的轴对齐包围盒。</summary>
        public static Bounds2d AabbOf(XY[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
            {
                return default(Bounds2d);
            }

            double minX = vertices[0].X;
            double maxX = vertices[0].X;
            double minY = vertices[0].Y;
            double maxY = vertices[0].Y;
            for (int i = 1; i < vertices.Length; i++)
            {
                minX = Math.Min(minX, vertices[i].X);
                maxX = Math.Max(maxX, vertices[i].X);
                minY = Math.Min(minY, vertices[i].Y);
                maxY = Math.Max(maxY, vertices[i].Y);
            }

            return new Bounds2d(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// MTEXT 实际文字的估测宽度（最宽一行，逐字符累加）。
        /// 用于判断"文字框 (41) 是否明显比文字宽"——原生背景填充按框宽画，
        /// 框宽松时遮罩一侧会空出一大截。这是估算值，误差 ±10% 量级。
        /// </summary>
        public static double MeasureMTextTightWidth(MText mtext)
        {
            if (mtext == null)
            {
                return 0;
            }

            double height = mtext.Height > 0 ? mtext.Height : 2.5;
            double maxLine = 0;
            foreach (string line in MTextTokenizer.ToLines(mtext.Value))
            {
                maxLine = Math.Max(maxLine, MeasureWidth(line, height));
            }

            return maxLine;
        }

        /// <summary>
        /// 获取 TEXT/MTEXT/ATTRIB 真实朝向的遮罩四角。返回 null 表示无法从实体计算，
        /// 调用方可退化为轴对齐包围盒。Padding 在文字自身坐标系内应用，避免旋转后变成大 AABB。
        ///
        /// <paramref name="tightFrame"/> 只对 MTEXT 有意义：原生背景填充是按**文字框宽度**
        /// (DXF 41) 画的（受控出图实测：框 60 / 文字 6，擦除断口正好 60），框比文字宽时
        /// 遮罩一侧空出一大截。tightFrame=true 时宽度改用**实测文字宽度**（逐字符估算），
        /// 让遮罩贴住文字本体；误差只影响留白多少，绝不会再引起折行——因为我们从不改 41。
        /// </summary>
        public static XY[] TextMaskVertices(Entity entity, double padding, bool tightFrame = false)
        {
            var mtext = entity as MText;
            if (mtext != null)
            {
                double height = mtext.Height > 0 ? mtext.Height : 2.5;
                double spacing = mtext.LineSpacing > 0 ? mtext.LineSpacing : 1.0;
                List<string> lines = MTextTokenizer.ToLines(mtext.Value);
                if (lines.Count == 0) lines.Add(string.Empty);
                double maxLine = 0;
                foreach (string line in lines) maxLine = Math.Max(maxLine, MeasureWidth(line, height));
                double width = tightFrame || mtext.RectangleWidth <= 0 ? maxLine : mtext.RectangleWidth;
                double boxHeight = height + (lines.Count - 1) * height * spacing;
                double dx = 0;
                double dy = 0;
                switch (mtext.AttachmentPoint)
                {
                    case AttachmentPointType.TopCenter:
                    case AttachmentPointType.MiddleCenter:
                    case AttachmentPointType.BottomCenter: dx = -width * 0.5; break;
                    case AttachmentPointType.TopRight:
                    case AttachmentPointType.MiddleRight:
                    case AttachmentPointType.BottomRight: dx = -width; break;
                }
                switch (mtext.AttachmentPoint)
                {
                    case AttachmentPointType.MiddleLeft:
                    case AttachmentPointType.MiddleCenter:
                    case AttachmentPointType.MiddleRight: dy = -boxHeight * 0.5; break;
                    case AttachmentPointType.BottomLeft:
                    case AttachmentPointType.BottomCenter:
                    case AttachmentPointType.BottomRight: dy = -boxHeight; break;
                }
                return RotatedRectVertices(mtext.InsertPoint, dx, dy, width, boxHeight, mtext.Rotation, padding);
            }

            var text = entity as TextEntity;
            if (text != null)
            {
                double height = text.Height > 0 ? text.Height : 2.5;
                double widthFactor = text.WidthFactor > 0 ? text.WidthFactor : 1.0;
                double width = MeasureWidth(text.Value ?? string.Empty, height * widthFactor);
                XYZ anchor = text.InsertPoint;
                bool usesAlignmentPoint = text.HorizontalAlignment != TextHorizontalAlignment.Left
                                          || text.VerticalAlignment != TextVerticalAlignmentType.Baseline;
                if (usesAlignmentPoint && (Math.Abs(text.AlignmentPoint.X) > 1e-9 || Math.Abs(text.AlignmentPoint.Y) > 1e-9))
                    anchor = text.AlignmentPoint;
                double dx = 0;
                switch (text.HorizontalAlignment)
                {
                    case TextHorizontalAlignment.Center:
                    case TextHorizontalAlignment.Middle:
                    case TextHorizontalAlignment.Aligned:
                    case TextHorizontalAlignment.Fit: dx = -width * 0.5; break;
                    case TextHorizontalAlignment.Right: dx = -width; break;
                }
                double dy = 0;
                switch (text.VerticalAlignment)
                {
                    case TextVerticalAlignmentType.Middle: dy = -height * 0.5; break;
                    case TextVerticalAlignmentType.Top: dy = -height; break;
                }
                return RotatedRectVertices(anchor, dx, dy, width, height, text.Rotation, padding);
            }

            return null;
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
