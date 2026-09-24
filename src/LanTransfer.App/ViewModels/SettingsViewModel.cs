using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Logging;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>设置页。修改端口后可一键重启网络服务，不会因为端口被占用而崩溃。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IDiscoveryService _discovery;
    private readonly ITransferServer _server;
    private readonly INetworkDiagnosticsService _diagnostics;
    private readonly IAutoStartService _autoStartService;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settings,
        IDiscoveryService discovery,
        ITransferServer server,
        INetworkDiagnosticsService diagnostics,
        IAutoStartService autoStartService,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _discovery = discovery;
        _server = server;
        _diagnostics = diagnostics;
        _autoStartService = autoStartService;
        _dialogs = dialogs;
        _logger = logger;

        Load();
    }

    public IReadOnlyList<int> ChunkSizes { get; } = AppConstants.AllowedChunkSizes;

    public IReadOnlyList<int> SyncIntervals { get; } = AppConstants.AllowedSyncScanIntervals;

    public IReadOnlyList<int> TombstoneDays { get; } = AppConstants.AllowedTombstoneRetentionDays;

    public IReadOnlyList<string> LogLevels { get; } = LogSetup.AvailableLevels;

    public IReadOnlyList<string> Themes { get; } = new[] { "Light", "Dark", "System" };

    [ObservableProperty] private string _deviceName = string.Empty;

    [ObservableProperty] private int _discoveryPort;

    [ObservableProperty] private int _transferPort;

    [ObservableProperty] private string _downloadPath = string.Empty;

    [ObservableProperty] private int _chunkSize;

    [ObservableProperty] private bool _autoStart;

    /// <summary>关闭主窗口时的行为（Ask / Exit / MinimizeToTray）。</summary>
    [ObservableProperty] private CloseWindowAction _closeAction = CloseWindowAction.Ask;

    public IReadOnlyList<CloseWindowAction> CloseActions { get; } = new[]
    {
        CloseWindowAction.Ask, CloseWindowAction.MinimizeToTray, CloseWindowAction.Exit,
    };

    [ObservableProperty] private bool _autoAcceptTrustedDevice;

    [ObservableProperty] private bool _notifications;

    [ObservableProperty] private string _theme = "Light";

    [ObservableProperty] private string _logLevel = "Information";

    [ObservableProperty] private int _syncScanIntervalMinutes;

    [ObservableProperty] private int _tombstoneRetentionDays;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _portStatus = string.Empty;

    [ObservableProperty] private string _deviceId = string.Empty;

    [ObservableProperty] private string _certificateFingerprint = string.Empty;

    [ObservableProperty] private bool _isBusy;

    private void Load()
    {
        var settings = _settings.Current;

        DeviceName = settings.DeviceName;
        DiscoveryPort = settings.DiscoveryPort;
        TransferPort = settings.TransferPort;
        DownloadPath = settings.DownloadPath;
        ChunkSize = settings.ChunkSize;
        AutoStart = settings.AutoStart;
        CloseAction = settings.CloseAction;
        AutoAcceptTrustedDevice = settings.AutoAcceptTrustedDevice;
        Notifications = settings.Notifications;
        Theme = settings.Theme;
        LogLevel = settings.LogLevel;
        SyncScanIntervalMinutes = settings.SyncScanIntervalMinutes;
        TombstoneRetentionDays = settings.TombstoneRetentionDays;
        DeviceId = settings.DeviceId;
    }

    public void SetIdentityInfo(string fingerprint)
    {
        CertificateFingerprint = fingerprint;
        DeviceId = _settings.Current.DeviceId;
    }

    [RelayCommand]
    private void BrowseDownloadPath()
    {
        var folder = _dialogs.PickFolder(DownloadPath);
        if (!string.IsNullOrEmpty(folder)) DownloadPath = folder;
    }

    [RelayCommand]
    private void OpenDownloadPath()
    {
        try
        {
            Directory.CreateDirectory(DownloadPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("打开目录失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("打开日志目录失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CheckPortsAsync()
    {
        IsBusy = true;

        try
        {
            var udp = await _diagnostics.CheckPortAsync(DiscoveryPort, udp: true).ConfigureAwait(true);
            var tcp = await _diagnostics.CheckPortAsync(TransferPort, udp: false).ConfigureAwait(true);

            PortStatus = $"UDP {DiscoveryPort}：{(udp.InUse ? "已被占用" : "可用")}\n" +
                         $"TCP {TransferPort}：{(tcp.InUse ? "已被占用" : "可用")}";

            if (udp.InUse || tcp.InUse)
            {
                PortStatus += "\n\n提示：如果占用者就是 LAN Transfer 本身，属于正常现象。" +
                              "如需更换端口，请修改后点击「保存并重启网络服务」。";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (DiscoveryPort is < 1 or > 65535 || TransferPort is < 1 or > 65535)
        {
            _dialogs.ShowError("端口无效", "端口必须在 1 ~ 65535 之间。");
            return;
        }

        if (DiscoveryPort == TransferPort)
        {
            _dialogs.ShowError("端口冲突", "UDP 发现端口与 TCP 传输端口不能相同。");
            return;
        }

        IsBusy = true;
        StatusMessage = "正在保存配置…";

        try
        {
            var settings = _settings.Current.Clone();
            settings.DeviceName = DeviceName.Trim();
            settings.DiscoveryPort = DiscoveryPort;
            settings.TransferPort = TransferPort;
            settings.DownloadPath = DownloadPath;
            settings.ChunkSize = ChunkSize;
            settings.AutoStart = AutoStart;
            settings.CloseAction = CloseAction;
            settings.AutoAcceptTrustedDevice = AutoAcceptTrustedDevice;
            settings.Notifications = Notifications;
            settings.Theme = Theme;
            settings.LogLevel = LogLevel;
            settings.SyncScanIntervalMinutes = SyncScanIntervalMinutes;
            settings.TombstoneRetentionDays = TombstoneRetentionDays;

            await _settings.ApplyAsync(settings).ConfigureAwait(true);
            _autoStartService.SetEnabled(settings.AutoStart);
            await RestartNetworkAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败";
            _dialogs.ShowError("保存设置失败", ex.Message);
            _logger.LogError(ex, "保存设置失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartNetworkAsync()
    {
        IsBusy = true;
        StatusMessage = "正在重启网络服务…";

        try
        {
            await _discovery.StopAsync().ConfigureAwait(true);
            await _server.StopAsync().ConfigureAwait(true);

            await _server.StartAsync().ConfigureAwait(true);
            await _discovery.StartAsync().ConfigureAwait(true);

            var messages = new List<string>();

            messages.Add(_server.IsRunning
                ? $"传输服务已启动（TCP {_server.Port}）"
                : $"传输服务启动失败：{_server.LastError}");

            messages.Add(_discovery.IsRunning
                ? $"设备发现已启动（UDP {_discovery.Port}）"
                : $"设备发现启动失败：{_discovery.LastError}");

            StatusMessage = string.Join("；", messages);

            if (!_server.IsRunning || !_discovery.IsRunning)
            {
                _dialogs.ShowError("网络服务启动失败",
                    string.Join("\n\n", messages) +
                    "\n\n请修改端口后重试。程序不会尝试绕过任何网络策略。");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"重启失败：{ex.Message}";
            _logger.LogError(ex, "重启网络服务失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 为用户主动添加防火墙规则（只开放本程序使用的端口，绝不关闭防火墙）。
    /// 必须由用户点击触发，并会弹出 UAC 授权。
    /// </summary>
    [RelayCommand]
    private void AddFirewallRule()
    {
        var confirmed = _dialogs.Confirm("添加防火墙规则",
            $"即将为 LAN Transfer 开放以下端口：\n\n" +
            $"· TCP {TransferPort}（文件传输）\n" +
            $"· UDP {DiscoveryPort}（设备发现）\n\n" +
            "仅开放上述端口，不会关闭 Windows 防火墙。\n" +
            "需要管理员权限，系统会弹出授权窗口。\n\n是否继续？");

        if (!confirmed) return;

        try
        {
            var arguments =
                $"/c netsh advfirewall firewall add rule name=\"LAN Transfer (TCP {TransferPort})\" " +
                $"dir=in action=allow protocol=TCP localport={TransferPort} profile=private & " +
                $"netsh advfirewall firewall add rule name=\"LAN Transfer (UDP {DiscoveryPort})\" " +
                $"dir=in action=allow protocol=UDP localport={DiscoveryPort} profile=private";

            var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            process?.WaitForExit(15000);

            _dialogs.ShowInfo("防火墙规则", "防火墙规则命令已执行。\n\n" +
                                          "如果出现 UAC 提示被取消，规则不会生效。\n" +
                                          "可在「高级安全 Windows Defender 防火墙」中查看名为 " +
                                          "LAN Transfer 的入站规则。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "添加防火墙规则失败");
            _dialogs.ShowError("添加防火墙规则失败",
                $"{ex.Message}\n\n你也可以手动在「Windows Defender 防火墙 → 允许应用通过防火墙」中" +
                "勾选 LAN Transfer 并允许专用网络通信。");
        }
    }

    [RelayCommand]
    private void OpenFirewallSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("打开防火墙设置失败", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyDeviceId() => CopyToClipboard(DeviceId);

    [RelayCommand]
    private void CopyFingerprint() => CopyToClipboard(CertificateFingerprint);

    private void CopyToClipboard(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        try
        {
            System.Windows.Clipboard.SetText(value);
            StatusMessage = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "复制到剪贴板失败");
        }
    }
}
