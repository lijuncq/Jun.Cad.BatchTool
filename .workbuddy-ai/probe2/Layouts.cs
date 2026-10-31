using System;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.IO;

namespace Probe
{
    internal static class Layouts
    {
        public static void Run(string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "layout.dwg");

            var doc = new CadDocument();
            doc.Header.Version = ACadVersion.AC1032;

            Console.WriteLine("原始布局：");
            foreach (var b in doc.BlockRecords.Where(x => x.Layout != null))
                Console.WriteLine("   block=" + b.Name + " layout=" + b.Layout.Name);

            var target = doc.BlockRecords.FirstOrDefault(b => b.Layout != null
                && !string.Equals(b.Layout.Name, "Model", StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                Console.WriteLine("  没有图纸空间布局，跳过改名测试");
                return;
            }

            string oldName = target.Layout.Name;
            target.Layout.Name = "A1-平面(1)";
            Console.WriteLine("改名：" + oldName + " → " + target.Layout.Name);

            DwgWriter.Write(path, doc, null, null);
            var back = DwgReader.Read(path);
            Console.WriteLine("回读布局：");
            foreach (var b in back.BlockRecords.Where(x => x.Layout != null))
                Console.WriteLine("   block=" + b.Name + " layout=" + b.Layout.Name);

            // 再写一次，确认改名后能稳定往返
            string path2 = Path.Combine(dir, "layout2.dwg");
            DwgWriter.Write(path2, back, null, null);
            var back2 = DwgReader.Read(path2);
            Console.WriteLine("二次回读：");
            foreach (var b in back2.BlockRecords.Where(x => x.Layout != null))
                Console.WriteLine("   block=" + b.Name + " layout=" + b.Layout.Name);
        }
    }
}
