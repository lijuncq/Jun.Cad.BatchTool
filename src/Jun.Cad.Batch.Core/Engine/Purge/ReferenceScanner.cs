using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace Jun.Cad.Batch.Engine.Purge
{
    /// <summary>
    /// 引用扫描：算出"哪些符号表项仍被引用"。
    ///
    /// 为什么必须自己写：AutoCAD 版靠 <c>Database.Purge(ObjectIdCollection)</c> 让内核判定，
    /// 离线工具没有这个 API。自己判定的风险是"误删仍在使用的东西"，
    /// 所以这里的原则是**宁可少删**：
    ///   * 只从"活的容器"出发（布局块 + 被引用到的块定义）；
    ///   * 不认识的实体类型一律视为"可能引用了任何东西"，对应类别整体放弃清理（保守回退）；
    ///   * 标准项（0 层、Continuous、Standard、当前项）永不列入候选。
    /// </summary>
    public sealed class PurgeAnalysis
    {
        public PurgeAnalysis()
        {
            LiveBlocks = new HashSet<BlockRecord>();
            UsedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UsedLinetypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UsedTextStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UsedDimStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UnresolvedEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>仍在使用（或被使用中的块引用）的块定义。</summary>
        public HashSet<BlockRecord> LiveBlocks { get; private set; }

        public HashSet<string> UsedLayers { get; private set; }

        public HashSet<string> UsedLinetypes { get; private set; }

        public HashSet<string> UsedTextStyles { get; private set; }

        public HashSet<string> UsedDimStyles { get; private set; }

        /// <summary>扫描中遇到的、本类不认识是否引用符号表的实体类型。</summary>
        public HashSet<string> UnresolvedEntityTypes { get; private set; }
    }

    /// <summary>引用扫描器。</summary>
    public static class ReferenceScanner
    {
        /// <summary>
        /// 分析整个文档的引用关系。
        /// </summary>
        public static PurgeAnalysis Analyze(CadDocument doc)
        {
            var analysis = new PurgeAnalysis();
            if (doc == null)
            {
                return analysis;
            }

            // ---- 1) 找出"活的块定义"：布局块 + 从它们可达的块定义 ----
            var queue = new Queue<BlockRecord>();

            foreach (BlockRecord block in DatabaseWalker.GetSpaces(doc, ScanScope.ModelAndLayouts))
            {
                if (analysis.LiveBlocks.Add(block))
                {
                    queue.Enqueue(block);
                }
            }

            while (queue.Count > 0)
            {
                BlockRecord block = queue.Dequeue();
                foreach (Entity ent in block.Entities)
                {
                    var ins = ent as Insert;
                    if (ins == null || ins.Block == null)
                    {
                        continue;
                    }

                    if (analysis.LiveBlocks.Add(ins.Block))
                    {
                        queue.Enqueue(ins.Block);
                    }
                }
            }

            // ---- 2) 从活块里的实体收集引用 ----
            foreach (BlockRecord block in analysis.LiveBlocks)
            {
                foreach (Entity ent in block.Entities)
                {
                    CollectFromEntity(ent, analysis);
                }
            }

            // ---- 3) 表项之间的隐式引用 ----
            // 图层 → 线型
            foreach (string layerName in new List<string>(analysis.UsedLayers))
            {
                Layer layer;
                if (doc.Layers.TryGetValue(layerName, out layer) && layer != null && layer.LineType != null)
                {
                    analysis.UsedLinetypes.Add(layer.LineType.Name);
                }
            }

            // 标注样式 → 文字样式 / 线型（只有"被用到"的标注样式才把依赖带活）
            foreach (string dimStyleName in new List<string>(analysis.UsedDimStyles))
            {
                DimensionStyle ds;
                if (!doc.DimensionStyles.TryGetValue(dimStyleName, out ds) || ds == null)
                {
                    continue;
                }

                if (ds.Style != null)
                {
                    analysis.UsedTextStyles.Add(ds.Style.Name);
                }

                if (ds.LineType != null)
                {
                    analysis.UsedLinetypes.Add(ds.LineType.Name);
                }
            }

            // ---- 4) 当前项永远算"在用"（即使图纸里一个对象都没用）----
            AddProtectedCurrent(doc, analysis);

            return analysis;
        }

        private static void CollectFromEntity(Entity ent, PurgeAnalysis analysis)
        {
            if (ent == null)
            {
                return;
            }

            if (ent.Layer != null)
            {
                analysis.UsedLayers.Add(ent.Layer.Name);
            }

            if (ent.LineType != null)
            {
                analysis.UsedLinetypes.Add(ent.LineType.Name);
            }

            var text = ent as TextEntity;
            if (text != null)
            {
                if (text.Style != null)
                {
                    analysis.UsedTextStyles.Add(text.Style.Name);
                }

                return;
            }

            var mtext = ent as MText;
            if (mtext != null)
            {
                if (mtext.Style != null)
                {
                    analysis.UsedTextStyles.Add(mtext.Style.Name);
                }

                return;
            }

            var dim = ent as Dimension;
            if (dim != null)
            {
                if (dim.Style != null)
                {
                    analysis.UsedDimStyles.Add(dim.Style.Name);

                    if (dim.Style.Style != null)
                    {
                        analysis.UsedTextStyles.Add(dim.Style.Style.Name);
                    }

                    if (dim.Style.LineType != null)
                    {
                        analysis.UsedLinetypes.Add(dim.Style.LineType.Name);
                    }
                }

                return;
            }

            if (ent is Insert || ent is Hatch || ent is Line || ent is LwPolyline
                || ent is Circle || ent is Arc || ent is Polyline2D || ent is Polyline3D
                || ent is Solid || ent is Point || ent is Ellipse || ent is Spline
                || ent is Face3D || ent is Solid3D || ent is Region || ent is MLine
                || ent is RasterImage || ent is Wipeout || ent is Viewport || ent is Leader
                || ent is MultiLeader || ent is Tolerance || ent is XLine || ent is Ray)
            {
                // 这些类型只用到图层/线型，已在上面统一收集
                return;
            }

            // 不认识的类型：记下来，让调用方决定是否整体放弃清理
            analysis.UnresolvedEntityTypes.Add(ent.GetType().Name);
        }

        private static void AddProtectedCurrent(CadDocument doc, PurgeAnalysis analysis)
        {
            try
            {
                if (doc.Header != null)
                {
                    if (doc.Header.CurrentLayer != null)
                    {
                        analysis.UsedLayers.Add(doc.Header.CurrentLayer.Name);
                    }

                    if (doc.Header.CurrentTextStyle != null)
                    {
                        analysis.UsedTextStyles.Add(doc.Header.CurrentTextStyle.Name);
                    }

                    if (doc.Header.CurrentDimensionStyle != null)
                    {
                        analysis.UsedDimStyles.Add(doc.Header.CurrentDimensionStyle.Name);
                    }

                    if (doc.Header.CurrentLineType != null)
                    {
                        analysis.UsedLinetypes.Add(doc.Header.CurrentLineType.Name);
                    }
                }
            }
            catch
            {
                // 头部变量读取失败不影响主流程：标准项还有下面的硬保护
            }

            analysis.UsedLayers.Add("0");
            analysis.UsedLayers.Add("Defpoints");
            analysis.UsedLinetypes.Add("Continuous");
            analysis.UsedLinetypes.Add("ByLayer");
            analysis.UsedLinetypes.Add("ByBlock");
            analysis.UsedTextStyles.Add("Standard");
            analysis.UsedDimStyles.Add("Standard");
        }
    }
}
