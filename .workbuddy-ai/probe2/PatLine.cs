using System;
using System.Linq;
using System.Reflection;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class PatLine
    {
        public static void Run(string path)
        {
            Console.WriteLine("HatchPattern.Line 的成员：");
            foreach (var p in typeof(HatchPattern.Line).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine("   P " + p.PropertyType.Name + " " + p.Name);
            foreach (var f in typeof(HatchPattern.Line).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine("   F " + f.FieldType.Name + " " + f.Name);

            var doc = DwgReader.Read(path);
            var hatch = doc.ModelSpace.Entities.OfType<Hatch>().FirstOrDefault(h => !h.IsSolid && h.Pattern != null);
            if (hatch == null) { Console.WriteLine("没有图案填充"); return; }

            Console.WriteLine("样本填充：" + hatch.Pattern.Name + " 比例 " + hatch.PatternScale + " 角度 " + hatch.PatternAngle);
            Console.WriteLine("   PatternType=" + hatch.PatternType + " IsSolid=" + hatch.IsSolid + " Color=" + hatch.Color.Index + " trueColor=" + hatch.Color.IsTrueColor);
            foreach (var line in hatch.Pattern.Lines)
                Console.WriteLine(string.Format("   line angle={0:0.####} base=({1:0.####},{2:0.####}) offset=({3:0.####},{4:0.####})",
                    line.Angle, line.BasePoint.X, line.BasePoint.Y, line.Offset.X, line.Offset.Y));
        }
    }
}
