using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 文字遮罩的几何构造。
    ///
    /// 两种形态：
    ///   * <b>Wipeout</b>（首选）：DWG 原生遮罩，打印时按"擦除"处理，视觉上最干净；
    ///   * <b>2D Solid</b>（降级）：不透明实心四边形，靠"放在不打印图层 + 用背景色"近似遮罩。
    ///     它不是真遮罩——打印出来是一块实色，所以必须放在不打印图层上。
    ///
    /// Wipeout 的构造有个硬性要求：<c>ClipBoundaryVertices</c> 至少 2 个点（矩形），
    /// 且坐标是**相对 <c>InsertPoint</c> 的局部坐标**；U/V 向量决定旋转。
    /// 这里统一用"轴对齐矩形 + InsertPoint 取左下角"，旋转交给调用方（本规则不生成旋转遮罩）。
    /// </summary>
    public static class MaskService
    {
        public const string DefaultMaskLayer = "_MASK";

        private const string WipeoutDefinitionName = "*JUN_TEXTMASK";

        /// <summary>取/建 Wipeout 需要的图像定义（没有它写出的 Wipeout 是不完整的）。</summary>
        public static ImageDefinition EnsureDefinition(CadDocument doc)
        {
            if (doc == null)
            {
                return null;
            }

            ImageDefinition existing = doc.ImageDefinitions.FirstOrDefault(
                d => string.Equals(d.Name, WipeoutDefinitionName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return existing;
            }

            var definition = new ImageDefinition(WipeoutDefinitionName)
            {
                Size = new XY(1, 1),
                DefaultSize = new XY(1, 1),
                FileName = string.Empty
            };

            doc.ImageDefinitions.Add(definition);
            return definition;
        }

        /// <summary>按矩形造一个 Wipeout（轴对齐）。</summary>
        public static Wipeout CreateWipeout(CadDocument doc, Bounds2d rect)
        {
            var wipeout = new Wipeout
            {
                InsertPoint = new XYZ(rect.MinX, rect.MinY, 0),
                Size = new XY(1, 1),
                Definition = EnsureDefinition(doc)
            };

            wipeout.ClipBoundaryVertices.Add(new XY(0, 0));
            wipeout.ClipBoundaryVertices.Add(new XY(Math.Max(rect.Width, 1e-6), Math.Max(rect.Height, 1e-6)));

            return wipeout;
        }

        /// <summary>
        /// 按矩形造一个 2D Solid。
        /// 角点顺序按 DWG 的 1-2-4-3 语义给：左下、右下、左上、右上
        /// （回读时 <c>ThirdCorner</c>/<c>FourthCorner</c> 会对调，视觉上才是矩形）。
        /// </summary>
        public static Solid CreateSolid(Bounds2d rect, int colorIndex)
        {
            var solid = new Solid(
                new XYZ(rect.MinX, rect.MinY, 0),
                new XYZ(rect.MaxX, rect.MinY, 0),
                new XYZ(rect.MinX, rect.MaxY, 0),
                new XYZ(rect.MaxX, rect.MaxY, 0));

            solid.Color = new Color((short)colorIndex);
            return solid;
        }

        /// <summary>
        /// 把遮罩放到文字的**绘制顺序之前**（Wipeout 只擦除它之前画的东西，
        /// 放在文字后面会把文字自己擦掉）。
        ///
        /// 为什么用"移出文字 → 追加遮罩 → 再追加文字"而不是按索引插入：
        /// <c>BlockRecord.Entities</c> 是 <c>CadObjectCollection&lt;Entity&gt;</c>，
        /// 只有 Add/AddRange/Remove/Clear，**没有 Insert**。
        /// 而 <c>Clear()</c> 会把整批对象从文档里摘掉，重新 Add 后句柄/归属都可能变
        /// （实测：整表重建后差异预览里几乎所有实体都显示成"新增"）。
        /// 只动目标文字这一个对象，影响面最小。
        /// </summary>
        public static void PlaceMaskBefore(BlockRecord space, Entity textEntity, Entity mask)
        {
            if (space == null || mask == null)
            {
                return;
            }

            if (textEntity != null)
            {
                space.Entities.Remove(textEntity);
            }

            space.Entities.Add(mask);

            if (textEntity != null)
            {
                space.Entities.Add(textEntity);
            }
        }
    }
}
