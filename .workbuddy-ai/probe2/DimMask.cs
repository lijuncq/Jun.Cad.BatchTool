using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    /// <summary>
    /// 查标注文字遮罩为什么判"无遮挡"。
    ///
    /// 打印每个 DIMENSION 的：*D 块里的文字实体位置、TextBounds 算出的矩形、
    /// 以及这个矩形是否落在某个 HATCH 的包围盒里。
    /// 三者对不上就能定位是"锚点错"还是"遮挡判定错"。
    /// </summary>
    internal static class DimMask
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            BlockRecord model = doc.ModelSpace;

            var hatches = new List<KeyValuePair<Bounds2d, Hatch>>();
            foreach (Entity e in model.Entities)
            {
                var h = e as Hatch;
                if (h == null)
                {
                    continue;
                }

                Bounds2d hb;
                if (TextBounds.TryGet(h, out hb))
                {
                    hatches.Add(new KeyValuePair<Bounds2d, Hatch>(hb, h));
                }
            }

            Console.WriteLine(string.Format("模型空间 HATCH {0} 个（拿到包围盒 {1} 个）", CountHatches(model), hatches.Count));

            // 遮罩图层的状态：冻结/关闭都会让 Wipeout 完全不显示
            foreach (Layer layer in doc.Layers)
            {
                if (layer.Name.IndexOf("_MASK", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Console.WriteLine(string.Format(
                    "图层 {0}：IsOn={1} PlotFlag={2} 颜色={3} Flags={4}",
                    layer.Name, layer.IsOn, layer.PlotFlag, layer.Color, layer.Flags));
            }

            // 统计所有块定义（含 *D 匿名块）里的 Wipeout：标注遮罩塞的就是这里
            int wipeouts = 0;
            int blocksWithWipeout = 0;
            foreach (BlockRecord br in doc.BlockRecords)
            {
                int n = 0;
                foreach (Entity be in br.Entities)
                {
                    if (be is Wipeout)
                    {
                        n++;
                    }
                }

                if (n > 0)
                {
                    wipeouts += n;
                    blocksWithWipeout++;
                }
            }

            int modelWipeouts = 0;
            foreach (Entity me in model.Entities)
            {
                if (me is Wipeout)
                {
                    modelWipeouts++;
                }
            }

            Console.WriteLine(string.Format("Wipeout：模型空间 {0} 个；块定义内 {1} 个（分布在 {2} 个块里）",
                modelWipeouts, wipeouts, blocksWithWipeout));

            // 输出图里标注块内 Wipeout 的位置 vs 文字插入点：验证遮罩有没有盖在字上
            foreach (BlockRecord br in doc.BlockRecords)
            {
                if (!br.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Wipeout wipe = null;
                MText mt = null;
                TextEntity tx = null;
                foreach (Entity be in br.Entities)
                {
                    if (be is Wipeout) wipe = (Wipeout)be;
                    else if (be is MText) mt = (MText)be;
                    else if (be is TextEntity) tx = (TextEntity)be;
                }

                if (wipe == null)
                {
                    continue;
                }

                Bounds2d wb;
                bool ok = TextBounds.TryGet(wipe, out wb);
                Console.WriteLine(string.Format(
                    "{0,-10} Wipeout[{1}] X[{2:0.##},{3:0.##}] Y[{4:0.##},{5:0.##}]",
                    br.Name, ok ? "有" : "无", wb.MinX, wb.MaxX, wb.MinY, wb.MaxY));

                if (mt != null)
                {
                    Console.WriteLine(string.Format("            MText 插入点({0:0.##},{1:0.##}) 字高 {2:0.##} 内容 '{3}'",
                        mt.InsertPoint.X, mt.InsertPoint.Y, mt.Height, Trim(mt.Value)));
                }
                else if (tx != null)
                {
                    Console.WriteLine(string.Format("            Text  插入点({0:0.##},{1:0.##}) 字高 {2:0.##} 内容 '{3}'",
                        tx.InsertPoint.X, tx.InsertPoint.Y, tx.Height, Trim(tx.Value)));
                }
            }

            // 列出全部标注的文字内容，用来对上用户截图里的数字
            var wanted = new HashSet<string> { "2850", "1550", "2500" };
            // 在**所有块定义**里找截图那三个数字
            Console.WriteLine("=== 在块定义里搜 2850 / 1550 / 2500 ===");
            // （下面这段只在单文件模式下用；全目录扫描走 scan 命令）
            foreach (BlockRecord br in doc.BlockRecords)
            {
                foreach (Entity be in br.Entities)
                {
                    string val = null;
                    var m = be as MText;
                    var t = be as TextEntity;
                    if (m != null) val = m.Value;
                    else if (t != null) val = t.Value;
                    if (val == null) continue;

                    string plain = val.Substring(val.LastIndexOf(';') + 1).Trim();
                    if (plain != "2850" && plain != "1550" && plain != "2500") continue;

                    Console.WriteLine(string.Format("  块 {0,-18} 实体 {1,-8} 文字 '{2}'", br.Name, be.GetType().Name, plain));
                }
            }

            // 绘制顺序：无排序表时按句柄升序绘制，句柄大的画在上面
            var order = new List<KeyValuePair<ulong, string>>();
            foreach (Entity e in model.Entities)
            {
                if (e is Hatch) order.Add(new KeyValuePair<ulong, string>(e.Handle, "HATCH"));
                else if (e is Dimension) order.Add(new KeyValuePair<ulong, string>(e.Handle, "DIM"));
            }
            order.Sort((a, b) => a.Key.CompareTo(b.Key));
            Console.WriteLine("=== 模型空间 HATCH/DIMENSION 按句柄排序（后画的在上面）===");
            var sb = new System.Text.StringBuilder();
            foreach (var kv in order)
            {
                sb.Append(string.Format("{0}:{1} ", kv.Value, kv.Key.ToString("X")));
            }
            Console.WriteLine("  " + sb.ToString());

            Console.WriteLine("=== 全部标注文字 ===");
            foreach (Entity e in model.Entities)
            {
                var d = e as Dimension;
                if (d == null) continue;

                string shown = null;
                if (d.Block != null)
                {
                    foreach (Entity c in d.Block.Entities)
                    {
                        var m = c as MText;
                        var t = c as TextEntity;
                        if (m != null) { shown = m.Value; break; }
                        if (t != null) { shown = t.Value; break; }
                    }
                }

                if (shown == null) continue;
                // "\A1;25200" → 取最后一个分号之后的部分
                string plain = shown.Substring(shown.LastIndexOf(';') + 1).Trim();
                string mark = wanted.Contains(plain) ? "   <<< 截图里的数字" : "";
                bool hasWipe = false;
                if (d.Block != null)
                {
                    foreach (Entity c in d.Block.Entities)
                    {
                        if (c is Wipeout) { hasWipe = true; break; }
                    }
                }

                Console.WriteLine(string.Format("  句柄 {0} 文字 '{1}' 遮罩={2}{3}", d.Handle, plain, hasWipe ? "有" : "无", mark));
            }

            int index = 0;
            foreach (Entity e in model.Entities)
            {
                var dim = e as Dimension;
                if (dim == null)
                {
                    continue;
                }

                index++;
                Console.WriteLine(string.Format("--- DIMENSION #{0} 句柄 {1} 类型 {2}", index, dim.Handle, dim.GetType().Name));

                // 1) *D 块里的文字实体
                if (dim.Block != null)
                {
                    foreach (Entity child in dim.Block.Entities)
                    {
                        var mt = child as MText;
                        var tx = child as TextEntity;
                        if (mt != null)
                        {
                            Console.WriteLine(string.Format("    *D 块 MText 插入点 ({0:0.###}, {1:0.###}) 字高 {2:0.###} 内容 '{3}'",
                                mt.InsertPoint.X, mt.InsertPoint.Y, mt.Height, Trim(mt.Value)));
                        }
                        else if (tx != null)
                        {
                            Console.WriteLine(string.Format("    *D 块 Text  插入点 ({0:0.###}, {1:0.###}) 字高 {2:0.###} 内容 '{3}'",
                                tx.InsertPoint.X, tx.InsertPoint.Y, tx.Height, Trim(tx.Value)));
                        }
                    }
                }

                // 2) TextBounds 的锚点
                XYZ anchor;
                double height;
                double rotation;
                if (TextBounds.TryGetDimensionTextAnchor(dim, out anchor, out height, out rotation))
                {
                    Console.WriteLine(string.Format("    锚点 ({0:0.###}, {1:0.###}) 字高 {2:0.###}", anchor.X, anchor.Y, height));
                }
                else
                {
                    Console.WriteLine("    锚点：取不到");
                }

                // 3) TextBounds 整体包围盒（规则里 target.Rect 的来源之一）
                Bounds2d box;
                if (TextBounds.TryGet(dim, out box))
                {
                    Console.WriteLine(string.Format("    ForDimension 矩形 X[{0:0.##},{1:0.##}] Y[{2:0.##},{3:0.##}] 宽 {4:0.###} 高 {5:0.###}",
                        box.MinX, box.MaxX, box.MinY, box.MaxY, box.Width, box.Height));
                }
                else
                {
                    Console.WriteLine("    ForDimension 矩形：取不到");
                }

                // 4) 有多少 HATCH 包住这个锚点
                int covering = 0;
                foreach (KeyValuePair<Bounds2d, Hatch> pair in hatches)
                {
                    if (pair.Key.Contains(anchor.X, anchor.Y))
                    {
                        covering++;
                    }
                }

                Console.WriteLine("    包住锚点的 HATCH 数：" + covering);
            }
        }

        private static int CountHatches(BlockRecord space)
        {
            int n = 0;
            foreach (Entity e in space.Entities)
            {
                if (e is Hatch)
                {
                    n++;
                }
            }

            return n;
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 24 ? value : value.Substring(0, 24) + "…";
        }
    }
}
