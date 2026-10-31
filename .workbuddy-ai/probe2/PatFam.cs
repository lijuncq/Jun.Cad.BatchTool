using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class PatFam
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            var seen = new HashSet<string>();

            foreach (var h in doc.ModelSpace.Entities.OfType<Hatch>().Where(x => !x.IsSolid && x.Pattern != null))
            {
                if (!seen.Add(h.Pattern.Name)) continue;

                Console.WriteLine(h.Pattern.Name + "  比例 " + h.PatternScale.ToString("0.####") + "  线族 " + h.Pattern.Lines.Count);
                foreach (var l in h.Pattern.Lines)
                {
                    double cos = Math.Cos(l.Angle), sin = Math.Sin(l.Angle);
                    double perp = Math.Abs(l.Offset.X * sin - l.Offset.Y * cos);
                    Console.WriteLine(string.Format("   角度 {0,7:0.###}°  垂距 {1,9:0.####}", l.Angle * 180 / Math.PI, perp));
                }
            }
        }
    }
}
