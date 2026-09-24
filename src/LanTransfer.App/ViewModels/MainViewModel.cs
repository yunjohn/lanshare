using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Engine;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>主窗口 ViewModel：导航、状态栏、以及接收确认 / 配对 / 同步请求的弹窗调度。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITransferServer _server;
    private readonly SyncEngine _syncEngine;
    private readonly IDeviceManager _devices;
    private readonly IIdentityService _identity;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>
    /// 确认弹窗串行化队列：接收确认 / 配对确认 / 同步请求共用一条队列。
    /// 见 <see cref="PromptQueue"/> 的注释——模态框的嵌套消息循环会让后到的弹窗直接叠在前面那个之上。
    /// </summary>
    private readonly PromptQueue _prompts;

    public MainViewModel(
        TransferViewModel transfer,
        SyncViewModel sync,
        DevicesViewModel devicesPage,
        HistoryViewModel history,
        SettingsViewModel settingsPage,
        DiagnosticsViewModel diagnostics,
        ITransferServer server,
        SyncEngine syncEngine,
        IDeviceManager devices,
        IIdentityService identity,
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        Transfer = transfer;
        Sync = sync;
        DevicesPage = devicesPage;
        History = history;
        SettingsPage = settingsPage;
        Diagnostics = diagnostics;

        _server = server;
        _syncEngine = syncEngine;
        _devices = devices;
        _identity = identity;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _prompts = new PromptQueue(onError: ex => _logger.LogError(ex, "确认弹窗执行失败"));

        _server.IncomingTransferRequested += OnIncomingTransferRequested;
        _server.PairingRequested += OnPairingRequested;
        _syncEngine.SyncRequested += OnSyncRequested;

        // 设备页的「发送文件」直接复用文件传输页的发送链路
        // （此前它传的是一个空实现的回调，按钮点了没有任何反应）。
        devicesPage.SendRequested = transfer.StartSendAsync;

        _devices.DeviceOnline += OnDeviceOnline;
        _devices.DeviceOffline += OnDeviceOffline;
        _devices.DevicesChanged += (_, _) => RefreshStatusBar();

        RefreshStatusBar();
    }

    public TransferViewModel Transfer { get; }
    public SyncViewModel Sync { get; }
    public DevicesViewModel DevicesPage { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel SettingsPage { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public IReadOnlyList<string> NavigationItems { get; } = new[]
    {
        "文件传输", "同步任务", "设备", "历史记录", "网络诊断", "设置",
    };

    [ObservableProperty] private int _selectedNavigationIndex;

    [ObservableProperty] private string _localDeviceText = string.Empty;

    [ObservableProperty] private string _serverStatusText = string.Empty;

    [ObservableProperty] private bool _serverRunning;

    [ObservableProperty] private string _deviceCountText = string.Empty;

    [ObservableProperty] private string _toastMessage = string.Empty;

    [ObservableProperty] private bool _hasToast;

    public void RefreshStatusBar()
    {
        UiDispatcher.Send(() =>
        {
            var local = _devices.LocalDevice;

            // 端口以配置为准：LocalDevice.Port 是启动时的快照，
            // 用户在设置页改端口并重启服务后它不会更新，界面会一直显示旧端口
            // （而诊断页读的是配置，两处自相矛盾）。
            LocalDeviceText = $"{local.DeviceName}    {local.IpAddress}:{_settings.Current.TransferPort}";
            ServerRunning = _server.IsRunning;

            ServerStatusText = _server.IsRunning
                ? $"服务运行中 · TCP {_server.Port} · 设备 {_devices.Devices.Count(d => d.IsOnline)} 在线"
                : $"服务未运行 · {_server.LastError ?? "请检查端口设置"}";

            DeviceCountText = $"{_devices.Devices.Count(d => d.IsOnline)} / {_devices.Devices.Count}";
        });
    }

    private void OnDeviceOnline(object? sender, DeviceInfo device)
        => ShowToast($"设备上线：{device.DeviceName}（{device.IpAddress}）");

    private void OnDeviceOffline(object? sender, DeviceInfo device)
        => ShowToast($"设备离线：{device.DeviceName}");

    private void ShowToast(string message)
    {
        // 设置里关掉通知后就不该再弹提示条（该设置此前没有任何消费方，形同虚设）
        if (!_settings.Current.Notifications) return;

        UiDispatcher.Send(() =>
        {
            ToastMessage = message;
            HasToast = true;
        });
    }

    [RelayCommand]
    private void DismissToast() => HasToast = false;

    [RelayCommand]
    private void Navigate(string? index)
    {
        if (int.TryParse(index, out var value)) SelectedNavigationIndex = value;
    }

    // ---------------------------------------------------------------- 弹窗调度

    private void OnIncomingTransferRequested(object? sender, IncomingTransferEventArgs request)
    {
        _logger.LogInformation("收到来自 {Device} 的传输请求 {TransferId}", request.RemoteDeviceName,
            request.TransferId);

        // 用 Post 而不是 Send：事件是在 Kestrel 的请求线程上抛出的，
        // Send（Dispatcher.Invoke）会把响应写回阻塞到用户点完弹窗，
        // 而发送端创建传输的默认超时只有 20 秒 —— 用户没及时点，发送端就被误报成「已取消」。
        // Post 让请求立刻返回 WaitingApproval，发送端随后按自己的 10 分钟窗口轮询审批结果。
        // 经 PromptQueue 串行化后再 Post：多个请求同时到达时只弹一个框，不会层层叠起来。
        var queued = _prompts.Enqueue(() =>
        {
            var accepted = false;
            var remember = false;

            try
            {
                accepted = _dialogs.ConfirmIncomingTransfer(request);

                remember = accepted && !request.IsTrusted &&
                           _dialogs.Confirm("信任该设备",
                               $"是否将 {request.RemoteDeviceName} 标记为可信设备？\n\n" +
                               "标记后可在设置中启用「自动接收可信设备文件」。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "显示接收确认弹窗失败，按拒绝处理");
                accepted = false;
                remember = false;
            }
            finally
            {
                // 必须无条件应答：异常路径上漏掉应答会让发送端一直等到审批超时。
                _server.RespondToApproval(request.TransferId, accepted, remember);
            }
        });

        if (!queued)
        {
            _logger.LogWarning("确认弹窗队列已满，自动拒绝传输请求 {TransferId}（来自 {Device}）",
                request.TransferId, request.RemoteDeviceName);
            _server.RespondToApproval(request.TransferId, approve: false, rememberDevice: false);
        }
    }

    private void OnPairingRequested(object? sender, PairingRequestedEventArgs request)
    {
        var queued = _prompts.Enqueue(() =>
        {
            var accepted = false;

            try
            {
                accepted = _dialogs.ConfirmPairing(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "显示配对确认弹窗失败，按拒绝处理");
                accepted = false;
            }
            finally
            {
                _server.RespondToPairing(request.PairingSessionId, accepted);
            }
        });

        if (!queued)
        {
            _logger.LogWarning("确认弹窗队列已满，自动拒绝配对请求 {SessionId}（来自 {Device}）",
                request.PairingSessionId, request.RemoteDeviceName);
            _server.RespondToPairing(request.PairingSessionId, accept: false);
        }
    }

    private void OnSyncRequested(object? sender, SyncRequestEventArgs request)
    {
        var queued = _prompts.Enqueue(() =>
        {
            var accepted = false;
            string? localPath = null;

            try
            {
                (accepted, localPath) = _dialogs.ConfirmSyncRequest(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "显示同步请求弹窗失败，按拒绝处理");
                accepted = false;
                localPath = null;
            }
            finally
            {
                _syncEngine.RespondToSyncRequest(request.SyncPairId, accepted, localPath);
            }
        });

        if (!queued)
        {
            _logger.LogWarning("确认弹窗队列已满，自动拒绝同步请求 {PairId}（来自 {Device}）",
                request.SyncPairId, request.RemoteDeviceName);
            _syncEngine.RespondToSyncRequest(request.SyncPairId, accept: false, resolvedLocalPath: null);
        }
    }

    public void Initialize()
    {
        SettingsPage.SetIdentityInfo(_identity.CertificateFingerprint);
        RefreshStatusBar();

        _logger.LogInformation("主界面已就绪：本机 {Name}（{DeviceId}）",
            _settings.Current.DeviceName, _settings.Current.DeviceId);
    }
}
