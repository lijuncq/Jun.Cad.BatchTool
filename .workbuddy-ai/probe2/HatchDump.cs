using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class HatchDump
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            foreach (var h in doc.ModelSpace.Entities.OfType<Hatch>().OrderBy(x => x.Handle.ToString("X")))
            {
                string color = h.Color.IsTrueColor
                    ? string.Format("#{0:X2}{1:X2}{2:X2}", h.Color.R, h.Color.G, h.Color.B)
                    : h.Color.Index.ToString();

                Console.WriteLine(string.Format("{0}|{1}|{2}|{3}|{4:0.####}|{5:0.####}|{6}",
                    h.Handle.ToString("X"),
                    color,
                    h.Layer == null ? "?" : h.Layer.Name,
                    h.IsSolid ? "SOLID" : (h.Pattern == null ? "(null)" : h.Pattern.Name),
                    h.PatternScale,
                    h.PatternAngle,
                    h.PatternType));
            }
        }
    }
}
