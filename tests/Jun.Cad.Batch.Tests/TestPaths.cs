using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 测试数据位置。
    ///
    /// 样图目录用环境变量 <c>JUN_CAD_SAMPLE_DWG</c> 指定，默认是本机那份海工模块布置图。
    /// 目录不存在时，依赖样图的用例在**发现阶段**跳过（`Skip`），而不是静默通过——
    /// 静默通过会让人以为"跑了但没事"，而实际上一条断言都没执行。
    /// </summary>
    internal static class TestPaths
    {
        public const string SampleDwgEnvVar = "JUN_CAD_SAMPLE_DWG";

        private const string DefaultSampleDirectory = @"C:\Users\lijun\Downloads\SampleDwg";

        public static string SampleDwgDirectory
        {
            get
            {
                string fromEnv = Environment.GetEnvironmentVariable(SampleDwgEnvVar);
                return string.IsNullOrWhiteSpace(fromEnv) ? DefaultSampleDirectory : fromEnv;
            }
        }

        public static string[] SampleDwgFiles()
        {
            string dir = SampleDwgDirectory;
            if (!Directory.Exists(dir))
            {
                return new string[0];
            }

            string[] files = Directory.GetFiles(dir, "*.dwg", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>样图目录是否可用（存在且至少有一个 .dwg）。</summary>
        public static bool HasSamples
        {
            get { return SampleDwgFiles().Length > 0; }
        }

        /// <summary>取一张样图（按名字排序的第一张），没有则抛。</summary>
        public static string FirstSample()
        {
            string[] files = SampleDwgFiles();
            if (files.Length == 0)
            {
                throw new InvalidOperationException("没有样图：" + SampleDwgDirectory);
            }

            return files[0];
        }

        /// <summary>建一个本次测试专用的临时目录（调用方负责删）。</summary>
        public static string NewTempDirectory(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "jun-cad-tests",
                tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
                // 临时目录删不掉不影响测试结论
            }
        }
    }

    /// <summary>样图目录不可用时自动跳过的 [Fact]。</summary>
    public sealed class SampleDwgFactAttribute : FactAttribute
    {
        public SampleDwgFactAttribute()
        {
            if (!TestPaths.HasSamples)
            {
                Skip = "未找到样图目录（" + TestPaths.SampleDwgDirectory
                       + "），设置环境变量 " + TestPaths.SampleDwgEnvVar + " 后重跑。";
            }
        }
    }

    /// <summary>样图目录不可用时自动跳过的 [Theory]。</summary>
    public sealed class SampleDwgTheoryAttribute : TheoryAttribute
    {
        public SampleDwgTheoryAttribute()
        {
            if (!TestPaths.HasSamples)
            {
                Skip = "未找到样图目录（" + TestPaths.SampleDwgDirectory
                       + "），设置环境变量 " + TestPaths.SampleDwgEnvVar + " 后重跑。";
            }
        }
    }

}
