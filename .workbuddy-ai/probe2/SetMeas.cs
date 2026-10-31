using System;
using ACadSharp;
using ACadSharp.Header;
using ACadSharp.IO;

namespace Probe
{
    internal static class SetMeas
    {
        public static void Run(string src, string dst)
        {
            var doc = DwgReader.Read(src);
            Console.WriteLine("读入 MeasurementUnits = " + doc.Header.MeasurementUnits);
            doc.Header.MeasurementUnits = MeasurementUnits.Metric;
            DwgWriter.Write(dst, doc, null, null);
            var back = DwgReader.Read(dst);
            Console.WriteLine("回读 MeasurementUnits = " + back.Header.MeasurementUnits);
        }
    }
}
