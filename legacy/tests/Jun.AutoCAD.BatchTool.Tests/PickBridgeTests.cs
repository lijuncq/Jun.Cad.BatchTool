using Jun.AutoCAD.Core;
using Xunit;

namespace Jun.AutoCAD.BatchTool.Tests
{
    /// <summary>
    /// 拾取桥的回归测试。
    ///
    /// 背景（2026-10-29 实机反馈"拾取不成功"）：旧实现在模态抽屉里 Hide 后排队命令，
    /// 而模态窗口 Hide 会让 ShowDialog 立刻返回、抽屉被 Dispose；同时 4 秒看门狗会把
    /// "用户还在图上慢慢点"当成"命令没跑起来"取消掉。这里钉住修正后的三条契约：
    ///   1. 请求带号，旧号取消不能误伤新请求；
    ///   2. 命令标记开始后，界面必须能区分"在等用户点选"与"命令没跑起来"；
    ///   3. 回调只触发一次，迟到的结果被忽略。
    /// </summary>
    public class PickBridgeTests
    {
        [Fact]
        public void 请求会拿到请求号_已有请求在途时拒绝第二次()
        {
            PickBridge.CancelPending();

            int first = PickBridge.Request(NameKind.Block, delegate { });
            int second = PickBridge.Request(NameKind.Layer, delegate { });

            Assert.True(first > 0);
            Assert.Equal(0, second);
            Assert.True(PickBridge.HasPending);

            PickBridge.CancelPending();
            Assert.False(PickBridge.HasPending);
        }

        [Fact]
        public void 取消旧请求号不会误伤新请求()
        {
            PickBridge.CancelPending();

            int first = PickBridge.Request(NameKind.Block, delegate { });
            Assert.True(PickBridge.Cancel(first));

            int second = PickBridge.Request(NameKind.Layer, delegate { });
            Assert.True(second > first);

            // 上一次的看门狗/超时晚到，不能把这一次的请求打掉
            Assert.False(PickBridge.Cancel(first));
            Assert.True(PickBridge.IsPending(second));

            PickBridge.Cancel(second);
        }

        [Fact]
        public void 命令标记开始后界面能区分_在等用户点选_与_命令没跑起来()
        {
            PickBridge.CancelPending();

            int token = PickBridge.Request(NameKind.Block, delegate { });

            Assert.False(PickBridge.HasStarted(token)); // 命令还没进来 → 看门狗该管

            NameKind kind;
            int taken;
            Assert.True(PickBridge.TryTake(out kind, out taken));
            Assert.Equal(token, taken);
            Assert.Equal(NameKind.Block, kind);

            PickBridge.MarkStarted(token);

            Assert.True(PickBridge.HasStarted(token)); // 已经在等用户点选 → 看门狗必须让路
            Assert.True(PickBridge.IsPending(token));

            PickBridge.CancelPending();
        }

        [Fact]
        public void 回调只触发一次_迟到的结果被忽略()
        {
            PickBridge.CancelPending();

            int calls = 0;
            string got = null;
            int token = PickBridge.Request(NameKind.Layer, delegate(string name)
            {
                calls++;
                got = name;
            });

            PickBridge.Complete(token, "WALL");
            PickBridge.Complete(token, "LATE");

            Assert.Equal(1, calls);
            Assert.Equal("WALL", got);
            Assert.False(PickBridge.IsPending(token));
        }

        [Fact]
        public void 取消请求会以null回调_界面据此重开抽屉()
        {
            PickBridge.CancelPending();

            bool called = false;
            string got = "sentinel";
            int token = PickBridge.Request(NameKind.Block, delegate(string name)
            {
                called = true;
                got = name;
            });

            Assert.True(PickBridge.Cancel(token));
            Assert.True(called);
            Assert.Null(got);
        }
    }
}
