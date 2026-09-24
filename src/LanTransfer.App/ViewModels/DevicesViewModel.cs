using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>设备页：设备详情、配对、解除信任、发送入口。</summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly IDeviceManager _devices;
    private readonly ITrustStore _trustStore;
    private readonly IPairingService _pairing;
    private readonly ITransferClient _client;
    private readonly ITransferManager _transfers;
    private readonly IDialogService _dialogs;
    private readonly ILogger<DevicesViewModel> _logger;

    public DevicesViewModel(
        IDeviceManager devices,
        ITrustStore trustStore,
        IPairingService pairing,
        ITransferClient client,
        ITransferManager transfers,
        IDialogService dialogs,
        ILogger<DevicesViewModel> logger)
    {
        _devices = devices;
        _trustStore = trustStore;
        _pairing = pairing;
        _client = client;
        _transfers = transfers;
        _dialogs = dialogs;
        _logger = logger;

        _devices.DevicesChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    /// <summary>
    /// 由 MainViewModel 注入：设备页的「发送文件」复用文件传输页的同一条发送链路。
    /// 注入之前创建的列表项同样可用——回调在点击时才读取本属性。
    /// </summary>
    public Func<DeviceInfo, IReadOnlyList<string>, Task>? SendRequested { get; set; }

    [ObservableProperty] private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => SelectedDevice is not null;
    public bool CanPair => SelectedDevice is { IsTrusted: false };
    public bool CanRevoke => SelectedDevice is { IsTrusted: true };

    public void Refresh()
    {
        UiDispatcher.Send(() =>
        {
            var existing = Devices.ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);

            foreach (var device in _devices.Devices)
            {
                if (existing.TryGetValue(device.DeviceId, out var item)) item.Apply(device);
                else Devices.Add(new DeviceItemViewModel(device, _devices, _trustStore, _transfers, _dialogs,
                    SendFromDevicePageAsync));
            }

            foreach (var stale in Devices.Where(d => _devices.Devices.All(x => x.DeviceId != d.DeviceId)).ToList())
                Devices.Remove(stale);

            if (SelectedDevice is not null)
                SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice.DeviceId);

            // Refresh 通常把同一个实例重新赋给 SelectedDevice（引用相等 → 不触发
            // OnSelectedDeviceChanged），于是配对/解除信任后按钮可见性不会更新：
            // 配对成功后仍显示「配对」、不显示「解除信任」，需要切换选中项才恢复。
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanPair));
            OnPropertyChanged(nameof(CanRevoke));
        });
    }

    /// <summary>设备页「发送文件」：先选文件，再交给文件传输页的发送链路（同一套离线/陌生设备确认）。</summary>
    private async Task SendFromDevicePageAsync(DeviceItemViewModel item)
    {
        var send = SendRequested;
        if (send is null)
        {
            _dialogs.ShowInfo("发送文件", "请到「文件传输」页选择设备后发送。");
            return;
        }

        var paths = _dialogs.PickFiles($"发送到 {item.DeviceName}");
        if (paths is null || paths.Count == 0) return;

        await send(item.Device, paths).ConfigureAwait(true);
    }

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(CanRevoke));
    }

    [RelayCommand]
    private async Task PairAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;

        PairingSession? session = null;
        var pairingCompleted = false;

        IsBusy = true;
        StatusMessage = "正在获取对方身份信息…";

        try
        {
            var info = await _client.GetDeviceInfoAsync(device.IpAddress, device.Port).ConfigureAwait(true);

            session = _pairing.CreateSession(info.DeviceId, info.DeviceName, info.CertificateFingerprint);

            using var pairCts = new CancellationTokenSource();
            var pairTask = _client.PairAsync(device.IpAddress, device.Port, new PairRequest
            {
                DeviceId = _devices.LocalDevice.DeviceId,
                DeviceName = _devices.LocalDevice.DeviceName,
                CertificateFingerprint = _devices.LocalDevice.CertificateFingerprint ?? string.Empty,
                PairingSessionId = session.PairingSessionId,
                VerificationCode = session.VerificationCode,
                ProtocolVersion = LanTransfer.Common.Constants.AppConstants.ProtocolVersion,
            }, pairCts.Token);

            // 请求先发出，让两端都能看到同一验证码；网络等待在后台继续，不阻塞本机模态窗口。
            var localConfirmed = _dialogs.ConfirmPairing(new PairingRequestedEventArgs
            {
                PairingSessionId = session.PairingSessionId,
                RemoteDeviceId = info.DeviceId,
                RemoteDeviceName = info.DeviceName,
                RemoteFingerprint = info.CertificateFingerprint,
                VerificationCode = session.VerificationCode,
            });

            if (!localConfirmed)
            {
                pairCts.Cancel();
                try { await pairTask.ConfigureAwait(true); }
                catch (OperationCanceledException) { /* 本机用户主动取消 */ }
                StatusMessage = "已取消配对";
                return;
            }

            StatusMessage = "等待对方确认验证码…";
            var response = await pairTask.ConfigureAwait(true);

            if (!response.Success || !response.Accepted)
            {
                StatusMessage = $"配对失败：{response.Message}";
                _dialogs.ShowError("配对失败", response.Message ?? "对方拒绝或未确认配对。");
                return;
            }

            await _trustStore.TrustAsync(info.DeviceId, info.DeviceName, info.CertificateFingerprint)
                .ConfigureAwait(true);

            await _devices.UpdateTrustAsync(info.DeviceId, TrustState.Trusted, info.CertificateFingerprint)
                .ConfigureAwait(true);

            await _pairing.ConfirmAsync(session).ConfigureAwait(true);
            pairingCompleted = true;

            StatusMessage = $"已与 {info.DeviceName} 完成配对";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"配对失败：{ex.Message}";
            _dialogs.ShowError("配对失败", ex.Message);
            _logger.LogError(ex, "配对失败");
        }
        finally
        {
            if (session is not null && !pairingCompleted)
                _pairing.Cancel(session.PairingSessionId);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        IsBusy = true;
        StatusMessage = "正在刷新设备在线状态…";

        try
        {
            await _devices.RefreshOnlineStatesAsync().ConfigureAwait(true);
            Refresh();
            StatusMessage = "设备状态已刷新";
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败：{ex.Message}";
            _logger.LogWarning(ex, "刷新设备在线状态失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;

        if (!_dialogs.Confirm("解除信任", $"确定解除对 {device.DeviceName} 的信任？\n\n" +
                                         "解除后对方再次发送文件时需要重新配对。"))
            return;

        try
        {
            await _trustStore.RevokeAsync(device.DeviceId).ConfigureAwait(true);
            await _devices.UpdateTrustAsync(device.DeviceId, TrustState.Revoked, null).ConfigureAwait(true);

            StatusMessage = $"已解除对 {device.DeviceName} 的信任";
            Refresh();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("解除信任失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;

        IsBusy = true;

        try
        {
            var info = await _client.GetDeviceInfoAsync(device.IpAddress, device.Port).ConfigureAwait(true);

            StatusMessage = $"连接正常：{info.DeviceName}（协议 {info.ProtocolVersion}，版本 {info.AppVersion}）";

            if (info.ProtocolVersion != LanTransfer.Common.Constants.AppConstants.ProtocolVersion)
            {
                _dialogs.ShowError("版本不兼容",
                    $"目标设备 LAN Transfer 版本不兼容。\n\n" +
                    $"对端协议：{info.ProtocolVersion}\n本机协议：{LanTransfer.Common.Constants.AppConstants.ProtocolVersion}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败：{ex.Message}";
            _dialogs.ShowError("连接失败",
                $"无法连接 {device.IpAddress}:{device.Port}\n\n{ex.Message}\n\n" +
                "请检查：对方程序是否运行、防火墙是否放行、端口是否一致。");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
