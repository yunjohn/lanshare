using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Diagnostics;

/// <summary>
/// 网络诊断服务：接口枚举、端口占用检测、目标连通性测试（DNS → TCP → HTTPS API → 协议版本）。
/// 只做诊断与提示，绝不尝试绕过任何网络安全策略。
/// </summary>
public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    private readonly ITransferClient _client;
    private readonly ISettingsService _settings;
    private readonly IIdentityService _identity;
    private readonly NetworkCategoryDetector _categoryDetector;
    private readonly ILogger<NetworkDiagnosticsService> _logger;

    public NetworkDiagnosticsService(
        ITransferClient client,
        ISettingsService settings,
        IIdentityService identity,
        NetworkCategoryDetector categoryDetector,
        ILogger<NetworkDiagnosticsService> logger)
    {
        _client = client;
        _settings = settings;
        _identity = identity;
        _categoryDetector = categoryDetector;
        _logger = logger;
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => NetworkHelper.GetInterfaces();

    public IReadOnlyList<string> GetLocalIPv4Addresses() => NetworkHelper.GetLocalIPv4Addresses();

    public Task<NetworkCategory> GetNetworkCategoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_categoryDetector.Detect());

    public async Task<ConnectionTestResult> TestConnectionAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        var result = new ConnectionTestResult
        {
            Host = host,
            Port = port,
        };

        if (string.IsNullOrWhiteSpace(host))
        {
            result.Message = "请输入目标 IP 或主机名。";
            result.Suggestions.Add("例如 192.168.1.102");
            return result;
        }

        // 1. DNS（仅当输入的是主机名）
        if (!IPAddress.TryParse(host, out var parsedAddress))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4 is null)
                {
                    result.Message = "DNS 解析未返回 IPv4 地址（V1 仅支持 IPv4）。";
                    result.Suggestions.Add("请直接输入 IPv4 地址。");
                    return result;
                }

                result.ResolvedIp = ipv4.ToString();
                parsedAddress = ipv4;
                result.DnsResolved = true;
            }
            catch (Exception ex)
            {
                result.ErrorCode = ErrorCodes.NetworkUnreachable;
                result.Message = $"DNS 解析失败：{ex.Message}";
                result.Suggestions.Add("请检查主机名是否正确，或直接输入 IP 地址。");
                return result;
            }
        }
        else
        {
            result.ResolvedIp = host;
            result.DnsResolved = true;
        }

        // 2. TCP Connect
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient(parsedAddress.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await tcp.ConnectAsync(parsedAddress, port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            result.TcpConnected = true;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Message = $"TCP 连接 {host}:{port} 超时（5 秒）。";
            AddTcpFailureSuggestions(result);
            return result;
        }
        catch (SocketException ex)
        {
            result.Message = $"TCP 连接失败：{ex.SocketErrorCode}（{ex.Message}）";
            AddTcpFailureSuggestions(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"TCP 连接失败：{ex.Message}";
            AddTcpFailureSuggestions(result);
            return result;
        }

        // 3. HTTPS API（健康检查）
        try
        {
            var health = await _client.PingAsync(result.ResolvedIp, port, cancellationToken).ConfigureAwait(false);

            if (health.Success)
            {
                result.ServiceResponded = true;
                result.ProtocolVersion = health.ProtocolVersion;
                result.ProtocolCompatible = health.ProtocolVersion == AppConstants.ProtocolVersion;

                if (!result.ProtocolCompatible)
                {
                    result.Message =
                        $"目标设备 LAN Transfer 版本不兼容（对端协议 {health.ProtocolVersion}，本机 {AppConstants.ProtocolVersion}）。";
                    return result;
                }
            }
            else
            {
                result.Message = "TCP 已连通，但服务未返回有效的健康检查响应。";
                result.Suggestions.Add("目标端口上的程序可能不是 LAN Transfer。");
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Message = $"TCP 已连通，但 HTTPS API 请求失败：{ex.Message}";
            result.Suggestions.Add("可能原因：目标为普通 TCP 服务而非 LAN Transfer；或 TLS 证书被拒绝。");
            return result;
        }

        // 4. 设备信息
        try
        {
            var device = await _client.GetDeviceInfoAsync(result.ResolvedIp, port, cancellationToken)
                .ConfigureAwait(false);

            result.RemoteDeviceId = device.DeviceId;
            result.RemoteDeviceName = device.DeviceName;
            result.RemoteAppVersion = device.AppVersion;

            if (string.Equals(device.DeviceId, _identity.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "该地址指向本机自身。";
                result.Suggestions.Add("请输入另一台电脑的 IP 地址。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取目标设备信息失败");
            result.Suggestions.Add("已连通服务，但未能读取设备信息。");
        }

        if (string.IsNullOrEmpty(result.Message))
            result.Message = "连接成功";

        return result;
    }

    private static void AddTcpFailureSuggestions(ConnectionTestResult result)
    {
        result.ErrorCode = ErrorCodes.NetworkUnreachable;
        result.Suggestions.Add("目标电脑上的 LAN Transfer 未启动。");
        result.Suggestions.Add("Windows 防火墙阻止了 LAN Transfer 在专用网络通信。");
        result.Suggestions.Add("两台电脑的 TCP 传输端口配置不同。");
        result.Suggestions.Add("当前网络禁止终端之间互访（如访客网络 / 客户端隔离）。");
        result.Suggestions.Add("可以先用 ping 验证基本可达性；若 ping 不通，请检查网络本身。");
    }

    public Task<PortUsageInfo> CheckPortAsync(int port, bool udp, CancellationToken cancellationToken = default)
    {
        var info = new PortUsageInfo { Port = port, Udp = udp };

        if (port is < 1 or > 65535)
        {
            info.Message = "端口号必须在 1 ~ 65535 之间。";
            return Task.FromResult(info);
        }

        // 用「能否绑定」判断占用：能绑定即未被占用
        try
        {
            if (udp)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                info.Available = true;
                info.Message = $"UDP 端口 {port} 可用。";
            }
            else
            {
                using var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                info.Available = true;
                info.Message = $"TCP 端口 {port} 可用。";
            }
        }
        catch (SocketException ex)
        {
            info.InUse = true;
            info.Available = false;
            info.Message = $"{(udp ? "UDP" : "TCP")} 端口 {port} 已被占用（{ex.SocketErrorCode}）。" +
                           "请在设置中改用其它端口。";
        }
        catch (Exception ex)
        {
            info.Available = false;
            info.Message = $"检测端口 {port} 失败：{ex.Message}";
        }

        return Task.FromResult(info);
    }

    /// <summary>收集诊断页面所需的本机摘要信息。</summary>
    public NetworkSummary BuildSummary(bool serverRunning, bool discoveryRunning, int discoveryPort,
        int transferPort, IReadOnlyList<string> discoveredDevices)
    {
        var interfaces = GetInterfaces();

        return new NetworkSummary
        {
            DeviceName = _identity.DeviceName,
            DeviceId = _identity.DeviceId,
            CertificateFingerprint = _identity.CertificateFingerprint,
            Interfaces = interfaces,
            DiscoveryPort = discoveryPort,
            TransferPort = transferPort,
            ServerRunning = serverRunning,
            DiscoveryRunning = discoveryRunning,
            DiscoveredDevices = discoveredDevices,
            NetworkCategory = _categoryDetector.Detect(),
            FirewallHint = "如果无法连接，请确认 Windows 防火墙允许 LAN Transfer 在专用网络通信。" +
                           "程序不会自动关闭防火墙。",
            DownloadPath = _settings.Current.DownloadPath,
            ChunkSize = _settings.Current.ChunkSize,
        };
    }
}

/// <summary>网络诊断页面的数据摘要。</summary>
public sealed class NetworkSummary
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; set; } = Array.Empty<NetworkInterfaceInfo>();
    public int DiscoveryPort { get; set; }
    public int TransferPort { get; set; }
    public bool ServerRunning { get; set; }
    public bool DiscoveryRunning { get; set; }
    public IReadOnlyList<string> DiscoveredDevices { get; set; } = Array.Empty<string>();
    public NetworkCategory NetworkCategory { get; set; }
    public string FirewallHint { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public int ChunkSize { get; set; }

    public bool HasUsableInterface => Interfaces.Count > 0;
}
