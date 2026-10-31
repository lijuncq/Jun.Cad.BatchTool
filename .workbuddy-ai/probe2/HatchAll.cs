using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class HatchAll
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);

            Console.WriteLine("Header: Version=" + doc.Header.Version
                
                + " CodePage=" + doc.Header.CodePage
                + " InsUnits=" + doc.Header.InsUnits);

            var list = doc.ModelSpace.Entities.OfType<Hatch>().ToList();
            Console.WriteLine("填充总数 " + list.Count);

            foreach (var g in list.GroupBy(h => Describe(h)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine("  {0,-40} x{1}", g.Key, g.Count());
            }
        }

        private static string Describe(Hatch h)
        {
            if (h.IsSolid && (h.GradientColor == null || !h.GradientColor.Enabled)) return "SOLID";

            string name = h.Pattern == null ? "(null)" : h.Pattern.Name;
            double spacing = 0, gap = 0;
            int fam = h.Pattern == null || h.Pattern.Lines == null ? 0 : h.Pattern.Lines.Count;

            if (fam > 0)
            {
                var lines = h.Pattern.Lines;
                double min = double.MaxValue;
                foreach (var l in lines)
                {
                    double cos = Math.Cos(l.Angle), sin = Math.Sin(l.Angle);
                    double s = Math.Abs(l.Offset.X * sin - l.Offset.Y * cos);
                    if (s > 1e-9 && s < min) min = s;
                }
                spacing = min == double.MaxValue ? 0 : min;

                if (fam >= 2)
                {
                    var a = lines[0]; var b = lines[1];
                    double sin = Math.Sin(a.Angle), cos = Math.Cos(a.Angle);
                    double pa = a.BasePoint.X * -sin + a.BasePoint.Y * cos;
                    double pb = b.BasePoint.X * -sin + b.BasePoint.Y * cos;
                    gap = Math.Abs(pa - pb);
                }
            }

            return string.Format("{0} | s={1:0.####} a={2:0.#} fam={3} sp={4:0.####} gap={5:0.####} type={6}",
                name, h.PatternScale, h.PatternAngle * 180 / Math.PI, fam, spacing, gap, h.PatternType);
        }
    }
}
