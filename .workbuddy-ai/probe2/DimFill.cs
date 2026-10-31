using System;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace Probe
{
    /// <summary>
    /// 试写标注样式的"文字背景填充"（AutoCAD 的 DIMTFILL）。
    ///
    /// 这是给标注文字加遮罩的**原生**办法：不新建实体、不动 *D 块、
    /// 标注重生成也不会丢，而且随标注一起移动。
    /// 比 TextMask 现在那条 "把 Wipeout 塞进 *D 块" 的路子干净得多。
    /// </summary>
    internal static class DimFill
    {
        public static void Run(string source, string destination, string mode)
        {
            CadDocument doc = DwgReader.Read(source);

            DimensionTextBackgroundFillMode value;
            switch ((mode ?? "1").Trim())
            {
                case "0": value = DimensionTextBackgroundFillMode.NoBackground; break;
                case "2": value = DimensionTextBackgroundFillMode.DimensionTextBackgroundColor; break;
                default: value = DimensionTextBackgroundFillMode.DrawingBackgroundColor; break;
            }

            Console.WriteLine("标注样式共 " + doc.DimensionStyles.Count + " 个：");
            foreach (DimensionStyle style in doc.DimensionStyles)
            {
                Console.WriteLine(string.Format("  {0,-24} 原 DIMTFILL = {1}", style.Name, style.TextBackgroundFillMode));
                style.TextBackgroundFillMode = value;
            }

            DwgWriter.Write(destination, doc);
            Console.WriteLine("设为 " + value + "，已写出：" + destination);
        }
    }
}
