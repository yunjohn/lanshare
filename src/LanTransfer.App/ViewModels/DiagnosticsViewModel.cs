using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>网络诊断页。展示本机网络信息、端口状态，并可测试到目标设备的连通性。</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly INetworkDiagnosticsService _diagnostics;
    private readonly IDeviceManager _devices;
    private readonly ISettingsService _settings;
    private readonly IDiscoveryService _discovery;
    private readonly ITransferServer _server;
    private readonly IIdentityService _identity;
    private readonly ILogger<DiagnosticsViewModel> _logger;

    public DiagnosticsViewModel(
        INetworkDiagnosticsService diagnostics,
        IDeviceManager devices,
        ISettingsService settings,
        IDiscoveryService discovery,
        ITransferServer server,
        IIdentityService identity,
        ILogger<DiagnosticsViewModel> logger)
    {
        _diagnostics = diagnostics;
        _devices = devices;
        _settings = settings;
        _discovery = discovery;
        _server = server;
        _identity = identity;
        _logger = logger;

        TargetHost = string.Empty;
        TargetPort = settings.Current.TransferPort.ToString();

        _devices.DevicesChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();

    public ObservableCollection<string> DiscoveredDevices { get; } = new();

    public ObservableCollection<string> TestLog { get; } = new();

    [ObservableProperty] private string _deviceName = string.Empty;

    [ObservableProperty] private string _deviceId = string.Empty;

    [ObservableProperty] private string _fingerprint = string.Empty;

    [ObservableProperty] private int _discoveryPort;

    [ObservableProperty] private int _transferPort;

    [ObservableProperty] private bool _serverRunning;

    [ObservableProperty] private bool _discoveryRunning;

    [ObservableProperty] private string _serverStateText = string.Empty;

    [ObservableProperty] private string _discoveryStateText = string.Empty;

    [ObservableProperty] private string _networkCategoryText = string.Empty;

    [ObservableProperty] private string _downloadPath = string.Empty;

    [ObservableProperty] private string _chunkSizeText = string.Empty;

    [ObservableProperty] private string _firewallHint = string.Empty;

    [ObservableProperty] private string _targetHost = string.Empty;

    [ObservableProperty] private string _targetPort = string.Empty;

    [ObservableProperty] private string _testResult = string.Empty;

    [ObservableProperty] private bool _testSucceeded;

    [ObservableProperty] private bool _isBusy;

    public void Refresh()
    {
        UiDispatcher.Send(() =>
        {
            DeviceName = _devices.LocalDevice.DeviceName;
            DeviceId = _identity.DeviceId;
            Fingerprint = _identity.CertificateFingerprint;
            DiscoveryPort = _settings.Current.DiscoveryPort;
            TransferPort = _settings.Current.TransferPort;
            ServerRunning = _server.IsRunning;
            DiscoveryRunning = _discovery.IsRunning;
            DownloadPath = _settings.Current.DownloadPath;
            ChunkSizeText = ((long)_settings.Current.ChunkSize).ToSizeString();

            ServerStateText = _server.IsRunning
                ? $"运行中（TCP {_server.Port}）"
                : $"未运行：{_server.LastError ?? "未知原因"}";

            DiscoveryStateText = _discovery.IsRunning
                ? $"运行中（UDP {_discovery.Port}）"
                : $"未运行：{_discovery.LastError ?? "未知原因"}";

            FirewallHint = "如果连接失败，请确认 Windows 防火墙允许 LAN Transfer 在专用网络通信。" +
                           "程序不会自动关闭防火墙。";

            Interfaces.Clear();
            foreach (var nic in _diagnostics.GetInterfaces()) Interfaces.Add(nic);

            DiscoveredDevices.Clear();
            foreach (var device in _devices.Devices)
                DiscoveredDevices.Add($"{device.DeviceName}  {device.IpAddress}:{device.Port}  " +
                                      $"{(device.IsOnline ? "在线" : "离线")}");

            _ = UpdateCategoryAsync();
        });
    }

    private async Task UpdateCategoryAsync()
    {
        try
        {
            var category = await _diagnostics.GetNetworkCategoryAsync().ConfigureAwait(true);

            NetworkCategoryText = category switch
            {
                NetworkCategory.Private => "专用网络（推荐）",
                NetworkCategory.Public => "公用网络（存在安全提醒，不会自动接收文件）",
                NetworkCategory.DomainAuthenticated => "域网络",
                _ => "未知（无法通过 NLM 判断）",
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取网络类别失败");
            NetworkCategoryText = "未知";
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetHost))
        {
            TestResult = "请输入目标 IP 或主机名。";
            TestSucceeded = false;
            return;
        }

        var port = int.TryParse(TargetPort, out var parsed) ? parsed : 39521;

        IsBusy = true;
        TestResult = "正在测试…";
        TestLog.Clear();

        try
        {
            var result = await _diagnostics.TestConnectionAsync(TargetHost.Trim(), port).ConfigureAwait(true);

            TestSucceeded = result.Success;

            var lines = new List<string?>
            {
                $"目标：{TargetHost}:{port}",
                result.ResolvedIp is not null ? $"解析地址：{result.ResolvedIp}" : null,
                $"DNS 解析：{(result.DnsResolved ? "成功" : "跳过/失败")}",
                $"TCP 连接：{(result.TcpConnected ? "成功" : "失败")}",
                $"服务响应：{(result.ServiceResponded ? "成功" : "失败")}",
                result.ProtocolVersion > 0 ? $"协议版本：{result.ProtocolVersion}" : null,
                result.LatencyMs > 0 ? $"延迟：{result.LatencyMs} ms" : null,
                result.RemoteDeviceName is not null
                    ? $"设备：{result.RemoteDeviceName}（{result.RemoteDeviceId}）"
                    : null,
                result.RemoteAppVersion is not null ? $"版本：{result.RemoteAppVersion}" : null,
                string.IsNullOrWhiteSpace(result.Message) ? null : $"结果：{result.Message}",
            };

            TestResult = string.Join("\n", lines.Where(l => l is not null));

            if (result.Suggestions.Count > 0)
            {
                TestLog.Add("可能原因：");
                foreach (var suggestion in result.Suggestions) TestLog.Add($"· {suggestion}");
            }
            else if (result.Success)
            {
                TestLog.Add("两台电脑之间的自定义 TCP 通信正常，可以开始传输文件。");
            }
        }
        catch (Exception ex)
        {
            TestSucceeded = false;
            TestResult = $"测试失败：{ex.Message}";
            _logger.LogError(ex, "连接测试失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var text = string.Join(Environment.NewLine, new[]
            {
                $"设备名称：{DeviceName}",
                $"DeviceId：{DeviceId}",
                $"证书指纹：{Fingerprint}",
                $"UDP 发现端口：{DiscoveryPort}",
                $"TCP 传输端口：{TransferPort}",
                $"Server 状态：{ServerStateText}",
                $"发现服务状态：{DiscoveryStateText}",
                $"网络类别：{NetworkCategoryText}",
                "网络接口：",
            }.Concat(Interfaces.Select(i =>
                $"  · {i.Name} / {i.InterfaceType} / {i.IPv4Address} / 掩码 {i.SubnetMask} / " +
                $"广播 {i.BroadcastAddress}{(i.IsVirtual ? " / 虚拟接口" : string.Empty)}"))
             .Concat(new[] { "已发现设备：" })
             .Concat(DiscoveredDevices.Select(d => $"  · {d}")));

            System.Windows.Clipboard.SetText(text);
            TestLog.Clear();
            TestLog.Add("诊断信息已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "复制诊断信息失败");
        }
    }
}
