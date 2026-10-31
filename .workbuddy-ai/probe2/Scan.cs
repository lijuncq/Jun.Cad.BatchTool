using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Probe
{
    internal static class Scan
    {
        public static void Run(string[] dirs)
        {
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.dwg", SearchOption.AllDirectories))
                {
                    try
                    {
                        CadDocument doc = DwgReader.Read(file);
                        int tables = 0, attdef = 0, attrib = 0, text = 0, dim = 0;
                        foreach (var block in doc.BlockRecords)
                        {
                            tables += block.Entities.OfType<TableEntity>().Count();
                            attdef += block.Entities.OfType<AttributeDefinition>().Count();
                            foreach (var ins in block.Entities.OfType<Insert>())
                            {
                                if (ins.Attributes != null) attrib += ins.Attributes.Count;
                            }
                            text += block.Entities.OfType<TextEntity>().Count(t => !(t is AttributeBase));
                            dim += block.Entities.OfType<Dimension>().Count();
                        }
                        if (tables > 0 || attdef > 0)
                            Console.WriteLine(string.Format("TABLE={0,-3} ATTDEF={1,-4} ATTRIB={2,-4} TEXT={3,-5} DIM={4,-4} {5}",
                                tables, attdef, attrib, text, dim, Path.GetFileName(file)));
                    }
                    catch { }
                }
            }
        }
    }
}
