using System;
using ACadSharp.Entities;

namespace Probe
{
    internal static class EnumVals
    {
        public static void Dump()
        {
            Console.WriteLine("BackgroundFillFlags 数值:");
            foreach (BackgroundFillFlags f in Enum.GetValues(typeof(BackgroundFillFlags)))
            {
                Console.WriteLine(string.Format("  {0,-30} = {1} (0x{2:X})", f, (int)f, (int)f));
            }
        }
    }
}
