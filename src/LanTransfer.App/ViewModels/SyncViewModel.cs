using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Engine;
using LanTransfer.Sync.Models;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>同步任务页。管理同步关系、手动同步与冲突裁决。</summary>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly SyncEngine _engine;
    private readonly IDeviceManager _devices;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SyncViewModel> _logger;

    public SyncViewModel(SyncEngine engine, IDeviceManager devices, IDialogService dialogs,
        ILogger<SyncViewModel> logger)
    {
        _engine = engine;
        _devices = devices;
        _dialogs = dialogs;
        _logger = logger;

        _engine.PairChanged += (_, _) => Refresh();
        _engine.SyncCompleted += OnSyncCompleted;

        Refresh();
    }

    public ObservableCollection<SyncPairItemViewModel> Pairs { get; } = new();

    public ObservableCollection<SyncConflictRecord> Conflicts { get; } = new();

    [ObservableProperty] private SyncPairItemViewModel? _selectedPair;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _isBusy;

    public bool HasPairs => Pairs.Count > 0;
    public bool HasConflicts => Conflicts.Count > 0;

    public void Refresh()
    {
        UiDispatcher.Send(() =>
        {
            var existing = Pairs.ToDictionary(p => p.PairId, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _engine.Pairs)
            {
                if (existing.TryGetValue(pair.SyncPairId, out var item)) item.Apply(pair);
                else Pairs.Add(new SyncPairItemViewModel(pair, _engine, this));
            }

            foreach (var stale in Pairs.Where(p => _engine.Pairs.All(x => x.SyncPairId != p.PairId)).ToList())
                Pairs.Remove(stale);

            OnPropertyChanged(nameof(HasPairs));

            if (SelectedPair is not null)
                SelectedPair = Pairs.FirstOrDefault(p => p.PairId == SelectedPair.PairId);
        });
    }

    private void OnSyncCompleted(object? sender, SyncRunResult result)
    {
        UiDispatcher.Send(() =>
        {
            if (!result.Success)
            {
                StatusMessage = $"同步失败：{result.Message}";
            }
            else
            {
                var summary = $"同步完成：上传 {result.Uploaded}，下载 {result.Downloaded}，" +
                              $"删除对端 {result.DeletedRemote}，删除本机 {result.DeletedLocal}，" +
                              $"冲突 {result.Conflicts}";

                // 有文件被其它程序占用时不能只报「完成」——否则用户看到「已同步」却找不到文件
                if (result.HasPendingRetry)
                {
                    summary += $"；{result.Skipped + result.Failed} 个文件被占用或暂时无法读取，" +
                               "关闭相关程序后会自动重试";
                }

                StatusMessage = summary;
            }

            IsBusy = false;
        });

        Refresh();
        _ = LoadConflictsAsync(SelectedPair?.PairId);
    }

    public async Task LoadConflictsAsync(string? syncPairId)
    {
        if (string.IsNullOrEmpty(syncPairId))
        {
            UiDispatcher.Send(() =>
            {
                Conflicts.Clear();
                OnPropertyChanged(nameof(HasConflicts));
            });
            return;
        }

        try
        {
            var conflicts = await _engine.GetConflictsAsync(syncPairId).ConfigureAwait(true);

            UiDispatcher.Send(() =>
            {
                Conflicts.Clear();
                foreach (var conflict in conflicts.Where(c => !c.Resolved))
                    Conflicts.Add(conflict);

                OnPropertyChanged(nameof(HasConflicts));
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载冲突列表失败");
        }
    }

    [RelayCommand]
    private async Task CreatePairAsync()
    {
        var device = _devices.Devices.FirstOrDefault(d => d.OnlineState == OnlineState.Online);

        if (device is null)
        {
            _dialogs.ShowError("创建同步", "没有在线的设备。请先确认对方电脑已运行 LAN Transfer。");
            return;
        }

        var defaultLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "LanTransferSync");

        var (confirmed, localPath, remotePath, name, mode) =
            _dialogs.CreateSyncPair(defaultLocal, device.DeviceName, device.DeviceName);

        if (!confirmed) return;

        IsBusy = true;
        StatusMessage = "正在请求对方确认同步关系…";

        try
        {
            var pair = await _engine.CreatePairAsync(localPath, device, remotePath, name, mode)
                .ConfigureAwait(true);

            StatusMessage = pair.Enabled
                ? $"同步关系「{pair.Name}」已建立"
                : $"同步关系创建失败：{pair.LastError}";

            Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = "创建同步关系失败";
            _dialogs.ShowError("创建同步失败", ex.Message);
            _logger.LogError(ex, "创建同步关系失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (SelectedPair is null) return;
        await RunSyncAsync(SelectedPair.PairId).ConfigureAwait(true);
    }

    public async Task RunSyncAsync(string pairId)
    {
        IsBusy = true;
        StatusMessage = "正在同步…";

        try
        {
            await _engine.SyncNowAsync(pairId, fullScan: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"同步失败：{ex.Message}";
            _logger.LogError(ex, "手动同步失败");
        }
        finally
        {
            IsBusy = false;
            Refresh();
            await LoadConflictsAsync(pairId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResolveConflictAsync(object? parameter)
    {
        if (parameter is not ConflictResolutionRequest request) return;

        try
        {
            await _engine.ResolveConflictAsync(request.SyncPairId, request.Conflict, request.Resolution)
                .ConfigureAwait(true);

            StatusMessage = $"冲突已裁决：{request.Conflict.RelativePath}";
            await LoadConflictsAsync(request.SyncPairId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("裁决冲突失败", ex.Message);
            _logger.LogError(ex, "裁决冲突失败");
        }
    }

    partial void OnSelectedPairChanged(SyncPairItemViewModel? value)
    {
        _ = LoadConflictsAsync(value?.PairId);
    }
}

/// <summary>冲突裁决请求（供命令参数使用）。</summary>
public sealed record ConflictResolutionRequest(string SyncPairId, SyncConflictRecord Conflict,
    ConflictResolution Resolution);

/// <summary>同步关系卡片。</summary>
public sealed partial class SyncPairItemViewModel : ObservableObject
{
    private readonly SyncEngine _engine;
    private readonly SyncViewModel _parent;

    public SyncPairItemViewModel(SyncPair pair, SyncEngine engine, SyncViewModel parent)
    {
        _engine = engine;
        _parent = parent;
        Apply(pair);
    }

    public string PairId { get; private set; } = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _localPath = string.Empty;

    [ObservableProperty] private string _remotePath = string.Empty;

    [ObservableProperty] private string _remoteDeviceName = string.Empty;

    [ObservableProperty] private SyncStatus _status = SyncStatus.Idle;

    [ObservableProperty] private SyncMode _mode = SyncMode.TwoWay;

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty] private bool _isInitiator;

    [ObservableProperty] private string _lastSyncText = "尚未同步";

    [ObservableProperty] private string? _lastError;

    public string StatusText => Converters.SyncStatusTextConverter.Describe(Status);

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public void Apply(SyncPair pair)
    {
        UiDispatcher.Send(() =>
        {
            PairId = pair.SyncPairId;
            Name = pair.Name;
            LocalPath = pair.LocalPath;
            RemotePath = pair.RemotePath;
            RemoteDeviceName = pair.RemoteDeviceName;
            Status = pair.Status;
            Mode = pair.Mode;
            Enabled = pair.Enabled;
            IsInitiator = pair.IsInitiator;
            LastError = pair.LastError;
            LastSyncText = pair.LastSyncAt.HasValue
                ? $"最后同步：{pair.LastSyncAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "尚未同步";

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasError));
        });
    }

    [RelayCommand]
    private Task SyncNowAsync() => _parent.RunSyncAsync(PairId);

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        await _engine.SetEnabledAsync(PairId, !Enabled).ConfigureAwait(false);
        _parent.Refresh();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await _engine.DeletePairAsync(PairId).ConfigureAwait(false);
        _parent.Refresh();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            if (Directory.Exists(LocalPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LocalPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDispatcher.Send(() => LastError = ex.Message);
        }
    }
}
