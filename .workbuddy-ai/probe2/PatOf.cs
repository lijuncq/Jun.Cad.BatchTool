using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class PatOf
    {
        public static void Run(string path, string name)
        {
            var doc = DwgReader.Read(path);
            var h = doc.ModelSpace.Entities.OfType<Hatch>()
                .FirstOrDefault(x => !x.IsSolid && x.Pattern != null && x.Pattern.Name == name);
            if (h == null) { Console.WriteLine("找不到图案 " + name); return; }

            Console.WriteLine(name + "  scale=" + h.PatternScale.ToString("0.######") + " angle=" + (h.PatternAngle * 180 / Math.PI).ToString("0.###"));
            foreach (var l in h.Pattern.Lines)
            {
                double cos = Math.Cos(l.Angle), sin = Math.Sin(l.Angle);
                double nx = -sin, ny = cos;
                double perp = l.BasePoint.X * nx + l.BasePoint.Y * ny;
                double spacing = Math.Abs(l.Offset.X * sin - l.Offset.Y * cos);
                Console.WriteLine(string.Format("  angle={0,6:0.###}  base=({1,9:0.####},{2,9:0.####})  法向投影={3,9:0.####}  垂距={4,8:0.####}",
                    l.Angle * 180 / Math.PI, l.BasePoint.X, l.BasePoint.Y, perp, spacing));
            }
        }
    }
}
