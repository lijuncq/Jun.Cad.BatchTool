using System;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Objects;

namespace Probe
{
    /// <summary>
    /// 读入一张图，改 ACAD_WIPEOUT_VARS 的 DisplayImageFrame，再写回去。
    /// 用来判断"ACadSharp 写这个对象，AutoCAD 到底认不认"。
    /// </summary>
    internal static class SetWFrame
    {
        public static void Run(string input, string output, string value)
        {
            CadDocument doc = DwgReader.Read(input);
            bool flag = value != "0";

            WipeoutVariables vars = doc.RootDictionary.GetEntry<WipeoutVariables>(MaskServiceKey);
            if (vars == null)
            {
                Console.WriteLine("no ACAD_WIPEOUT_VARS in source");
                vars = new WipeoutVariables { Name = MaskServiceKey, DisplayImageFrame = flag };
                doc.RootDictionary.Add(MaskServiceKey, vars);
            }
            else
            {
                Console.WriteLine("found, was " + vars.DisplayImageFrame);
                vars.DisplayImageFrame = flag;
            }

            DwgWriter.Write(output, doc);
            Console.WriteLine("wrote " + output + " DisplayImageFrame=" + flag);
        }

        private const string MaskServiceKey = "ACAD_WIPEOUT_VARS";
    }
}
