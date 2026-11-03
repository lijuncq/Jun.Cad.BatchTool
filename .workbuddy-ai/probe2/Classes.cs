using System;
using ACadSharp;
using ACadSharp.Classes;
using ACadSharp.IO;

namespace Probe
{
    /// <summary>
    /// 列出两张图的 CLASSES 段里与 WIPEOUT 相关的类登记，用来判断
    /// AutoCAD 是否因为"类没登记"而忽略我们写的 ACAD_WIPEOUT_VARS。
    /// </summary>
    internal static class Classes
    {
        public static void Run(string a, string b)
        {
            Show(a);
            Show(b);
        }

        private static void Show(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("=== " + path + "  classes=" + doc.Classes.Count);
            foreach (DxfClass c in doc.Classes)
            {
                if (c.DxfName != null && c.DxfName.ToUpperInvariant().Contains("WIPEOUT"))
                {
                    Console.WriteLine("  " + c.DxfName + " | " + c.CppClassName + " | itemClassId=" + c.ItemClassId);
                }
            }
        }
    }
}
