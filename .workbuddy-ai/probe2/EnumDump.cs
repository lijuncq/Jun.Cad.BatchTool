using System;
using System.Linq;
using ACadSharp;
using ACadSharp.Header;

namespace Probe
{
    internal static class EnumDump
    {
        public static void Run()
        {
            var t = typeof(CadHeader).GetProperty("MeasurementUnits").PropertyType;
            Console.WriteLine("枚举类型：" + t.FullName + "  底层：" + Enum.GetUnderlyingType(t).Name);
            foreach (var n in Enum.GetNames(t))
            {
                Console.WriteLine("  " + n + " = " + Convert.ToInt64(Enum.Parse(t, n)));
            }

            var pt = typeof(ACadSharp.Entities.Hatch).GetProperty("PatternType").PropertyType;
            Console.WriteLine("PatternType：" + pt.FullName);
            foreach (var n in Enum.GetNames(pt))
            {
                Console.WriteLine("  " + n + " = " + Convert.ToInt64(Enum.Parse(pt, n)));
            }
        }
    }
}
