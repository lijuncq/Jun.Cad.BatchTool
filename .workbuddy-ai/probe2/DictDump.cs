using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Objects;

namespace Probe
{
    /// <summary>
    /// 把 DWG 的根字典（named object dictionary）逐层打出来，用来确认
    /// AutoCAD 把 WipeoutVariables 挂在哪个 key 下面、外层是字典还是直接挂对象。
    /// </summary>
    internal static class DictDump
    {
        public static void Run(string path)
        {
            CadDocument doc = DwgReader.Read(path);
            Console.WriteLine("file: " + path);
            Dump(doc.RootDictionary, "NOD", 0);
        }

        private static void Dump(CadDictionary dict, string label, int depth)
        {
            if (dict == null || depth > 2)
            {
                return;
            }

            string pad = new string(' ', depth * 2);
            Console.WriteLine(pad + label + " [" + dict.EntryNames.Length + " entries]");

            foreach (string name in dict.EntryNames)
            {
                NonGraphicalObject value = dict.GetEntry<NonGraphicalObject>(name);
                string type = value == null ? "null" : value.GetType().Name;
                string extra = string.Empty;

                var vars = value as WipeoutVariables;
                if (vars != null)
                {
                    extra = "  DisplayImageFrame=" + vars.DisplayImageFrame;
                }

                Console.WriteLine(pad + "  " + name + " -> " + type + "  (handle " + (value == null ? "?" : value.Handle.ToString("X")) + ")" + extra);

                var child = value as CadDictionary;
                if (child != null)
                {
                    Dump(child, name, depth + 1);
                }
            }
        }
    }
}
