using System;
using System.Linq;
using System.Reflection;
using ACadSharp;
using ACadSharp.Header;
using ACadSharp.IO;

namespace Probe
{
    internal static class Hdr
    {
        public static void Run(string path)
        {
            var doc = DwgReader.Read(path);
            var props = typeof(CadHeader).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name)
                .ToList();

            Console.WriteLine("CadHeader 属性数：" + props.Count);
            foreach (var p in props)
            {
                string n = p.Name.ToLowerInvariant();
                if (n.Contains("meas") || n.Contains("codepage") || n.Contains("unit") || n.Contains("pattern") || n.Contains("attmode") || n.Contains("version"))
                {
                    object v = null;
                    try { v = p.GetValue(doc.Header); } catch (Exception ex) { v = "<" + ex.GetType().Name + ">"; }
                    Console.WriteLine(string.Format("  {0,-24} {1,-14} = {2}", p.Name, p.PropertyType.Name, v));
                }
            }

            // 是否有兜底的"未建模变量"入口
            var others = typeof(CadHeader).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.ToLowerInvariant().Contains("variable") || m.Name.ToLowerInvariant().Contains("get"))
                .Select(m => m.Name).Distinct().ToList();
            Console.WriteLine("可疑方法：" + string.Join(", ", others));
        }
    }
}
