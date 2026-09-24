using LanTransfer.App.Services;
using Xunit;

namespace LanTransfer.App.Tests;

/// <summary>
/// App 层确认弹窗调度的回归测试。
///
/// <para>
/// 历史缺陷：接收确认 / 配对确认 / 同步请求都是「网络线程抛事件 → Post 到 UI 线程 → ShowDialog」。
/// <c>ShowDialog</c> 会开启**嵌套消息循环**，期间 Dispatcher 仍会把已排队的回调执行掉，
/// 于是第一个确认框还没关，第二个请求的弹窗就已经弹出来了 —— 多个确认框一层层叠在一起
/// （用户得从最上面那个开始一个个关，也看不清是谁发来的）。这里用一个假的 Dispatcher
/// 把「嵌套消息循环」如实模拟出来，验证串行化真的成立。
/// </para>
/// </summary>
public sealed class PromptQueueTests
{
    [Fact]
    public void PromptQueue_KeepsOnlyOneDialogOpen_WhenRequestsArriveWhileDialogIsOpen()
    {
        var sim = new DispatcherSim();
        var script = new List<string>();
        var active = 0;
        var maxActive = 0;

        PromptQueue? queue = null;

        void Dialog(string name, Action? whileOpen = null)
        {
            script.Add($"open:{name}");
            active++;
            maxActive = Math.Max(maxActive, active);

            // 弹窗正开着的时候，网络线程又送来一个请求
            whileOpen?.Invoke();

            // ShowDialog 的嵌套消息循环：会把已经排队的回调立刻执行掉
            sim.DrainNested();

            active--;
            script.Add($"close:{name}");
        }

        queue = new PromptQueue(sim.Post, capacity: 8, onError: ex => throw ex);

        // A 弹窗打开期间又来了 B
        queue.Enqueue(() => Dialog("A", () => queue.Enqueue(() => Dialog("B"))));
        sim.PumpTopLevel();

        Assert.Equal(1, maxActive);
        Assert.Equal(new[] { "open:A", "close:A", "open:B", "close:B" }, script);

        // 串行化之后整个队列只投递一次「启动驱动循环」的回调：
        // 没有第二次投递，就不可能在嵌套消息循环里重入。
        Assert.Equal(1, sim.PostedCount);
        Assert.False(queue.IsPromptActive);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void HarnessItself_DetectsStacking_WhenEveryRequestPostsItsOwnDialog()
    {
        // 这是修复前的写法（每个请求各自 Post 自己的弹窗回调）。
        // 它存在的意义是证明上面那条断言不是「永远为真」的空测试：
        // 同一套模拟环境下，旧写法确实会让两个弹窗同时开着。
        var sim = new DispatcherSim();
        var active = 0;
        var maxActive = 0;

        void Dialog(string name, Action? whileOpen = null)
        {
            active++;
            maxActive = Math.Max(maxActive, active);

            whileOpen?.Invoke();
            sim.DrainNested();

            active--;
        }

        sim.Post(() => Dialog("A", () => sim.Post(() => Dialog("B"))));
        sim.PumpTopLevel();

        Assert.Equal(2, maxActive);
    }

    [Fact]
    public void PromptQueue_RunsQueuedPromptsInOrder()
    {
        var sim = new DispatcherSim();
        var ran = new List<int>();

        PromptQueue? queue = null;

        queue = new PromptQueue(sim.Post, capacity: 8);

        queue.Enqueue(() =>
        {
            ran.Add(1);

            // 排队期间（第一个弹窗还没关）又来了两个请求
            queue.Enqueue(() => ran.Add(2));
            queue.Enqueue(() => ran.Add(3));
        });

        sim.PumpTopLevel();

        Assert.Equal(new[] { 1, 2, 3 }, ran);
    }

    [Fact]
    public void FullQueue_RejectsExtraPrompt_WithoutRunningIt()
    {
        var sim = new DispatcherSim();
        var ran = new List<string>();
        var rejected = false;

        PromptQueue? queue = null;

        queue = new PromptQueue(sim.Post, capacity: 2);

        queue.Enqueue(() =>
        {
            ran.Add("A");
            queue.Enqueue(() => ran.Add("B"));
            queue.Enqueue(() => ran.Add("C"));

            // 队列上限是 2，此时 B、C 已占满 → 第 4 个请求必须被明确拒绝
            // （调用方据此按「拒绝」应答对端，而不是静默丢弃）
            rejected = !queue.Enqueue(() => ran.Add("D"));

            sim.DrainNested();
        });

        sim.PumpTopLevel();

        Assert.True(rejected);
        Assert.Equal(new[] { "A", "B", "C" }, ran);
    }

    [Fact]
    public void ThrowingPrompt_DoesNotWedgeTheQueue()
    {
        var sim = new DispatcherSim();
        var errors = new List<Exception>();
        var ran = new List<string>();

        var queue = new PromptQueue(sim.Post, capacity: 4, onError: errors.Add);

        queue.Enqueue(() => throw new InvalidOperationException("弹窗炸了"));
        sim.PumpTopLevel();

        Assert.Single(errors);
        Assert.False(queue.IsPromptActive);

        // 一个弹窗失败之后，后续请求仍必须能正常弹出来
        queue.Enqueue(() => ran.Add("later"));
        sim.PumpTopLevel();

        Assert.Equal(new[] { "later" }, ran);
    }

    [Fact]
    public void ConcurrentEnqueue_RunsEveryPromptExactlyOnce()
    {
        var sim = new DispatcherSim();
        var queue = new PromptQueue(sim.Post, capacity: 1024);

        var active = 0;
        var maxActive = 0;
        var count = 0;

        void Body()
        {
            var now = Interlocked.Increment(ref active);
            Max(ref maxActive, now);
            Interlocked.Increment(ref count);
            Thread.Sleep(1);
            Interlocked.Decrement(ref active);
        }

        Parallel.For(0, 64, _ => queue.Enqueue(Body));
        sim.PumpTopLevel();

        Assert.Equal(64, count);
        Assert.Equal(1, maxActive);
        Assert.Equal(1, sim.PostedCount);
    }

    [Fact]
    public void Enqueue_ReportsFailure_WhenPostingFails()
    {
        // Dispatcher 已经关闭时 BeginInvoke 会抛异常：必须报「失败」让调用方拒绝请求，
        // 而不是留下一个永远转不起来的驱动循环（后续所有请求都会石沉大海）。
        var errors = new List<Exception>();
        var queue = new PromptQueue(_ => throw new InvalidOperationException("dispatcher 已关闭"),
            capacity: 4, onError: errors.Add);

        Assert.False(queue.Enqueue(() => { }));
        Assert.Single(errors);

        // 队列没有被卡住：换一个可用的投递方式（这里直接内联执行）后仍能正常工作
        var sim = new DispatcherSim();
        var recovered = new PromptQueue(sim.Post, capacity: 4);
        var ran = false;

        Assert.True(recovered.Enqueue(() => ran = true));
        sim.PumpTopLevel();

        Assert.True(ran);
    }

    private static void Max(ref int target, int value)
    {
        int current;

        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    /// <summary>模拟 WPF Dispatcher：Post 只是排队，模态弹窗的嵌套消息循环会把队列里的回调执行掉。</summary>
    private sealed class DispatcherSim
    {
        private readonly Queue<Action> _queued = new();

        public int PostedCount { get; private set; }

        public void Post(Action action)
        {
            PostedCount++;
            _queued.Enqueue(action);
        }

        /// <summary>嵌套消息循环：把当前已排队的回调全部执行（弹窗打开期间 Dispatcher 的行为）。</summary>
        public void DrainNested()
        {
            while (_queued.Count > 0) _queued.Dequeue()();
        }

        /// <summary>顶层消息循环。</summary>
        public void PumpTopLevel() => DrainNested();
    }
}
