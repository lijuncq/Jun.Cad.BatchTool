using System;
using System.Linq;
using System.Reflection;
using ACadSharp.Entities;

namespace Probe
{
    internal static class PatUpd
    {
        public static void Run()
        {
            Console.WriteLine("HatchPattern 的公开方法：");
            foreach (var m in typeof(HatchPattern).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
        }
    }
}
