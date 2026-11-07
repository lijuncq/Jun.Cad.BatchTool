using System;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Tables;
using Jun.Cad.Batch.Engine;

namespace Probe
{
    internal static class Cal
    {
        private static readonly string[] Samples =
        {
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "TOPSIDE MECHANICAL EQUIPMENT LIST",
            "KLD-ZHJT-PIP-LYT-2000ZZ-30210",
            "abcdefghijklmnopqrstuvwxyz",
            "The quick brown fox jumps over the lazy dog"
        };

        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("== FontMetrics width (chars, height=100) ==");
            foreach (TextStyle st in doc.TextStyles)
            {
                if (string.IsNullOrEmpty(st.Filename))
                {
                    continue;
                }

                if (st.Name != "ISOCPEUR" && st.Name != "Arial Narrow")
                {
                    continue;
                }

                foreach (string s in Samples)
                {
                    double w = TextBounds.MeasureWidth(s, 100.0, st);
                    Console.WriteLine("CAL3|{0}|{1:0.0}|{2}", st.Name, w, s);
                }
            }
        }
    }
}
