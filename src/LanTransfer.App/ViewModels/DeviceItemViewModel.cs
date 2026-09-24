using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.ViewModels;

/// <summary>设备列表项。点击后可发送文件 / 文件夹、配对或解除信任。</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly IDeviceManager _devices;
    private readonly ITrustStore _trustStore;
    private readonly ITransferManager _transfers;
    private readonly IDialogService _dialogs;
    private readonly Func<DeviceItemViewModel, Task> _onSendRequested;

    public DeviceItemViewModel(DeviceInfo device, IDeviceManager devices, ITrustStore trustStore,
        ITransferManager transfers, IDialogService dialogs, Func<DeviceItemViewModel, Task> onSendRequested)
    {
        _devices = devices;
        _trustStore = trustStore;
        _transfers = transfers;
        _dialogs = dialogs;
        _onSendRequested = onSendRequested;

        Apply(device);
    }

    public DeviceInfo Device { get; private set; } = new();

    public string DeviceId => Device.DeviceId;

    [ObservableProperty] private string _deviceName = string.Empty;

    [ObservableProperty] private string _ipAddress = string.Empty;

    [ObservableProperty] private int _port;

    [ObservableProperty] private string _appVersion = string.Empty;

    [ObservableProperty] private OnlineState _onlineState = OnlineState.Offline;

    [ObservableProperty] private TrustState _trustState = TrustState.Unknown;

    [ObservableProperty] private bool _isManual;

    public bool IsOnline => OnlineState == OnlineState.Online;
    public bool IsTrusted => TrustState == TrustState.Trusted;
    public bool IsIdentityChanged => TrustState == TrustState.IdentityChanged;
    public string EndPoint => $"{IpAddress}:{Port}";

    public string TrustText => TrustState switch
    {
        TrustState.Trusted => "可信设备",
        TrustState.Pending => "待确认",
        TrustState.Revoked => "已解除信任",
        TrustState.IdentityChanged => "身份已变化",
        _ => "陌生设备",
    };

    public void Apply(DeviceInfo device)
    {
        UiDispatcher.Send(() =>
        {
            Device = device;
            DeviceName = device.DeviceName;
            IpAddress = device.IpAddress;
            Port = device.Port;
            AppVersion = device.AppVersion;
            OnlineState = device.OnlineState;
            TrustState = device.TrustState;
            IsManual = device.IsManual;

            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(IsTrusted));
            OnPropertyChanged(nameof(IsIdentityChanged));
            OnPropertyChanged(nameof(EndPoint));
            OnPropertyChanged(nameof(TrustText));
        });
    }

    [RelayCommand]
    private Task SendAsync() => _onSendRequested(this);

    [RelayCommand]
    private async Task RevokeTrustAsync()
    {
        if (!_dialogs.Confirm("解除信任", $"确定解除对 {DeviceName} 的信任？解除后需要重新配对。"))
            return;

        try
        {
            await _trustStore.RevokeAsync(DeviceId).ConfigureAwait(false);
            await _devices.UpdateTrustAsync(DeviceId, TrustState.Revoked, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("解除信任失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (!_dialogs.Confirm("移除设备", $"确定从列表中移除 {DeviceName}？"))
            return;

        try
        {
            await _devices.RemoveDeviceAsync(DeviceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("移除设备失败", ex.Message);
        }
    }
}
