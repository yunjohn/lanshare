using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace LanTransfer.App.Services;

/// <summary>
/// 保证同一 Windows 用户在整台电脑上只运行一个应用实例，
/// 并把后续启动转换为「激活已有窗口」请求。
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private Task? _listener;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(string applicationId = "LanTransfer")
    {
        // %LOCALAPPDATA% 下的配置、数据库和身份文件会被同一用户的多个
        // Windows 会话共享，因此不能用 SessionId 隔离互斥锁。SID 既能跨会话
        // 保持稳定，又不会误阻止同一台电脑上的其他 Windows 用户。
        var userScope = WindowsIdentity.GetCurrent().User?.Value
                        ?? $"Session.{Process.GetCurrentProcess().SessionId}";
        _mutexName = $"Global\\{applicationId}.SingleInstance.{userScope}";
        _pipeName = $"{applicationId}.SingleInstance.{userScope}";
    }

    /// <summary>尝试成为主实例。只有返回 true 的进程可以继续初始化。</summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutex is not null) return _ownsMutex;

        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        _ownsMutex = createdNew;
        return _ownsMutex;
    }

    /// <summary>主实例开始接收来自后续启动进程的激活请求。</summary>
    public void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (!_ownsMutex) throw new InvalidOperationException("只有主实例可以启动唤醒监听。");
        if (_listener is not null) return;

        _listener = Task.Run(() => ListenAsync(activate, _stop.Token));
    }

    /// <summary>从第二个进程通知主实例显示窗口。</summary>
    public async Task NotifyPrimaryAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync("activate").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 主实例可能正处于启动或退出边界；互斥锁仍能保证不会启动第二套服务。
        }
        catch (IOException)
        {
            // 同上：唤醒失败不得让第二实例继续运行。
        }
        catch (UnauthorizedAccessException)
        {
            // 主实例与次实例的提权级别不同时，管道可能拒绝连接。
            // 互斥锁仍然已阻止双开，因此次实例应直接退出。
        }
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, "activate", StringComparison.Ordinal)) activate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // 客户端中途退出时重建管道，继续接受下一次唤醒。
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();

        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* 进程退出边界上已释放 */ }
        }

        _mutex?.Dispose();
        _stop.Dispose();
    }
}
