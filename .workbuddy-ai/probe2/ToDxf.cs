using System;
using ACadSharp;
using ACadSharp.IO;

namespace Probe
{
    /// <summary>把 DWG 转成 DXF，用来看 OBJECTS 段里对象是怎么写的。</summary>
    internal static class ToDxf
    {
        public static void Run(string input, string output)
        {
            CadDocument doc = DwgReader.Read(input);
            DxfWriter.Write(output, doc, false);
            Console.WriteLine("wrote " + output);
        }
    }
}
