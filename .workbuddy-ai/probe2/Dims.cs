using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class Dims
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            var dims = doc.ModelSpace.Entities.OfType<Dimension>().ToList();
            Console.WriteLine("dims=" + dims.Count);

            int withWipe = 0;
            foreach (var d in dims)
            {
                if (d.Block != null && d.Block.Entities.OfType<Wipeout>().Any()) withWipe++;
            }
            Console.WriteLine("dims-with-wipeout=" + withWipe);

            foreach (var d in dims.Take(2))
            {
                Console.WriteLine("  " + d.Block?.Name + " -> " +
                    string.Join(" → ", d.Block.Entities.Select(e => e.GetType().Name)));
            }
        }
    }
}
