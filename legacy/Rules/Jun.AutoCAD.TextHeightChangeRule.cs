using System;
using System.Collections.Generic;
using System.Reflection;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using Jun.AutoCAD.Core;

namespace Jun.AutoCAD.Rules
{
    /// <summary>
    /// 扩展规则：批量修改文字高度。
    ///
    /// 覆盖：TEXT / MTEXT / ATTDEF / ATTRIB / DIMENSION（改标注样式 Dimtxt）/ ACAD_TABLE（单元格）/
    ///       MULTILEADER / TOLERANCE。
    ///
    /// 关于 MText 的行距联动：
    ///   MText 的行距 = 字符高度 × LineSpacingFactor，改 TextHeight 不会自动改系数。
    ///   若希望"字高变了、行距比例保持"，本规则默认不动系数（视觉上行距随字高线性变化）；
    ///   若图纸要求固定行距系数（如 1.0 或 1.5），在 Action.LineSpacingFactor 里显式指定即可。
    /// </summary>
    public sealed class TextHeightChangeRule : IRuleHandler
    {
        public string RuleType
        {
            get { return "TextHeightChange"; }
        }

        public void Execute(RuleExecutionContext ctx, RuleExecutionResult result)
        {
            if (!ctx.Action.Height.HasValue || ctx.Action.Height.Value <= 0)
            {
                throw new RuleValidationException("TextHeightChange 规则缺少有效的 Action.Height（必须为正数）。");
            }

            var tr = ctx.Transaction;
            double height = ctx.Action.Height.Value;
            bool alsoUpdateDimStyle = RuleExecutionContext.Bool(ctx.Action.AlsoUpdateDimensionStyle, true);

            RuleFilterAdapter.PrepareTextFilter(ctx.Filter);

            var filter = DxfFilter.Build(ctx.Filter, null, tr);
            if (!filter.HasCondition)
            {
                throw new RuleValidationException(
                    "TextHeightChange 规则没有任何过滤条件，为避免误改整张图的文字，规则中止。");
            }

            ctx.Log.Detail("DXF 过滤组码：" + filter.Describe());
            ctx.Log.Detail(string.Format("目标字高：{0}；同步标注样式：{1}", height, alsoUpdateDimStyle ? "是" : "否"));

            List<ScanHit> hits = EntityScanner.Scan(ctx, filter);
            ctx.Log.Detail(string.Format("命中 {0} 个对象。", hits.Count));

            foreach (ScanHit hit in hits)
            {
                Entity ent = null;
                try
                {
                    ent = tr.GetObject(hit.Id, OpenMode.ForRead, false) as Entity;
                }
                catch (Exception ex)
                {
                    result.MarkFailed(string.Format("{0} 打开失败：{1}", ObjectUtil.HandleOf(hit.Id), ex.Message));
                    continue;
                }

                if (ent == null || ent.IsErased || !TextEntityHelper.IsTextCarrier(ent))
                {
                    continue;
                }

                result.Hit();
                string desc = ObjectUtil.Describe(ent);

                if (ctx.SkipLockedLayers && LayerService.IsLocked(tr, ctx.Database, ent.Layer))
                {
                    string msg = string.Format("{0} 位于锁定图层「{1}」，跳过。", desc, ent.Layer);
                    result.MarkSkipped(msg);
                    ctx.Log.Warn(msg);
                    continue;
                }

                try
                {
                    ApplyHeight(ctx, result, ent, desc, height, alsoUpdateDimStyle);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改字高失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }
            }
        }

        private static void ApplyHeight(
            RuleExecutionContext ctx,
            RuleExecutionResult result,
            Entity ent,
            string desc,
            double height,
            bool alsoUpdateDimStyle)
        {
            var tr = ctx.Transaction;

            // ---- 标注：字高写在标注样式的 Dimtxt ----
            var dim = ent as Dimension;
            if (dim != null)
            {
                if (!alsoUpdateDimStyle)
                {
                    result.MarkSkipped(desc + "：标注字高由标注样式控制，AlsoUpdateDimensionStyle=false，跳过。");
                    return;
                }

                try
                {
                    var record = tr.GetObject(dim.DimensionStyle, OpenMode.ForWrite, false) as DimStyleTableRecord;
                    if (record == null)
                    {
                        result.MarkSkipped(desc + "：标注样式无法打开，跳过。");
                        return;
                    }

                    if (Math.Abs(record.Dimtxt - height) < 1e-9)
                    {
                        result.MarkSkipped(string.Format("{0}：标注样式「{1}」字高已是 {2}，跳过。", desc, record.Name, height));
                        return;
                    }

                    double old = record.Dimtxt;
                    record.Dimtxt = height;

                    string detail = string.Format("{0}：标注样式「{1}」字高 {2} → {3}", desc, record.Name, old, height);
                    result.MarkChanged(detail);
                    ctx.Log.Detail(detail);
                }
                catch (Exception ex)
                {
                    string msg = string.Format("{0} 修改标注字高失败：{1}", desc, ex.Message);
                    result.MarkFailed(msg);
                    ctx.Log.Error(msg);
                }

                return;
            }

            // ---- 表格：逐单元格改字高 ----
            var table = ent as Table;
            if (table != null)
            {
                ApplyToTable(ctx, result, table, desc, height);
                return;
            }

            // ---- 常规文字实体 ----
            string note = null;
            bool changed = false;

            var dbText = ent as DBText;
            if (dbText != null)
            {
                if (Math.Abs(dbText.Height - height) < 1e-9)
                {
                    result.MarkSkipped(string.Format("{0} 字高已是 {1}，跳过。", desc, height));
                    return;
                }

                dbText.UpgradeOpen();
                dbText.Height = height;
                changed = true;
            }

            var mtext = ent as MText;
            if (mtext != null)
            {
                if (Math.Abs(mtext.TextHeight - height) < 1e-9 && !ctx.Action.LineSpacingFactor.HasValue)
                {
                    result.MarkSkipped(string.Format("{0} 字高已是 {1}，跳过。", desc, height));
                    return;
                }

                mtext.UpgradeOpen();
                mtext.TextHeight = height;
                changed = true;

                if (ctx.Action.LineSpacingFactor.HasValue)
                {
                    double factor = ctx.Action.LineSpacingFactor.Value;
                    if (factor < 0.25)
                    {
                        factor = 0.25;
                    }
                    else if (factor > 4.0)
                    {
                        factor = 4.0;
                    }

                    mtext.LineSpacingFactor = factor;
                    note = string.Format("（行距系数设为 {0:0.##}）", factor);
                }
            }

            var attDef = ent as AttributeDefinition;
            if (attDef != null)
            {
                if (Math.Abs(attDef.Height - height) < 1e-9)
                {
                    result.MarkSkipped(string.Format("{0} 字高已是 {1}，跳过。", desc, height));
                    return;
                }

                attDef.UpgradeOpen();
                attDef.Height = height;
                changed = true;
            }

            var attRef = ent as AttributeReference;
            if (attRef != null)
            {
                if (Math.Abs(attRef.Height - height) < 1e-9)
                {
                    result.MarkSkipped(string.Format("{0} 字高已是 {1}，跳过。", desc, height));
                    return;
                }

                attRef.UpgradeOpen();
                attRef.Height = height;
                changed = true;
            }

            if (changed)
            {
                string detail = string.Format("{0}：字高 → {1}{2}", desc, height, note ?? string.Empty);
                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
                return;
            }

            // ---- MLeader / Tolerance：属性名跨版本存在差异，走反射 ----
            string reason;
            if (TrySetDoubleProperty(ent, "TextHeight", height, out reason))
            {
                string detail = string.Format("{0}：字高 → {1}", desc, height);
                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
                return;
            }

            result.MarkSkipped(string.Format("{0} 不支持字高修改（{1}），跳过。", desc, reason ?? "无可用属性"));
        }

        private static void ApplyToTable(RuleExecutionContext ctx, RuleExecutionResult result, Table table, string desc, double height)
        {
            int changedCells = 0;
            int rows = 0;
            int columns = 0;

            try
            {
                rows = table.Rows.Count;
                columns = table.Columns.Count;
                table.UpgradeOpen();
            }
            catch (Exception ex)
            {
                result.MarkFailed(string.Format("{0} 读取表格失败：{1}", desc, ex.Message));
                return;
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    try
                    {
                        Cell cell = table.Cells[r, c];
                        if (cell == null)
                        {
                            continue;
                        }

                        string reason;
                        if (TrySetDoubleProperty(cell, "TextHeight", height, out reason))
                        {
                            changedCells++;
                        }
                    }
                    catch
                    {
                        // 合并单元格等特殊情况，跳过该单元格
                    }
                }
            }

            if (changedCells > 0)
            {
                string detail = string.Format("{0}：{1} 个单元格字高 → {2}", desc, changedCells, height);
                result.MarkChanged(detail);
                ctx.Log.Detail(detail);
            }
            else
            {
                result.MarkSkipped(desc + "：表格单元格字高未发生变化。");
            }
        }

        /// <summary>
        /// 反射写入 double 属性（用于 MLeader / 形位公差 / Table Cell 这类跨版本或可空类型的属性）。
        /// 注意 Cell.TextHeight 的类型是 Nullable&lt;double&gt;，所以这里用 Nullable.GetUnderlyingType 取真实类型。
        /// </summary>
        private static bool TrySetDoubleProperty(object obj, string propertyName, double value, out string reason)
        {
            reason = null;
            if (obj == null)
            {
                reason = "对象为空";
                return false;
            }

            try
            {
                PropertyInfo prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite)
                {
                    reason = string.Format("{0} 没有可写的 {1}", obj.GetType().Name, propertyName);
                    return false;
                }

                Type valueType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (valueType != typeof(double))
                {
                    reason = string.Format("{0}.{1} 不是 double 类型", obj.GetType().Name, propertyName);
                    return false;
                }

                object current = prop.GetValue(obj, null);
                if (current != null && Math.Abs((double)current - value) < 1e-9)
                {
                    reason = "已是目标值";
                    return false;
                }

                prop.SetValue(obj, value, null);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }
}
