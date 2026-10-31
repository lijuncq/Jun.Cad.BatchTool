using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace Probe
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "mkdirty")
            {
                Dirty.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "layouts")
            {
                Layouts.Run(args[1]);
                return;
            }

            if (args.Length > 2 && args[0] == "setmeas")
            {
                SetMeas.Run(args[1], args[2]);
                return;
            }

            if (args.Length > 0 && args[0] == "enum")
            {
                EnumDump.Run();
                return;
            }

            if (args.Length > 1 && args[0] == "sortprobe")
            {
                SortProbe.Run(args[1], args.Length > 2 ? args[2] : null);
                return;
            }

            if (args.Length > 1 && args[0] == "textscan")
            {
                TextScan.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "dimmask")
            {
                DimMask.Run(args[1]);
                return;
            }

            if (args.Length > 2 && args[0] == "dimfill")
            {
                DimFill.Run(args[1], args[2], args.Length > 3 ? args[3] : "1");
                return;
            }

            if (args.Length > 1 && args[0] == "pstyle")
            {
                Pstyle.Run(args);
                return;
            }

            if (args.Length > 1 && args[0] == "hdr")
            {
                Hdr.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "hatchall")
            {
                HatchAll.Run(args[1]);
                return;
            }

            if (args.Length > 2 && args[0] == "patof")
            {
                PatOf.Run(args[1], args[2]);
                return;
            }

            if (args.Length > 1 && args[0] == "hatchang")
            {
                HatchAng.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "patfam")
            {
                PatFam.Run(args[1]);
                return;
            }

            if (args.Length > 0 && args[0] == "patupd")
            {
                PatUpd.Run();
                return;
            }

            if (args.Length > 1 && args[0] == "hatchdump")
            {
                HatchDump.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "patline")
            {
                PatLine.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "attrs")
            {
                Attrs.Run(args[1]);
                return;
            }

            if (args.Length > 1 && args[0] == "dims")
            {
                Dims.Run(args[1]);
                return;
            }

            if (args.Length > 0 && args[0] == "scan")
            {
                Scan.Run(args.Skip(1).ToArray());
                return;
            }

            string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "jun-sort-probe");
            Directory.CreateDirectory(dir);

            Console.WriteLine("== A. MoveToBottom ==");
            RunCase(dir, "bottom", s => s.MoveToBottom);
            Console.WriteLine();
            Console.WriteLine("== B. MoveToTop ==");
            RunCase(dir, "top", s => s.MoveToTop);
            Console.WriteLine();
            Console.WriteLine("== C. 不加排序表 ==");
            RunCase(dir, "none", null);
            Console.WriteLine();
            Console.WriteLine("== D. Remove/Add 后句柄 ==");
            HandleCase(dir);
        }

        private static void RunCase(string dir, string tag, Func<SortEntitiesTable, Action<Entity>> pick)
        {
            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            var line = new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0));
            var text = new TextEntity { Value = "HELLO", Height = 2.5, InsertPoint = new XYZ(0, 0, 0) };
            doc.ModelSpace.Entities.Add(line);
            doc.ModelSpace.Entities.Add(text);

            var def = new ImageDefinition("*P2") { Size = new XY(1, 1), DefaultSize = new XY(1, 1), FileName = "" };
            doc.ImageDefinitions.Add(def);

            var wipe = new Wipeout { InsertPoint = new XYZ(0, 0, 0), Size = new XY(1, 1), Definition = def };
            wipe.ClipBoundaryVertices.Add(new XY(0, 0));
            wipe.ClipBoundaryVertices.Add(new XY(5, 5));
            doc.ModelSpace.Entities.Add(wipe);

            var sort = doc.ModelSpace.SortEntitiesTable;
            Console.WriteLine("  sort table null? " + (sort == null));
            if (sort != null && pick != null)
            {
                pick(sort)(wipe);
                foreach (var s in sort)
                {
                    Console.WriteLine("    sorter " + s.Entity.GetType().Name + " sortHandle=" + s.SortHandle + " entityHandle=" + s.Entity.Handle);
                }
            }

            string path = Path.Combine(dir, "case-" + tag + ".dwg");
            DwgWriter.Write(path, doc, null, null);

            var back = DwgReader.Read(path);
            Console.WriteLine("  写入顺序: " + string.Join(" → ", doc.ModelSpace.Entities.Select(e => e.GetType().Name)));
            Console.WriteLine("  回读顺序: " + string.Join(" → ", back.ModelSpace.Entities.Select(e => e.GetType().Name)));

            var backSort = back.ModelSpace.SortEntitiesTable;
            Console.WriteLine("  回读排序表: " + (backSort == null ? "null" : string.Join(",", backSort.Select(s => s.Entity.GetType().Name + "@" + s.SortHandle))));
        }

        private static void HandleCase(string dir)
        {
            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            var text = new TextEntity { Value = "KEEP", Height = 2.5, InsertPoint = new XYZ(0, 0, 0) };
            doc.ModelSpace.Entities.Add(text);
            ulong before = text.Handle;
            Console.WriteLine("  原始句柄: " + before.ToString("X"));

            var mask = new Solid(new XYZ(0, 0, 0), new XYZ(1, 0, 0), new XYZ(0, 1, 0), new XYZ(1, 1, 0));
            doc.ModelSpace.Entities.Remove(text);
            doc.ModelSpace.Entities.Add(mask);
            doc.ModelSpace.Entities.Add(text);
            Console.WriteLine("  Remove/Add 后句柄: " + text.Handle.ToString("X") + "（变了？" + (text.Handle != before) + "）");

            Console.WriteLine("  （Handle 是只读属性，无法手工恢复）");

            string path = Path.Combine(dir, "case-handle.dwg");
            try
            {
                DwgWriter.Write(path, doc, null, null);
                var back = DwgReader.Read(path);
                var t = back.ModelSpace.Entities.OfType<TextEntity>().FirstOrDefault();
                Console.WriteLine("  回读句柄: " + (t == null ? "找不到文字" : t.Handle.ToString("X")));
                Console.WriteLine("  回读顺序: " + string.Join(" → ", back.ModelSpace.Entities.Select(e => e.GetType().Name)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  写出失败: " + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
