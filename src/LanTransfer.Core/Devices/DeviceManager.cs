using System.Collections.Concurrent;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Devices;

/// <summary>
/// 设备管理器：维护设备缓存、在线状态与信任状态。
/// 在线判定基于 LastSeen，超过 <see cref="AppConstants.OfflineThreshold"/> 未收到报文即离线。
/// </summary>
public sealed class DeviceManager : IDeviceManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDeviceRepository _repository;
    private readonly IIdentityService _identity;
    private readonly ISettingsService _settings;
    private readonly ITransferClient _client;
    private readonly IPeerCertificateRegistry _certificates;
    private readonly ILogger<DeviceManager> _logger;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProbes =
        new(StringComparer.OrdinalIgnoreCase);

    private Timer? _sweeper;
    private DeviceInfo _localDevice = new();

    public DeviceManager(
        IDeviceRepository repository,
        IIdentityService identity,
        ISettingsService settings,
        ITransferClient client,
        IPeerCertificateRegistry certificates,
        ILogger<DeviceManager> logger)
    {
        _repository = repository;
        _identity = identity;
        _settings = settings;
        _client = client;
        _certificates = certificates;
        _logger = logger;
    }

    public IReadOnlyList<DeviceInfo> Devices =>
        _devices.Values.OrderByDescending(d => d.IsOnline).ThenBy(d => d.DeviceName).ToList();

    public DeviceInfo LocalDevice => _localDevice;

    public event EventHandler<DeviceInfo>? DeviceOnline;
    public event EventHandler<DeviceInfo>? DeviceOffline;
    public event EventHandler? DevicesChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _identity.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var localAddress = NetworkHelper.GetPrimaryIPv4Address();

        _localDevice = new DeviceInfo
        {
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            IpAddress = localAddress ?? "127.0.0.1",
            Port = _settings.Current.TransferPort,
            AppVersion = AppConstants.AppVersion,
            ProtocolVersion = AppConstants.ProtocolVersion,
            TrustState = TrustState.Trusted,
            OnlineState = OnlineState.Online,
            LastSeen = DateTimeOffset.UtcNow,
            FirstSeen = DateTimeOffset.UtcNow,
            CertificateFingerprint = _identity.CertificateFingerprint,
        };

        var stored = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var device in stored)
        {
            if (string.Equals(device.DeviceId, _localDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            // 启动时一律视为离线，等待 UDP / 手工探测重新确认
            device.OnlineState = OnlineState.Offline;
            _devices[device.DeviceId] = device;

            // 启动即把已记录指纹的可信设备钉住，之后任何伪造端点都过不了 TLS 握手
            PinTrustedEndpoint(device.CertificateFingerprint, device.IpAddress);
        }

        _sweeper = new Timer(_ => _ = SweepAsync(), null, TimeSpan.Zero,
            AppConstants.DeviceSweepInterval);

        _logger.LogInformation("设备管理器初始化完成: 本机 {Name} ({DeviceId})，已加载 {Count} 个历史设备",
            _localDevice.DeviceName, _localDevice.DeviceId, _devices.Count);

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把某个端点钉在该设备已记录的证书指纹上（TLS pinning）。
    ///
    /// 这是对「UDP 报文伪造 DeviceId + 按 IP 首次接触即信任（TOFU）」的兜底防线：
    /// 即使攻击者用伪造报文把可信设备的 IP 改成自己的地址，我们连过去时也会
    /// 要求对方出示**该设备已记录的证书**，攻击者拿不出来，握手直接失败。
    /// 反之，对端真的换了 IP（DHCP/换网卡）时证书不变，连接照常成功。
    /// </summary>
    private void PinTrustedEndpoint(string? fingerprint, string? host)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(host)) return;

        _certificates.Remember(host, fingerprint);
    }

    public async Task RefreshOnlineStatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        try
        {
            foreach (var device in _devices.Values.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProbeAsync(device, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task ReportSeenAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId)) return;
        if (string.Equals(device.DeviceId, _localDevice.DeviceId, StringComparison.OrdinalIgnoreCase)) return;

        var isNew = false;
        DeviceInfo? justCameOnline = null;

        _devices.AddOrUpdate(device.DeviceId,
            _ =>
            {
                isNew = true;
                justCameOnline = device;
                return device;
            },
            (_, existing) =>
            {
                var wasOffline = existing.OnlineState == OnlineState.Offline;

                var endpointChanged = !string.Equals(existing.IpAddress, device.IpAddress,
                                          StringComparison.OrdinalIgnoreCase) ||
                                      existing.Port != device.Port;

                if (endpointChanged && existing.TrustState == TrustState.Trusted)
                {
                    _logger.LogWarning("可信设备 {Name} 的端点发生变化: {Old} → {New}",
                        existing.DeviceName, existing.EndPoint, device.EndPoint);
                }

                existing.DeviceName = string.IsNullOrWhiteSpace(device.DeviceName)
                    ? existing.DeviceName
                    : device.DeviceName;
                existing.IpAddress = device.IpAddress;
                existing.Port = device.Port;
                existing.AppVersion = device.AppVersion;
                existing.ProtocolVersion = device.ProtocolVersion;
                existing.LastSeen = DateTimeOffset.UtcNow;
                existing.OnlineState = OnlineState.Online;
                existing.IsManual = existing.IsManual && device.IsManual;
                if (wasOffline) justCameOnline = existing;

                // 无论端点来自 UDP 还是探测，都把它钉在「该设备已记录的证书指纹」上：
                // UDP 报文里的 DeviceId 可以伪造，但伪造者拿不出对应证书的私钥，
                // 于是 TLS 握手会因指纹不匹配而失败——文件不会发到冒充者手里。
                PinTrustedEndpoint(existing.CertificateFingerprint, existing.IpAddress);
                return existing;
            });

        var current = _devices[device.DeviceId];
        if (current.OnlineState != OnlineState.Online)
            current.OnlineState = OnlineState.Online;
        current.LastSeen = DateTimeOffset.UtcNow;

        if (isNew)
        {
            current.FirstSeen = DateTimeOffset.UtcNow;
            var stored = await _repository.GetAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                current.TrustState = stored.TrustState;
                current.CertificateFingerprint = stored.CertificateFingerprint;
                current.FirstSeen = stored.FirstSeen;
                current.IsManual = stored.IsManual;
            }

            _logger.LogInformation("发现新设备: {Name} ({DeviceId}) {Endpoint}",
                current.DeviceName, current.DeviceId, current.EndPoint);
        }

        await _repository.UpsertAsync(current, cancellationToken).ConfigureAwait(false);

        if (justCameOnline is not null)
        {
            _logger.LogInformation("设备上线: {Name} {Endpoint}", current.DeviceName, current.EndPoint);
            DeviceOnline?.Invoke(this, current);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<DeviceInfo> AddManualDeviceAsync(string ipAddress, int port,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            throw new ArgumentException("IP 地址不能为空", nameof(ipAddress));

        var info = await _client.GetDeviceInfoAsync(ipAddress.Trim(), port, cancellationToken)
            .ConfigureAwait(false);

        var device = new DeviceInfo
        {
            DeviceId = info.DeviceId,
            DeviceName = info.DeviceName,
            IpAddress = ipAddress.Trim(),
            Port = info.Port > 0 ? info.Port : port,
            AppVersion = info.AppVersion,
            ProtocolVersion = info.ProtocolVersion,
            CertificateFingerprint = info.CertificateFingerprint,
            TrustState = TrustState.Unknown,
            OnlineState = OnlineState.Online,
            LastSeen = DateTimeOffset.UtcNow,
            FirstSeen = DateTimeOffset.UtcNow,
            IsManual = true,
        };

        if (string.Equals(device.DeviceId, _localDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能添加本机为远程设备。");

        await ReportSeenAsync(device, cancellationToken).ConfigureAwait(false);

        var current = _devices[device.DeviceId];
        current.IsManual = true;
        await _repository.UpsertAsync(current, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("手工添加设备: {Name} ({DeviceId}) {Endpoint}",
            current.DeviceName, current.DeviceId, current.EndPoint);

        return current;
    }

    public async Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_devices.TryRemove(deviceId, out var removed))
        {
            _certificates.Forget(removed.IpAddress);
            _lastProbes.TryRemove(deviceId, out _);
            _logger.LogInformation("移除设备: {Name} ({DeviceId})", removed.DeviceName, removed.DeviceId);
        }

        await _repository.DeleteAsync(deviceId, cancellationToken).ConfigureAwait(false);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public DeviceInfo? Find(string deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? device : null;

    public async Task UpdateTrustAsync(string deviceId, TrustState trustState, string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        device.TrustState = trustState;
        if (!string.IsNullOrEmpty(fingerprint)) device.CertificateFingerprint = fingerprint;

        if (trustState == TrustState.Trusted)
        {
            // 配对成功 / 重新确认身份后立即钉住该端点，后续连接必须出示这张证书
            PinTrustedEndpoint(device.CertificateFingerprint, device.IpAddress);
        }
        else
        {
            _certificates.Forget(device.IpAddress);
        }

        await _repository.UpdateTrustAsync(deviceId, trustState, fingerprint, cancellationToken)
            .ConfigureAwait(false);

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SweepAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            foreach (var device in _devices.Values.ToList())
            {
                // UDP 心跳正常时无需额外探测；心跳过期或当前离线时，用 HTTPS 做一次确认。
                if (device.OnlineState == OnlineState.Online && now - device.LastSeen <= AppConstants.OfflineThreshold)
                    continue;

                if (_lastProbes.TryGetValue(device.DeviceId, out var lastProbe) &&
                    now - lastProbe < AppConstants.DeviceProbeInterval)
                    continue;

                _lastProbes[device.DeviceId] = now;
                await ProbeAsync(device, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task ProbeAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AppConstants.DeviceProbeTimeout);

            var info = await _client.GetDeviceInfoAsync(device.IpAddress, device.Port, timeout.Token)
                .ConfigureAwait(false);

            if (!string.Equals(info.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("设备探测返回了不同的 DeviceId: {Endpoint} 期望={Expected} 实际={Actual}",
                    device.EndPoint, device.DeviceId, info.DeviceId);
                MarkOffline(device);
                return;
            }

            await ReportSeenAsync(new DeviceInfo
            {
                DeviceId = info.DeviceId,
                DeviceName = info.DeviceName,
                IpAddress = device.IpAddress,
                Port = info.Port > 0 ? info.Port : device.Port,
                AppVersion = info.AppVersion,
                ProtocolVersion = info.ProtocolVersion,
                CertificateFingerprint = info.CertificateFingerprint,
                IsManual = device.IsManual,
                OnlineState = OnlineState.Online,
                LastSeen = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "主动探测设备失败: {Device} {Endpoint}", device.DeviceName, device.EndPoint);
            MarkOffline(device);
        }
    }

    private void MarkOffline(DeviceInfo device)
    {
        if (device.OnlineState != OnlineState.Online) return;
        if (DateTimeOffset.UtcNow - device.LastSeen <= AppConstants.OfflineThreshold) return;

        device.OnlineState = OnlineState.Offline;
        _logger.LogInformation("设备离线: {Name} {Endpoint}", device.DeviceName, device.EndPoint);
        DeviceOffline?.Invoke(this, device);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        _sweeper?.Dispose();
        _sweeper = null;
        // Timer 回调可能仍在完成最后一次 HTTPS 探测；此处不销毁 gate，避免回调 finally Release 时竞态。
        return ValueTask.CompletedTask;
    }
}
