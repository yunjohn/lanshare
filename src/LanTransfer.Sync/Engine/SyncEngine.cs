using System.Collections.Concurrent;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Conflict;
using LanTransfer.Sync.Metadata;
using LanTransfer.Sync.Models;
using LanTransfer.Sync.Planner;
using LanTransfer.Sync.Scanner;
using LanTransfer.Sync.Watcher;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Sync.Engine;

/// <summary>
/// 双向同步引擎。
/// 设计要点（对应任务书第 78 / 86 / 95 / 99 节）：
/// 1. 绝不做「A 扫一次复制给 B，B 扫一次复制给 A」——所有动作必须经过 SyncPlanner；
/// 2. 判断必须同时比较 Baseline / Local / Remote，不能只比较两端；
/// 3. 删除使用墓碑（Tombstone），避免「删除的文件在另一端复活」；
/// 4. V1 由同步关系的发起方单侧驱动，避免双端同时规划导致重复传输与竞态；
/// 5. 冲突默认保留双方文件，禁止默认覆盖任意一边。
/// </summary>
public sealed class SyncEngine : ISyncServerHandler, ISyncPathProvider, IAsyncDisposable
{
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(750);

    /// <summary>被占用条目的首次重试延时（之后按 15s/30s/1m/2m/4m 退避）。</summary>
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(15);

    /// <summary>重试退避上限。</summary>
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>连续重试次数上限，超过后交给周期全量扫描兜底。</summary>
    private const int RetryMaxAttempts = 5;

    private const int RunRequested = 1;
    private const int FullScanRequested = 2;

    private readonly ISyncRepository _repository;
    private readonly ITransferClient _client;
    private readonly IDeviceManager _devices;
    private readonly ISettingsService _settings;
    private readonly ISafePathResolver _paths;
    private readonly IHashService _hash;
    private readonly DirectoryScanner _scanner;
    private readonly SyncPlanner _planner;
    private readonly SyncMetadataManager _metadata;
    private readonly ConflictResolver _conflicts;
    private readonly ILogger<SyncEngine> _logger;

    private readonly ConcurrentDictionary<string, SyncPair> _pairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileChangeWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个同步关系「因文件被占用而重试」的连续次数。</summary>
    private readonly ConcurrentDictionary<string, int> _retryAttempts = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, int> _pendingRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _runLoops = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SyncDecision>> _pendingRequests =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Timer? _scanTimer;
    private bool _initialized;

    public SyncEngine(
        ISyncRepository repository,
        ITransferClient client,
        IDeviceManager devices,
        ISettingsService settings,
        ISafePathResolver paths,
        IHashService hash,
        DirectoryScanner scanner,
        SyncPlanner planner,
        SyncMetadataManager metadata,
        ConflictResolver conflicts,
        ILogger<SyncEngine> logger)
    {
        _repository = repository;
        _client = client;
        _devices = devices;
        _settings = settings;
        _paths = paths;
        _hash = hash;
        _scanner = scanner;
        _planner = planner;
        _metadata = metadata;
        _conflicts = conflicts;
        _logger = logger;
    }

    public event EventHandler<SyncRequestEventArgs>? SyncRequested;
    public event EventHandler<SyncRunResult>? SyncCompleted;
    public event EventHandler<SyncPair>? PairChanged;

    public IReadOnlyList<SyncPair> Pairs => _pairs.Values.OrderBy(p => p.CreatedAt).ToList();

    // ---------------------------------------------------------------- 生命周期

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _metadata.InitializeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var pair in await _repository.GetPairsAsync(cancellationToken).ConfigureAwait(false))
        {
            _pairs[pair.SyncPairId] = pair;
            if (pair.Enabled) StartWatcher(pair);
        }

        // 启动时执行一次全量扫描，捕获程序关闭期间发生的变化
        foreach (var pair in _pairs.Values.Where(p => p.Enabled && p.IsInitiator))
        {
            ScheduleSync(pair.SyncPairId, fullScan: true);
        }

        _devices.DeviceOnline += OnDeviceOnline;

        _scanTimer = new Timer(_ => PeriodicScan(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        _initialized = true;
        _logger.LogInformation("同步引擎初始化完成，已加载 {Count} 个同步关系", _pairs.Count);
    }

    private void PeriodicScan()
    {
        try
        {
            var interval = TimeSpan.FromMinutes(_settings.Current.SyncScanIntervalMinutes);
            var now = DateTimeOffset.UtcNow;

            foreach (var pair in _pairs.Values.Where(p => p.Enabled && p.IsInitiator))
            {
                if (_lastRun.TryGetValue(pair.SyncPairId, out var last) && now - last < interval) continue;
                ScheduleSync(pair.SyncPairId, fullScan: true);
            }

            // 清理过期墓碑（仅清理双方均已确认删除的）
            var retention = DateTimeOffset.UtcNow.AddDays(-_settings.Current.TombstoneRetentionDays);
            _ = _metadata.PurgeTombstonesAsync(retention);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "周期性同步扫描出错");
        }
    }

    private void StartWatcher(SyncPair pair)
    {
        if (_watchers.ContainsKey(pair.SyncPairId)) return;

        var watcher = new FileChangeWatcher(pair.LocalPath, WatcherDebounce, _logger);
        watcher.Changed += (_, _) => OnWatcherTriggered(pair.SyncPairId);
        watcher.Start();

        _watchers[pair.SyncPairId] = watcher;
    }

    private void StopWatcher(string syncPairId)
    {
        if (_watchers.TryRemove(syncPairId, out var watcher)) watcher.Dispose();
    }

    private void OnWatcherTriggered(string syncPairId)
    {
        if (!_pairs.TryGetValue(syncPairId, out var pair) || !pair.Enabled) return;

        if (pair.IsInitiator)
        {
            ScheduleSync(syncPairId, fullScan: false);
            return;
        }

        // V1 仍由发起端单侧规划，非发起端只发送轻量通知，避免两端同时规划造成竞态。
        _ = Task.Run(() => NotifyInitiatorAsync(pair), CancellationToken.None);
    }

    private void ScheduleSync(string syncPairId, bool fullScan)
    {
        var flags = RunRequested | (fullScan ? FullScanRequested : 0);
        _pendingRuns.AddOrUpdate(syncPairId, flags, (_, current) => current | flags);
        StartRunLoopIfNeeded(syncPairId);
    }

    private void StartRunLoopIfNeeded(string syncPairId)
    {
        if (!_runLoops.TryAdd(syncPairId, 0)) return;
        _ = Task.Run(() => DrainScheduledRunsAsync(syncPairId), CancellationToken.None);
    }

    private async Task DrainScheduledRunsAsync(string syncPairId)
    {
        try
        {
            while (_pendingRuns.TryRemove(syncPairId, out var flags))
                await SafeSyncAsync(syncPairId, (flags & FullScanRequested) != 0).ConfigureAwait(false);
        }
        finally
        {
            _runLoops.TryRemove(syncPairId, out _);

            // 处理“队列刚判断为空、同时又收到文件事件”的窄竞态。
            if (_pendingRuns.ContainsKey(syncPairId)) StartRunLoopIfNeeded(syncPairId);
        }
    }

    private async Task NotifyInitiatorAsync(SyncPair pair)
    {
        var device = _devices.Find(pair.RemoteDeviceId);
        if (device is null)
        {
            _logger.LogDebug("无法发送实时同步通知，对端设备不在列表中: {PairId}", pair.SyncPairId);
            return;
        }

        try
        {
            var response = await _client.NotifySyncChangedAsync(device.IpAddress, device.Port, pair.SyncPairId)
                .ConfigureAwait(false);
            if (!response.Success)
                _logger.LogDebug("实时同步通知未被接受: {PairId} {Message}", pair.SyncPairId, response.Message);
        }
        catch (Exception ex)
        {
            // 周期全量扫描仍会兜底，通知失败不把同步关系永久置为 Error。
            _logger.LogDebug(ex, "发送实时同步通知失败: {PairId}", pair.SyncPairId);
        }
    }

    private void OnDeviceOnline(object? sender, DeviceInfo device)
    {
        foreach (var pair in _pairs.Values.Where(p => p.Enabled &&
                                                       string.Equals(p.RemoteDeviceId, device.DeviceId,
                                                           StringComparison.OrdinalIgnoreCase)))
        {
            if (pair.IsInitiator) ScheduleSync(pair.SyncPairId, fullScan: true);
            else _ = Task.Run(() => NotifyInitiatorAsync(pair), CancellationToken.None);
        }
    }

    private async Task SafeSyncAsync(string syncPairId, bool fullScan)
    {
        try
        {
            await SyncNowAsync(syncPairId, fullScan).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步执行失败: {SyncPairId}", syncPairId);
        }
    }

    // ---------------------------------------------------------------- 占用重试

    /// <summary>
    /// 本轮存在「被占用/读不到」或执行失败的条目时安排一次短延时重试。
    ///
    /// 为什么需要它：用户保存文件后往往仍然开着编辑器（Word/Excel 持有写句柄），
    /// 这一轮读不到该文件；只靠默认 10 分钟的周期扫描兜底，体感就是「同步没反应」。
    /// 这里按 15s / 30s / 1m / 2m / 4m（上限 5 分钟、最多 5 次）退避重试，
    /// 重试用全量扫描，顺带规避「同秒内保存且文件大小不变」导致的两级检测漏判。
    /// </summary>
    private void ScheduleRetry(string syncPairId)
    {
        var attempt = _retryAttempts.AddOrUpdate(syncPairId, 1, (_, current) => current + 1);

        if (attempt > RetryMaxAttempts)
        {
            _logger.LogInformation("同步重试已达 {Max} 次，改由周期扫描兜底: {PairId}",
                RetryMaxAttempts, syncPairId);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(RetryBaseDelay.Ticks * (1L << (attempt - 1)),
            RetryMaxDelay.Ticks));

        _logger.LogDebug("第 {Attempt} 次占用重试将在 {Delay} 后进行: {PairId}", attempt, delay, syncPairId);

        _ = Task.Delay(delay, _lifetime.Token).ContinueWith(task =>
        {
            if (!_pairs.TryGetValue(syncPairId, out var pair) || !pair.Enabled || !pair.IsInitiator) return;
            ScheduleSync(syncPairId, fullScan: true);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ResetRetry(string syncPairId) => _retryAttempts.TryRemove(syncPairId, out _);

    // ---------------------------------------------------------------- 同步关系管理

    /// <summary>创建同步关系：必须由对端用户确认后才建立长期关系。</summary>
    public async Task<SyncPair> CreatePairAsync(string localPath, DeviceInfo remoteDevice, string remotePath,
        string name, SyncMode mode = SyncMode.TwoWay, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(localPath))
            throw new DirectoryNotFoundException($"本地同步目录不存在：{localPath}");

        var syncPairId = Guid.NewGuid().ToString();

        var pair = new SyncPair
        {
            SyncPairId = syncPairId,
            Name = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(localPath).Name : name,
            LocalPath = Path.GetFullPath(localPath),
            RemoteDeviceId = remoteDevice.DeviceId,
            RemoteDeviceName = remoteDevice.DeviceName,
            RemotePath = remotePath,
            Mode = mode,
            Enabled = true,
            IsInitiator = true,
            Status = SyncStatus.WaitingApproval,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _pairs[syncPairId] = pair;
        await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
        PairChanged?.Invoke(this, pair);

        _logger.LogInformation("发起同步关系 {Name}（{PairId}）: 本机 {Local} ⇄ {Device} {Remote}",
            pair.Name, syncPairId, pair.LocalPath, remoteDevice.DeviceName, remotePath);

        var response = await _client.RequestSyncAsync(remoteDevice.IpAddress, remoteDevice.Port,
            new SyncRequest
            {
                SyncPairId = syncPairId,
                Name = pair.Name,
                RemoteDeviceId = _devices.LocalDevice.DeviceId,
                RemoteDeviceName = _devices.LocalDevice.DeviceName,
                RequesterLocalPath = pair.LocalPath,
                TargetRemotePath = remotePath,
                Mode = mode == SyncMode.TwoWay ? "two-way" : mode == SyncMode.SendOnly ? "send-only" : "receive-only",
                CertificateFingerprint = _devices.LocalDevice.CertificateFingerprint ?? string.Empty,
            }, cancellationToken).ConfigureAwait(false);

        if (!response.Success || !response.Accepted)
        {
            pair.Enabled = false;
            pair.Status = SyncStatus.Error;
            pair.LastError = response.Message ?? "对方拒绝了同步请求。";
            await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
            PairChanged?.Invoke(this, pair);

            _logger.LogWarning("同步关系被拒绝或失败: {PairId} {Message}", syncPairId, pair.LastError);
            return pair;
        }

        if (!string.IsNullOrWhiteSpace(response.ResolvedLocalPath))
            pair.RemotePath = response.ResolvedLocalPath;

        pair.Status = SyncStatus.Scanning;
        pair.LastError = null;
        await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
        PairChanged?.Invoke(this, pair);

        StartWatcher(pair);

        // 首次扫描 + 建立基线
        await SyncNowAsync(syncPairId, fullScan: true, cancellationToken).ConfigureAwait(false);

        return _pairs[syncPairId];
    }

    public async Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
    {
        StopWatcher(syncPairId);

        // 必须与正在执行的一轮互斥：否则那一轮会在结束时 UpsertPairAsync 把刚删掉的关系写回库里
        // （并在同一轮继续写 SyncEntries），重启后 InitializeAsync 又会把它加载回来
        // ——用户看到关系被"删掉"了，它却在下次启动后复活并继续同步。
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pairs.TryRemove(syncPairId, out _);
            _pendingRuns.TryRemove(syncPairId, out _);
            ResetRetry(syncPairId);

            await _repository.DeletePairAsync(syncPairId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }

        _logger.LogInformation("已删除同步关系 {PairId}", syncPairId);
    }

    public async Task SetEnabledAsync(string syncPairId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!_pairs.TryGetValue(syncPairId, out var pair)) return;

        // 暂停同样要与执行中的一轮互斥：本次「暂停」应在这一轮结束后立即生效，
        // 而不是让已经排队的计划继续删/传文件。
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pair.Enabled = enabled;
            pair.Status = enabled ? SyncStatus.Idle : SyncStatus.Paused;
        }
        finally
        {
            _runGate.Release();
        }

        if (enabled)
        {
            StartWatcher(pair);
            if (pair.IsInitiator) ScheduleSync(pair.SyncPairId, fullScan: true);
            else _ = Task.Run(() => NotifyInitiatorAsync(pair), CancellationToken.None);
        }
        else
        {
            StopWatcher(syncPairId);
            _pendingRuns.TryRemove(syncPairId, out _);
        }

        await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
        PairChanged?.Invoke(this, pair);
    }

    public Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(string syncPairId,
        CancellationToken cancellationToken = default)
        => _repository.GetConflictsAsync(syncPairId, cancellationToken);

    public string? GetLocalPath(string syncPairId) =>
        _pairs.TryGetValue(syncPairId, out var pair) ? pair.LocalPath : null;

    public Task<string?> GetLocalPathAsync(string syncPairId, CancellationToken cancellationToken = default)
        => Task.FromResult(GetLocalPath(syncPairId));

    // ---------------------------------------------------------------- 服务端：同步请求 / 清单 / 内容

    public async Task<SyncRequestResponse> HandleSyncRequestAsync(SyncRequest request, string remoteDeviceId,
        string remoteDeviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SyncPairId))
            return new SyncRequestResponse
            {
                Success = false,
                Accepted = false,
                ErrorCode = ErrorCodes.BadRequest,
                Message = "缺少 syncPairId。",
            };

        if (_pairs.ContainsKey(request.SyncPairId))
            return new SyncRequestResponse
            {
                Success = false,
                Accepted = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.TransferStateConflict,
                Message = "该同步关系已存在。",
            };

        var suggestedLocalPath = string.IsNullOrWhiteSpace(request.TargetRemotePath)
            ? Path.Combine(_settings.Current.DownloadPath, "Sync", SanitizeFolderName(request.Name))
            : request.TargetRemotePath;

        var waiter = _pendingRequests.GetOrAdd(request.SyncPairId,
            _ => new TaskCompletionSource<SyncDecision>(TaskCreationOptions.RunContinuationsAsynchronously));

        SyncRequested?.Invoke(this, new SyncRequestEventArgs
        {
            SyncPairId = request.SyncPairId,
            Name = request.Name,
            RemoteDeviceId = remoteDeviceId,
            RemoteDeviceName = remoteDeviceName,
            RequestedRemotePath = request.RequesterLocalPath,
            SuggestedLocalPath = suggestedLocalPath,
            Mode = ParseMode(request.Mode),
        });

        _logger.LogInformation("收到同步关系请求 {Name}（{PairId}）来自 {Device}，等待本机用户确认",
            request.Name, request.SyncPairId, remoteDeviceName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        SyncDecision decision;
        try
        {
            decision = await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(request.SyncPairId, out _);
            return new SyncRequestResponse
            {
                Success = false,
                Accepted = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.Timeout,
                Message = "等待本机用户确认超时。",
            };
        }

        if (!decision.Accepted)
        {
            return new SyncRequestResponse
            {
                Success = false,
                Accepted = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.SyncRejected,
                Message = "本机用户拒绝了同步请求。",
            };
        }

        var localPath = string.IsNullOrWhiteSpace(decision.ResolvedLocalPath)
            ? suggestedLocalPath
            : decision.ResolvedLocalPath;

        try
        {
            Directory.CreateDirectory(localPath);
        }
        catch (Exception ex)
        {
            return new SyncRequestResponse
            {
                Success = false,
                Accepted = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.AccessDenied,
                Message = $"无法创建同步目录：{ex.Message}",
            };
        }

        var pair = new SyncPair
        {
            SyncPairId = request.SyncPairId,
            Name = request.Name,
            LocalPath = Path.GetFullPath(localPath),
            RemoteDeviceId = remoteDeviceId,
            RemoteDeviceName = remoteDeviceName,
            RemotePath = request.RequesterLocalPath,
            Mode = ParseMode(request.Mode),
            Enabled = true,
            IsInitiator = false,
            Status = SyncStatus.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _pairs[pair.SyncPairId] = pair;
        await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
        StartWatcher(pair);
        PairChanged?.Invoke(this, pair);

        _logger.LogInformation("已创建同步关系 {Name}（{PairId}）: {Local} ⇄ {Device}",
            pair.Name, pair.SyncPairId, pair.LocalPath, remoteDeviceName);

        return new SyncRequestResponse
        {
            Success = true,
            Accepted = true,
            SyncPairId = pair.SyncPairId,
            ResolvedLocalPath = pair.LocalPath,
        };
    }

    public void RespondToSyncRequest(string syncPairId, bool accept, string? resolvedLocalPath)
    {
        if (_pendingRequests.TryRemove(syncPairId, out var waiter))
            waiter.TrySetResult(new SyncDecision(accept, resolvedLocalPath));
    }

    public async Task<SyncManifestResponse> HandleManifestAsync(SyncManifestRequest request,
        string remoteDeviceId, CancellationToken cancellationToken = default)
    {
        if (!_pairs.TryGetValue(request.SyncPairId, out var pair))
            return new SyncManifestResponse
            {
                Success = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.SyncPairNotFound,
                Message = "本机不存在该同步关系。",
            };

        if (!string.IsNullOrWhiteSpace(pair.RemoteDeviceId) &&
            !string.Equals(pair.RemoteDeviceId, remoteDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("同步清单请求来自非配对设备 {Device}，已拒绝", remoteDeviceId);
            return new SyncManifestResponse
            {
                Success = false,
                SyncPairId = request.SyncPairId,
                ErrorCode = ErrorCodes.Unauthorized,
                Message = "请求设备与同步关系不匹配。",
            };
        }

        var baseline = await _metadata.LoadBaselineAsync(pair.SyncPairId, cancellationToken)
            .ConfigureAwait(false);

        var scan = await _scanner.ScanAsync(pair.LocalPath, baseline, request.FullScan, cancellationToken)
            .ConfigureAwait(false);

        // 本机根目录不可读时必须明确告知对端放弃本轮：若照常返回「空清单」，
        // 对端会把它当成「本机删除了所有文件」，进而删掉它自己的全部副本。
        if (scan.RootUnreadable)
        {
            _logger.LogWarning("同步目录不可读，已拒绝本次清单请求: {Pair} {Path}", pair.Name, pair.LocalPath);
            return new SyncManifestResponse
            {
                Success = false,
                SyncPairId = pair.SyncPairId,
                ErrorCode = ErrorCodes.SyncPairError,
                Message = "本机同步目录无法读取，已拒绝本轮清单请求。",
                RootUnreadable = true,
            };
        }

        // 被动端也要落基线：否则每轮清单请求都会对整棵树重新做 SHA-256
        // （基线为空 → 两级检测失效），而且「读不到→按基线占位」的保护在被动端无从生效。
        await PersistScannedBaselineAsync(pair, baseline, scan, cancellationToken).ConfigureAwait(false);

        return new SyncManifestResponse
        {
            Success = true,
            SyncPairId = pair.SyncPairId,
            // 读不到的条目以基线状态占位，避免对端把「读不到」当成「已删除」而删掉自己的副本
            Entries = SyncMetadataManager.ToManifest(scan, baseline),
            // 基线里没有的读不到条目，用显式「未知」清单告知对端，由对端整条跳过
            UnreadablePaths = scan.UnreadablePaths.ToList(),
        };
    }

    public async Task<SimpleOperationResponse> HandleChangeNotificationAsync(string syncPairId,
        string remoteDeviceId, CancellationToken cancellationToken = default)
    {
        var pair = await AuthorizeAsync(syncPairId, remoteDeviceId, cancellationToken).ConfigureAwait(false);
        if (pair is null)
            return Fail(ErrorCodes.Unauthorized, "同步关系不存在、已暂停或请求设备不匹配。");

        if (!pair.IsInitiator)
            return Fail(ErrorCodes.SyncPairError, "本机不是该同步关系的调度端。");

        ScheduleSync(syncPairId, fullScan: false);
        _logger.LogDebug("收到实时同步通知，已加入调度队列: {PairId}", syncPairId);

        return new SimpleOperationResponse
        {
            Success = true,
            TransferId = syncPairId,
            State = "scheduled",
        };
    }

    public async Task<SyncContentResult> OpenReadAsync(string syncPairId, string relativePath,
        string remoteDeviceId, CancellationToken cancellationToken = default)
    {
        var pair = await AuthorizeAsync(syncPairId, remoteDeviceId, cancellationToken).ConfigureAwait(false);
        if (pair is null) return SyncContentResult.Fail(ErrorCodes.SyncPairNotFound, "同步关系不存在或未授权。");

        if (!_paths.TryResolve(pair.LocalPath, relativePath, out var fullPath))
            return SyncContentResult.Fail(ErrorCodes.PathEscapeDetected, "路径非法。");

        if (!File.Exists(fullPath))
            return SyncContentResult.Fail(ErrorCodes.FileNotFound, "文件不存在。");

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, AppConstants.FileReadSharing,
                AppConstants.StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new SyncContentResult
            {
                Success = true,
                Content = stream,
                Length = stream.Length,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取同步文件失败: {Path}", fullPath);
            return SyncContentResult.Fail(ErrorCodes.AccessDenied, $"读取失败：{ex.Message}");
        }
    }

    public async Task<SimpleOperationResponse> WriteAsync(string syncPairId, string relativePath,
        Stream content, string remoteDeviceId, CancellationToken cancellationToken = default)
    {
        var pair = await AuthorizeAsync(syncPairId, remoteDeviceId, cancellationToken).ConfigureAwait(false);
        if (pair is null)
            return Fail(ErrorCodes.SyncPairNotFound, "同步关系不存在或未授权。");

        if (!_paths.TryResolve(pair.LocalPath, relativePath, out var fullPath))
            return Fail(ErrorCodes.PathEscapeDetected, "路径非法。");

        var directory = Path.GetDirectoryName(fullPath);
        var partPath = fullPath + AppConstants.PartExtension;

        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, AppConstants.StreamBufferSize, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(target, AppConstants.StreamBufferSize, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (File.Exists(fullPath)) File.Delete(fullPath);
            File.Move(partPath, fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入同步文件失败: {Path}", fullPath);
            TryDelete(partPath);
            return Fail(ErrorCodes.AccessDenied, $"写入失败：{ex.Message}");
        }

        _logger.LogInformation("同步文件已写入: {Pair}/{Path}", pair.Name, relativePath);

        // 被动端也必须落基线，否则：
        // 1) 每轮清单请求都要对整棵树重算 SHA-256（基线为空 → 两级检测失效，大目录下每轮读全盘）；
        // 2) 「读不到→用基线占位」的保护在被动端完全失效（对端清单里缺条目 → 发起端判成已删除）。
        await TryUpdateBaselineAfterWriteAsync(pair, relativePath, fullPath, cancellationToken)
            .ConfigureAwait(false);

        return new SimpleOperationResponse { Success = true, TransferId = syncPairId, State = "written" };
    }

    private async Task TryUpdateBaselineAfterWriteAsync(SyncPair pair, string relativePath, string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var sha = await TryHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (sha is null) return;

            var baseline = await _metadata.LoadBaselineAsync(pair.SyncPairId, cancellationToken)
                .ConfigureAwait(false);

            await UpdateBaselineAsync(pair, baseline, relativePath, false, sha, false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 基线写失败不影响文件已落盘的事实，下轮扫描会重新计算
            _logger.LogWarning(ex, "写入后更新基线失败: {Pair}/{Path}", pair.Name, relativePath);
        }
    }

    public async Task<SimpleOperationResponse> DeleteAsync(string syncPairId, IReadOnlyList<string> relativePaths,
        string remoteDeviceId, CancellationToken cancellationToken = default)
    {
        var pair = await AuthorizeAsync(syncPairId, remoteDeviceId, cancellationToken).ConfigureAwait(false);
        if (pair is null) return Fail(ErrorCodes.SyncPairNotFound, "同步关系不存在或未授权。");

        var deleted = 0;
        var failedPaths = new List<string>();

        var baseline = await _metadata.LoadBaselineAsync(pair.SyncPairId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var relativePath in relativePaths)
        {
            if (!_paths.TryResolve(pair.LocalPath, relativePath, out var fullPath)) continue;

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                // 删不掉（文件被占用等）必须如实上报：否则发起端会写「已传播」的墓碑，
                // 而那个文件其实还在本机，下一轮就会被 Download 复活——用户的删除被静默撤销。
                _logger.LogWarning(ex, "删除同步文件失败: {Path}", fullPath);
                failedPaths.Add(relativePath);
                continue;
            }

            // 成功删除（或本来就不存在）都落墓碑，避免下一轮把「基线里有、本地没有」重新判成待删除
            await UpdateBaselineAsync(pair, baseline, relativePath, false, string.Empty, deleted: true,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("按对端请求删除同步文件: {Pair} 共 {Count} 项，失败 {Failed} 项",
            pair.Name, deleted, failedPaths.Count);

        return new SimpleOperationResponse
        {
            Success = failedPaths.Count == 0,
            TransferId = syncPairId,
            State = $"deleted:{deleted}",
            ErrorCode = failedPaths.Count == 0 ? null : ErrorCodes.AccessDenied,
            Message = failedPaths.Count == 0
                ? null
                : $"有 {failedPaths.Count} 个文件未能删除（可能正被其它程序占用）。",
        };
    }

    /// <summary>
    /// 把扫描结果里「基线缺失或已变化」的条目写回基线（跳过目录与读不到的条目）。
    /// 让下一轮扫描命中两级检测（size+mtime 未变就不重算哈希），
    /// 同时让「读不到就按基线占位」的保护在对端方向也能生效。
    /// </summary>
    private async Task PersistScannedBaselineAsync(SyncPair pair, Dictionary<string, SyncEntry> baseline,
        DirectoryScanResult scan, CancellationToken cancellationToken)
    {
        List<SyncEntry>? pending = null;

        foreach (var entry in scan.Entries)
        {
            if (entry.IsDirectory) continue;
            if (string.IsNullOrEmpty(entry.Sha256)) continue;
            if (scan.IsUnreadable(entry.RelativePath)) continue;

            if (baseline.TryGetValue(entry.RelativePath, out var known) && !known.Deleted &&
                string.Equals(known.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;   // 基线已一致，无需写库
            }

            (pending ??= new List<SyncEntry>()).Add(
                BuildBaselineEntry(pair, baseline, entry.RelativePath, false, entry.Sha256, deleted: false));
        }

        // 一次性批量落库：首次同步可能有上万个文件，逐条写会变成上万次 SQLite 事务
        // （事务是 BEGIN IMMEDIATE，写操作全库串行，慢盘上会拖很久）。
        if (pending is { Count: > 0 })
            await _metadata.SaveAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SyncPair?> AuthorizeAsync(string syncPairId, string remoteDeviceId,
        CancellationToken cancellationToken)
    {
        if (!_pairs.TryGetValue(syncPairId, out var pair)) return null;

        if (!string.IsNullOrWhiteSpace(pair.RemoteDeviceId) &&
            !string.Equals(pair.RemoteDeviceId, remoteDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("同步请求来自非配对设备 {Device}，已拒绝（{PairId}）", remoteDeviceId, syncPairId);
            return null;
        }

        if (!pair.Enabled) return null;

        await Task.CompletedTask;
        return pair;
    }

    // ---------------------------------------------------------------- 同步主流程

    public async Task<SyncRunResult> SyncNowAsync(string syncPairId, bool fullScan = false,
        CancellationToken cancellationToken = default)
    {
        var result = new SyncRunResult { SyncPairId = syncPairId };
        var started = DateTimeOffset.UtcNow;

        if (!_pairs.TryGetValue(syncPairId, out var pair))
        {
            result.ErrorCode = ErrorCodes.SyncPairNotFound;
            result.Message = "同步关系不存在。";
            return result;
        }

        if (!pair.Enabled)
        {
            result.ErrorCode = ErrorCodes.SyncPairError;
            result.Message = "同步关系已暂停。";
            return result;
        }

        if (!pair.IsInitiator)
        {
            result.Success = true;
            result.Message = "该同步关系由对端发起，本机被动接收。";
            return result;
        }

        var device = _devices.Find(pair.RemoteDeviceId);
        if (device is null)
        {
            result.ErrorCode = ErrorCodes.TransferNotFound;
            result.Message = "对端设备不在设备列表中。";
            await MarkErrorAsync(pair, result.Message, cancellationToken).ConfigureAwait(false);
            ScheduleRetry(syncPairId);
            return result;
        }

        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 同步根不存在时绝不能让扫描结果为空跑进规划器：
            // 那会把两端所有文件都判成「已删除」，直接删掉对端数据。
            if (!Directory.Exists(pair.LocalPath))
            {
                result.ErrorCode = ErrorCodes.SyncPairError;
                result.Message = $"同步目录不存在，已跳过本次同步（避免误删对端）：{pair.LocalPath}";
                _logger.LogWarning("同步目录不存在，已跳过本次同步: {Pair} {Path}", pair.Name, pair.LocalPath);
                await MarkErrorAsync(pair, result.Message, cancellationToken).ConfigureAwait(false);
                return result;
            }

            pair.Status = SyncStatus.Scanning;
            PairChanged?.Invoke(this, pair);

            var baseline = await _metadata.LoadBaselineAsync(pair.SyncPairId, cancellationToken)
                .ConfigureAwait(false);

            var scan = await _scanner.ScanAsync(pair.LocalPath, baseline, fullScan, cancellationToken)
                .ConfigureAwait(false);

            // 本机整棵树读不到时绝不进入规划器：空扫描结果会把两端所有文件判成「本机已删除」
            if (scan.RootUnreadable)
            {
                result.ErrorCode = ErrorCodes.SyncPairError;
                result.Message = $"本机同步目录本轮无法读取，已跳过本次同步（避免误删数据）：{pair.LocalPath}";
                _logger.LogWarning("本机同步目录无法读取，已跳过: {Pair} {Path}", pair.Name, pair.LocalPath);
                await MarkErrorAsync(pair, result.Message, cancellationToken).ConfigureAwait(false);
                return result;
            }

            var local = scan.Entries;

            var manifestResponse = await _client.ExchangeManifestAsync(device.IpAddress, device.Port,
                new SyncManifestRequest
                {
                    SyncPairId = pair.SyncPairId,
                    Entries = SyncMetadataManager.ToManifest(scan, baseline),
                    FullScan = fullScan,
                }, cancellationToken).ConfigureAwait(false);

            if (!manifestResponse.Success)
            {
                result.ErrorCode = manifestResponse.ErrorCode ?? ErrorCodes.NetworkUnreachable;
                result.Message = manifestResponse.Message ?? "获取对端清单失败。";
                await MarkErrorAsync(pair, result.Message, cancellationToken).ConfigureAwait(false);
                // 对端刚启动/正忙/网络抖动都会走到这里：安排一次退避重试，
                // 否则关系会一直停在「错误」直到下一个 10 分钟周期扫描（用户以为同步坏了）。
                ScheduleRetry(syncPairId);
                return result;
            }

            // 对端根目录不可读时同样必须放弃本轮：把它当成「空清单」＝把对端整个目录视为已删除
            if (manifestResponse.RootUnreadable)
            {
                result.ErrorCode = ErrorCodes.SyncPairError;
                result.Message = "对端同步目录本轮无法读取，已跳过本次同步（避免误删数据）。";
                _logger.LogWarning("对端同步目录无法读取，已跳过本轮: {Pair}", pair.Name);
                await MarkErrorAsync(pair, result.Message, cancellationToken).ConfigureAwait(false);
                return result;
            }

            var remoteUnreadable = new HashSet<string>(manifestResponse.UnreadablePaths,
                StringComparer.OrdinalIgnoreCase);

            var plan = _planner.Plan(pair.SyncPairId, baseline, local, manifestResponse.Entries, pair.Mode,
                device.DeviceName, unreadableLocalPaths: scan.UnreadablePaths,
                unreadableRemotePaths: remoteUnreadable);

            pair.Status = plan.HasWork || plan.Conflicts.Count > 0 ? SyncStatus.Syncing : SyncStatus.Idle;
            PairChanged?.Invoke(this, pair);

            var failures = await ExecutePlanAsync(pair, device, plan, baseline, cancellationToken)
                .ConfigureAwait(false);

            result.Uploaded = plan.Uploads.Count();
            result.Downloaded = plan.Downloads.Count();
            result.DeletedRemote = plan.RemoteDeletions.Count();
            result.DeletedLocal = plan.LocalDeletions.Count();
            result.Conflicts = plan.Conflicts.Count;

            // 被占用/读不到的条目（本机侧）与执行失败的条目（本机或对端占用）都要在
            // 解锁后补同步：记进结果里，稍后做一次短延时重试，而不是干等到下个周期扫描。
            result.Skipped = plan.SkippedUnreadable.Count;
            result.Failed = failures.Count;
            result.Success = true;

            if (result.HasPendingRetry)
            {
                result.Message = $"有 {result.Skipped + result.Failed} 个文件被占用或暂时无法处理，" +
                                 "稍后会自动重试";

                _logger.LogInformation(
                    "本轮有 {Skipped} 个条目状态未知、{Failed} 个条目执行失败，将自动重试（{Pair}）",
                    result.Skipped, result.Failed, pair.Name);
            }

            pair.LastSyncAt = DateTimeOffset.UtcNow;
            pair.LastScanAt = DateTimeOffset.UtcNow;
            pair.Status = plan.Conflicts.Count > 0 ? SyncStatus.Conflict : SyncStatus.Idle;
            pair.LastError = null;

            // 兜底：本轮进行期间关系可能已被用户在另一处删除，此时不能把 SyncPairs 行写回去
            // （否则重启后 InitializeAsync 会把「已删除」的关系重新加载并继续同步）。
            if (!_pairs.ContainsKey(syncPairId))
            {
                // SyncCompleted 由 finally 统一触发，这里直接返回即可
                _logger.LogInformation("同步关系已被删除，丢弃本轮结果: {PairId}", syncPairId);
                return result;
            }

            await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
            PairChanged?.Invoke(this, pair);

            _logger.LogInformation(
                "同步完成 {Pair}: 上传 {Up}，下载 {Down}，删除对端 {DelR}，删除本机 {DelL}，冲突 {Conf}" +
                "，跳过 {Skip}，失败 {Fail}",
                pair.Name, result.Uploaded, result.Downloaded, result.DeletedRemote, result.DeletedLocal,
                result.Conflicts, result.Skipped, result.Failed);

            if (result.HasPendingRetry) ScheduleRetry(syncPairId);
            else ResetRetry(syncPairId);
        }
        catch (OperationCanceledException)
        {
            result.ErrorCode = ErrorCodes.CancelledByUser;
            result.Message = "同步已取消。";
        }
        catch (Exception ex)
        {
            result.ErrorCode = ErrorCodes.InternalError;
            result.Message = ex.Message;
            _logger.LogError(ex, "同步失败 {PairId}", syncPairId);
            await MarkErrorAsync(pair, ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
            _lastRun[syncPairId] = DateTimeOffset.UtcNow;
            result.Duration = DateTimeOffset.UtcNow - started;
            SyncCompleted?.Invoke(this, result);
        }

        return result;
    }

    /// <summary>
    /// 执行同步计划。返回本轮执行失败的相对路径（对端/本机文件被占用等），
    /// 供调用方安排稍后重试——失败项不会写入基线，因此重试时会重新规划。
    /// </summary>
    private async Task<List<string>> ExecutePlanAsync(SyncPair pair, DeviceInfo device, SyncPlan plan,
        Dictionary<string, SyncEntry> baseline, CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        // 0. 两端内容一致但基线缺失/过期：按本地实际内容（或墓碑）建立基线。
        //    此前这一支没有任何执行代码，基线永不收敛：每轮对已知文件重复全量哈希，
        //    而且「两端都有同一文件、基线却为空」会被判成「两端都改且不同」→ 误报冲突 + 生成冲突副本。
        foreach (var action in plan.Actions.Where(a => a.Type == SyncActionType.UpdateBaseline))
        {
            if (action.IsDirectory) continue;   // 目录不写基线：协议无法在对端建目录，会引发误删
            if (!_paths.TryResolve(pair.LocalPath, action.RelativePath, out var fullPath)) continue;

            if (File.Exists(fullPath))
            {
                var sha = await TryHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (sha is null)
                {
                    failures.Add(action.RelativePath);
                    continue;
                }

                await UpdateBaselineAsync(pair, baseline, action.RelativePath, false, sha, false,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 两端都已删除：补一枚墓碑，避免「删除」在后续轮次被误判成「新文件」而复活
                await UpdateBaselineAsync(pair, baseline, action.RelativePath, false, string.Empty, true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // 1. 先处理删除（文件删除先于目录删除，避免目录非空）
        foreach (var action in plan.LocalDeletions.OrderByDescending(a => a.RelativePath.Length))
        {
            if (!_paths.TryResolve(pair.LocalPath, action.RelativePath, out var fullPath)) continue;

            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception ex)
            {
                // 文件正被其它程序打开时删不掉：留到下一轮，别写基线
                _logger.LogWarning(ex, "删除本地文件失败（可能被占用），稍后重试: {Path}", fullPath);
                failures.Add(action.RelativePath);
                continue;
            }

            await UpdateBaselineAsync(pair, baseline, action.RelativePath, isDirectory: action.IsDirectory,
                sha256: string.Empty, deleted: true, cancellationToken).ConfigureAwait(false);
        }

        foreach (var action in plan.RemoteDeletions)
        {
            var response = await _client.DeleteSyncFilesAsync(device.IpAddress, device.Port, pair.SyncPairId,
                new[] { action.RelativePath }, cancellationToken).ConfigureAwait(false);

            if (response.Success)
            {
                await UpdateBaselineAsync(pair, baseline, action.RelativePath, action.IsDirectory,
                    string.Empty, deleted: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("删除对端文件失败: {Path} {Message}", action.RelativePath, response.Message);
                failures.Add(action.RelativePath);
            }
        }

        // 2. 下载（对端 → 本机）
        foreach (var action in plan.Downloads)
        {
            if (!_paths.TryResolve(pair.LocalPath, action.RelativePath, out var fullPath)) continue;

            if (action.IsDirectory)
            {
                try
                {
                    Directory.CreateDirectory(fullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "创建目录失败: {Path}", fullPath);
                    failures.Add(action.RelativePath);
                    continue;
                }

                await UpdateBaselineAsync(pair, baseline, action.RelativePath, true, string.Empty, false,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ok = await DownloadFileAsync(pair, device, action.RelativePath, fullPath, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                failures.Add(action.RelativePath);
                continue;
            }

            var sha = await TryHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (sha is null)
            {
                failures.Add(action.RelativePath);
                continue;
            }

            await UpdateBaselineAsync(pair, baseline, action.RelativePath, false, sha, false, cancellationToken)
                .ConfigureAwait(false);
        }

        // 3. 上传（本机 → 对端）
        foreach (var action in plan.Uploads)
        {
            if (!_paths.TryResolve(pair.LocalPath, action.RelativePath, out var fullPath)) continue;

            if (action.IsDirectory)
            {
                // 目录不写基线、也不在对端单独建目录：
                // 写基线会让下一轮把「对端没有该目录」判成「对端已删除」而递归删除本机目录；
                // 目录内的文件会在上传时由对端隐式创建，收敛后两端状态自然一致。
                continue;
            }

            if (!File.Exists(fullPath)) continue;

            // 上传前先记住「准备发送的内容哈希」（扫描时算出的值）
            var plannedSha = action.Sha256;

            var ok = await UploadFileAsync(pair, device, action.RelativePath, fullPath, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                failures.Add(action.RelativePath);
                continue;
            }

            var sha = await TryHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (sha is null)
            {
                failures.Add(action.RelativePath);
                continue;
            }

            // 上传期间源文件被追加/保存时**绝不能**把新哈希写进基线：
            // 对端实际收到的是旧字节，基线却声称「对端已是新内容」，
            // 下一轮就会按「对端修改、本机未修改」把本机的新内容用对端旧内容覆盖（丢失更新）。
            // 这里保持基线不变并计入失败项，下一轮会重新上传新内容。
            if (plannedSha is { Length: > 0 } &&
                !string.Equals(sha, plannedSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "上传期间源文件发生变化，本轮不写基线（下一轮重新同步）: {Pair}/{Path}",
                    pair.Name, action.RelativePath);
                failures.Add(action.RelativePath);
                continue;
            }

            await UpdateBaselineAsync(pair, baseline, action.RelativePath, false, sha, false, cancellationToken)
                .ConfigureAwait(false);
        }

        // 4. 冲突：默认保留双方文件，禁止自动覆盖
        foreach (var action in plan.Conflicts)
        {
            await HandleConflictAsync(pair, device, action, baseline, cancellationToken).ConfigureAwait(false);
        }

        return failures;
    }

    /// <summary>算摘要失败（文件又被占用/删掉）返回 null，交由调用方按失败项处理。</summary>
    private async Task<string?> TryHashAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            return await _hash.ComputeFileHashAsync(fullPath, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "计算文件摘要失败，本轮跳过该条目: {Path}", fullPath);
            return null;
        }
    }

    private async Task<bool> DownloadFileAsync(SyncPair pair, DeviceInfo device, string relativePath,
        string fullPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var partPath = fullPath + AppConstants.PartExtension;

        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             AppConstants.StreamBufferSize, FileOptions.Asynchronous))
            {
                var ok = await _client.DownloadSyncFileAsync(device.IpAddress, device.Port, pair.SyncPairId,
                    relativePath, target, cancellationToken).ConfigureAwait(false);

                if (!ok)
                {
                    TryDelete(partPath);
                    _logger.LogWarning("下载同步文件失败: {Path}", relativePath);
                    return false;
                }
            }

            // 只有完整下载后才原子替换，避免出现半截文件
            if (File.Exists(fullPath)) File.Delete(fullPath);
            File.Move(partPath, fullPath);

            _logger.LogInformation("同步下载完成: {Pair}/{Path}", pair.Name, relativePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载同步文件失败: {Path}", relativePath);
            TryDelete(partPath);
            return false;
        }
    }

    private async Task<bool> UploadFileAsync(SyncPair pair, DeviceInfo device, string relativePath,
        string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                AppConstants.FileReadSharing, AppConstants.StreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var response = await _client.UploadSyncFileAsync(device.IpAddress, device.Port, pair.SyncPairId,
                relativePath, source, source.Length, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                _logger.LogWarning("上传同步文件失败: {Path} {Message}", relativePath, response.Message);
                return false;
            }

            _logger.LogInformation("同步上传完成: {Pair}/{Path}", pair.Name, relativePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上传同步文件失败: {Path}", relativePath);
            return false;
        }
    }

    private async Task HandleConflictAsync(SyncPair pair, DeviceInfo device, SyncAction action,
        Dictionary<string, SyncEntry> baseline, CancellationToken cancellationToken)
    {
        if (!_paths.TryResolve(pair.LocalPath, action.RelativePath, out var localFullPath)) return;

        var localSha = File.Exists(localFullPath)
            ? await TryHashAsync(localFullPath, cancellationToken).ConfigureAwait(false) ?? string.Empty
            : string.Empty;

        // 冲突去重（重要）：未裁决的冲突不会更新基线，因此下一轮扫描仍会判出同一个冲突。
        // 若不去重，每轮都会重新下载对端版本、再生成一个新冲突副本，
        // 副本又落在同步根内触发文件监视 → 立刻再来一轮，形成无界增长（磁盘/带宽/冲突记录）。
        var expectedRemoteSha = action.Sha256 ?? string.Empty;
        if (!string.IsNullOrEmpty(localSha) && !string.IsNullOrEmpty(expectedRemoteSha))
        {
            var open = await _conflicts.GetOpenConflictsAsync(pair.SyncPairId, cancellationToken)
                .ConfigureAwait(false);

            if (open.Any(c => string.Equals(c.RelativePath, action.RelativePath,
                               StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(c.LocalSha256, localSha, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(c.RemoteSha256, expectedRemoteSha, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("冲突已存在且双方内容未变，跳过重复处理: {Pair}/{Path}",
                    pair.Name, action.RelativePath);
                return;
            }
        }

        var remoteSha = string.Empty;
        string? conflictCopyPath = null;

        // 冲突默认策略：保留双方文件。把对端版本另存为冲突副本，不动本机正式文件。
        if (action.ConflictCopyRelativePath is { Length: > 0 } copyRelative)
        {
            try
            {
                conflictCopyPath = _conflicts.ReserveConflictCopyPath(pair.LocalPath, copyRelative);

                if (File.Exists(localFullPath) || !File.Exists(localFullPath))
                {
                    await using var target = new FileStream(conflictCopyPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, AppConstants.StreamBufferSize, FileOptions.Asynchronous);

                    var ok = await _client.DownloadSyncFileAsync(device.IpAddress, device.Port, pair.SyncPairId,
                        action.RelativePath, target, cancellationToken).ConfigureAwait(false);

                    if (!ok)
                    {
                        TryDelete(conflictCopyPath);
                        conflictCopyPath = null;
                    }
                    else
                    {
                        remoteSha = await _hash.ComputeFileHashAsync(conflictCopyPath, null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存冲突副本失败: {Path}", action.RelativePath);
                conflictCopyPath = null;
            }
        }

        await _conflicts.RecordAsync(pair.SyncPairId, action.RelativePath, localSha, remoteSha,
            conflictCopyPath, action.Reason, cancellationToken).ConfigureAwait(false);

        // 冲突项不更新基线：保留冲突状态，等待用户裁决
        await Task.CompletedTask;
    }

    /// <summary>用户裁决冲突。</summary>
    public async Task ResolveConflictAsync(string syncPairId, SyncConflictRecord conflict,
        ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        if (!_pairs.TryGetValue(syncPairId, out var pair)) return;

        var device = _devices.Find(pair.RemoteDeviceId);
        if (device is null) return;

        if (!_paths.TryResolve(pair.LocalPath, conflict.RelativePath, out var localFullPath)) return;

        var baseline = await _metadata.LoadBaselineAsync(pair.SyncPairId, cancellationToken)
            .ConfigureAwait(false);

        switch (resolution)
        {
            case ConflictResolution.UseLocal:
                if (File.Exists(localFullPath))
                {
                    var uploaded = await UploadFileAsync(pair, device, conflict.RelativePath, localFullPath,
                        cancellationToken).ConfigureAwait(false);

                    // 上传失败却写基线 = 谎报「两端一致」：下一轮会按「对端修改」把本机版本用对端旧内容
                    // 覆盖回去，用户刚做的裁决被静默回滚（数据丢失）。失败必须保留冲突待重试。
                    if (!uploaded)
                        throw new InvalidOperationException(
                            $"未能把本机版本推送给对端（{conflict.RelativePath}），冲突仍保留，请稍后重试。");

                    var sha = await _hash.ComputeFileHashAsync(localFullPath, null, cancellationToken)
                        .ConfigureAwait(false);

                    await UpdateBaselineAsync(pair, baseline, conflict.RelativePath, false, sha, false,
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case ConflictResolution.UseRemote:
            {
                var downloaded = await DownloadFileAsync(pair, device, conflict.RelativePath, localFullPath,
                    cancellationToken).ConfigureAwait(false);

                if (!downloaded)
                    throw new InvalidOperationException(
                        $"未能取回对端版本（{conflict.RelativePath}），冲突仍保留，请稍后重试。");

                if (File.Exists(localFullPath))
                {
                    var sha = await _hash.ComputeFileHashAsync(localFullPath, null, cancellationToken)
                        .ConfigureAwait(false);

                    await UpdateBaselineAsync(pair, baseline, conflict.RelativePath, false, sha, false,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case ConflictResolution.KeepBoth:
                // 本机版本保留为正式文件并推送对端；对端版本已作为冲突副本保存在本机
                if (File.Exists(localFullPath))
                {
                    var uploaded = await UploadFileAsync(pair, device, conflict.RelativePath, localFullPath,
                        cancellationToken).ConfigureAwait(false);

                    if (!uploaded)
                        throw new InvalidOperationException(
                            $"未能把本机版本推送给对端（{conflict.RelativePath}），冲突仍保留，请稍后重试。");

                    var sha = await _hash.ComputeFileHashAsync(localFullPath, null, cancellationToken)
                        .ConfigureAwait(false);

                    await UpdateBaselineAsync(pair, baseline, conflict.RelativePath, false, sha, false,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
        }

        await _conflicts.ResolveAsync(conflict.ConflictId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("冲突已裁决: {Path} -> {Resolution}", conflict.RelativePath, resolution);
    }

    // ---------------------------------------------------------------- 基线维护

    private async Task UpdateBaselineAsync(SyncPair pair, Dictionary<string, SyncEntry> baseline,
        string relativePath, bool isDirectory, string sha256, bool deleted,
        CancellationToken cancellationToken)
    {
        var entry = BuildBaselineEntry(pair, baseline, relativePath, isDirectory, sha256, deleted);
        await _metadata.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构造基线条目并同步更新内存基线（写库由调用方决定批量还是单条）。</summary>
    private SyncEntry BuildBaselineEntry(SyncPair pair, Dictionary<string, SyncEntry> baseline,
        string relativePath, bool isDirectory, string sha256, bool deleted)
    {
        baseline.TryGetValue(relativePath, out var existing);
        var nextVersion = (existing?.Version ?? 0) + 1;

        SyncEntry entry;

        if (deleted)
        {
            entry = _metadata.BuildTombstone(pair.SyncPairId, relativePath, nextVersion, propagated: true);
        }
        else
        {
            var fullPath = DirectoryScanner.Resolve(_paths, pair.LocalPath, relativePath);

            long size = 0;
            var lastWrite = DateTimeOffset.UtcNow;

            if (!isDirectory && fullPath is not null && File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                size = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }

            entry = _metadata.BuildBaseline(pair.SyncPairId, relativePath, existing?.FileId ?? string.Empty,
                isDirectory, size, lastWrite, sha256, nextVersion);
        }

        baseline[relativePath] = entry;
        return entry;
    }

    private async Task MarkErrorAsync(SyncPair pair, string message, CancellationToken cancellationToken)
    {
        pair.Status = SyncStatus.Error;
        pair.LastError = message;

        try
        {
            await _repository.UpsertPairAsync(pair, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入同步关系状态失败: {PairId}", pair.SyncPairId);
        }

        PairChanged?.Invoke(this, pair);
    }

    // ---------------------------------------------------------------- 辅助

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }

    private static SimpleOperationResponse Fail(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        Message = message,
    };

    private static SyncMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "send-only" or "sendonly" => SyncMode.SendOnly,
        "receive-only" or "receiveonly" => SyncMode.ReceiveOnly,
        _ => SyncMode.TwoWay,
    };

    private static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Sync";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();

        return string.IsNullOrEmpty(cleaned) ? "Sync" : cleaned;
    }

    private sealed record SyncDecision(bool Accepted, string? ResolvedLocalPath);

    public async ValueTask DisposeAsync()
    {
        _devices.DeviceOnline -= OnDeviceOnline;
        _scanTimer?.Dispose();
        _scanTimer = null;

        try { _lifetime.Cancel(); } catch { /* 忽略 */ }
        _lifetime.Dispose();

        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();

        _runGate.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>冲突裁决方式。</summary>
public enum ConflictResolution
{
    /// <summary>使用本机版本（覆盖对端）。</summary>
    UseLocal = 0,

    /// <summary>使用远程版本（覆盖本机）。</summary>
    UseRemote = 1,

    /// <summary>保留两个版本。</summary>
    KeepBoth = 2,
}
