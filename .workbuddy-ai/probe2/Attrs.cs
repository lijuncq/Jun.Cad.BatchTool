using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class Attrs
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            foreach (var block in doc.BlockRecords)
            {
                foreach (var ins in block.Entities.OfType<Insert>())
                {
                    if (ins.Attributes == null || ins.Attributes.Count == 0) continue;
                    Console.WriteLine(EntityName(ins) + " attribs=" + ins.Attributes.Count);
                    foreach (var at in ins.Attributes)
                        Console.WriteLine("   " + at.Tag + " = [" + at.Value + "]");
                }
            }
        }

        private static string EntityName(Insert ins)
        {
            return ins.Block == null ? "?" : ins.Block.Name;
        }
    }
}
