using Jun.Cad.Batch.Engine;
using Xunit;

namespace Jun.Cad.Batch.Tests
{
    /// <summary>
    /// 进度文案。界面进度条上那行字就是 <see cref="BatchProgress.Describe"/> 的输出，
    /// 所以格式（第几个 / 总数 / 文件名 / 阶段）在这里锁住。
    /// </summary>
    public class BatchProgressTests
    {
        [Fact]
        public void 处理中显示序号总数与文件名()
        {
            var progress = new BatchProgress
            {
                Index = 3,
                Total = 11,
                FileName = "a.dwg",
                Phase = "执行"
            };

            Assert.Equal("执行 3/11  a.dwg", progress.Describe());
        }

        [Fact]
        public void 结束后只说完成与总数()
        {
            var progress = new BatchProgress
            {
                Index = 11,
                Total = 11,
                Phase = "预览",
                Finished = true
            };

            Assert.Equal("预览完成：共 11 个文件", progress.Describe());
        }

        [Fact]
        public void 缺字段也不抛异常()
        {
            Assert.NotNull(new BatchProgress().Describe());
        }
    }
}
