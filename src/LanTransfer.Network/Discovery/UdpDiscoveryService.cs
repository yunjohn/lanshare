using System.Net;
using System.Net.Sockets;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Interfaces;
using LanTransfer.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Discovery;

/// <summary>
/// UDP 广播设备发现。
/// 关键点：
/// 1. 设备 IP 以 UDP 报文实际 Source IP 为准，不信任报文内声明值；
/// 2. 多网卡环境下向每个有效接口的定向广播地址发送，而非只取第一张网卡；
/// 3. 端口被占用时不得崩溃，只记录 LastError 并允许修改端口后重启。
/// </summary>
public sealed class UdpDiscoveryService : IDiscoveryService
{
    private readonly ISettingsService _settings;
    private readonly IDeviceManager _deviceManager;
    private readonly IIdentityService _identity;
    private readonly ILogger<UdpDiscoveryService> _logger;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _socketLock = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _broadcastLoop;

    public UdpDiscoveryService(
        ISettingsService settings,
        IDeviceManager deviceManager,
        IIdentityService identity,
        ILogger<UdpDiscoveryService> logger)
    {
        _settings = settings;
        _deviceManager = deviceManager;
        _identity = identity;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;

            await StopInternalAsync().ConfigureAwait(false);

            Port = _settings.Current.DiscoveryPort;

            UdpClient? client = null;
            try
            {
                client = new UdpClient(AddressFamily.InterNetwork)
                {
                    EnableBroadcast = true,
                    ExclusiveAddressUse = false,
                };

                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            }
            catch (SocketException ex)
            {
                // 绑定失败时必须释放已创建的 socket，否则每次「保存并重启网络服务」都会漏一个句柄
                client?.Dispose();
                LastError = $"UDP 端口 {Port} 无法监听：{ex.Message}。请在设置中修改 UDP 发现端口后重试。";
                _logger.LogError(ex, "UDP 发现服务启动失败，端口 {Port} 可能被占用", Port);
                IsRunning = false;
                return;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                LastError = $"UDP 发现服务启动失败：{ex.Message}";
                _logger.LogError(ex, "UDP 发现服务启动失败");
                IsRunning = false;
                return;
            }

            lock (_socketLock) _client = client;

            _cts = new CancellationTokenSource();
            IsRunning = true;
            LastError = null;

            _logger.LogInformation("UDP 发现服务已启动，监听 0.0.0.0:{Port}，广播间隔 {Interval}",
                Port, AppConstants.BroadcastInterval);

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
            _broadcastLoop = Task.Run(() => BroadcastLoopAsync(_cts.Token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _logger.LogInformation("UDP 发现服务已停止");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        IsRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略
        }

        lock (_socketLock)
        {
            try
            {
                _client?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _client = null;
        }

        foreach (var task in new[] { _receiveLoop, _broadcastLoop })
        {
            if (task is null) continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 停止超时不影响后续重启
            }
        }

        _receiveLoop = null;
        _broadcastLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async Task BroadcastAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null || !IsRunning) return;

        var message = new DiscoveryMessage
        {
            Type = DiscoveryMessageTypes.Discover,
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            AppVersion = AppConstants.AppVersion,
            Version = AppConstants.ProtocolVersion,
            Port = _settings.Current.TransferPort,
        };

        var payload = ProtocolJson.Serialize(message);

        foreach (var (_, broadcastAddress) in NetworkHelper.GetBroadcastTargets())
        {
            if (!IPAddress.TryParse(broadcastAddress, out var address)) continue;

            try
            {
                await client.SendAsync(payload, new IPEndPoint(address, Port), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个网卡发送失败不应影响其它网卡
                _logger.LogDebug(ex, "向 {Broadcast} 发送发现广播失败", broadcastAddress);
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await BroadcastAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(AppConstants.BroadcastInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP 广播循环异常退出");
            LastError = $"UDP 广播循环异常退出：{ex.Message}";
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // ICMP 端口不可达等错误不应终止接收循环
                _logger.LogDebug(ex, "UDP 接收出现 Socket 错误，继续监听");
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP 接收异常，继续监听");
                continue;
            }

            try
            {
                await HandleDatagramAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "处理发现报文失败，来源 {Remote}", result.RemoteEndPoint);
            }
        }
    }

    private async Task HandleDatagramAsync(UdpReceiveResult result, CancellationToken cancellationToken)
    {
        var message = ProtocolJson.Deserialize<DiscoveryMessage>(result.Buffer);
        if (message is null) return;

        if (!string.Equals(message.Protocol, AppConstants.ProtocolName, StringComparison.Ordinal))
            return;

        if (string.IsNullOrWhiteSpace(message.DeviceId)) return;

        // 不处理自己发出的报文（多网卡回环）
        if (string.Equals(message.DeviceId, _identity.DeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        // 关键：IP 以实际 Source IP 为准，防止伪造地址
        var sourceIp = result.RemoteEndPoint.Address.ToString();

        if (message.Type == DiscoveryMessageTypes.Discover)
        {
            await ReportDeviceAsync(message, sourceIp, cancellationToken).ConfigureAwait(false);
            await RespondAsync(result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.Type == DiscoveryMessageTypes.DiscoverResponse)
        {
            await ReportDeviceAsync(message, sourceIp, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ReportDeviceAsync(DiscoveryMessage message, string sourceIp, CancellationToken cancellationToken)
    {
        var device = new DeviceInfo
        {
            DeviceId = message.DeviceId,
            DeviceName = message.DeviceName,
            IpAddress = sourceIp,
            Port = message.Port > 0 ? message.Port : AppConstants.DefaultTransferPort,
            AppVersion = message.AppVersion,
            ProtocolVersion = message.Version,
            OnlineState = OnlineState.Online,
            LastSeen = DateTimeOffset.UtcNow,
        };

        return _deviceManager.ReportSeenAsync(device, cancellationToken);
    }

    private async Task RespondAsync(IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null) return;

        var response = new DiscoveryMessage
        {
            Type = DiscoveryMessageTypes.DiscoverResponse,
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            AppVersion = AppConstants.AppVersion,
            Version = AppConstants.ProtocolVersion,
            Port = _settings.Current.TransferPort,
        };

        try
        {
            await client.SendAsync(ProtocolJson.Serialize(response), remoteEndPoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "向 {Remote} 回复发现响应失败", remoteEndPoint);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
