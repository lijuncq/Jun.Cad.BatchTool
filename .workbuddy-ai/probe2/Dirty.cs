using System;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Probe
{
    internal static class Dirty
    {
        public static void Run(string path)
        {
            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            // 2 条正常线、2 条零长线、1 个空 MTEXT、1 个空 TEXT、1 组重复圆
            doc.ModelSpace.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
            doc.ModelSpace.Entities.Add(new Line(new XYZ(0, 10, 0), new XYZ(100, 10, 0)));
            doc.ModelSpace.Entities.Add(new Line(new XYZ(50, 50, 0), new XYZ(50, 50, 0)));
            doc.ModelSpace.Entities.Add(new Line(new XYZ(70, 70, 0), new XYZ(70, 70, 0)));
            doc.ModelSpace.Entities.Add(new MText { Value = "   ", Height = 2.5, InsertPoint = new XYZ(0, 0, 0) });
            doc.ModelSpace.Entities.Add(new TextEntity { Value = "", Height = 2.5, InsertPoint = new XYZ(0, 20, 0) });

            var c1 = new Circle { Center = new XYZ(200, 200, 0), Radius = 5 };
            var c2 = new Circle { Center = new XYZ(200, 200, 0), Radius = 5 };
            doc.ModelSpace.Entities.Add(c1);
            doc.ModelSpace.Entities.Add(c2);

            DwgWriter.Write(path, doc, null, null);
            Console.WriteLine("written: " + path + " entities=" + doc.ModelSpace.Entities.Count);
        }
    }
}
