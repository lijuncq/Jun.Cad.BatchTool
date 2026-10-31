using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class HatchAng
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            foreach (var h in doc.ModelSpace.Entities.OfType<Hatch>().OrderBy(x => x.Handle.ToString("X")))
            {
                string name = h.IsSolid ? "SOLID" : (h.Pattern == null ? "(null)" : h.Pattern.Name);
                string angles = h.IsSolid || h.Pattern == null || h.Pattern.Lines == null
                    ? "-"
                    : string.Join(",", h.Pattern.Lines
                        .Select(l => Math.Round(((l.Angle * 180 / Math.PI) % 180 + 180) % 180, 1))
                        .OrderBy(x => x));

                Console.WriteLine(string.Format("{0}|{1}|{2}|{3:0.####}|{4}", h.Handle.ToString("X"), name, angles,
                    h.PatternScale, h.Color.IsTrueColor ? "T" : h.Color.Index.ToString()));
            }
        }
    }
}
