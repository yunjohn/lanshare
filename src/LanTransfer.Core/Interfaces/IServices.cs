using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;

namespace LanTransfer.Core.Interfaces;

/// <summary>应用配置（持久化到 %LOCALAPPDATA%\LanTransfer\settings.json）。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>保存并立即生效（含重启网络服务）。</summary>
    Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>可持久化的应用配置。</summary>
public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;

    public string DeviceId { get; set; } = string.Empty;

    public int DiscoveryPort { get; set; } = Common.Constants.AppConstants.DefaultDiscoveryPort;

    public int TransferPort { get; set; } = Common.Constants.AppConstants.DefaultTransferPort;

    public string DownloadPath { get; set; } = DefaultDownloadPath();

    public int ChunkSize { get; set; } = Common.Constants.AppConstants.DefaultChunkSize;

    public bool AutoStart { get; set; }

    /// <summary>自动接收可信设备文件，默认关闭。</summary>
    public bool AutoAcceptTrustedDevice { get; set; }

    public bool Notifications { get; set; } = true;

    public string Theme { get; set; } = "Light";

    public string LogLevel { get; set; } = "Information";

    public int SyncScanIntervalMinutes { get; set; } = Common.Constants.AppConstants.DefaultSyncScanIntervalMinutes;

    public int TombstoneRetentionDays { get; set; } = Common.Constants.AppConstants.DefaultTombstoneRetentionDays;

    /// <summary>
    /// 点击关闭按钮时的行为。默认 <see cref="CloseWindowAction.Ask"/>：
    /// 第一次关闭时询问并把用户选择记下来，之后不再重复询问。
    /// </summary>
    public CloseWindowAction CloseAction { get; set; } = CloseWindowAction.Ask;

    public static string DefaultDownloadPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "LAN Transfer");

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>设备管理器。</summary>
public interface IDeviceManager
{
    IReadOnlyList<DeviceInfo> Devices { get; }

    DeviceInfo LocalDevice { get; }

    event EventHandler<DeviceInfo>? DeviceOnline;

    event EventHandler<DeviceInfo>? DeviceOffline;

    event EventHandler? DevicesChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>UDP / 手工发现结果上报。</summary>
    Task ReportSeenAsync(DeviceInfo device, CancellationToken cancellationToken = default);

    /// <summary>通过 HTTPS 主动探测已知设备，修正 UDP 广播被防火墙或网络设备丢弃造成的假离线。</summary>
    Task RefreshOnlineStatesAsync(CancellationToken cancellationToken = default);

    /// <summary>手工添加 IP 设备。</summary>
    Task<DeviceInfo> AddManualDeviceAsync(string ipAddress, int port, CancellationToken cancellationToken = default);

    Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    DeviceInfo? Find(string deviceId);

    Task UpdateTrustAsync(string deviceId, TrustState trustState, string? fingerprint,
        CancellationToken cancellationToken = default);
}

/// <summary>UDP 广播设备发现。</summary>
public interface IDiscoveryService : IAsyncDisposable
{
    bool IsRunning { get; }

    int Port { get; }

    string? LastError { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>立即广播一次发现请求。</summary>
    Task BroadcastAsync(CancellationToken cancellationToken = default);
}

/// <summary>网络接口枚举与诊断。</summary>
public interface INetworkDiagnosticsService
{
    /// <summary>枚举所有有效网络接口（已过滤 Loopback / 未启用 / 虚拟无效接口）。</summary>
    IReadOnlyList<NetworkInterfaceInfo> GetInterfaces();

    /// <summary>本机所有可用于广播的 IPv4 地址。</summary>
    IReadOnlyList<string> GetLocalIPv4Addresses();

    /// <summary>对目标执行 DNS → TCP → HTTPS API 的完整连通性测试。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(string host, int port,
        CancellationToken cancellationToken = default);

    /// <summary>检测端口是否被本机其它进程占用。</summary>
    Task<PortUsageInfo> CheckPortAsync(int port, bool udp, CancellationToken cancellationToken = default);

    /// <summary>判断当前网络是否为「公用网络」（公用网络需要安全提醒）。</summary>
    Task<NetworkCategory> GetNetworkCategoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>连通性测试结果。</summary>
public sealed class ConnectionTestResult
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? ResolvedIp { get; set; }
    public bool DnsResolved { get; set; }
    public bool TcpConnected { get; set; }
    public bool ServiceResponded { get; set; }
    public bool ProtocolCompatible { get; set; }
    public int ProtocolVersion { get; set; }
    public string? RemoteDeviceName { get; set; }
    public string? RemoteDeviceId { get; set; }
    public string? RemoteAppVersion { get; set; }
    public long LatencyMs { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public List<string> Suggestions { get; set; } = new();

    public bool Success => TcpConnected && ServiceResponded;
}

/// <summary>端口占用信息。</summary>
public sealed class PortUsageInfo
{
    public int Port { get; set; }
    public bool Udp { get; set; }
    public bool InUse { get; set; }
    public bool Available { get; set; }
    public string? Message { get; set; }
}

/// <summary>Windows 网络类别。</summary>
public enum NetworkCategory
{
    Unknown = 0,
    Private = 1,
    Public = 2,
    DomainAuthenticated = 3,
}
