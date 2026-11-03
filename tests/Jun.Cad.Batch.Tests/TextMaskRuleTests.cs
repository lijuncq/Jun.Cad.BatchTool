using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Config;
using Jun.Cad.Batch.Engine;
using Jun.Cad.Batch.Rules;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 文字遮罩的回归用例。
    ///
    /// 2026-11-02 再次整体改写，锁的是"只剩原生遮罩"这一版行为：
    ///
    /// | 旧行为 | 为什么不再允许 |
    /// |---|---|
    /// | 2D Solid 兜底 | 不透明色块，`monochrome.ctb` 会把它打成黑块；不是遮罩 |
    /// | 默认"仅遮挡时"加 | 需求是"所有文字默认都加"，判遮挡反而漏掉大量文字 |
    /// | `MaskPadding` 绝对值 | 与字高无关，换个字高就错；改成"字高 × 比例" |
    /// | 已补过 `RectangleWidth` | DXF 41 是定义宽度，会触发重新折行（实测一行变两行） |
    /// | 块内文字一律跳过 | 30250-001 一张图就有 318 个块内文字被丢掉 |
    /// | 标注默认不处理 | 标注文字同样是"文字"，默认应当加 |
    /// </summary>
    public class TextMaskRuleTests
    {
        // ------------------------------------------------------------------ 准备

        /// <summary>造一个"真实图纸里长什么样"的 MTEXT：`RectangleWidth = 0`（AutoCAD 自动排版）。</summary>
        private static MText NewMText(string value, double height)
        {
            return new MText
            {
                Value = value,
                InsertPoint = new XYZ(100, 100, 0),
                Height = height,
                RectangleWidth = 0,          // ← 真实图纸的常态
                RectangleHeight = 0,
                AttachmentPoint = AttachmentPointType.MiddleLeft
            };
        }

        private static TextEntity NewText(string value, double height)
        {
            return NewTextAt(value, height, 0, 0);
        }

        private static TextEntity NewTextAt(string value, double height, double x, double y)
        {
            return new TextEntity
            {
                Value = value,
                InsertPoint = new XYZ(x, y, 0),
                Height = height
            };
        }

        private static RuleConfig NewRule(Action<RuleAction> configure = null)
        {
            var action = new RuleAction
            {
                MaskMode = "Auto",
                MaskLayer = "_MASK"
            };

            if (configure != null)
            {
                configure(action);
            }

            return new RuleConfig
            {
                Name = "遮罩",
                Type = "TextMask",
                Filter = new RuleFilter(),
                Action = action
            };
        }

        private static BatchConfig ConfigFor(Action<RuleAction> configure = null)
        {
            return new BatchConfig { Rules = new List<RuleConfig> { NewRule(configure) } };
        }

        /// <summary>跑一条 TextMask 规则，返回结果。</summary>
        private static RuleExecutionResult RunRule(CadDocument doc, BatchConfig config)
        {
            using (var log = new BatchLogger(TextWriter.Null, null, true))
            {
                var ctx = new RuleContext(doc, config, config.Rules[0], log, new ResourceLocator());
                var result = new RuleExecutionResult(config.Rules[0].Name, config.Rules[0].Type);

                new TextMaskRule().Execute(ctx, result);
                return result;
            }
        }

        private static RuleExecutionResult RunRule(Entity entity, BatchConfig config)
        {
            var doc = new CadDocument();
            doc.ModelSpace.Entities.Add(entity);
            return RunRule(doc, config);
        }

        private static CadDocument DocWith(params Entity[] entities)
        {
            var doc = new CadDocument();
            foreach (Entity entity in entities)
            {
                doc.ModelSpace.Entities.Add(entity);
            }

            return doc;
        }

        // ------------------------------------------------------------------ MTEXT：原生背景

        /// <summary>
        /// MTEXT 走自身背景填充，填充色是**图形背景色**（不是固定 RGB），
        /// 并且写上边界偏移因子 1 + ratio。
        ///
        /// 标志必须两位置齐（2026-11-02 出图实测）：bit0 = 遮罩开（属性面板
        /// "背景遮罩：是"），bit1 = 色源用图形窗口底色。只写 bit1 时库不会落盘
        /// 45/63，AutoCAD 按"没有遮罩"处理。
        /// </summary>
        [Fact]
        public void MTEXT用图形背景色且写入15个百分点偏移()
        {
            MText mtext = NewMText("LIFTING HATCH", 2.5);

            RunRule(mtext, ConfigFor());

            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) != 0,
                "应设置 UseDrawingWindowColor（图形背景色）");
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0,
                "应设置 UseBackgroundFillColor（bit0 = 遮罩开），缺了 AutoCAD 不认");
            Assert.Equal(1.15, mtext.BackgroundScale, 6);
        }

        /// <summary>
        /// 旧版本只写过 bit1（90=2 且没有 45/63），AutoCAD 显示"背景遮罩：否"也不渲染。
        /// 这种文件重跑时必须补齐 bit0，而不是被当成"已有遮罩"跳过。
        /// </summary>
        [Fact]
        public void 旧版的仅图形背景色标志要补齐bit0()
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);
            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseDrawingWindowColor;

            RuleExecutionResult result = RunRule(mtext, ConfigFor());

            Assert.True(result.Changed > 0, "旧版残缺标志应记为改动而不是跳过");
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0,
                "应补上 bit0");
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) != 0,
                "bit1 保留");
            Assert.Equal(1.15, mtext.BackgroundScale, 6);
        }

        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(0.1, 1.1)]
        [InlineData(0.15, 1.15)]
        [InlineData(0.3, 1.3)]
        public void 偏移因子随配置的比例变化(double ratio, double expectedFactor)
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);

            RunRule(mtext, ConfigFor(a => a.MaskPaddingRatio = ratio));

            Assert.Equal(expectedFactor, mtext.BackgroundScale, 6);
        }

        /// <summary>
        /// 核心回归：**绝不改写 RectangleWidth / RectangleHeight**。
        ///
        /// DXF 41 是"定义宽度"，写非零值会让 AutoCAD 按它重新折行。
        /// 旧实现按"估算宽度"补 41（西文按 0.62×字高，真实 SHX 在 0.7 上下），
        /// 估窄 10% 以上 → 一行文字被拆成两行。改用户排版比没加遮罩严重。
        /// </summary>
        [Fact]
        public void 加遮罩不许改文字矩形_否则AutoCAD会重新折行()
        {
            MText mtext = NewMText("MTEXT OVER LINES", 2.5);
            double widthBefore = mtext.RectangleWidth;
            double heightBefore = mtext.RectangleHeight;

            RunRule(mtext, ConfigFor());

            Assert.Equal(widthBefore, mtext.RectangleWidth);
            Assert.Equal(heightBefore, mtext.RectangleHeight);
        }

        [Fact]
        public void 已经有固定宽度的MTEXT也不许被改宽()
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);
            mtext.RectangleWidth = 12.5;

            RunRule(mtext, ConfigFor());

            Assert.Equal(12.5, mtext.RectangleWidth);
        }

        [Fact]
        public void 加遮罩不改文字内容()
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);
            string valueBefore = mtext.Value;

            RunRule(mtext, ConfigFor());

            Assert.Equal(valueBefore, mtext.Value);
        }

        /// <summary>已经开过遮罩（bit0）的不重复处理。</summary>
        [Fact]
        public void 已经有背景遮罩的不重复处理()
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);
            mtext.BackgroundFillFlags |= BackgroundFillFlags.UseBackgroundFillColor;

            RuleExecutionResult result = RunRule(mtext, ConfigFor());

            Assert.True(result.Skipped > 0, "应记为跳过");
        }

        /// <summary>已经有自定义填充色（UseBackgroundFillColor）时也不该被改写成图形背景色。</summary>
        [Fact]
        public void 已有自定义填充色时保持原样()
        {
            MText mtext = NewMText("DRAW LIMIT", 2.5);
            mtext.BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor;
            mtext.BackgroundScale = 1.5;

            RunRule(mtext, ConfigFor());

            Assert.Equal(BackgroundFillFlags.UseBackgroundFillColor, mtext.BackgroundFillFlags);
            Assert.Equal(1.5, mtext.BackgroundScale, 6);
        }

        // ------------------------------------------------------------------ 遮罩框模式与前置

        /// <summary>
        /// 框宽比文字宽的 MTEXT：AutoCAD 的原生背景按**框宽**（DXF 41）填充
        /// （受控出图实测：框 60 / 文字 6，横线擦除断口正好 60），一侧空出一大截。
        /// Auto 模式下改用紧贴 Wipeout——遮罩宽度跟实测文字走，`RectangleWidth`
        /// 绝不动（零折行风险），残留的原生背景标志清掉。
        /// </summary>
        [Fact]
        public void 框宽松弛的MTEXT在Auto模式下改用紧贴遮罩()
        {
            MText mtext = NewMText("DROP ZONE", 2.5);
            mtext.RectangleWidth = 60;   // 文字本体只需要 ~5.6
            CadDocument doc = DocWith(mtext);

            RuleExecutionResult result = RunRule(doc, ConfigFor());

            Assert.Equal(1, result.Changed);
            Wipeout mask = Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) == 0,
                "松弛的原生背景填充应被清掉，否则它会继续画在 Wipeout 之上");
            Assert.Equal(60, mtext.RectangleWidth);

            Bounds2d maskBox = TextBounds.ForWipeout(mask);
            Assert.True(maskBox.Width < 30,
                "遮罩宽度应按实测文字（约 14）而不是框宽 60，实测 " + maskBox.Width);
        }

        /// <summary>没有框宽（41=0）的 MTEXT 原生背景本来就贴文字，Auto 模式保持原生、不新增实体。</summary>
        [Fact]
        public void 框宽贴文字的MTEXT在Auto模式下保持原生()
        {
            MText mtext = NewMText("TIGHT FRAME", 2.5);   // RectangleWidth = 0
            CadDocument doc = DocWith(mtext);

            RuleExecutionResult result = RunRule(doc, ConfigFor());

            Assert.Equal(1, result.Changed);
            Assert.Empty(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0);
        }

        /// <summary>Tight 模式强制紧贴：即使没有框宽也用 Wipeout。</summary>
        [Fact]
        public void Tight模式强制使用紧贴遮罩()
        {
            MText mtext = NewMText("FORCE TIGHT", 2.5);
            CadDocument doc = DocWith(mtext);

            RuleExecutionResult result = RunRule(doc, ConfigFor(a => a.MaskTextBox = "Tight"));

            Assert.Equal(1, result.Changed);
            Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) == 0);
        }

        /// <summary>Native 模式保持旧行为：框松也用原生背景（不建 Wipeout）。</summary>
        [Fact]
        public void Native模式即使框松也用原生背景()
        {
            MText mtext = NewMText("WIDE FRAME", 2.5);
            mtext.RectangleWidth = 60;
            CadDocument doc = DocWith(mtext);

            RuleExecutionResult result = RunRule(doc, ConfigFor(a => a.MaskTextBox = "Native"));

            Assert.Equal(1, result.Changed);
            Assert.Empty(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0);
        }

        [Theory]
        [InlineData("Wide")]
        [InlineData("solid")]
        public void 遮罩框模式未知值直接报错(string mode)
        {
            MText mtext = NewMText("BAD MODE", 2.5);

            var ex = Assert.Throws<RuleValidationException>(
                () => RunRule(mtext, ConfigFor(a => a.MaskTextBox = mode)));

            Assert.Contains("MaskTextBox", ex.Message);
        }

        /// <summary>
        /// 文字前置（默认开）：文字和遮罩挪到容器实体集合末尾 = 绘制最上层，
        /// 后画的线/填充压不住遮罩。代价是句柄变化（SORTENTS 路径走不通，
        /// 见 MaskService.BringToFront：块记录扩展字典写出来 AutoCAD 读不到）。
        /// </summary>
        [Fact]
        public void 默认把遮罩和文字移到绘制顺序最上层()
        {
            MText mtext = NewMText("DROP ZONE", 2.5);
            mtext.RectangleWidth = 60;
            CadDocument doc = DocWith(new Line(new XYZ(0, 0, 0), new XYZ(200, 0, 0)), mtext);

            RunRule(doc, ConfigFor());

            Entity last = doc.ModelSpace.Entities.Last();
            Assert.Same(mtext, last);
            // 遮罩紧挨在文字之前
            Assert.IsType<Wipeout>(doc.ModelSpace.Entities[doc.ModelSpace.Entities.Count - 2]);
        }

        /// <summary>关掉前置时实体保持在原位（原生路径不再动集合）。</summary>
        [Fact]
        public void 关闭前置时原生路径不挪动文字()
        {
            MText mtext = NewMText("STAY PUT", 2.5);
            CadDocument doc = DocWith(new Line(new XYZ(0, 0, 0), new XYZ(200, 0, 0)), mtext);

            RunRule(doc, ConfigFor(a => a.MaskBringTextToFront = false));

            Assert.Same(mtext, doc.ModelSpace.Entities[1]);
            Assert.IsType<Line>(doc.ModelSpace.Entities[0]);
        }

        /// <summary>幂等：对同一输出文件重复运行，TEXT 的 Wipeout 不再逐次叠加。</summary>
        [Fact]
        public void 重复运行不再叠加同位置的Wipeout()
        {
            TextEntity text = NewText("RUN TWICE", 2.5);
            CadDocument doc = DocWith(text);

            RunRule(doc, ConfigFor());
            int afterFirst = doc.ModelSpace.Entities.OfType<Wipeout>().Count();
            Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());

            RuleExecutionResult second = RunRule(doc, ConfigFor());

            Assert.Equal(0, second.Changed);
            Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
        }

        // ------------------------------------------------------------------ TEXT / ATTRIB：Wipeout

        /// <summary>普通 TEXT 没有原生背景，默认就建 Wipeout（不需要额外开开关），且绝不建 Solid。</summary>
        [Fact]
        public void 普通TEXT默认生成Wipeout而不是实色块()
        {
            TextEntity text = NewText("TEXT OVER LINE", 2.5);
            CadDocument doc = DocWith(text);

            RunRule(doc, ConfigFor());

            Wipeout wipeout = Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.Empty(doc.ModelSpace.Entities.OfType<Solid>());
            Assert.True(wipeout.ClippingState);
            Assert.Null(wipeout.Definition);

            List<Entity> kids = doc.ModelSpace.Entities.ToList();
            Assert.True(kids.IndexOf(wipeout) < kids.IndexOf(text),
                "遮罩应排在文字之前，否则会盖住文字本身");
        }

        /// <summary>
        /// 边界偏移 = **字高 × 比例**，每边各一份。字高 10、比例 0.15 → 每边 1.5。
        /// </summary>
        [Fact]
        public void 遮罩边界按字高15个百分点张开()
        {
            TextEntity text = NewText("AB", 10);
            CadDocument doc = DocWith(text);

            RunRule(doc, ConfigFor());

            Bounds2d textBox = TextBounds.ForText(text);
            Bounds2d maskBox = TextBounds.ForWipeout(doc.ModelSpace.Entities.OfType<Wipeout>().Single());

            Assert.Equal(textBox.MinX - 1.5, maskBox.MinX, 3);
            Assert.Equal(textBox.MaxX + 1.5, maskBox.MaxX, 3);
            Assert.Equal(textBox.MinY - 1.5, maskBox.MinY, 3);
            Assert.Equal(textBox.MaxY + 1.5, maskBox.MaxY, 3);
        }

        /// <summary>偏移比例可以配成 0：此时遮罩正好贴合文字矩形。</summary>
        [Fact]
        public void 比例为0时遮罩贴合文字()
        {
            TextEntity text = NewText("AB", 10);
            CadDocument doc = DocWith(text);

            RunRule(doc, ConfigFor(a => a.MaskPaddingRatio = 0));

            Bounds2d textBox = TextBounds.ForText(text);
            Bounds2d maskBox = TextBounds.ForWipeout(doc.ModelSpace.Entities.OfType<Wipeout>().Single());

            Assert.Equal(textBox.MinX, maskBox.MinX, 3);
            Assert.Equal(textBox.MaxY, maskBox.MaxY, 3);
        }

        /// <summary>默认不再要求"有遮挡"：孤立文字也加遮罩。</summary>
        [Fact]
        public void 默认给所有文字加遮罩_不再要求有遮挡()
        {
            CadDocument doc = DocWith(NewText("NOBODY OVER ME", 2.5), NewMText("ALSO ALONE", 2.5));

            RuleExecutionResult result = RunRule(doc, ConfigFor());

            Assert.Equal(2, result.Changed);
            Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
        }

        [Fact]
        public void 打开仅遮挡时没有遮挡的文字被跳过()
        {
            CadDocument doc = DocWith(NewText("NOBODY OVER ME", 2.5));

            RuleExecutionResult result = RunRule(doc, ConfigFor(a => a.MaskOnlyWhenOccluded = true));

            Assert.Equal(0, result.Changed);
            Assert.Empty(doc.ModelSpace.Entities.OfType<Wipeout>());
        }

        [Fact]
        public void 默认不限制遮罩数量_零与负数视为不限()
        {
            // 不同位置：同位置的相同 Wipeout 会被幂等判重合并，不能用来数上限
            var texts = Enumerable.Range(0, 5)
                .Select(i => NewTextAt("T" + i, 2.5, i * 10.0, 0))
                .ToArray();
            CadDocument doc = DocWith(texts);

            RunRule(doc, ConfigFor(a => a.MaxMasksPerDrawing = 0));

            Assert.Equal(5, doc.ModelSpace.Entities.OfType<Wipeout>().Count());
        }

        [Theory]
        [InlineData("Solid")]
        [InlineData("solid ")]
        [InlineData("Unknown")]
        public void 非原生遮罩模式直接报错_不静默降级(string mode)
        {
            CadDocument doc = DocWith(NewText("TEXT OVER LINE", 2.5));

            var ex = Assert.Throws<RuleValidationException>(() => RunRule(doc, ConfigFor(a => a.MaskMode = mode)));

            Assert.Contains("MaskMode", ex.Message);
            Assert.Empty(doc.ModelSpace.Entities.OfType<Solid>());
        }

        /// <summary>
        /// 建 Wipeout 时必须把 `WIPEOUTFRAME` 关掉。
        ///
        /// 默认值 1 = 显示并打印边框，出图时每个文字外面都套一个矩形框
        /// （受控图实测可见）。变量挂在根字典 `ACAD_WIPEOUT_VARS` 下。
        /// </summary>
        [Fact]
        public void 建Wipeout时关掉边框()
        {
            CadDocument doc = DocWith(NewText("TEXT OVER LINE", 2.5));

            RunRule(doc, ConfigFor());

            Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());

            ACadSharp.Objects.WipeoutVariables variables = doc.RootDictionary
                .GetEntry<ACadSharp.Objects.WipeoutVariables>(MaskService.WipeoutVariablesKey);

            Assert.NotNull(variables);
            Assert.False(variables.DisplayImageFrame);
        }

        /// <summary>只处理 MTEXT 时不写 `ACAD_WIPEOUT_VARS`：不给用户图纸凭空加设置。</summary>
        [Fact]
        public void 只加MTEXT背景时不写Wipeout全局变量()
        {
            CadDocument doc = DocWith(NewMText("ONLY MTEXT", 2.5));

            RunRule(doc, ConfigFor());

            Assert.Null(doc.RootDictionary.GetEntry<ACadSharp.Objects.WipeoutVariables>(MaskService.WipeoutVariablesKey));
        }

        // ------------------------------------------------------------------ 块

        /// <summary>
        /// 块定义里的 TEXT 也要加遮罩：遮罩写进**块定义**，排在文字之前。
        ///
        /// 旧实现无条件跳过 `Depth > 0`，30250-001 的 318 个块内文字全丢。
        /// </summary>
        [Fact]
        public void 块定义里的TEXT默认也加遮罩()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);

            TextEntity text = NewText("BLOCK TEXT", 2.5);
            block.Entities.Add(text);
            doc.ModelSpace.Entities.Add(new Insert(block));

            RunRule(doc, ConfigFor());

            List<Entity> kids = block.Entities.ToList();
            Wipeout mask = kids.OfType<Wipeout>().FirstOrDefault();

            Assert.NotNull(mask);
            Assert.True(mask.ClippingState);
            Assert.Null(mask.Definition);
            Assert.True(kids.IndexOf(mask) < kids.IndexOf(text),
                "遮罩应排在文字之前，否则会盖住文字");
        }

        [Fact]
        public void 关掉MaskInsideBlocks后块内文字被跳过()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);
            block.Entities.Add(NewText("BLOCK TEXT", 2.5));
            doc.ModelSpace.Entities.Add(new Insert(block));

            RunRule(doc, ConfigFor(a => a.MaskInsideBlocks = false));

            Assert.Empty(block.Entities.OfType<Wipeout>());
        }

        /// <summary>块内 MTEXT 走原生背景：不新增实体，但标志要置上。</summary>
        [Fact]
        public void 块定义里的MTEXT走原生背景()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);

            MText mtext = NewMText("BLOCK MTEXT", 2.5);
            block.Entities.Add(mtext);
            doc.ModelSpace.Entities.Add(new Insert(block));

            RunRule(doc, ConfigFor());

            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseDrawingWindowColor) != 0);
            Assert.True((mtext.BackgroundFillFlags & BackgroundFillFlags.UseBackgroundFillColor) != 0);
            Assert.Empty(block.Entities.OfType<Wipeout>());
            Assert.Empty(block.Entities.OfType<Solid>());
        }

        /// <summary>常属性只存在于定义里（插入时没有 ATTRIB），必须靠 ATTDEF 自己加遮罩。</summary>
        [Fact]
        public void 常属性定义加遮罩()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);

            var attdef = new AttributeDefinition
            {
                Value = "CONST",
                Tag = "SCALE",
                InsertPoint = new XYZ(0, 0, 0),
                Height = 2.5,
                Flags = AttributeFlags.Constant
            };
            block.Entities.Add(attdef);
            doc.ModelSpace.Entities.Add(new Insert(block));

            RunRule(doc, ConfigFor());

            Assert.Single(block.Entities.OfType<Wipeout>());
        }

        /// <summary>非常属性在插入时会生成 ATTRIB，定义里的 ATTDEF 不能重复加遮罩。</summary>
        [Fact]
        public void 非常属性定义不重复加遮罩()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);

            block.Entities.Add(new AttributeDefinition
            {
                Value = "VAR",
                Tag = "TAG",
                InsertPoint = new XYZ(0, 0, 0),
                Height = 2.5,
                Flags = AttributeFlags.None
            });

            var insert = new Insert(block);
            doc.ModelSpace.Entities.Add(insert);

            RunRule(doc, ConfigFor());

            Assert.Empty(block.Entities.OfType<Wipeout>());
        }

        /// <summary>
        /// 块属性：遮罩排在**所属块参照**之前，且不能把属性搬出 Insert。
        ///
        /// 旧实现按"文字实体"处理属性：从空间移出（无效）→ 追加遮罩 → 再把属性加进空间实体表，
        /// 这会把属性的 Owner 改成块记录、直接破坏块参照。
        /// </summary>
        [Fact]
        public void 块属性加遮罩且不破坏块参照归属()
        {
            var doc = new CadDocument();
            var block = new BlockRecord("TBLK");
            doc.BlockRecords.Add(block);

            var insert = new Insert(block);
            var attrib = new AttributeEntity
            {
                Value = "A-01",
                Tag = "TAG",
                InsertPoint = new XYZ(10, 10, 0),
                Height = 2.5
            };
            insert.Attributes.Add(attrib);
            doc.ModelSpace.Entities.Add(insert);

            RunRule(doc, ConfigFor());

            Assert.Single(insert.Attributes);
            Assert.Same(insert, attrib.Owner);

            Wipeout wipeout = Assert.Single(doc.ModelSpace.Entities.OfType<Wipeout>());
            List<Entity> kids = doc.ModelSpace.Entities.ToList();
            Assert.True(kids.IndexOf(wipeout) < kids.IndexOf(insert),
                "遮罩应排在块参照之前，否则会盖住属性文字");
            Assert.DoesNotContain(attrib, kids);
        }

        // ------------------------------------------------------------------ 标注

        private static (CadDocument Doc, DimensionLinear Dim, Entity Text) DimensionCase(bool cachedIsMText)
        {
            var doc = new CadDocument();
            var block = new BlockRecord("*D1");
            doc.BlockRecords.Add(block);

            Entity cached = cachedIsMText
                ? (Entity)new MText
                {
                    Value = "1250",
                    InsertPoint = new XYZ(50, 20, 0),
                    Height = 2.5,
                    AttachmentPoint = AttachmentPointType.MiddleCenter
                }
                : new TextEntity
                {
                    Value = "1250",
                    InsertPoint = new XYZ(50, 20, 0),
                    Height = 2.5
                };

            block.Entities.Add(cached);

            var dim = new DimensionLinear
            {
                DefinitionPoint = new XYZ(0, 0, 0),
                InsertionPoint = new XYZ(50, 20, 0),
                TextMiddlePoint = new XYZ(50, 20, 0),
                Block = block
            };

            doc.ModelSpace.Entities.Add(dim);
            return (doc, dim, cached);
        }

        /// <summary>标注默认写 DIMTFILL=1（图形背景色）的样式覆盖。</summary>
        [Fact]
        public void 标注文字默认写图形背景色覆盖()
        {
            (CadDocument doc, DimensionLinear dim, Entity _) = DimensionCase(true);

            RuleExecutionResult result = RunRule(doc, ConfigFor());

            Assert.True(dim.HasStyleOverride);
            Assert.True(dim.GetStyleOverrideMap().DxfProperties.ContainsKey(69));
            Assert.Equal(1, result.Changed);
            // 不建实体、不动 *D 缓存
            Assert.Empty(doc.ModelSpace.Entities.OfType<Wipeout>());
            Assert.Empty(doc.ModelSpace.Entities.OfType<Solid>());
            Assert.Null(FindNewWipeout(dim.Block));
        }

        /// <summary>已带 DIMTFILL=1 覆盖的标注不再重复处理。</summary>
        [Fact]
        public void 标注已有背景填充时跳过()
        {
            (CadDocument doc, DimensionLinear dim, Entity _) = DimensionCase(true);
            ApplyDrawingBackgroundOverride(dim);

            RuleExecutionResult result = RunRule(doc, ConfigFor());

            Assert.Equal(0, result.Changed);
            Assert.True(result.Skipped > 0);
        }

        /// <summary>已带其它样式覆盖（如 DIMTXT）时，追加 DIMTFILL 不能抹掉原有覆盖。</summary>
        [Fact]
        public void 标注覆盖是追加而不是清空()
        {
            (CadDocument doc, DimensionLinear dim, Entity _) = DimensionCase(true);

            // 先手工造一个 DIMTFILL 之外的覆盖（77 = DIMTAD）
            DxfClassMap existing = new DxfClassMap { Name = ACadSharp.Tables.DimensionStyle.StyleOverrideEntryName };
            DxfProperty dimtad = DxfClassMap.Create<ACadSharp.Tables.DimensionStyle>().DxfProperties[77];
            dimtad.StoredValue = (short)1;
            existing.DxfProperties.Add(77, dimtad);
            dim.SetStyleOverrideMap(existing);

            RunRule(doc, ConfigFor());

            DxfClassMap map = dim.GetStyleOverrideMap();
            Assert.True(map.DxfProperties.ContainsKey(77), "原有覆盖必须保留");
            Assert.True(map.DxfProperties.ContainsKey(69), "新覆盖必须写入");
        }

        private static Wipeout FindNewWipeout(ACadSharp.Tables.BlockRecord block)
        {
            if (block == null)
            {
                return null;
            }

            return block.Entities.OfType<Wipeout>().FirstOrDefault();
        }

        [Fact]
        public void 标注遮罩可以整条关掉()
        {
            (CadDocument doc, DimensionLinear dim, Entity cached) = DimensionCase(true);

            RunRule(doc, ConfigFor(a => a.DimensionMaskMode = "Off"));

            Assert.False(dim.HasStyleOverride);
            Assert.Equal(BackgroundFillFlags.None, ((MText)cached).BackgroundFillFlags);
            Assert.Empty(dim.Block.Entities.OfType<Wipeout>());
        }

        [Fact]
        public void 标注文字范围限制为显式文字时跳过测量值()
        {
            (CadDocument doc, DimensionLinear dim, Entity cached) = DimensionCase(true);

            RunRule(doc, ConfigFor(a => a.DimensionTextMode = "ExplicitOnly"));

            Assert.False(dim.HasStyleOverride);
            Assert.Equal(BackgroundFillFlags.None, ((MText)cached).BackgroundFillFlags);
        }

        [Fact]
        public void 标注显式文字会被处理()
        {
            (CadDocument doc, DimensionLinear dim, Entity cached) = DimensionCase(true);
            dim.Text = "1250 TYP";

            RunRule(doc, ConfigFor(a => a.DimensionTextMode = "ExplicitOnly"));

            Assert.True(dim.HasStyleOverride);
            Assert.True(dim.GetStyleOverrideMap().DxfProperties.ContainsKey(69));
        }

        /// <summary>与产品规则同一套写法：只追加 DIMTFILL=1 一项覆盖。</summary>
        private static void ApplyDrawingBackgroundOverride(Dimension dimension)
        {
            DxfClassMap map = new DxfClassMap { Name = ACadSharp.Tables.DimensionStyle.StyleOverrideEntryName };
            DxfProperty fill = DxfClassMap.Create<ACadSharp.Tables.DimensionStyle>().DxfProperties[69];
            fill.StoredValue = (short)ACadSharp.Tables.DimensionTextBackgroundFillMode.DrawingBackgroundColor;
            map.DxfProperties.Add(69, fill);
            dimension.SetStyleOverrideMap(map);
        }

        // ------------------------------------------------------------------ Wipeout 编码

        [Fact]
        public void WipeoutRect编码为归一化像素坐标且不引用ImageDefinition()
        {
            Wipeout wipeout = MaskService.CreateWipeout(new Bounds2d(0, 0, 100, 50));

            Assert.True(wipeout.ClippingState);
            Assert.Null(wipeout.Definition);
            Assert.Equal(new XYZ(0, 0, 0), wipeout.InsertPoint);
            Assert.Equal(new XYZ(100, 0, 0), wipeout.UVector);
            Assert.Equal(new XYZ(0, 100, 0), wipeout.VVector);
            Assert.Equal(new XY(1, 1), wipeout.Size);
            Assert.Equal(4, wipeout.ClipBoundaryVertices.Count);
            Assert.Equal(new XY(-0.5, 0.5), wipeout.ClipBoundaryVertices[0]);
            Assert.Equal(new XY(0.5, 0), wipeout.ClipBoundaryVertices[2]);

            Bounds2d actual = TextBounds.ForWipeout(wipeout);
            Assert.Equal(0, actual.MinX, 3);
            Assert.Equal(0, actual.MinY, 3);
            Assert.Equal(100, actual.Width, 3);
            Assert.Equal(50, actual.Height, 3);
        }

        [Fact]
        public void Wipeout保留旋转矩形方向而非包围盒方向()
        {
            XY[] corners = TextBounds.RotatedRectVertices(new XYZ(20, 30, 0), 0, 0, 10, 2, Math.PI / 2);
            Wipeout wipeout = MaskService.CreateWipeout(corners);

            Bounds2d actual = TextBounds.ForWipeout(wipeout);
            Assert.Equal(18, actual.MinX, 3);
            Assert.Equal(20, actual.MaxX, 3);
            Assert.Equal(30, actual.MinY, 3);
            Assert.Equal(40, actual.MaxY, 3);
            Assert.Equal(4, wipeout.ClipBoundaryVertices.Count);

            // 旋转矩形的裁剪路径不是一个轴对齐矩形，而是仍有倾角的四边形。
            Assert.NotEqual(wipeout.ClipBoundaryVertices[0].Y, wipeout.ClipBoundaryVertices[1].Y);
        }

        [Fact]
        public void 编码自检能拦下未开裁剪的Wipeout()
        {
            var wipeout = MaskService.CreateWipeout(new Bounds2d(0, 0, 10, 5));
            wipeout.ClippingState = false;

            string reason;
            Assert.False(MaskService.IsWipeoutEncoded(wipeout, out reason));
            Assert.Contains("裁剪", reason);
        }
    }
}
