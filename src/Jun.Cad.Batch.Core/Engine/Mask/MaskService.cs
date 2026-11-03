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
    /// 只有一种遮罩形态：**原生遮罩**。
    ///   * MTEXT → 自身背景填充（<c>BackgroundFillFlags</c>），使用**图形背景色**；
    ///   * TEXT / ATTRIB / ATTDEF → WIPEOUT（DWG 原生擦除）。
    ///
    /// 两者的共同点是"擦掉下面的东西、露出背景"，没有可选填充色，
    /// 因此不存在 2D Solid 那种"不透明色块"路径——Solid 会被 CTB 重映射成黑块，
    /// 早就从方案里移除了，不要再加回来。
    ///
    /// 边界偏移：<see cref="MaskOffsetFactor"/> 把"每边偏移 = 字高 × ratio"翻译成
    /// MTEXT 的 <c>BackgroundScale</c>；Wipeout 直接在几何上张开这么多。
    /// </summary>
    public static class MaskService
    {
        public const string DefaultMaskLayer = "_MASK";

        /// <summary>边界偏移比例的默认值：每边 = 字高 × 0.15。</summary>
        public const double DefaultPaddingRatio = 0.15;

        /// <summary>根字典里存放 WIPEOUT 全局变量的键名（AutoCAD 自己就这么挂）。</summary>
        public const string WipeoutVariablesKey = "ACAD_WIPEOUT_VARS";

        /// <summary>
        /// 关掉 WIPEOUT 的边框。
        ///
        /// <c>WIPEOUTFRAME</c> 默认是 1 = **显示并打印**边框，于是每个文字遮罩
        /// 出图时都会多出一个矩形框（受控图实测：`TEXT OVER LINES` 外面套了一圈方框）。
        /// 遮罩的语义是"擦掉下面的东西"，不该自己被看见，所以建遮罩前先把这个开关关掉。
        ///
        /// 变量挂在根字典 <c>ACAD_WIPEOUT_VARS</c> 下（对象类型 <see cref="WipeoutVariables"/>），
        /// 从既有图纸读入时可能已经存在——存在就改值，不存在才新建。
        /// 注意这条是**整张图**的设置：图上已有的其它 Wipeout 边框也会一起消失，
        /// 这正是遮罩该有的样子，但要写进变更说明。
        /// </summary>
        public static void HideWipeoutFrame(CadDocument doc)
        {
            if (doc == null || doc.RootDictionary == null)
            {
                return;
            }

            WipeoutVariables variables = doc.RootDictionary.GetEntry<WipeoutVariables>(WipeoutVariablesKey);
            if (variables == null)
            {
                variables = new WipeoutVariables
                {
                    Name = WipeoutVariablesKey,
                    DisplayImageFrame = false
                };

                doc.RootDictionary.Add(WipeoutVariablesKey, variables);
                return;
            }

            variables.DisplayImageFrame = false;
        }

        /// <summary>
        /// 边界偏移因子：MTEXT 原生背景的 <c>BackgroundScale</c>。
        ///
        /// 语义按需求定义：**每边留白 = 字高 × ratio**（默认 0.15）。AutoCAD 的
        /// "边界偏移因子"是 1.0 = 正好贴住文字、1.5 = 默认值，即
        /// 等效留白 = (factor − 1) × 字高。因此 <c>factor = 1 + ratio</c>。
        ///
        /// 组码 45 能不能落盘，取决于 bit0（背景填充开）：库的写出器只在
        /// 90 含 bit0 或文字框时写 45。旧实现只写 bit1，45 丢了、AutoCAD 也不认；
        /// 现在 <see cref="ApplyMTextBackground"/> 两位置齐，45 随文件保存，
        /// AutoCAD 2025 实测回读 `(45 . 1.15)`。Wipeout 那条路径的留白
        /// 由我们自己的几何决定，不走这个因子。
        /// </summary>
        public static double MaskOffsetFactor(double paddingRatio)
        {
            double ratio = double.IsNaN(paddingRatio) || double.IsInfinity(paddingRatio) ? DefaultPaddingRatio : paddingRatio;
            if (ratio < 0)
            {
                ratio = 0;
            }
            else if (ratio > 2)
            {
                ratio = 2;
            }

            return 1.0 + ratio;
        }

        /// <summary>
        /// 给 MTEXT 开原生背景遮罩，用**图形背景色**（不是某个 RGB 实色）。
        ///
        /// **只设背景标志，绝不碰 <c>RectangleWidth</c> / <c>RectangleHeight</c>。**
        ///
        /// 早先的实现会在 `RectangleWidth &lt;= 0` 时按"估算文字宽度 + 留白"补一个非零值，
        /// 理由是"41 为 0 时 AutoCAD 画不出背景框"。这个理由至少是不完整的，
        /// 而它的副作用是确凿的：**DXF 41 是"定义宽度"，AutoCAD 会按它重新折行**。
        /// 实测（受控测试图 + 出图对比）：一行 `MTEXT OVER LINES` 被拆成了两行
        /// `MTEXT OVER` / `LINES`。改用户的文字排版比"没加遮罩"严重得多。
        ///
        /// **标志必须两位置齐**（2026-11-02 出图实测，见 textmask-failure-report.md 9.6）：
        /// bit0 = 背景填充开（属性面板"背景遮罩：是"就是这一位），bit1 = 填充色用图形窗口底色。
        /// 只写 bit1（旧实现）得到 `(90 . 2)` 且库的写出器不会落盘 45/63——
        /// 按 ODA/DXF 规范 45、90、63 必须成套，缺了 AutoCAD 直接当"没有遮罩"：
        /// 属性面板显示"否"，渲染时也不画填充。两位置齐时库会写出
        /// `(90 . 3) (63 . 7) (45 . 1.15)`，AutoCAD 实测**忽略 63 的值、按窗口底色填充**
        /// （monochrome.ctb 下没被打成黑块，横线被干净擦断）。
        /// </summary>
        /// <param name="mtext">要加遮罩的 MTEXT。</param>
        /// <param name="paddingRatio">边界偏移比例（每边 = 字高 × 该值）。</param>
        /// <returns>true 表示这次真的改了（false 表示已经有背景遮罩，跳过）。</returns>
        public static bool ApplyMTextBackground(MText mtext, double paddingRatio)
        {
            if (mtext == null)
            {
                return false;
            }

            // 判重只看 bit0：bit0 才是"遮罩开着"的标志。
            // 旧版本只写过 bit1（90=2、无 45/63），AutoCAD 不认——这种文件要在
            // 这里补齐 bit0，而不是被当成"已有遮罩"跳过。
            bool already = (mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0;

            if (already)
            {
                return false;
            }

            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseBackgroundFillColor
                                         | BackgroundFillFlags.UseDrawingWindowColor;
            // 63 的值在 bit1 下被 AutoCAD 忽略，但 DXF 规范要求 45/90/63 成套出现，
            // 库的写出器也只在 bit0 时落盘这三个组码——给个 ACI 7 让文件自洽。
            mtext.BackgroundColor = new Color(7);
            mtext.BackgroundScale = MaskOffsetFactor(paddingRatio);

            return true;
        }

        /// <summary>
        /// 按轴对齐矩形造 AutoCAD 兼容的 Wipeout。
        ///
        /// ACadSharp 的 <c>Wipeout.SetMaskingArea</c> 将 WCS 顶点编码为规范化像素裁剪路径；
        /// WIPEOUT 自身不应关联 IMAGEDEF/IMAGEDEF_REACTOR。
        /// </summary>
        public static Wipeout CreateWipeout(Bounds2d rect)
        {
            double minX = rect.MinX;
            double minY = rect.MinY;
            double maxX = Math.Max(rect.MaxX, minX + 1e-6);
            double maxY = Math.Max(rect.MaxY, minY + 1e-6);
            return CreateWipeout(new[]
            {
                new XY(minX, minY),
                new XY(maxX, minY),
                new XY(maxX, maxY),
                new XY(minX, maxY)
            });
        }

        private static double NormalizeZero(double value)
        {
            return Math.Abs(value) < 1e-9 ? 0 : Math.Round(value, 9);
        }

        /// <summary>按 WCS 多边形顶点造一个 Wipeout，保留旋转矩形的真实方向。</summary>
        public static Wipeout CreateWipeout(IEnumerable<XY> vertices)
        {
            var wipeout = new Wipeout();
            // Each boundary point is persisted as a 2D DXF code pair; remove floating-point
            // noise from rotated rectangles so Wipeout.IsValid does not see signed-area dust.
            XY[] points = vertices.ToArray();
            if (points.Length < 3)
            {
                throw new ArgumentException("A Wipeout clipping polygon needs at least three vertices.", nameof(vertices));
            }

            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new XY(NormalizeZero(points[i].X), NormalizeZero(points[i].Y));
            }

            wipeout.SetMaskingArea(points);
            wipeout.Color = MaskFrameColor();
            return wipeout;
        }

        /// <summary>
        /// Wipeout 的**实体颜色**——注意它只画边框，不决定填充（填充是"露出背景"）。
        ///
        /// 为什么还要设颜色：`WIPEOUTFRAME` 默认 1 = 显示并打印边框，写在
        /// `ACAD_WIPEOUT_VARS` 里的 70=0 在 AutoCAD 2025 上不生效（实测重开仍报 1），
        /// 于是每个遮罩外面都套一个矩形框。用真彩色白让边框在白纸上消失；
        /// 真彩色不受 monochrome.ctb 重映射（ACI 7 会被打成黑框）。
        /// 深色绘图区里仍能看到白色细框，这是已知限制。
        /// </summary>
        public static Color MaskFrameColor()
        {
            return Color.FromTrueColor(0xFFFFFF);
        }

        /// <summary>
        /// 清掉 MTEXT 的两个背景遮罩位（bit0 显式填充 / bit1 图形背景色），保留其它位
        /// （如 bit4 文字框——那是用户自己开的边框，不是遮罩）。
        ///
        /// 用途：<see cref="MaskTextBox"/> 的 Tight 路径把"原生背景遮罩"换成"紧贴 Wipeout"，
        /// 原生背景不清掉的话，松弛的框形填充还会继续画在 Wipeout 之上，等于白改。
        /// </summary>
        public static void ClearMTextBackground(MText mtext)
        {
            if (mtext == null)
            {
                return;
            }

            mtext.BackgroundFillFlags &= ~(BackgroundFillFlags.UseBackgroundFillColor
                                           | BackgroundFillFlags.UseDrawingWindowColor);
        }

        /// <summary>
        /// 在内存里检查 Wipeout 的编码是否符合 AutoCAD 的读取要求。
        ///
        /// 不做磁盘往返（那由 preflight 的 <c>CapabilityProbe</c> 负责），只拦"明显写歪"：
        /// 裁剪没开、带了 IMAGEDEF、顶点不足、WCS 包围盒退化。返回 false 时调用方
        /// **必须报错而不是换成色块**——本项目没有 Solid 兜底。
        /// </summary>
        public static bool IsWipeoutEncoded(Wipeout wipeout, out string reason)
        {
            reason = null;

            if (wipeout == null)
            {
                reason = "未生成 Wipeout";
                return false;
            }

            if (!wipeout.ClippingState)
            {
                reason = "Wipeout 的裁剪未开启（ClippingState=false），AutoCAD 不会擦除";
                return false;
            }

            if (wipeout.Definition != null)
            {
                reason = "Wipeout 错误地关联了 IMAGEDEF；其影像定义句柄必须为 0";
                return false;
            }

            if (wipeout.ClipBoundaryVertices == null || wipeout.ClipBoundaryVertices.Count < 3)
            {
                reason = "Wipeout 裁剪顶点少于 3 个";
                return false;
            }

            Bounds2d bounds = TextBounds.ForWipeout(wipeout);
            if (!bounds.IsValid || bounds.Width <= 0 || bounds.Height <= 0)
            {
                reason = "Wipeout 的 WCS 包围盒退化：" + bounds;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 把遮罩放到文字的**绘制顺序之前**，并把两者都推到容器实体集合的**末尾 = 最上层**。
        ///
        /// 为什么必须这样做：Wipeout 只擦除它之前画的东西（放在文字后面会把文字自己擦掉），
        /// 而后画的线/填充会盖住先画的遮罩——所以正确顺序是 [...其它图形..., 遮罩, 文字]。
        ///
        /// 为什么用"占位实体"：<c>BlockRecord.Entities</c> 是 <c>CadObjectCollection</c>，
        /// 底层是 **HashSet**，没有按位 Insert。直接"Remove(文字) → Add(遮罩) → Add(文字)"
        /// 时，文字刚释放的槽位会被 Add(遮罩) 立刻复用——遮罩留在文字原来的位置（还在
        /// 其它图形下面），文字则重新拿到一个新槽（在末尾）。实测（2026-11-02，出图验证）
        /// 这样线照样穿过遮罩。占位实体先把释放的槽位填掉，逼后续 Add 走新槽：
        /// <code>
        ///     Remove(文字)      → 释放文字的旧槽
        ///     Add(占位)          → 占位吃掉旧槽，freeList 清空
        ///     Add(遮罩)          → 遮罩 = 新末尾槽
        ///     Add(文字)          → 文字 = 新末尾槽（在遮罩之上）
        ///     Remove(占位)       → 旧槽变空洞，枚举时跳过
        /// </code>
        /// 占位实体在保存前就被移除，不会出现在文件里。
        ///
        /// <paramref name="anchor"/> 是"文字在实体流里的位置代表"：普通文字就是它自己，
        /// 块属性则是它所属的 <c>Insert</c>——属性不在 <c>BlockRecord.Entities</c> 里，
        /// 把它塞进去会改掉它的 Owner、直接破坏块参照。
        /// </summary>
        public static void PlaceMaskBefore(BlockRecord space, Entity anchor, Entity mask)
        {
            ReorderToTop(space, anchor, mask);
        }

        /// <summary>
        /// 把单个实体移到容器实体集合的**末尾 = 绘制顺序最上层**。
        ///
        /// 不用 SORTENTS 排序表：ACadSharp 写出的块记录扩展字典 AutoCAD 读不到
        /// （ACAD_SORTENTS 挂在那里；2026-11-02 dictsearch 实测返回 nil，连库自己
        /// 往返 AutoCAD 写的排序表都会丢）。集合顺序是没有排序表时唯一可靠的绘制顺序。
        ///
        /// 代价：Remove + Add 会给实体分配**新句柄**，调用方要在变更说明里写清楚。
        /// </summary>
        public static void BringToFront(BlockRecord space, Entity entity)
        {
            ReorderToTop(space, entity, null);
        }

        /// <summary>
        /// 见 <see cref="PlaceMaskBefore"/>：占位实体占住释放的槽位，
        /// 把 <paramref name="first"/>（可选，遮罩）和 <paramref name="second"/>（文字）
        /// 依次推到集合末尾。
        /// </summary>
        private static void ReorderToTop(BlockRecord space, Entity second, Entity first)
        {
            if (space == null || second == null)
            {
                return;
            }

            var placeholder = new Point(new XYZ(0, 0, 0));

            space.Entities.Remove(second);
            if (first != null && first.Owner == space.Owner)
            {
                // 遮罩还没进集合：先摘文字再摘它没有意义，但它必须排在文字之前，
                // 所以也在占位之后、文字之前加入。
                space.Entities.Remove(first);
            }

            space.Entities.Add(placeholder);
            if (first != null)
            {
                space.Entities.Add(first);
            }

            space.Entities.Add(second);
            space.Entities.Remove(placeholder);
        }
    }
}
