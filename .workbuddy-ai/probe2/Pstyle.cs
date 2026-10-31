using System;
using System.Linq;
using System.Reflection;
using ACadSharp;
using ACadSharp.IO;

namespace Probe
{
    /// <summary>
    /// 查 $PSTYLEMODE：ACadSharp 到底能不能读、能不能写。
    ///
    /// 现象：工具输出后图纸从"颜色相关(CTB)"变成"命名(STB)"，
    /// AutoCAD 的打印样式下拉框就只列 STB，用户自建的 CTB 目录整个消失。
    ///
    /// 用法：
    ///   pstyle read  &lt;dwg&gt;                       读 Header.PlotStyleMode 原始值
    ///   pstyle write &lt;src&gt; &lt;dst&gt; &lt;value&gt;        设成 value 后写出（用来反推写入口径）
    /// </summary>
    internal static class Pstyle
    {
        public static void Run(string[] args)
        {
            if (args.Length > 1 && args[1] == "write" && args.Length > 4)
            {
                Write(args[2], args[3], short.Parse(args[4]));
                return;
            }

            // pstyle read <dwg>  /  pstyle <dwg>
            string file = args.Length > 2 && args[1] == "read" ? args[2] : (args.Length > 1 ? args[1] : null);
            Read(file);
        }

        private static PropertyInfo ModeProperty(CadDocument doc)
        {
            return doc.Header.GetType().GetProperty("PlotStyleMode");
        }

        private static void Read(string source)
        {
            var header = typeof(CadDocument).GetProperty("Header").PropertyType;
            PropertyInfo prop = header.GetProperty("PlotStyleMode");
            Console.WriteLine("CadHeader.PlotStyleMode 类型: " + (prop == null ? "（不存在）" : prop.PropertyType.FullName));
            if (prop != null)
            {
                Console.WriteLine("  可读=" + prop.CanRead + "  可写=" + prop.CanWrite);
            }

            CadDocument doc = DwgReader.Read(source);
            Console.WriteLine("读入：" + source);

            if (prop != null)
            {
                try
                {
                    Console.WriteLine("  读 PSTYLEMODE = " + prop.GetValue(doc.Header));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  读 PSTYLEMODE 抛异常：" + (ex.InnerException ?? ex).GetType().Name
                                      + " - " + (ex.InnerException ?? ex).Message);
                }
            }

            Console.WriteLine("Header 里与 plot 相关的属性：");
            foreach (PropertyInfo p in header.GetProperties())
            {
                if (p.Name.IndexOf("Plot", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string val;
                try
                {
                    val = Convert.ToString(p.GetValue(doc.Header));
                }
                catch (Exception ex)
                {
                    val = "<抛异常 " + (ex.InnerException ?? ex).GetType().Name + ">";
                }

                Console.WriteLine(string.Format("  {0,-28} {1,-24} = {2}", p.Name, p.PropertyType.Name, val));
            }
        }

        /// <summary>把 PlotStyleMode 设成指定值再写出，用来反推"写入口径"。</summary>
        private static void Write(string source, string destination, short value)
        {
            CadDocument doc = DwgReader.Read(source);
            PropertyInfo prop = ModeProperty(doc);

            Console.WriteLine("源图读到的 PSTYLEMODE = " + prop.GetValue(doc.Header));
            prop.SetValue(doc.Header, value);
            Console.WriteLine("设为 " + value + "，内存读回 = " + prop.GetValue(doc.Header));

            DwgWriter.Write(destination, doc);
            Console.WriteLine("已写出：" + destination);
        }
    }
}
