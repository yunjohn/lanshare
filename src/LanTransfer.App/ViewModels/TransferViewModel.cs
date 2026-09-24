using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>文件传输页（首页）。设备发现、拖放发送、传输任务卡片。</summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly IDeviceManager _devices;
    private readonly ITransferManager _transfers;
    private readonly IDiscoveryService _discovery;
    private readonly INetworkDiagnosticsService _diagnostics;
    private readonly ITrustStore _trustStore;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly ILogger<TransferViewModel> _logger;

    public TransferViewModel(
        IDeviceManager devices,
        ITransferManager transfers,
        IDiscoveryService discovery,
        INetworkDiagnosticsService diagnostics,
        ITrustStore trustStore,
        IDialogService dialogs,
        ISettingsService settings,
        ILogger<TransferViewModel> logger)
    {
        _devices = devices;
        _transfers = transfers;
        _discovery = discovery;
        _diagnostics = diagnostics;
        _trustStore = trustStore;
        _dialogs = dialogs;
        _settings = settings;
        _logger = logger;

        _devices.DevicesChanged += OnDevicesChanged;
        _transfers.TransferAdded += OnTransferAdded;
        _transfers.TransferUpdated += OnTransferUpdated;
        _transfers.ProgressChanged += OnProgressChanged;

        RefreshDevices();
        RefreshTransfers();

        _ = CheckEnvironmentAsync();
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();

    public IReadOnlyList<ConflictPolicy> ConflictPolicies { get; } = new[]
    {
        ConflictPolicy.Rename, ConflictPolicy.Overwrite, ConflictPolicy.Skip,
    };

    [ObservableProperty] private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty] private ConflictPolicy _conflictPolicy = ConflictPolicy.Rename;

    [ObservableProperty] private string _localDeviceName = string.Empty;

    [ObservableProperty] private string _localIpAddress = string.Empty;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string? _warningMessage;

    [ObservableProperty] private bool _isAddDevicePanelOpen;

    [ObservableProperty] private string _manualIp = string.Empty;

    [ObservableProperty] private string _manualPort = string.Empty;

    [ObservableProperty] private bool _isBusy;

    public bool HasDevices => Devices.Count > 0;
    public bool HasTransfers => Transfers.Count > 0;
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);
    public bool HasSelectedDevice => SelectedDevice is not null;

    private async Task CheckEnvironmentAsync()
    {
        try
        {
            var category = await _diagnostics.GetNetworkCategoryAsync().ConfigureAwait(false);

            var messages = new List<string>();

            if (category == NetworkCategory.Public)
            {
                messages.Add("当前网络被 Windows 识别为「公用网络」。公用网络下不会自动接收文件，" +
                             "建议在「设置 → 网络和 Internet」中把本网络改为「专用网络」。");
            }

            if (!_discovery.IsRunning && !string.IsNullOrEmpty(_discovery.LastError))
                messages.Add(_discovery.LastError!);

            if (!_diagnostics.GetInterfaces().Any())
                messages.Add("未检测到可用的 IPv4 网络接口，请检查网线 / Wi-Fi 连接。");

            UiDispatcher.Send(() => WarningMessage = messages.Count > 0 ? string.Join("\n\n", messages) : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "环境检查失败");
        }
    }

    public void RefreshDevices()
    {
        UiDispatcher.Send(() =>
        {
            var existing = Devices.ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
            var current = _devices.Devices;

            foreach (var device in current)
            {
                if (existing.TryGetValue(device.DeviceId, out var item)) item.Apply(device);
                else Devices.Add(new DeviceItemViewModel(device, _devices, _trustStore, _transfers, _dialogs,
                    SendToDeviceAsync));
            }

            foreach (var stale in Devices.Where(d => current.All(c => c.DeviceId != d.DeviceId)).ToList())
                Devices.Remove(stale);

            LocalDeviceName = _devices.LocalDevice.DeviceName;
            // 端口取配置：LocalDevice.Port 是启动快照，改端口重启服务后不会更新
            LocalIpAddress = $"{_devices.LocalDevice.IpAddress}:{_settings.Current.TransferPort}";

            OnPropertyChanged(nameof(HasDevices));
        });
    }

    public void RefreshTransfers()
    {
        UiDispatcher.Send(() =>
        {
            Transfers.Clear();
            foreach (var record in _transfers.Transfers)
                Transfers.Add(new TransferItemViewModel(record, _transfers));

            OnPropertyChanged(nameof(HasTransfers));
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => RefreshDevices();

    private void OnTransferAdded(object? sender, TransferRecord record) =>
        UiDispatcher.Send(() =>
        {
            if (Transfers.Any(t => t.TransferId == record.TransferId)) return;

            Transfers.Insert(0, new TransferItemViewModel(record, _transfers));
            OnPropertyChanged(nameof(HasTransfers));
        });

    private void OnTransferUpdated(object? sender, TransferRecord record)
    {
        var item = Transfers.FirstOrDefault(t => t.TransferId == record.TransferId);
        item?.Update(record);
    }

    private void OnProgressChanged(object? sender, TransferProgressSnapshot snapshot)
    {
        var item = Transfers.FirstOrDefault(t => t.TransferId == snapshot.TransferId);
        item?.UpdateProgress(snapshot);
    }

    // ---------------------------------------------------------------- 命令

    [RelayCommand]
    private void ToggleAddDevicePanel() => IsAddDevicePanelOpen = !IsAddDevicePanelOpen;

    [RelayCommand]
    private async Task AddManualDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualIp))
        {
            _dialogs.ShowError("添加设备", "请输入目标 IP 地址。");
            return;
        }

        var port = int.TryParse(ManualPort, out var parsed) ? parsed : 39521;

        IsBusy = true;
        StatusMessage = "正在连接目标设备…";

        try
        {
            var device = await _devices.AddManualDeviceAsync(ManualIp.Trim(), port).ConfigureAwait(true);

            RefreshDevices();
            SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);

            StatusMessage = $"已添加设备 {device.DeviceName}（{device.IpAddress}:{device.Port}）";
            ManualIp = string.Empty;
            IsAddDevicePanelOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手工添加设备失败");
            StatusMessage = "添加失败";
            _dialogs.ShowError("无法连接目标设备",
                $"连接 {ManualIp}:{port} 失败：{ex.Message}\n\n可能原因：\n" +
                "· 目标电脑上的 LAN Transfer 未启动\n" +
                "· Windows 防火墙阻止了通信\n" +
                "· 端口配置不同\n" +
                "· 网络禁止终端互访");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "正在广播发现请求…";

        try
        {
            await _discovery.BroadcastAsync().ConfigureAwait(true);
            await _devices.RefreshOnlineStatesAsync().ConfigureAwait(true);
            RefreshDevices();
            var onlineCount = Devices.Count(d => d.IsOnline);
            StatusMessage = onlineCount > 0 ? $"发现 {onlineCount} 台在线设备" : "暂未发现在线设备";
        }
        catch (Exception ex)
        {
            StatusMessage = "刷新失败";
            _logger.LogWarning(ex, "刷新设备失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendFilesAsync()
    {
        var target = SelectedDevice;
        if (target is null)
        {
            _dialogs.ShowError("发送文件", "请先在设备列表中选择目标设备。");
            return;
        }

        var paths = _dialogs.PickFiles();
        if (paths is null || paths.Count == 0) return;

        await StartSendAsync(target, paths).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SendFolderAsync()
    {
        var target = SelectedDevice;
        if (target is null)
        {
            _dialogs.ShowError("发送文件夹", "请先在设备列表中选择目标设备。");
            return;
        }

        var folder = _dialogs.PickFolder();
        if (string.IsNullOrEmpty(folder)) return;

        await StartSendAsync(target, new[] { folder }).ConfigureAwait(true);
    }

    /// <summary>拖放发送入口（由视图代码调用）。</summary>
    public async Task DropPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (list.Count == 0) return;

        var target = SelectedDevice;

        if (target is null)
        {
            if (Devices.Count == 0)
            {
                _dialogs.ShowError("发送文件", "尚未发现任何设备。请先启动对方电脑上的 LAN Transfer，或手工添加 IP。");
                return;
            }

            // 未选择设备时，默认使用第一个在线设备，并提示用户
            target = Devices.FirstOrDefault(d => d.IsOnline) ?? Devices[0];
            SelectedDevice = target;
            StatusMessage = $"未选择设备，已自动使用 {target.DeviceName}";
        }

        await StartSendAsync(target, list).ConfigureAwait(true);
    }

    private async Task SendToDeviceAsync(DeviceItemViewModel device)
    {
        SelectedDevice = device;
        await SendFilesAsync().ConfigureAwait(true);
    }

    private async Task StartSendAsync(DeviceItemViewModel target, IReadOnlyList<string> paths)
        => await StartSendAsync(target.Device, paths).ConfigureAwait(true);

    /// <summary>
    /// 发送入口（设备页「发送文件」也走这里，保证与文件传输页是同一条链路与同一套前置确认）。
    /// </summary>
    public async Task StartSendAsync(DeviceInfo target, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (target.OnlineState != OnlineState.Online)
        {
            if (!_dialogs.Confirm("设备离线",
                    $"{target.DeviceName} 当前显示为离线，仍要尝试发送吗？"))
                return;
        }

        if (target.TrustState is not (TrustState.Trusted or TrustState.Pending))
        {
            var proceed = _dialogs.Confirm("陌生设备",
                $"{target.DeviceName} 尚未与本机配对。\n\n" +
                "对方仍然会收到接收确认弹窗，由对方用户决定是否接收。\n\n是否继续发送？");

            if (!proceed) return;
        }

        IsBusy = true;
        StatusMessage = "正在创建传输任务…";

        try
        {
            var record = await _transfers.CreateSendTransferAsync(target, paths, ConflictPolicy)
                .ConfigureAwait(true);

            StatusMessage = $"已创建传输：{record.RootName}（{record.TotalSize.ToSizeString()}）";
        }
        catch (Exception ex)
        {
            StatusMessage = "创建传输失败";
            _dialogs.ShowError("发送失败", ex.Message);
            _logger.LogError(ex, "创建发送任务失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!_dialogs.Confirm("清空历史", "确定清空全部已结束的传输记录？（未完成的任务会保留）"))
            return;

        await _transfers.ClearHistoryAsync().ConfigureAwait(true);
        RefreshTransfers();
    }

    [RelayCommand]
    private void SelectDevice(DeviceItemViewModel? device) => SelectedDevice = device;

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
        => OnPropertyChanged(nameof(HasSelectedDevice));

    partial void OnWarningMessageChanged(string? value) => OnPropertyChanged(nameof(HasWarning));
}
