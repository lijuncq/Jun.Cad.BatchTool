using System;
using System.Collections.Generic;
using System.IO;
using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 按文件夹收集图纸。命令行（`--input 目录 --recursive`）与界面的"输入图纸文件夹"共用这一份，
    /// 所以这里的用例同时守住两边。
    ///
    /// 重点是三条排除规则——它们错一条，第二次运行就会把上一次的产物当原料。
    /// </summary>
    public class DrawingFolderTests : IDisposable
    {
        private readonly string _root;

        public DrawingFolderTests()
        {
            _root = TestPaths.NewTempDirectory("folder");
        }

        public void Dispose()
        {
            TestPaths.TryDelete(_root);
        }

        private string Touch(string relative)
        {
            string path = Path.Combine(_root, relative);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, "x");
            return path;
        }

        private List<string> Enumerate(bool recursive, string outputRoot = null)
        {
            return DrawingFolder.Enumerate(_root, null, recursive,
                outputRoot ?? DrawingFolder.DefaultOutputRoot(_root));
        }

        [Fact]
        public void 非递归只收顶层()
        {
            Touch("a.dwg");
            Touch("sub\\b.dwg");

            List<string> files = Enumerate(false);

            Assert.Single(files);
            Assert.EndsWith("a.dwg", files[0]);
        }

        [Fact]
        public void 递归收子目录()
        {
            Touch("a.dwg");
            Touch("sub\\b.dwg");
            Touch("sub\\deep\\c.dwg");

            Assert.Equal(3, Enumerate(true).Count);
        }

        [Fact]
        public void 排除默认输出目录()
        {
            Touch("a.dwg");
            Touch("output\\a.dwg");

            List<string> files = Enumerate(true);

            Assert.Single(files);
            Assert.DoesNotContain(files, f => f.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void 排除运行清单目录()
        {
            Touch("a.dwg");
            Touch(".jun-runs\\20261031-120000\\backups\\a.dwg");

            Assert.Single(Enumerate(true));
        }

        /// <summary>
        /// "以前跑过、这次换了输出位置"的旧输出根：目录里有 `.jun-runs`，整棵都不该再当输入。
        /// </summary>
        [Fact]
        public void 排除含jun_runs的旧输出根()
        {
            Touch("a.dwg");
            Touch("old-out\\a.dwg");
            Touch("old-out\\.jun-runs\\x\\manifest.json");

            List<string> files = Enumerate(true);

            Assert.Single(files);
            Assert.EndsWith("a.dwg", files[0]);
        }

        [Fact]
        public void 不排除子目录里名字叫output的普通目录当输出根另有其处时()
        {
            // 输出根指向别处（例如 --out 指定了别的目录）时，`sub\output` 只是普通目录
            Touch("sub\\output\\b.dwg");

            string otherRoot = Path.Combine(_root, "somewhere-else");
            List<string> files = DrawingFolder.Enumerate(_root, null, true, otherRoot);

            Assert.Single(files);
        }

        [Fact]
        public void 多个通配符去重且按路径排序()
        {
            Touch("b.dwg");
            Touch("a.DWG");

            List<string> files = DrawingFolder.Enumerate(_root, new[] { "*.dwg", "*.DWG" }, false,
                DrawingFolder.DefaultOutputRoot(_root));

            Assert.Equal(2, files.Count);
            Assert.True(string.Compare(files[0], files[1], StringComparison.OrdinalIgnoreCase) < 0);
        }

        [Fact]
        public void 目录不存在返回空而不是抛异常()
        {
            Assert.Empty(DrawingFolder.Enumerate(Path.Combine(_root, "nope"), null, true, null));
            Assert.Empty(DrawingFolder.Enumerate(null, null, true, null));
        }

        [Fact]
        public void 输出根为空时不排除任何东西()
        {
            // --in-place 时输出就是源文件，不能把任何目录排除掉
            Touch("a.dwg");
            Touch("output\\b.dwg");

            Assert.Equal(2, DrawingFolder.Enumerate(_root, null, true, null).Count);
        }

        [Fact]
        public void 默认输出根是输入目录下的output()
        {
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "output"),
                DrawingFolder.DefaultOutputRoot(_root));
        }
    }
}
