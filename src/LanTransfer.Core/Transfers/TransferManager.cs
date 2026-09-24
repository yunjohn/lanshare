using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Transfers;

/// <summary>
/// 传输管理器：UI 唯一的传输入口。
/// 负责创建 / 排队 / 开始 / 暂停 / 继续 / 取消 / 恢复传输，并把进度推送给 UI。
/// 第一版同时只允许 1 个发送任务处于活动状态，其余排队。
/// </summary>
public sealed class TransferManager : ITransferManager
{
    private readonly ISettingsService _settings;
    private readonly IFileScanner _scanner;
    private readonly IHashService _hash;
    private readonly IChunkManager _chunks;
    private readonly ITransferClient _client;
    private readonly ITransferServer _server;
    private readonly ITransferRepository _repository;
    private readonly IDeviceManager _devices;
    private readonly ILogger<TransferManager> _logger;

    private readonly ConcurrentDictionary<string, TransferRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SendJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendSlot = new(1, 1);
    private readonly object _recordLock = new();

    public TransferManager(
        ISettingsService settings,
        IFileScanner scanner,
        IHashService hash,
        IChunkManager chunks,
        ITransferClient client,
        ITransferServer server,
        ITransferRepository repository,
        IDeviceManager devices,
        ILogger<TransferManager> logger)
    {
        _settings = settings;
        _scanner = scanner;
        _hash = hash;
        _chunks = chunks;
        _client = client;
        _server = server;
        _repository = repository;
        _devices = devices;
        _logger = logger;

        _server.IncomingTransferRequested += OnIncomingTransferRequested;
        _server.IncomingTransferStateChanged += OnIncomingTransferStateChanged;
        _server.IncomingFileCompleted += OnIncomingFileCompleted;
    }

    public IReadOnlyList<TransferRecord> Transfers =>
        _records.Values.OrderByDescending(r => r.CreatedAt).ToList();

    public event EventHandler<TransferProgressSnapshot>? ProgressChanged;
    public event EventHandler<TransferRecord>? TransferAdded;
    public event EventHandler<TransferRecord>? TransferUpdated;

    // ------------------------------------------------------------------ 发送

    public Task<TransferRecord> CreateSendTransferAsync(DeviceInfo target, IReadOnlyList<string> paths,
        ConflictPolicy conflictPolicy = ConflictPolicy.Rename, CancellationToken cancellationToken = default)
        => CreateSendTransferAsync(new SendTransferOptions
        {
            Target = target,
            Paths = paths,
            ConflictPolicy = conflictPolicy,
        }, cancellationToken);

    public async Task<TransferRecord> CreateSendTransferAsync(SendTransferOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var target = options.Target;
        var paths = options.Paths;

        if (paths.Count == 0)
            throw new ArgumentException("未选择任何文件或文件夹", nameof(options));

        IReadOnlyList<ScannedEntry> entries;

        if (options.RelativePaths is { Count: > 0 } relativePaths)
        {
            // 同步场景：显式指定接收端相对路径，不再由扫描结果推导
            if (relativePaths.Count != paths.Count)
                throw new ArgumentException("RelativePaths 与 Paths 数量不一致", nameof(options));

            var list = new List<ScannedEntry>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                if (!File.Exists(paths[i])) continue;

                var info = new FileInfo(paths[i]);
                var relative = relativePaths[i].Replace('/', '\\').TrimStart('\\');
                var directory = Path.GetDirectoryName(relative) ?? string.Empty;

                list.Add(new ScannedEntry
                {
                    SourcePath = info.FullName,
                    RelativePath = directory,
                    FileName = Path.GetFileName(relative),
                    FileSize = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                });
            }

            entries = list;
        }
        else
        {
            entries = _scanner.ScanSelection(paths);
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("所选路径中没有任何可发送的文件。");

        var transferId = Guid.NewGuid().ToString();
        var isFolder = entries.Any(e => !string.IsNullOrEmpty(e.RelativePath));
        var rootName = options.SyncPairId is { Length: > 0 } pairId
            ? pairId
            : ResolveRootName(paths, isFolder);

        var record = new TransferRecord
        {
            TransferId = transferId,
            Direction = TransferDirection.Send,
            TransferType = isFolder ? TransferType.Folder : TransferType.File,
            RemoteDeviceId = target.DeviceId,
            RemoteDeviceName = target.DeviceName,
            LocalDeviceId = _devices.LocalDevice.DeviceId,
            State = TransferState.Pending,
            TotalSize = entries.Sum(e => e.FileSize),
            TotalFiles = entries.Count,
            CompletedFiles = 0,
            RootName = rootName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Register(record);

        var job = new SendJob
        {
            TransferId = transferId,
            Target = target,
            ConflictPolicy = options.ConflictPolicy,
            SyncPairId = options.SyncPairId,
            Cts = new CancellationTokenSource(),
        };

        var index = 0;
        foreach (var entry in entries)
        {
            job.Files.Add(new JobFile
            {
                FileIndex = index++,
                SourcePath = entry.SourcePath,
                RelativePath = entry.RelativePath,
                FileName = entry.FileName,
                FileSize = entry.FileSize,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
            });
        }

        _jobs[transferId] = job;

        _logger.LogInformation(
            "创建发送传输 {TransferId}: {Files} 个文件，共 {Size}，目标 {Device} ({Endpoint}){Sync}",
            transferId, record.TotalFiles, record.TotalSize.ToSizeString(), target.DeviceName, target.EndPoint,
            options.SyncPairId is { Length: > 0 } ? $"，同步关系 {options.SyncPairId}" : string.Empty);

        await _repository.UpsertTransferAsync(record, cancellationToken).ConfigureAwait(false);

        foreach (var file in job.Files)
        {
            await _repository.UpsertFileAsync(new TransferFileRecord
            {
                FileId = Guid.NewGuid().ToString(),
                TransferId = transferId,
                FileIndex = file.FileIndex,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                FileSize = file.FileSize,
                ChunkSize = _settings.Current.ChunkSize,
                TotalChunks = _chunks.CalculateTotalChunks(file.FileSize, _settings.Current.ChunkSize),
                State = TransferState.Pending,
                SourcePath = file.SourcePath,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
            }, cancellationToken).ConfigureAwait(false);
        }

        _ = Task.Run(() => RunSendAsync(job), CancellationToken.None);

        return record;
    }

    public async Task ResumeAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(transferId, out var running) && running.PauseGate is not null)
        {
            running.PauseGate.TrySetResult();
            running.PauseGate = null;
            await UpdateStateAsync(transferId, TransferState.Transferring, null, null, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("恢复传输 {TransferId}", transferId);
            return;
        }

        var record = await _repository.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            throw new InvalidOperationException($"找不到传输任务 {transferId}");

        if (record.Direction != TransferDirection.Send)
        {
            // 入站传输：由接收端服务器处理，并通过事件回推状态
            await _server.ResumeIncomingAsync(transferId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var device = _devices.Find(record.RemoteDeviceId);
        if (device is null)
        {
            await UpdateStateAsync(transferId, TransferState.Failed, ErrorCodes.TransferNotFound,
                "目标设备已从列表移除，请重新添加。", cancellationToken).ConfigureAwait(false);
            return;
        }

        var files = await _repository.GetFilesAsync(transferId, cancellationToken).ConfigureAwait(false);
        var job = new SendJob
        {
            TransferId = transferId,
            Target = device,
            Cts = new CancellationTokenSource(),
        };

        var missingSources = new List<string>();

        foreach (var file in files.OrderBy(f => f.FileIndex))
        {
            if (file.State == TransferState.Completed) continue;

            if (string.IsNullOrEmpty(file.SourcePath) || !File.Exists(file.SourcePath))
            {
                // 源文件已不在（被移动/删除）时不能假装完成，也不能静默跳过
                missingSources.Add(file.FileName);
                continue;
            }

            var jobFile = new JobFile
            {
                FileIndex = file.FileIndex,
                SourcePath = file.SourcePath,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                FileSize = file.FileSize,
                LastWriteTimeUtc = file.LastWriteTimeUtc ?? File.GetLastWriteTimeUtc(file.SourcePath),
                Sha256 = file.Sha256,
                // 沿用建立传输时的分块大小，避免用户中途改设置导致块号错位
                ChunkSize = file.ChunkSize,
            };

            foreach (var chunk in await _repository.GetCompletedChunksAsync(transferId, file.FileIndex,
                         cancellationToken).ConfigureAwait(false))
            {
                jobFile.CompletedChunks.Add(chunk);
            }

            job.Files.Add(jobFile);
        }

        if (missingSources.Count > 0)
        {
            await UpdateStateAsync(transferId, TransferState.Failed, ErrorCodes.FileNotFound,
                $"源文件已不存在，无法继续传输：{string.Join("、", missingSources.Take(3))}" +
                (missingSources.Count > 3 ? $" 等 {missingSources.Count} 个" : string.Empty),
                cancellationToken, force: true).ConfigureAwait(false);
            return;
        }

        if (job.Files.Count == 0)
        {
            await UpdateStateAsync(transferId, TransferState.Completed, null, null, cancellationToken,
                force: true).ConfigureAwait(false);
            return;
        }

        // 恢复前先让接收端离开 Cancelled/Paused：
        // 用户此前可能取消过（接收端置 Cancelled），或接收端暂停了该传输。
        // 不先复位的话 CreateTransfer 会被「该传输已被终止」拒绝、每个分块也会被
        // 「接收端已暂停该传输」挡下，任务再也无法通过「继续」完成。
        try
        {
            var resumed = await _client.ResumeAsync(device.IpAddress, device.Port, transferId,
                cancellationToken).ConfigureAwait(false);

            if (!resumed.Success)
            {
                _logger.LogDebug("接收端没有可复位的传输（正常，可能是新任务或是首次续传）: {TransferId} {Message}",
                    transferId, resumed.Message);
            }
        }
        catch (Exception ex)
        {
            // 通知失败不阻断：CreateTransfer 是幂等的，接收端若真的处于终态会在那时明确报错
            _logger.LogWarning(ex, "通知接收端恢复传输失败: {TransferId}", transferId);
        }

        // 记录可能仍停留在 Failed/Cancelled 等终态，而「终态不可被中间态覆盖」的守卫会挡住后续
        // Preparing/Transferring，导致界面一直显示旧状态与旧错误。这里显式重新入队。
        await UpdateStateAsync(transferId, TransferState.Queued, null, null, cancellationToken, force: true)
            .ConfigureAwait(false);

        _jobs[transferId] = job;
        _logger.LogInformation("恢复传输 {TransferId}: 剩余 {Count} 个文件", transferId, job.Files.Count);

        _ = Task.Run(() => RunSendAsync(job), CancellationToken.None);
        await Task.CompletedTask;
    }

    public async Task PauseAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(transferId, out var job))
        {
            // 当前 Chunk 允许完成，之后停止发送新 Chunk
            job.PauseGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await UpdateStateAsync(transferId, TransferState.Paused, null, null, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("暂停传输 {TransferId}", transferId);
            return;
        }

        // 入站传输：由接收端服务器处理，并通过事件回推状态
        await _server.PauseIncomingAsync(transferId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryRemove(transferId, out var job))
        {
            try
            {
                job.PauseGate?.TrySetResult();
                job.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 任务已结束
            }

            try
            {
                await _client.CancelAsync(job.Target.IpAddress, job.Target.Port, transferId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通知对端取消失败（本地仍按取消处理）: {TransferId}", transferId);
            }
        }
        else
        {
            await _server.CancelIncomingAsync(transferId, deletePartial: false, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("取消入站传输 {TransferId}（保留 .part 与续传元数据）", transferId);
            return;
        }

        await UpdateStateAsync(transferId, TransferState.Cancelled, ErrorCodes.CancelledByUser, null,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("取消传输 {TransferId}（保留 .part 与续传元数据）", transferId);
    }

    public async Task DeleteIncompleteAsync(string transferId, CancellationToken cancellationToken = default)
    {
        var files = await _repository.GetFilesAsync(transferId, cancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            if (file.State == TransferState.Completed) continue;

            if (!string.IsNullOrEmpty(file.SourcePath) && file.SourcePath.EndsWith(".part",
                    StringComparison.OrdinalIgnoreCase))
            {
                await _chunks.DeletePartFilesAsync(file.SourcePath, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(file.TargetPath))
            {
                await _chunks.DeletePartFilesAsync(file.TargetPath, cancellationToken).ConfigureAwait(false);
            }
        }

        await _repository.DeleteTransferAsync(transferId, cancellationToken).ConfigureAwait(false);

        if (_records.TryRemove(transferId, out var removed))
            TransferUpdated?.Invoke(this, removed);

        _logger.LogInformation("已删除未完成的传输与残留文件: {TransferId}", transferId);
    }

    public async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        var records = await _repository.GetRecentTransfersAsync(500, cancellationToken).ConfigureAwait(false);

        foreach (var record in records)
        {
            // 历史记录中处于活动状态的，说明上次是被强制结束的，标记为失败以便续传
            if (record.State is TransferState.Transferring or TransferState.Preparing or
                TransferState.Verifying or TransferState.WaitingApproval)
            {
                record.State = TransferState.Failed;
                record.ErrorCode = ErrorCodes.NetworkUnreachable;
                record.ErrorMessage = "上次运行意外中断，可点击继续以断点续传。";
                await _repository.UpsertTransferAsync(record, cancellationToken).ConfigureAwait(false);
            }

            _records[record.TransferId] = record;
        }

        _logger.LogInformation("已加载 {Count} 条历史传输记录", records.Count);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _repository.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var key in _records.Keys.ToList())
        {
            if (_jobs.ContainsKey(key)) continue;
            _records.TryRemove(key, out _);
        }

        _logger.LogInformation("已清空历史记录");
    }

    public async Task AutoResumePendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _repository.GetIncompleteTransfersAsync(cancellationToken).ConfigureAwait(false);

        foreach (var record in pending)
        {
            if (record.Direction != TransferDirection.Send) continue;
            if (record.State is TransferState.Paused) continue;

            _logger.LogInformation("自动恢复未完成的传输: {TransferId}", record.TransferId);

            try
            {
                await ResumeAsync(record.TransferId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自动恢复失败: {TransferId}", record.TransferId);
            }
        }
    }

    // ------------------------------------------------------------------ 发送主循环

    private async Task RunSendAsync(SendJob job)
    {
        var record = _records[job.TransferId];
        var speed = new SpeedCalculator();

        try
        {
            // 第一版：同时只跑 1 个发送任务
            await _sendSlot.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await UpdateStateAsync(job.TransferId, TransferState.Cancelled, ErrorCodes.CancelledByUser, null,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            await UpdateStateAsync(job.TransferId, TransferState.Preparing, null, null, job.Cts.Token)
                .ConfigureAwait(false);

            var transferredBase = job.Files.Sum(f => (long)f.CompletedChunks.Sum(c =>
                _chunks.GetChunkRange(f.FileSize, ChunkSizeFor(f), c).Length));

            long transferred = transferredBase;

            foreach (var file in job.Files.OrderBy(f => f.FileIndex))
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(job).ConfigureAwait(false);

                // 传给 SendSingleFileAsync 的基数必须排除「当前文件」自己的已传块：
                // 它会在内部按块范围重新累加自己的已传字节，若也计入基数就会重复计入
                // （表现为进度虚高：1.2 GB / 1.0 GB，且虚高值会被写进数据库）。
                var otherFilesBytes = job.Files
                    .Where(f => !ReferenceEquals(f, file))
                    .Sum(f => (long)f.CompletedChunks.Sum(c =>
                        _chunks.GetChunkRange(f.FileSize, ChunkSizeFor(f), c).Length));

                var ok = await SendSingleFileAsync(job, file, otherFilesBytes, value =>
                {
                    transferred = value;
                    speed.AddSample(transferred);
                    RaiseProgress(record, file, transferred, speed);
                }).ConfigureAwait(false);

                if (!ok)
                {
                    return;
                }

                record = _records[job.TransferId];
                record.CompletedFiles++;
                await PersistRecordAsync(record, CancellationToken.None).ConfigureAwait(false);
                RaiseUpdated(record);
            }

            await UpdateStateAsync(job.TransferId, TransferState.Completed, null, null, CancellationToken.None)
                .ConfigureAwait(false);

            var final = _records[job.TransferId];
            final.CompletedAt = DateTimeOffset.UtcNow;
            await PersistRecordAsync(final, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("传输完成 {TransferId}: {Files} 个文件，共 {Size}",
                job.TransferId, final.TotalFiles, final.TotalSize.ToSizeString());
        }
        catch (OperationCanceledException) when (!job.Cts.IsCancellationRequested)
        {
            // 不是用户取消，而是分块上传超时（客户端 linked CTS 到点）。
            // 必须区分开：否则网络故障会被显示成「已取消（用户取消）」，用户完全看不到真实原因。
            await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.Timeout,
                $"传输超时：目标设备 {job.Target.EndPoint} 长时间无响应。",
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogError("传输超时 {TransferId} -> {Target}", job.TransferId, job.Target.EndPoint);
        }
        catch (OperationCanceledException)
        {
            await UpdateStateAsync(job.TransferId, TransferState.Cancelled, ErrorCodes.CancelledByUser, null,
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("传输已取消 {TransferId}", job.TransferId);
        }
        catch (HttpRequestException ex)
        {
            await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.NetworkUnreachable,
                $"无法连接目标设备 {job.Target.EndPoint}：{ex.Message}", CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "传输失败（网络不可达）{TransferId}", job.TransferId);
        }
        catch (Exception ex)
        {
            await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.InternalError, ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "传输失败 {TransferId}", job.TransferId);
        }
        finally
        {
            try
            {
                _sendSlot.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已在上面的失败分支释放
            }

            if (_jobs.TryGetValue(job.TransferId, out var current) && ReferenceEquals(current, job))
                _jobs.TryRemove(job.TransferId, out _);

            job.Cts.Dispose();
        }
    }

    private int ChunkSize => _settings.Current.ChunkSize;

    /// <summary>该文件实际使用的分块大小：优先沿用记录里的值，未指定时才用当前设置。</summary>
    private int ChunkSizeFor(JobFile file) => file.ChunkSize > 0 ? file.ChunkSize : ChunkSize;

    /// <summary>发送单个文件；返回 false 表示整个传输应终止（失败 / 被拒 / 取消）。</summary>
    private async Task<bool> SendSingleFileAsync(SendJob job, JobFile file, long transferredSoFar,
        Action<long> reportProgress)
    {
        var ct = job.Cts.Token;
        var chunkSize = ChunkSizeFor(file);
        var totalChunks = _chunks.CalculateTotalChunks(file.FileSize, chunkSize);

        // 1. 计算 SHA-256（流式）
        if (string.IsNullOrEmpty(file.Sha256))
        {
            file.Sha256 = await _hash.ComputeFileHashAsync(file.SourcePath, null, ct).ConfigureAwait(false);
            _logger.LogDebug("已计算摘要 {File} = {Hash}", file.FileName, file.Sha256);
        }

        // 2. 建立传输 / 新增文件（幂等：相同 transferId + fileIndex 不会重复创建）
        var createResponse = await _client.CreateTransferAsync(job.Target.IpAddress, job.Target.Port,
            new CreateTransferRequest
            {
                TransferType = string.IsNullOrEmpty(file.RelativePath) ? "file" : "folder",
                TransferId = job.TransferId,
                FileName = file.FileName,
                RelativePath = file.RelativePath,
                FileSize = file.FileSize,
                Sha256 = file.Sha256,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                FileIndex = file.FileIndex,
                TotalFiles = job.Files.Count,
                TotalSize = _records[job.TransferId].TotalSize,
                RootName = _records[job.TransferId].RootName,
                ConflictPolicy = job.ConflictPolicy.ToString().ToLowerInvariant(),
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                SyncPairId = job.SyncPairId ?? string.Empty,
            }, ct).ConfigureAwait(false);

        if (!createResponse.Success)
        {
            var (state, code, message) = MapCreateFailure(createResponse);
            await UpdateStateAsync(job.TransferId, state, code, message, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogError("建立传输被拒绝 {TransferId}/{File}: {Code} {Message}",
                job.TransferId, file.FileName, code, message);
            return false;
        }

        // 3. 等待对端用户确认
        var approved = await WaitForApprovalAsync(job, ct).ConfigureAwait(false);
        if (!approved)
            return false;

        await UpdateStateAsync(job.TransferId, TransferState.Transferring, null, null, ct).ConfigureAwait(false);

        // 4. 查询已完成的 Chunk（断点续传）
        var status = await _client.GetTransferStatusAsync(job.Target.IpAddress, job.Target.Port,
            job.TransferId, file.FileIndex, ct).ConfigureAwait(false);

        // 分块几何必须与接收端一致，否则块号无法互相解释（偏移/长度全错位）
        if (status.Success && status.TotalChunks > 0 && status.TotalChunks != totalChunks)
        {
            await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.ChunkSizeMismatch,
                $"接收端的分块几何与本机不一致（对端 {status.TotalChunks} 块 / 本机 {totalChunks} 块），" +
                "请删除该任务后重新发送。", CancellationToken.None).ConfigureAwait(false);
            _logger.LogError("分块几何不一致，拒绝续传: {TransferId}/{File} 对端={Remote} 本机={Local}",
                job.TransferId, file.FileName, status.TotalChunks, totalChunks);
            return false;
        }

        // 接收端的位图才是权威：本机位图可能包含接收端已经丢弃的分块
        // （用户在接收端点了「删除未完成」、或手工删掉了 .part）。取并集会让本机
        // 跳过接收端其实没有的块，CompleteFile 永远报「仍有 N 个分块未到达」且本机位图只增不减，
        // 该任务从此再也无法通过「继续」完成。
        HashSet<int> completed;
        if (status.Success)
        {
            completed = new HashSet<int>(status.CompletedChunks.Where(c => c >= 0 && c < totalChunks));
        }
        else
        {
            // 只有状态查询本身失败时才退回本机位图
            completed = new HashSet<int>(file.CompletedChunks.Where(c => c >= 0 && c < totalChunks));
            _logger.LogWarning("查询接收端进度失败，暂用本机位图续传: {TransferId}/{File}",
                job.TransferId, file.FileName);
        }

        if (status.Sha256.Length > 0 &&
            !string.Equals(status.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // 对端已存在的同名传输内容不同，不能续传
            _logger.LogWarning("对端已存在内容不同的同名传输，重新上传: {TransferId}/{File}",
                job.TransferId, file.FileName);
            completed.Clear();
        }

        file.CompletedChunks.Clear();
        foreach (var chunk in completed) file.CompletedChunks.Add(chunk);

        await PersistFileAsync(job, file, TransferState.Transferring, null, ct).ConfigureAwait(false);

        // 5. 计算已传字节
        var sentBytes = completed.Sum(c => (long)_chunks.GetChunkRange(file.FileSize, chunkSize, c).Length);
        var current = transferredSoFar + sentBytes;
        reportProgress(current);

        // 6. 上传缺失的 Chunk
        var buffer = new byte[chunkSize];

        for (var index = 0; index < totalChunks; index++)
        {
            ct.ThrowIfCancellationRequested();

            if (completed.Contains(index)) continue;

            await WaitWhilePausedAsync(job).ConfigureAwait(false);

            var (offset, length) = _chunks.GetChunkRange(file.FileSize, chunkSize, index);

            if (length > 0)
            {
                await using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read,
                    AppConstants.FileReadSharing, AppConstants.StreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                source.Seek(offset, SeekOrigin.Begin);

                var read = 0;
                while (read < length)
                {
                    var got = await source.ReadAsync(buffer.AsMemory(read, length - read), ct)
                        .ConfigureAwait(false);
                    if (got <= 0) break;
                    read += got;
                }

                if (read != length)
                {
                    await UpdateStateAsync(job.TransferId, TransferState.Failed,
                        ErrorCodes.SourceChangedDuringTransfer,
                        $"读取源文件失败：{file.FileName} 在传输过程中被修改或删除。",
                        CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
            }

            var upload = await UploadWithRetryAsync(job, file, index, buffer, length, ct)
                .ConfigureAwait(false);

            if (!upload)
                return false;

            completed.Add(index);
            file.CompletedChunks.Add(index);
            await _repository.MarkChunkCompletedAsync(job.TransferId, file.FileIndex, index, ct)
                .ConfigureAwait(false);

            current += length;
            reportProgress(current);

            await PersistRecordProgressAsync(job.TransferId, current, ct).ConfigureAwait(false);
        }

        // 7. 校验源文件未在传输过程中被修改
        if (!IsSourceUnchanged(file))
        {
            await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.SourceChangedDuringTransfer,
                $"源文件在传输过程中发生变化：{file.FileName}", CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        // 8. 通知对端完成 → 对端做 SHA-256 校验并改名
        await UpdateStateAsync(job.TransferId, TransferState.Verifying, null, null, ct).ConfigureAwait(false);

        var complete = await _client.CompleteFileAsync(job.Target.IpAddress, job.Target.Port, job.TransferId,
            new CompleteTransferRequest { FileIndex = file.FileIndex, Sha256 = file.Sha256 }, ct)
            .ConfigureAwait(false);

        if (!complete.Success || !complete.Verified)
        {
            await UpdateStateAsync(job.TransferId, TransferState.VerificationFailed,
                complete.ErrorCode ?? ErrorCodes.HashMismatch,
                complete.Message ?? $"文件校验失败：{file.FileName}", CancellationToken.None).ConfigureAwait(false);
            _logger.LogError("文件校验失败 {TransferId}/{File}: {Message}", job.TransferId, file.FileName,
                complete.Message);
            return false;
        }

        await PersistFileAsync(job, file, TransferState.Completed, null, ct).ConfigureAwait(false);
        _logger.LogInformation("文件传输完成 {TransferId}/{File} -> {Path}", job.TransferId, file.FileName,
            complete.SavedPath);

        return true;
    }

    private async Task<bool> UploadWithRetryAsync(SendJob job, JobFile file, int chunkIndex, byte[] buffer,
        int length, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // 每次尝试都新建流：HttpRequestMessage 释放时会连同 StreamContent 一起关闭底层流，
                // 复用同一个 MemoryStream 会让第 2 次尝试在设置 Position 时抛 ObjectDisposedException
                // （异常不被下面的 catch 过滤，直接冒泡成 Failed/INTERNAL_ERROR）——等于重试从未生效。
                using var stream = new MemoryStream(buffer, 0, length, writable: false);

                var response = await _client.UploadChunkAsync(job.Target.IpAddress, job.Target.Port,
                    job.TransferId, file.FileIndex, chunkIndex, stream, length, ct).ConfigureAwait(false);

                if (response.Success) return true;

                if (response.ErrorCode == ErrorCodes.InsufficientDiskSpace ||
                    response.ErrorCode == ErrorCodes.PathEscapeDetected ||
                    response.ErrorCode == ErrorCodes.HashMismatch)
                {
                    await UpdateStateAsync(job.TransferId, TransferState.Failed, response.ErrorCode,
                        response.Message, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                _logger.LogWarning("Chunk 上传失败（第 {Attempt}/{Max} 次）{TransferId}/{File} #{Index}: {Code} {Message}",
                    attempt, maxAttempts, job.TransferId, file.FileName, chunkIndex, response.ErrorCode,
                    response.Message);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Chunk 上传网络错误，准备重试 {TransferId}/{File} #{Index}",
                    job.TransferId, file.FileName, chunkIndex);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
        }

        await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.NetworkUnreachable,
            $"分块 {chunkIndex} 上传失败，已重试 3 次。", CancellationToken.None).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> WaitForApprovalAsync(SendJob job, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(job).ConfigureAwait(false);

            var status = await _client.GetTransferStatusAsync(job.Target.IpAddress, job.Target.Port,
                job.TransferId, 0, ct).ConfigureAwait(false);

            switch (TransferStateExtensions.FromWireString(status.State))
            {
                case TransferState.Transferring:
                case TransferState.Preparing:
                case TransferState.Verifying:
                case TransferState.Completed:
                    return true;

                case TransferState.Rejected:
                    await UpdateStateAsync(job.TransferId, TransferState.Rejected, ErrorCodes.RejectedByUser,
                        "对方拒绝了本次传输。", CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("对端拒绝接收 {TransferId}", job.TransferId);
                    return false;

                case TransferState.Cancelled:
                    await UpdateStateAsync(job.TransferId, TransferState.Cancelled, ErrorCodes.CancelledByUser,
                        "对方取消了本次传输。", CancellationToken.None).ConfigureAwait(false);
                    return false;

                case TransferState.Failed:
                    await UpdateStateAsync(job.TransferId, TransferState.Failed, status.ErrorCode,
                        status.Message ?? "对方处理传输时发生错误。", CancellationToken.None).ConfigureAwait(false);
                    return false;
            }

            await UpdateStateAsync(job.TransferId, TransferState.WaitingApproval, null, null, ct)
                .ConfigureAwait(false);

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        await UpdateStateAsync(job.TransferId, TransferState.Failed, ErrorCodes.Timeout,
            "等待对方确认超时（10 分钟）。", CancellationToken.None).ConfigureAwait(false);
        return false;
    }

    private static (TransferState State, string Code, string Message) MapCreateFailure(CreateTransferResponse response)
    {
        return response.ErrorCode switch
        {
            ErrorCodes.RejectedByUser => (TransferState.Rejected, ErrorCodes.RejectedByUser,
                response.Message ?? "对方拒绝了本次传输。"),
            ErrorCodes.DeviceNotTrusted => (TransferState.Failed, ErrorCodes.DeviceNotTrusted,
                response.Message ?? "对方未信任本机，请先完成设备配对。"),
            ErrorCodes.InsufficientDiskSpace => (TransferState.Failed, ErrorCodes.InsufficientDiskSpace,
                response.Message ?? "对方磁盘空间不足。"),
            ErrorCodes.PathEscapeDetected => (TransferState.Failed, ErrorCodes.PathEscapeDetected,
                response.Message ?? "文件路径被对方拒绝。"),
            ErrorCodes.ProtocolIncompatible => (TransferState.Failed, ErrorCodes.ProtocolIncompatible,
                response.Message ?? "目标设备 LAN Transfer 版本不兼容。"),
            _ => (TransferState.Failed, response.ErrorCode ?? ErrorCodes.InternalError,
                response.Message ?? "对方拒绝建立传输。"),
        };
    }

    private static bool IsSourceUnchanged(JobFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            if (!info.Exists) return false;
            if (info.Length != file.FileSize) return false;
            return Math.Abs((info.LastWriteTimeUtc - file.LastWriteTimeUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitWhilePausedAsync(SendJob job)
    {
        while (true)
        {
            var gate = Volatile.Read(ref job.PauseGate);
            if (gate is null) return;
            await gate.Task.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ 接收侧事件
    // 说明：接收端（TransferServer）是入站传输的唯一权威来源与唯一持久化写入者，
    // 此处只维护 UI 用的内存镜像，避免同一 TransferId 被两处写入而互相覆盖。

    private void OnIncomingTransferRequested(object? sender, IncomingTransferEventArgs e)
    {
        if (_records.TryGetValue(e.TransferId, out var existing))
        {
            ApplyIncoming(existing, e);
            RaiseUpdated(existing);
            return;
        }

        var record = new TransferRecord
        {
            TransferId = e.TransferId,
            Direction = TransferDirection.Receive,
            TransferType = e.TransferType,
            RemoteDeviceId = e.RemoteDeviceId,
            RemoteDeviceName = e.RemoteDeviceName,
            LocalDeviceId = _devices.LocalDevice.DeviceId,
            State = e.State,
            TotalSize = e.TotalSize,
            TransferredSize = e.TransferredSize,
            TotalFiles = e.TotalFiles,
            RootName = e.RootName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Register(record);

        _logger.LogInformation("收到传输请求 {TransferId}: 来自 {Device}，{Files} 个文件，共 {Size}",
            e.TransferId, e.RemoteDeviceName, e.TotalFiles, e.TotalSize.ToSizeString());
    }

    private void OnIncomingTransferStateChanged(object? sender, IncomingTransferEventArgs e)
    {
        if (!_records.TryGetValue(e.TransferId, out var record))
        {
            OnIncomingTransferRequested(sender, e);
            return;
        }

        ApplyIncoming(record, e);
        RaiseUpdated(record);

        ProgressChanged?.Invoke(this, new TransferProgressSnapshot
        {
            TransferId = record.TransferId,
            State = record.State,
            TotalSize = record.TotalSize,
            TransferredSize = record.TransferredSize,
            TotalFiles = record.TotalFiles,
            CompletedFiles = record.CompletedFiles,
            ErrorCode = record.ErrorCode,
            ErrorMessage = record.ErrorMessage,
        });
    }

    private void OnIncomingFileCompleted(object? sender, IncomingFileCompletedEventArgs e)
    {
        if (!_records.TryGetValue(e.TransferId, out var record)) return;

        lock (_recordLock)
        {
            record.CompletedFiles++;
            record.TransferredSize += e.FileSize;
        }

        RaiseUpdated(record);
    }

    private void ApplyIncoming(TransferRecord record, IncomingTransferEventArgs e)
    {
        lock (_recordLock)
        {
            record.State = e.State;
            if (e.TotalSize > 0) record.TotalSize = e.TotalSize;
            if (e.TotalFiles > 0) record.TotalFiles = e.TotalFiles;
            if (e.TransferredSize > 0) record.TransferredSize = e.TransferredSize;
            if (!string.IsNullOrEmpty(e.RootName)) record.RootName = e.RootName;

            record.ErrorCode = e.ErrorCode;
            record.ErrorMessage = e.ErrorMessage;

            if (e.State == TransferState.Completed)
                record.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    // ------------------------------------------------------------------ 记录维护

    private void Register(TransferRecord record)
    {
        _records[record.TransferId] = record;
        TransferAdded?.Invoke(this, record);
        RaiseUpdated(record);
    }

    private async Task UpdateStateAsync(string transferId, TransferState state, string? errorCode,
        string? errorMessage, CancellationToken cancellationToken, bool force = false)
    {
        if (!_records.TryGetValue(transferId, out var record)) return;

        lock (_recordLock)
        {
            // 终态不可被后续的中间态覆盖（防止迟到的回调把已完成的任务改回「传输中」）。
            // 但「用户主动继续」是显式意图，必须放行——否则恢复后状态会一直停在 Failed/Cancelled：
            // 界面显示旧错误、CanPause/CanCancel 反复失效，等待审批/计算哈希这类无进度事件的阶段
            // 甚至连取消都点不了。
            if (!force && record.State.IsTerminal() && !state.IsTerminal() && state != TransferState.Paused)
                return;

            record.State = state;
            if (errorCode is not null) record.ErrorCode = errorCode;
            if (errorMessage is not null) record.ErrorMessage = errorMessage;

            // 重新入队/重新开始时清掉上一次的失败信息，避免界面一直挂着旧错误
            if (force && !state.IsTerminal())
            {
                record.ErrorCode = null;
                record.ErrorMessage = null;
                record.CompletedAt = null;
            }

            if (state == TransferState.Completed)
            {
                record.CompletedAt = DateTimeOffset.UtcNow;
                record.TransferredSize = record.TotalSize;
                record.ErrorCode = null;
                record.ErrorMessage = null;
            }
        }

        await PersistRecordAsync(record, cancellationToken).ConfigureAwait(false);
        RaiseUpdated(record);

        ProgressChanged?.Invoke(this, new TransferProgressSnapshot
        {
            TransferId = record.TransferId,
            State = record.State,
            TotalSize = record.TotalSize,
            TransferredSize = record.TransferredSize,
            TotalFiles = record.TotalFiles,
            CompletedFiles = record.CompletedFiles,
            ErrorCode = record.ErrorCode,
            ErrorMessage = record.ErrorMessage,
        });
    }

    private async Task PersistRecordProgressAsync(string transferId, long transferred,
        CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(transferId, out var record)) return;

        lock (_recordLock)
        {
            record.TransferredSize = transferred;
        }

        await PersistRecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistRecordAsync(TransferRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.UpsertTransferAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "持久化传输记录失败: {TransferId}", record.TransferId);
        }
    }

    private async Task PersistFileAsync(SendJob job, JobFile file, TransferState state, string? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.UpsertFileAsync(new TransferFileRecord
            {
                FileId = Guid.NewGuid().ToString(),
                TransferId = job.TransferId,
                FileIndex = file.FileIndex,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                FileSize = file.FileSize,
                Sha256 = file.Sha256,
                // 记录该文件真正使用的分块几何：续传必须沿用它，
                // 不能用「当前设置」（用户中途改分块大小会让旧块号失去意义）
                ChunkSize = ChunkSizeFor(file),
                TotalChunks = _chunks.CalculateTotalChunks(file.FileSize, ChunkSizeFor(file)),
                CompletedChunks = file.CompletedChunks.Count,
                State = state,
                SourcePath = file.SourcePath,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "持久化文件记录失败: {TransferId}/{File}", job.TransferId, file.FileName);
        }
    }

    private void RaiseProgress(TransferRecord record, JobFile file, long transferred, SpeedCalculator speed)
    {
        var total = record.TotalSize;
        var remaining = Math.Max(0, total - transferred);

        ProgressChanged?.Invoke(this, new TransferProgressSnapshot
        {
            TransferId = record.TransferId,
            State = TransferState.Transferring,
            TotalSize = total,
            TransferredSize = transferred,
            TotalFiles = record.TotalFiles,
            CompletedFiles = record.CompletedFiles,
            CurrentFileName = file.FileName,
            BytesPerSecond = speed.BytesPerSecond,
            Eta = speed.EstimateEta(remaining),
        });
    }

    private void RaiseUpdated(TransferRecord record) => TransferUpdated?.Invoke(this, record);

    private static string ResolveRootName(IReadOnlyList<string> paths, bool isFolder)
    {
        if (!isFolder && paths.Count == 1)
            return Path.GetFileName(paths[0]);

        if (paths.Count == 1)
            return new DirectoryInfo(paths[0]).Name;

        return $"{Path.GetFileName(paths[0])} 等 {paths.Count} 项";
    }

    private sealed class SendJob
    {
        public string TransferId { get; init; } = string.Empty;
        public DeviceInfo Target { get; init; } = new();
        public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Rename;

        /// <summary>同步场景下指定接收端的写入根（同步关系 ID）。</summary>
        public string? SyncPairId { get; init; }
        public List<JobFile> Files { get; } = new();
        public CancellationTokenSource Cts { get; init; } = new();

        /// <summary>非 null 表示处于暂停态；完成后置回 null。</summary>
        public TaskCompletionSource? PauseGate;
    }

    private sealed class JobFile
    {
        public int FileIndex { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public DateTimeOffset LastWriteTimeUtc { get; init; }
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>
        /// 该文件使用的分块大小。续传时必须沿用建立传输时的值：
        /// 用户中途改了「分块大小」设置后，若用新值重新解释旧的块号，
        /// 偏移/长度会整体错位（写入长度不符 → 抛异常或写错位置 → 校验失败且 .part 已损坏）。
        /// 0 表示使用当前设置（新建传输时）。
        /// </summary>
        public int ChunkSize { get; init; }

        public HashSet<int> CompletedChunks { get; } = new();
    }
}
