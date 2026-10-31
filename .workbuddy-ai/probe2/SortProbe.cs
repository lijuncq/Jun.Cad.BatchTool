using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace Probe
{
    /// <summary>
    /// 查绘制顺序（SORTENTSTABLE）：源图有没有、写出去之后还在不在。
    ///
    /// 背景：文字遮罩加在标注的 *D 块里，如果填充画在标注之上，遮罩就完全看不见。
    /// 如果源图本来有排序表（把填充压在最下面）、而我们写出去把它弄丢了，
    /// 那"遮罩失效"的根因就是绘制顺序被破坏，而不是遮罩没生成。
    /// </summary>
    internal static class SortProbe
    {
        public static void Run(string path, string destination)
        {
            if (!string.IsNullOrEmpty(destination))
            {
                Fix(path, destination);
                return;
            }

            CadDocument doc = DwgReader.Read(path);
            BlockRecord model = doc.ModelSpace;

            SortEntitiesTable table = model.SortEntitiesTable;
            Console.WriteLine(path);
            Console.WriteLine("  ModelSpace.SortEntitiesTable = " + (table == null ? "null（没有排序表）" : "存在"));

            if (table == null)
            {
                return;
            }

            var all = new List<SortEntitiesTable.Sorter>();
            foreach (SortEntitiesTable.Sorter sorter in table)
            {
                all.Add(sorter);
            }

            all.Sort((a, b) => a.SortHandle.CompareTo(b.SortHandle));
            var head = new List<string>();
            var tail = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                string one = string.Format("{0}:{1:X}", DxfName(all[i].Entity), all[i].SortHandle);
                if (i < 6) head.Add(one);
                if (i >= all.Count - 3) tail.Add(one);
            }

            Console.WriteLine("  条目数 = " + all.Count);
            Console.WriteLine("  排序最前（画在最下面）= " + string.Join(" ", head.ToArray()));
            Console.WriteLine("  排序最后（画在最上面）= " + string.Join(" ", tail.ToArray()));
        }

        /// <summary>把模型空间所有 HATCH 送到最底层，写出后回读核对排序表头部。</summary>
        private static void Fix(string source, string destination)
        {
            CadDocument doc = DwgReader.Read(source);
            BlockRecord model = doc.ModelSpace;
            SortEntitiesTable table = model.SortEntitiesTable;

            Console.WriteLine("源排序表：" + (table == null ? "null" : "存在"));
            if (table == null)
            {
                return;
            }

            int n = 0;
            foreach (Entity e in model.Entities)
            {
                if (e is Hatch)
                {
                    table.MoveToTop(e);
                    n++;
                }
            }

            Console.WriteLine("已对 " + n + " 个 HATCH 调 MoveToTop");
            DwgWriter.Write(destination, doc);
            Console.WriteLine("已写出：" + destination);
            Run(destination, null);
        }

        private static string DxfName(Entity e)
        {
            if (e == null)
            {
                return "?";
            }

            return e.GetType().Name;
        }
    }
}
