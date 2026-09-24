namespace LanTransfer.App.Services;

/// <summary>
/// 确认弹窗串行化队列。
///
/// <para>
/// 为什么必须有这个东西：接收确认 / 配对确认 / 同步请求三处都是「网络线程抛事件 → 切到 UI 线程 →
/// <c>ShowDialog()</c> 弹模态框」。而 <c>ShowDialog</c> 会开启一个**嵌套消息循环**，
/// 期间 Dispatcher 依然会执行已经排队（<c>BeginInvoke</c>）的回调 ——
/// 也就是说第一个确认框还没关，第二个请求的弹窗回调就已经跑起来了，于是弹窗一层层叠在一起
/// （用户必须从最上面那个开始一个个关，也看不清到底是谁发来的）。串行化后同一时刻只有一个确认框。
/// </para>
///
/// <para>
/// 队列还有容量上限：/transfers、/pair 这类接口对未配对设备也是开放的，
/// 局域网里任何人（或一个坏掉的客户端）都能不停制造请求。没有上限时，界面会被确认框淹没，
/// 内存里也会攒下大量待执行回调。<b>超过上限的动作不会被执行，而是由调用方显式按「拒绝」应答</b>
/// （绝不能静默丢弃：发送端会一直等到审批超时才报错，用户完全不知道发生了什么）。
/// </para>
///
/// <para>
/// 本类不引用任何 WPF 类型（投递方式由构造函数注入），因此可以直接做回归测试。
/// </para>
/// </summary>
public sealed class PromptQueue
{
    /// <summary>默认队列上限。正常情况下待确认请求是个位数，这个值只作为「被刷屏」时的安全阀。</summary>
    public const int DefaultCapacity = 64;

    private readonly Action<Action> _post;
    private readonly Action<Exception>? _onError;
    private readonly Queue<Action> _pending = new();
    private readonly object _gate = new();

    /// <summary>是否已有「驱动循环」在跑。不变量：<c>_running == false</c> ⇒ 队列为空。</summary>
    private bool _running;

    /// <param name="post">把动作投递到 UI 线程的方式；默认走 <see cref="UiDispatcher.Post"/>。</param>
    /// <param name="capacity">队列上限（不含正在执行的那一个）。</param>
    /// <param name="onError">弹窗动作抛异常时的回调（不能让异常打断驱动循环，否则队列会永久卡死）。</param>
    public PromptQueue(Action<Action>? post = null, int capacity = DefaultCapacity,
        Action<Exception>? onError = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _post = post ?? UiDispatcher.Post;
        _onError = onError;
        Capacity = capacity;
    }

    /// <summary>队列上限。</summary>
    public int Capacity { get; }

    /// <summary>当前等待执行的弹窗动作数量（用于诊断与测试）。</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>是否有一个弹窗正在显示（或即将显示）。</summary>
    public bool IsPromptActive
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>
    /// 排队一个弹窗动作。返回 <c>false</c> 表示队列已满（或无法投递到 UI 线程），
    /// 该动作不会被执行，调用方必须按「拒绝」应答对端。
    /// </summary>
    public bool Enqueue(Action prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        lock (_gate)
        {
            if (_running)
            {
                if (_pending.Count >= Capacity) return false;

                _pending.Enqueue(prompt);
                return true;
            }

            // 没有驱动循环在跑 → 由本次入队负责启动（先占位，避免并发入队启动两个循环）
            _running = true;
        }

        try
        {
            // 注意：这里投递的只是「启动驱动循环」，后续动作由循环自己取，
            // 所以无论 post 是否立即执行（嵌套消息循环 / 无 Application 时的内联执行），
            // 都不会出现递归重入。
            _post(() => Run(prompt));
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) _running = false;
            _onError?.Invoke(ex);
            return false;
        }
    }

    /// <summary>驱动循环：执行当前动作，然后继续执行队列里的下一个，直到队列为空。</summary>
    private void Run(Action first)
    {
        var next = first;

        while (true)
        {
            try
            {
                next();
            }
            catch (Exception ex)
            {
                // 一个弹窗失败不能拖垮整个队列（否则后续所有请求都收不到应答）
                _onError?.Invoke(ex);
            }

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                next = _pending.Dequeue();
            }
        }
    }
}
