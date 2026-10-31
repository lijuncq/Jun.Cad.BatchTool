using System;
using System.Globalization;
using ACadSharp.Entities;

namespace Jun.Cad.Batch.Engine
{
    /// <summary>
    /// 日志/错误信息里描述对象的小工具：统一输出"类型(句柄:XXXX) 图层:YYY"，
    /// 便于用户按句柄在图纸里定位失败对象。
    /// </summary>
    public static class EntityDescribe
    {
        /// <summary>句柄的十六进制文本（DXF 组码 5 就是这个值）。</summary>
        public static string HandleOf(Entity entity)
        {
            if (entity == null)
            {
                return "null";
            }

            try
            {
                return entity.Handle.ToString("X");
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>描述一个实体。</summary>
        public static string Describe(Entity entity)
        {
            if (entity == null)
            {
                return "(null)";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}(句柄:{1}) 图层:{2}",
                EntityFilter.DxfNameOf(entity),
                HandleOf(entity),
                entity.Layer == null ? "(无)" : entity.Layer.Name);
        }

        /// <summary>描述一次扫描命中（带所属容器与嵌套深度）。</summary>
        public static string Describe(ScanHit hit)
        {
            if (hit == null)
            {
                return "(null)";
            }

            if (!hit.IsNested)
            {
                return Describe(hit.Entity);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ← 块「{1}」第 {2} 层",
                Describe(hit.Entity),
                hit.OwnerName,
                hit.Depth);
        }

        /// <summary>颜色描述：ACI 索引 + 随层/随块/真彩色三种特例。</summary>
        public static string DescribeColor(Entity entity)
        {
            if (entity == null)
            {
                return "?";
            }

            var c = entity.Color;
            if (c.IsByLayer)
            {
                return "随层";
            }

            if (c.IsByBlock)
            {
                return "随块";
            }

            if (c.IsTrueColor)
            {
                return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            }

            return c.Index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>线型描述（随层/随块也算出来，不然日志里只看到空）。</summary>
        public static string DescribeLinetype(Entity entity)
        {
            if (entity == null || entity.LineType == null)
            {
                return "(无)";
            }

            return entity.LineType.Name;
        }

        /// <summary>线宽描述。</summary>
        public static string DescribeLineWeight(Entity entity)
        {
            if (entity == null)
            {
                return "?";
            }

            return entity.LineWeight.ToString();
        }
    }
}
