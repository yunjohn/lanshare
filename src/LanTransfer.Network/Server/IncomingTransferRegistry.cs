using System.Collections.Concurrent;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Server;

/// <summary>接收端单个文件的落盘状态。</summary>
public sealed class IncomingFileState
{
    public int FileIndex { get; set; }
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public TransferState State { get; set; } = TransferState.WaitingApproval;

    /// <summary>最终目标路径（已按重名策略解析）。</summary>
    public string FinalPath { get; set; } = string.Empty;

    /// <summary>临时文件路径（filename.ext.part）。</summary>
    public string PartPath { get; set; } = string.Empty;

    public HashSet<int> Completed { get; } = new();
    public long ReceivedBytes { get; set; }
    public DateTimeOffset? LastWriteTimeUtc { get; set; }
}

/// <summary>接收端一个传输任务的状态。</summary>
public sealed class IncomingTransferState
{
    public string TransferId { get; set; } = string.Empty;
    public string RemoteDeviceId { get; set; } = string.Empty;
    public string RemoteDeviceName { get; set; } = string.Empty;
    public string RootName { get; set; } = string.Empty;
    public TransferType TransferType { get; set; } = TransferType.File;
    public TransferState State { get; set; } = TransferState.WaitingApproval;
    public long TotalSize { get; set; }
    public long TransferredSize { get; set; }
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public string DownloadRoot { get; set; } = string.Empty;

    /// <summary>同步关系 ID（空表示普通传输）。</summary>
    public string SyncPairId { get; set; } = string.Empty;

    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Rename;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 最后一次活动时间（收分块 / 状态变更）。用于回收**终态**传输的内存状态：
    /// 此前终态条目永远留在注册表里，长时间运行会持续累积（内存、以及被遗忘的会话）。
    /// </summary>
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    public bool Approved { get; set; }

    public TaskCompletionSource<bool> Approval { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 是否仍在等待本机用户确认（= 还需要弹确认框、审批任务还在等应答）。
    ///
    /// <para>
    /// 判定必须是「审批任务未完成 <b>且</b> 未进入终态」：
    /// </para>
    /// <list type="bullet">
    /// <item>只看 <c>Approval.Task.IsCompleted</c> 不够 —— 审批流程超时（10 分钟）后状态已变成 Failed，
    /// 但那个 TaskCompletionSource 永远不会完成，几台设备各超时几次就会把「待确认」额度永久占满。</item>
    /// <item>只看状态也不够 —— 自动放行的传输（可信设备 + 设置，或同步关系已授权）会立刻变成
    /// Transferring，它根本不需要用户点任何东西。</item>
    /// </list>
    /// </summary>
    public bool IsAwaitingUserDecision => !Approval.Task.IsCompleted && !State.IsTerminal();

    /// <summary>审批等待流程是否已经启动（0/1，配合 <see cref="TryBeginApprovalFlow"/> 使用）。</summary>
    private int _approvalFlowStarted;

    /// <summary>
    /// 抢占式地声明「由我来启动这个传输的审批等待流程」。
    ///
    /// <para>
    /// 为什么需要：发送端是**按文件**调用 /transfers 的（同一个 transferId 传 N 个文件就调 N 次），
    /// 而每次调用在「还没等到用户应答」时都会走到启动审批流程那一步。
    /// 没有这个闸门，一个 100 个文件的目录就会同时在等待队列里挂 100 个
    /// <c>WaitAsync</c> 任务，每个都带一个 10 分钟的 <c>CancellationTokenSource</c> 定时器。
    /// </para>
    /// </summary>
    public bool TryBeginApprovalFlow() => Interlocked.Exchange(ref _approvalFlowStarted, 1) == 0;

    public ConcurrentDictionary<int, IncomingFileState> Files { get; } = new();

    public object Gate { get; } = new();
}

/// <summary>
/// 接收端传输注册表：负责落盘、分块写入、断点续传状态、磁盘空间检查、路径安全与完整性校验。
/// 这是入站传输的唯一权威状态源。
/// </summary>
public sealed class IncomingTransferRegistry
{
    /// <summary>终态传输的内存状态保留时长（之后回收，数据库里的历史记录不受影响）。</summary>
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(30);

    /// <summary>两次回收扫描之间的最小间隔（回收在登记文件时顺带做，避免额外定时器）。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 同一台设备允许同时处于「等待本机用户确认」状态的传输数。
    /// 发送端同时只跑一个发送任务，正常情况恒为 1；留 3 是给「对端重发 / 多文件重试」的余量。
    /// </summary>
    private const int MaxPendingApprovalsPerDevice = 3;

    /// <summary>
    /// 全局「等待本机用户确认」的传输数上限。
    /// /transfers 对未配对设备开放，每次登记都会弹一个模态确认框并留下一个最长等 10 分钟的审批任务，
    /// 没有上限时局域网里任何人都能用不同的 transferId 把界面刷满、把内存吃光。
    /// </summary>
    private const int MaxPendingApprovalsTotal = 16;

    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    private readonly ISettingsService _settings;
    private readonly ISafePathResolver _paths;
    private readonly IChunkManager _chunks;
    private readonly IHashService _hash;
    private readonly ITransferRepository _repository;
    private readonly ITrustStore _trustStore;
    private readonly ISyncPathProvider? _syncPaths;
    private readonly ILogger<IncomingTransferRegistry> _logger;

    private readonly ConcurrentDictionary<string, IncomingTransferState> _transfers =
        new(StringComparer.OrdinalIgnoreCase);

    public IncomingTransferRegistry(
        ISettingsService settings,
        ISafePathResolver paths,
        IChunkManager chunks,
        IHashService hash,
        ITransferRepository repository,
        ITrustStore trustStore,
        ILogger<IncomingTransferRegistry> logger,
        ISyncPathProvider? syncPaths = null,
        TimeSpan? terminalRetention = null,
        TimeSpan? sweepInterval = null)
    {
        _settings = settings;
        _paths = paths;
        _chunks = chunks;
        _hash = hash;
        _repository = repository;
        _trustStore = trustStore;
        _logger = logger;
        _syncPaths = syncPaths;
        _terminalRetention = terminalRetention ?? TerminalRetention;
        _sweepInterval = sweepInterval ?? SweepInterval;
    }

    private readonly TimeSpan _terminalRetention;
    private readonly TimeSpan _sweepInterval;

    public IReadOnlyCollection<IncomingTransferState> ActiveTransfers => _transfers.Values.ToList();

    public IncomingTransferState? Find(string transferId) =>
        _transfers.TryGetValue(transferId, out var state) ? state : null;

    /// <summary>
    /// 回收「已进入终态且长时间没有活动」的传输内存状态。
    /// 只清理注册表里的条目：数据库中的传输历史与磁盘上的 .part 都保留
    /// （.part 是断点续传与排查所需，历史记录是用户要看的）。
    /// </summary>
    private void SweepTerminalTransfers()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < _sweepInterval) return;
        _lastSweep = now;

        var removed = 0;

        foreach (var (id, transfer) in _transfers)
        {
            if (!transfer.State.IsTerminal()) continue;
            if (now - transfer.LastActivity < _terminalRetention) continue;

            if (_transfers.TryRemove(id, out _)) removed++;
        }

        if (removed > 0)
            _logger.LogInformation("已回收 {Count} 个终态传输的内存状态（历史记录与临时文件保留）", removed);
    }

    /// <summary>
    /// 统计「还会弹确认框、且仍在等待用户应答」的传输数量（总 / 本设备）。
    /// 判定口径见 <see cref="IncomingTransferState.IsAwaitingUserDecision"/>。
    /// </summary>
    private (int Device, int Total) CountPendingApprovals(string remoteDeviceId)
    {
        var device = 0;
        var total = 0;

        foreach (var transfer in _transfers.Values)
        {
            if (!transfer.IsAwaitingUserDecision) continue;

            total++;

            if (string.Equals(transfer.RemoteDeviceId, remoteDeviceId, StringComparison.OrdinalIgnoreCase))
                device++;
        }

        return (device, total);
    }

    /// <summary>用户对待确认传输的应答。</summary>
    public void RespondToApproval(string transferId, bool approve)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer)) return;

        transfer.Approved = approve;
        transfer.Approval.TrySetResult(approve);

        if (!approve)
        {
            transfer.State = TransferState.Rejected;
            transfer.ErrorCode = ErrorCodes.RejectedByUser;
            transfer.ErrorMessage = "本机用户拒绝了本次传输。";
            _logger.LogInformation("用户拒绝了传输请求 {TransferId}", transferId);
        }
    }

    // ---------------------------------------------------------------- 建立传输 / 新增文件

    public async Task<(CreateTransferResponse Response, IncomingTransferState? Transfer)> CreateOrGetFileAsync(
        CreateTransferRequest request, string remoteDeviceId, string remoteDeviceName, bool isTrusted,
        CancellationToken cancellationToken)
    {
        // 顺带回收长时间处于终态的传输内存状态（限频，不需要额外定时器）
        SweepTerminalTransfers();

        if (string.IsNullOrWhiteSpace(request.TransferId) || string.IsNullOrWhiteSpace(request.FileName))
        {
            return (Fail(ErrorCodes.BadRequest, "缺少 transferId 或 fileName。"), null);
        }

        // 分块几何参数必须先在入口校验：request.ChunkSize <= 0 会让 CalculateTotalChunks 抛
        // ArgumentOutOfRangeException → HTTP 500，同时把会话残留在 _transfers 里。
        if (!AppConstants.AllowedChunkSizes.Contains(request.ChunkSize))
        {
            _logger.LogWarning("拒绝非法分块大小 {ChunkSize}", request.ChunkSize);
            return (Fail(ErrorCodes.ChunkSizeMismatch,
                $"分块大小非法：{request.ChunkSize}。允许值为 " +
                $"{string.Join(" / ", AppConstants.AllowedChunkSizes)} 字节。"), null);
        }

        // 同步场景：写入根必须来自已登记且已获用户授权的同步关系，远程无法任意指定目录
        var downloadRoot = _settings.Current.DownloadPath;

        if (!string.IsNullOrWhiteSpace(request.SyncPairId))
        {
            var syncRoot = _syncPaths is null
                ? null
                : await _syncPaths.GetLocalPathAsync(request.SyncPairId, cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(syncRoot))
            {
                _logger.LogWarning("拒绝未知同步关系的写入请求: {SyncPairId}", request.SyncPairId);
                return (Fail(ErrorCodes.SyncPairNotFound,
                    "接收端不存在该同步关系，或同步关系尚未获得授权。"), null);
            }

            downloadRoot = syncRoot;
        }

        // 未决的确认请求必须有上限。本接口对未配对设备也开放，而每一次「新传输」登记都会
        // 弹一个模态确认框、并留下一个最长等 10 分钟的审批任务：没有这道闸门时，
        // 局域网里任何人都能用脚本换着 transferId 刷屏（界面被确认框淹没、内存持续增长）。
        // 只对「新传输」判定：同一传输的后续文件（多文件发送）不算新增请求，
        // 否则一个大目录传到一半就会把自己挡住。
        if (!_transfers.ContainsKey(request.TransferId))
        {
            var (devicePending, totalPending) = CountPendingApprovals(remoteDeviceId);

            if (devicePending >= MaxPendingApprovalsPerDevice || totalPending >= MaxPendingApprovalsTotal)
            {
                _logger.LogWarning(
                    "拒绝登记新的传输请求：待用户确认的传输过多（本设备 {Device} {DevicePending}，全局 {TotalPending}）",
                    remoteDeviceId, devicePending, totalPending);

                return (Fail(ErrorCodes.TooManyPendingRequests,
                    "本机还有较多传输请求等待确认，请先在接收端处理已弹出的确认提示后重试。"), null);
            }
        }

        var transfer = _transfers.GetOrAdd(request.TransferId, _ => new IncomingTransferState
        {
            TransferId = request.TransferId,
            RemoteDeviceId = remoteDeviceId,
            RemoteDeviceName = remoteDeviceName,
            RootName = string.IsNullOrEmpty(request.RootName) ? request.FileName : request.RootName,
            TransferType = string.Equals(request.TransferType, "folder", StringComparison.OrdinalIgnoreCase)
                ? TransferType.Folder
                : TransferType.File,
            TotalSize = request.TotalSize > 0 ? request.TotalSize : request.FileSize,
            TotalFiles = request.TotalFiles > 0 ? request.TotalFiles : 1,
            DownloadRoot = downloadRoot,
            SyncPairId = request.SyncPairId ?? string.Empty,
            ConflictPolicy = ParsePolicy(request.ConflictPolicy),
            State = TransferState.WaitingApproval,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        if (transfer.State is TransferState.Rejected or TransferState.Cancelled)
        {
            return (new CreateTransferResponse
            {
                Success = false,
                TransferId = transfer.TransferId,
                State = transfer.State.ToWireString(),
                ErrorCode = transfer.ErrorCode ?? ErrorCodes.TransferStateConflict,
                Message = transfer.ErrorMessage ?? "该传输已被终止。",
            }, transfer);
        }

        // 1. 路径安全校验（先于任何磁盘操作）
        var combinedRelative = string.IsNullOrEmpty(request.RelativePath)
            ? request.FileName
            : Path.Combine(request.RelativePath, request.FileName);

        if (!_paths.IsValidFileName(request.FileName, out var reason))
        {
            _logger.LogWarning("拒绝非法文件名 {FileName}: {Reason}", request.FileName, reason);
            return (Fail(ErrorCodes.InvalidFileName, $"文件名非法：{reason}"), transfer);
        }

        if (!_paths.TryResolve(transfer.DownloadRoot, combinedRelative, out var resolvedPath))
        {
            _logger.LogWarning("检测到路径穿越，已拒绝: {Relative}", combinedRelative);
            return (Fail(ErrorCodes.PathEscapeDetected,
                "远程提供的文件路径试图逃逸接收目录，已拒绝。"), transfer);
        }

        // 「跳过」重名策略：目标已存在时不接收该文件。
        // ResolveConflictName 对 Skip 也返回原路径（与 Overwrite 相同），若不在这里拦住，
        // 收完分块后的「原子替换」会先删掉已有文件再改名——正好是选「跳过」时最不想要的结果。
        // 只在首次登记该文件时判定，避免续传途中目标文件恰好被创建而中断续传。
        if (!transfer.Files.ContainsKey(request.FileIndex) &&
            transfer.ConflictPolicy == ConflictPolicy.Skip &&
            (File.Exists(resolvedPath) || Directory.Exists(resolvedPath)))
        {
            _logger.LogInformation("目标已存在，按「跳过」策略不接收该文件: {Path}", resolvedPath);
            return (Fail(ErrorCodes.FileExistsSkipped,
                $"目标文件已存在，已按「跳过」策略不接收：{request.FileName}"), transfer);
        }

        // 2. 复用已存在的文件记录（断点续传 / 同一传输的后续文件）
        IncomingFileState? file;
        lock (transfer.Gate)
        {
            if (transfer.Files.TryGetValue(request.FileIndex, out var existing))
            {
                file = existing;
                if (file.FileSize != request.FileSize)
                {
                    // 内容规模变了，不能续传，重新开始
                    _logger.LogWarning("同一 FileIndex 的尺寸发生变化，重新开始: {TransferId}/{Index}",
                        transfer.TransferId, request.FileIndex);
                    file.FileSize = request.FileSize;
                    file.TotalChunks = _chunks.CalculateTotalChunks(request.FileSize, request.ChunkSize);
                    file.Completed.Clear();
                    file.ReceivedBytes = 0;
                }
            }
            else
            {
                file = new IncomingFileState
                {
                    FileIndex = request.FileIndex,
                    FileId = Guid.NewGuid().ToString(),
                    FileName = request.FileName,
                    RelativePath = request.RelativePath ?? string.Empty,
                    FileSize = request.FileSize,
                    Sha256 = request.Sha256 ?? string.Empty,
                    ChunkSize = request.ChunkSize > 0 ? request.ChunkSize : AppConstants.DefaultChunkSize,
                    TotalChunks = _chunks.CalculateTotalChunks(request.FileSize, request.ChunkSize),
                    LastWriteTimeUtc = request.LastWriteTimeUtc,
                    State = transfer.State,
                };

                file.FinalPath = _paths.ResolveConflictName(resolvedPath, transfer.ConflictPolicy,
                    transfer.RemoteDeviceName);

                // 临时文件名里带上 transferId：两台设备（或同一设备两个传输）同时发同名文件时，
                // ResolveConflictName 只看正式文件是否存在，两者会解析到同一个 FinalPath 与同一个
                // `.part`，并发写入必然互相踩（共享冲突或内容交错 → 校验失败）。
                // 用 transferId 区分后每个传输有自己的临时文件，互不干扰。
                // 注意只用 transferId、不用 FileId：FileId 每次登记都会重新生成，
                // 用它会让「重启后继续同一个传输」找不到原来的临时文件，断点全丢。
                file.PartPath = BuildPartPath(file.FinalPath, transfer.TransferId);

                transfer.Files[request.FileIndex] = file;
            }
        }

        // 3. 磁盘空间检查（按整个传输的剩余所需空间计算）
        var required = transfer.Files.Values.Sum(f => Math.Max(0, f.FileSize - f.ReceivedBytes));
        var available = _paths.GetAvailableFreeSpace(transfer.DownloadRoot);

        if (available >= 0 && required > available)
        {
            transfer.State = TransferState.Failed;
            transfer.ErrorCode = ErrorCodes.InsufficientDiskSpace;
            transfer.ErrorMessage =
                $"接收目录磁盘空间不足：需要 {required.ToSizeString()}，可用 {available.ToSizeString()}。";

            _logger.LogError("磁盘空间不足: 需要 {Required} 可用 {Available} ({Path})",
                required, available, transfer.DownloadRoot);

            return (new CreateTransferResponse
            {
                Success = false,
                TransferId = transfer.TransferId,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.InsufficientDiskSpace,
                Message = transfer.ErrorMessage,
                Details = new Dictionary<string, string>
                {
                    ["requiredBytes"] = required.ToString(),
                    ["availableBytes"] = available.ToString(),
                    ["downloadRoot"] = transfer.DownloadRoot,
                },
            }, transfer);
        }

        // 4. 创建目录并预创建 .part（失败必须明确反馈，不能静默）
        // 注意：要在预创建**之前**记录「临时文件原本是否存在」——
        // 之后的断点恢复需要靠它判断数据库里残留的位图是不是陈旧的。
        var partExistedBefore = File.Exists(file.PartPath) &&
                                new FileInfo(file.PartPath).Length > 0;

        try
        {
            var directory = Path.GetDirectoryName(file.PartPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using (var _ = new FileStream(file.PartPath, FileMode.OpenOrCreate, FileAccess.Write,
                             FileShare.Read, AppConstants.StreamBufferSize, FileOptions.Asynchronous))
            {
                // 仅确保文件存在
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException
                                       or NotSupportedException)
        {
            file.State = TransferState.Failed;
            transfer.State = TransferState.Failed;
            transfer.ErrorCode = ErrorCodes.AccessDenied;
            transfer.ErrorMessage = $"无法在接收目录创建文件：{ex.Message}";

            _logger.LogError(ex, "创建接收文件失败: {Path}", file.PartPath);

            return (new CreateTransferResponse
            {
                Success = false,
                TransferId = transfer.TransferId,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.AccessDenied,
                Message = transfer.ErrorMessage,
            }, transfer);
        }

        // 5. 载入已有断点信息
        await RestoreProgressAsync(transfer, file, partExistedBefore, cancellationToken).ConfigureAwait(false);

        // 6. 接收确认
        if (transfer.State == TransferState.WaitingApproval)
        {
            // 同步关系已由用户显式授权，写入其目录的文件无需再次逐次确认
            var isAuthorizedSync = !string.IsNullOrWhiteSpace(transfer.SyncPairId);

            var autoAccept = isAuthorizedSync ||
                             (isTrusted && _settings.Current.AutoAcceptTrustedDevice);

            if (autoAccept)
            {
                transfer.Approved = true;
                transfer.Approval.TrySetResult(true);

                _logger.LogInformation(
                    isAuthorizedSync
                        ? "同步关系 {SyncPairId} 的写入已按授权自动接收"
                        : "可信设备 {Device} 的传输已按设置自动接收",
                    isAuthorizedSync ? transfer.SyncPairId : remoteDeviceName);
            }
        }

        lock (transfer.Gate)
        {
            if (transfer.Approved && transfer.State == TransferState.WaitingApproval)
                transfer.State = TransferState.Transferring;

            file.State = transfer.State;
        }

        await PersistAsync(transfer, file, cancellationToken).ConfigureAwait(false);

        return (new CreateTransferResponse
        {
            Success = true,
            TransferId = transfer.TransferId,
            FileId = file.FileId,
            State = transfer.State.ToWireString(),
            ResolvedFileName = Path.GetFileName(file.FinalPath),
        }, transfer);
    }

    /// <summary>从 .part 元数据 / 数据库恢复已完成的 Chunk。</summary>
    /// <param name="partExistedBefore">
    /// 本次登记**之前**临时文件是否已存在且非空。由调用方在预创建 .part 之前判定：
    /// 预创建之后这里必然看到文件存在，无法再区分「真的有断点」与「数据库里是陈旧位图」。
    /// </param>
    private async Task RestoreProgressAsync(IncomingTransferState transfer, IncomingFileState file,
        bool partExistedBefore, CancellationToken cancellationToken)
    {
        if (!partExistedBefore)
        {
            // 临时文件不存在（被外部清理、或上次异常退出）时，内存进度与数据库位图都是**陈旧**的：
            // 直接采信会让接收端以为分块都到齐了、永远不再收，最终必然校验失败且无法恢复。
            var stale = file.Completed.Count;
            file.Completed.Clear();
            file.ReceivedBytes = 0;

            var storedStale = await _repository.GetCompletedChunksAsync(transfer.TransferId, file.FileIndex,
                cancellationToken).ConfigureAwait(false);

            if (storedStale.Count > 0 || stale > 0)
            {
                _logger.LogWarning(
                    "临时文件缺失或为空，判定 {Memory} 个内存进度与 {Stored} 个数据库位图为陈旧并重置: {TransferId}/{File}",
                    stale, storedStale.Count, transfer.TransferId, file.FileName);

                await _repository.ClearChunksAsync(transfer.TransferId, file.FileIndex, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (file.Completed.Count > 0) return;

        var metadata = await _chunks.ReadMetadataAsync(file.PartPath, cancellationToken).ConfigureAwait(false);

        if (metadata is not null &&
            metadata.FileSize == file.FileSize &&
            string.Equals(metadata.TransferId, transfer.TransferId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.FileId, file.FileId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var chunk in metadata.CompletedChunks.Where(c => c >= 0 && c < file.TotalChunks))
                file.Completed.Add(chunk);

            file.ReceivedBytes = file.Completed.Sum(c => (long)_chunks.GetChunkRange(file.FileSize,
                file.ChunkSize, c).Length);

            _logger.LogInformation("从元数据恢复断点 {TransferId}/{File}: {Count}/{Total} 块",
                transfer.TransferId, file.FileName, file.Completed.Count, file.TotalChunks);
            return;
        }

        // 元数据缺失（例如上次异常退出）时回退到数据库位图
        var stored = await _repository.GetCompletedChunksAsync(transfer.TransferId, file.FileIndex,
            cancellationToken).ConfigureAwait(false);

        foreach (var chunk in stored.Where(c => c >= 0 && c < file.TotalChunks))
            file.Completed.Add(chunk);

        if (file.Completed.Count > 0)
        {
            file.ReceivedBytes = file.Completed.Sum(c => (long)_chunks.GetChunkRange(file.FileSize,
                file.ChunkSize, c).Length);

            _logger.LogInformation("从数据库恢复断点 {TransferId}/{File}: {Count}/{Total} 块",
                transfer.TransferId, file.FileName, file.Completed.Count, file.TotalChunks);
        }
    }

    /// <summary>
    /// 生成某个传输专属的临时文件路径：`name.<transferId 前 8 位>.part`。
    /// 后缀仍是 .part，便于同步/扫描端统一忽略临时文件。
    /// </summary>
    private static string BuildPartPath(string finalPath, string transferId)
    {
        var tag = string.IsNullOrEmpty(transferId)
            ? "tmp"
            : transferId.Replace("-", string.Empty)[..Math.Min(8, transferId.Replace("-", string.Empty).Length)];

        return $"{finalPath}.{tag}{AppConstants.PartExtension}";
    }

    // ---------------------------------------------------------------- Chunk 写入

    public async Task<(ChunkUploadResponse Response, IncomingFileState? File)> WriteChunkAsync(string transferId,
        int fileIndex, int chunkIndex, Stream body, CancellationToken cancellationToken)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.TransferNotFound, "找不到该传输。"), null);

        if (transfer.State is TransferState.Paused)
        {
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.TransferStateConflict,
                "接收端已暂停该传输。"), null);
        }

        if (transfer.State is TransferState.Cancelled or TransferState.Rejected or TransferState.Failed)
        {
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.TransferStateConflict,
                "该传输已终止。"), null);
        }

        if (!transfer.Approved || transfer.State == TransferState.WaitingApproval)
        {
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.TransferStateConflict,
                "该传输尚未被接收端确认。"), null);
        }

        if (!transfer.Files.TryGetValue(fileIndex, out var file))
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.FileNotFound, "找不到该文件。"), null);

        if (chunkIndex < 0 || chunkIndex >= file.TotalChunks)
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.ChunkIndexOutOfRange,
                $"Chunk 序号越界（0..{file.TotalChunks - 1}）。"), null);

        var (offset, length) = _chunks.GetChunkRange(file.FileSize, file.ChunkSize, chunkIndex);

        try
        {
            await _chunks.WriteChunkAsync(file.PartPath, offset, body, length, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "写入 Chunk 失败 {TransferId}/{File} #{Index}", transferId, file.FileName,
                chunkIndex);
            return (ChunkFail(transferId, chunkIndex, ErrorCodes.AccessDenied,
                $"写入磁盘失败：{ex.Message}"), file);
        }

        lock (transfer.Gate)
        {
            if (file.Completed.Add(chunkIndex))
                file.ReceivedBytes += length;

            if (transfer.State == TransferState.Transferring)
                transfer.TransferredSize = transfer.Files.Values.Sum(f => f.ReceivedBytes);

            transfer.LastActivity = DateTimeOffset.UtcNow;
        }

        await _repository.MarkChunkCompletedAsync(transferId, fileIndex, chunkIndex, cancellationToken)
            .ConfigureAwait(false);

        await SaveMetadataAsync(transfer, file, cancellationToken).ConfigureAwait(false);

        return (new ChunkUploadResponse
        {
            Success = true,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            CompletedChunks = file.Completed.Count,
            TotalChunks = file.TotalChunks,
            State = transfer.State.ToWireString(),
        }, file);
    }

    // ---------------------------------------------------------------- 完成与校验

    public async Task<(CompleteTransferResponse Response, IncomingFileState? File)> CompleteFileAsync(
        string transferId, int fileIndex, string senderSha256, CancellationToken cancellationToken)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
            return (new CompleteTransferResponse
            {
                Success = false,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.TransferNotFound,
                Message = "找不到该传输。",
            }, null);

        if (!transfer.Files.TryGetValue(fileIndex, out var file))
            return (new CompleteTransferResponse
            {
                Success = false,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.FileNotFound,
                Message = "找不到该文件。",
            }, null);

        // 写盘前的最后一道闸门：把 .part 改成正式文件名之前必须再确认一次
        // 「这是用户确认过的传输，且没有被暂停/取消/拒绝」。
        // 状态在「收完分块」与「/complete」之间是会变的（用户暂停、取消、拒绝），
        // 只在收分块那一侧判定会漏掉这些窗口；而这一步一旦放过就是文件真落盘。
        if (!transfer.Approved || transfer.State is TransferState.WaitingApproval or TransferState.Rejected
                or TransferState.Cancelled or TransferState.Paused)
        {
            _logger.LogWarning("拒绝完成未经确认或已终止的传输 {TransferId}（状态 {State}，已确认 {Approved}）",
                transferId, transfer.State, transfer.Approved);

            return (new CompleteTransferResponse
            {
                Success = false,
                State = transfer.State.ToWireString(),
                ErrorCode = ErrorCodes.TransferStateConflict,
                Message = "该传输尚未获得接收端确认，或已被暂停/取消，无法写入文件。",
            }, file);
        }

        if (file.Completed.Count < file.TotalChunks)
        {
            return (new CompleteTransferResponse
            {
                Success = false,
                State = TransferState.Transferring.ToWireString(),
                ErrorCode = ErrorCodes.TransferStateConflict,
                Message = $"仍有 {file.TotalChunks - file.Completed.Count} 个分块未到达。",
            }, file);
        }

        // 已完成过就幂等返回成功：发送端在大文件上很容易超时后重试 /complete，
        // 这时不该被当成重复提交再校验一遍。
        if (file.State == TransferState.Completed)
        {
            return (new CompleteTransferResponse
            {
                Success = true,
                State = TransferState.Completed.ToWireString(),
                Verified = true,
                SavedPath = file.FinalPath,
            }, file);
        }

        lock (transfer.Gate) transfer.State = TransferState.Verifying;

        var expected = string.IsNullOrWhiteSpace(senderSha256) ? file.Sha256 : senderSha256;

        bool verified;
        try
        {
            verified = await _hash.VerifyFileHashAsync(file.PartPath, expected, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端超时/断开导致的取消**不是**校验失败：把它记成 HASH_MISMATCH 会让一个
            // 100% 收完的文件被判为校验失败并保留 .part，而发送端只看到超时——
            // 两端结论不一致，用户只能整文件重传。这里保持「校验中」，让发送端可以重试 /complete。
            _logger.LogWarning("校验被取消（客户端超时或断开），保留传输状态等待重试: {Path}", file.PartPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "校验文件失败: {Path}", file.PartPath);
            verified = false;
        }

        if (!verified)
        {
            file.State = TransferState.VerificationFailed;
            lock (transfer.Gate)
            {
                transfer.State = TransferState.VerificationFailed;
                transfer.ErrorCode = ErrorCodes.HashMismatch;
                transfer.ErrorMessage = $"文件 {file.FileName} 的 SHA-256 校验失败，未保存为正式文件。";
            }

            await PersistAsync(transfer, file, cancellationToken).ConfigureAwait(false);

            _logger.LogError("SHA-256 校验失败，保留 .part 不重命名: {Path}", file.PartPath);

            return (new CompleteTransferResponse
            {
                Success = false,
                State = TransferState.VerificationFailed.ToWireString(),
                Verified = false,
                ErrorCode = ErrorCodes.HashMismatch,
                Message = transfer.ErrorMessage,
            }, file);
        }

        // 校验通过：.part → 正式文件名（原子替换）
        try
        {
            // 正式文件名在「登记时」就已解析，但两个并发传输可能都解析到同一个名字
            // （登记时目标都还不存在）。提交前按同一策略再解析一次：
            // Rename（默认）会退让成 "name (1).ext"，避免后完成的那个把先完成的文件直接删掉覆盖。
            if (File.Exists(file.FinalPath) && transfer.ConflictPolicy == ConflictPolicy.Rename)
            {
                var resolved = _paths.ResolveConflictName(file.FinalPath, ConflictPolicy.Rename,
                    transfer.RemoteDeviceName);

                if (!string.Equals(resolved, file.FinalPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("目标文件在传输期间已出现，改存为: {Path}", resolved);
                    file.FinalPath = resolved;
                }
            }

            if (File.Exists(file.FinalPath)) File.Delete(file.FinalPath);
            File.Move(file.PartPath, file.FinalPath);
            await _chunks.DeletePartFilesAsync(file.PartPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名 .part 失败: {From} -> {To}", file.PartPath, file.FinalPath);

            file.State = TransferState.Failed;
            lock (transfer.Gate)
            {
                transfer.State = TransferState.Failed;
                transfer.ErrorCode = ErrorCodes.AccessDenied;
                transfer.ErrorMessage = $"保存文件失败：{ex.Message}";
            }

            return (new CompleteTransferResponse
            {
                Success = false,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.AccessDenied,
                Message = transfer.ErrorMessage,
            }, file);
        }

        file.State = TransferState.Completed;

        bool allDone;
        lock (transfer.Gate)
        {
            transfer.CompletedFiles++;
            transfer.TransferredSize = transfer.Files.Values.Sum(f => f.ReceivedBytes);
            allDone = transfer.Files.Values.All(f => f.State == TransferState.Completed);

            if (allDone)
            {
                transfer.State = TransferState.Completed;
                transfer.ErrorCode = null;
                transfer.ErrorMessage = null;
            }
            else
            {
                transfer.State = TransferState.Transferring;
            }
        }

        await PersistAsync(transfer, file, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("文件接收完成并校验通过: {File} -> {Path}", file.FileName, file.FinalPath);

        return (new CompleteTransferResponse
        {
            Success = true,
            State = transfer.State.ToWireString(),
            Verified = true,
            SavedPath = file.FinalPath,
        }, file);
    }

    // ---------------------------------------------------------------- 状态查询与操作

    public TransferStatusResponse GetStatus(string transferId, int fileIndex)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
        {
            return new TransferStatusResponse
            {
                Success = false,
                TransferId = transferId,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.TransferNotFound,
                Message = "找不到该传输。",
            };
        }

        var file = transfer.Files.TryGetValue(fileIndex, out var found)
            ? found
            : transfer.Files.Values.OrderBy(f => f.FileIndex).FirstOrDefault();

        return new TransferStatusResponse
        {
            Success = true,
            TransferId = transferId,
            State = transfer.State.ToWireString(),
            FileId = file?.FileId ?? string.Empty,
            FileName = file?.FileName ?? string.Empty,
            FileSize = file?.FileSize ?? 0,
            ChunkSize = file?.ChunkSize ?? 0,
            TotalChunks = file?.TotalChunks ?? 0,
            CompletedChunks = file is null ? new List<int>() : file.Completed.OrderBy(c => c).ToList(),
            ReceivedBytes = file?.ReceivedBytes ?? 0,
            Sha256 = file?.Sha256 ?? string.Empty,
            ErrorCode = transfer.ErrorCode,
            Message = transfer.ErrorMessage,
        };
    }

    public SimpleOperationResponse SetState(string transferId, TransferState state, string? errorCode = null,
        string? errorMessage = null, bool allowResumeFromTerminal = false)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer))
        {
            return new SimpleOperationResponse
            {
                Success = false,
                TransferId = transferId,
                State = TransferState.Failed.ToWireString(),
                ErrorCode = ErrorCodes.TransferNotFound,
                Message = "找不到该传输。",
            };
        }

        lock (transfer.Gate)
        {
            // 终态不可再变更（防止迟到的回调复活已取消/已拒绝的传输）。
            // 例外：「显式恢复」——用户或传输属主点「继续」时允许 Cancelled → Transferring，
            // 否则取消过的任务永远无法恢复（发送端会一直收到「该传输已被终止」）。
            var resuming = allowResumeFromTerminal && state == TransferState.Transferring &&
                           transfer.State == TransferState.Cancelled;

            if (transfer.State.IsTerminal() && !resuming) return Snapshot(transfer);

            // 「用户已确认」是进入接收状态的唯一前提。
            //
            // 为什么必须在这里拦：SetState(Transferring) 会顺带把 Approved 置为 true 并完成审批任务，
            // 而 WaitingApproval 不是终态 —— 于是远程只要 POST /transfers/{id}/resume 就能把
            // 自己刚建、用户还没点过的传输直接「解锁」成接收中，然后照常上传并落盘。
            // /approve 早就被禁用了（见 TransferServer.HandleSimpleOp），/resume 是同一个洞的另一扇门。
            // 合法路径不受影响：本机用户点「接收」时 RespondToApproval 已经先把 Approved 置为 true；
            // 自动接收（可信设备 + 设置、同步关系已授权）也是在登记时就置为 true。
            if (state == TransferState.Transferring && !transfer.Approved)
            {
                _logger.LogWarning(
                    "拒绝把未经用户确认的传输 {TransferId} 置为接收中（当前状态 {State}）",
                    transferId, transfer.State);

                return Snapshot(transfer);
            }

            transfer.State = state;
            transfer.LastActivity = DateTimeOffset.UtcNow;
            if (errorCode is not null) transfer.ErrorCode = errorCode;
            if (errorMessage is not null) transfer.ErrorMessage = errorMessage;

            if (state == TransferState.Rejected) transfer.Approval.TrySetResult(false);
            if (state == TransferState.Transferring)
            {
                transfer.Approved = true;
                transfer.Approval.TrySetResult(true);
            }
        }

        _logger.LogInformation("接收端传输 {TransferId} 状态变更为 {State}", transferId, state);

        _ = PersistAsync(transfer, null, CancellationToken.None);

        return Snapshot(transfer);
    }

    public async Task<SimpleOperationResponse> CancelAsync(string transferId, bool deletePartial,
        CancellationToken cancellationToken)
    {
        var response = SetState(transferId, TransferState.Cancelled, ErrorCodes.CancelledByUser,
            "接收端已取消该传输。");

        if (!response.Success) return response;

        var transfer = _transfers[transferId];

        foreach (var file in transfer.Files.Values)
        {
            if (file.State == TransferState.Completed) continue;

            if (deletePartial)
            {
                await _chunks.DeletePartFilesAsync(file.PartPath, cancellationToken).ConfigureAwait(false);
                await _repository.ClearChunksAsync(transferId, file.FileIndex, cancellationToken)
                    .ConfigureAwait(false);
                file.Completed.Clear();
                file.ReceivedBytes = 0;
            }
        }

        return response;
    }

    /// <summary>把内存中的接收状态写回数据库。</summary>
    private async Task PersistAsync(IncomingTransferState transfer, IncomingFileState? file,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.UpsertTransferAsync(new TransferRecord
            {
                TransferId = transfer.TransferId,
                Direction = TransferDirection.Receive,
                TransferType = transfer.TransferType,
                RemoteDeviceId = transfer.RemoteDeviceId,
                RemoteDeviceName = transfer.RemoteDeviceName,
                State = transfer.State,
                TotalSize = transfer.TotalSize,
                TransferredSize = transfer.TransferredSize,
                TotalFiles = transfer.TotalFiles,
                CompletedFiles = transfer.CompletedFiles,
                RootName = transfer.RootName,
                DownloadRoot = transfer.DownloadRoot,
                ErrorCode = transfer.ErrorCode,
                ErrorMessage = transfer.ErrorMessage,
                CreatedAt = transfer.CreatedAt,
                CompletedAt = transfer.State == TransferState.Completed ? DateTimeOffset.UtcNow : null,
            }, cancellationToken).ConfigureAwait(false);

            if (file is not null)
            {
                await _repository.UpsertFileAsync(new TransferFileRecord
                {
                    FileId = file.FileId,
                    TransferId = transfer.TransferId,
                    FileIndex = file.FileIndex,
                    RelativePath = file.RelativePath,
                    FileName = file.FileName,
                    FileSize = file.FileSize,
                    Sha256 = file.Sha256,
                    ChunkSize = file.ChunkSize,
                    TotalChunks = file.TotalChunks,
                    CompletedChunks = file.Completed.Count,
                    State = file.State,
                    TargetPath = file.PartPath,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "持久化接收状态失败: {TransferId}", transfer.TransferId);
        }
    }

    private async Task SaveMetadataAsync(IncomingTransferState transfer, IncomingFileState file,
        CancellationToken cancellationToken)
    {
        try
        {
            await _chunks.WriteMetadataAsync(file.PartPath, new PartMetadata
            {
                TransferId = transfer.TransferId,
                FileId = file.FileId,
                FileName = file.FileName,
                RelativePath = file.RelativePath,
                FileSize = file.FileSize,
                ChunkSize = file.ChunkSize,
                TotalChunks = file.TotalChunks,
                CompletedChunks = file.Completed.OrderBy(c => c).ToList(),
                Sha256 = file.Sha256,
                RemoteDeviceId = transfer.RemoteDeviceId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入续传元数据失败: {Path}", file.PartPath);
        }
    }

    /// <summary>把传输状态推送给上层（TransferServer 订阅后转发给 UI）。</summary>
    public event EventHandler<IncomingTransferState>? StateChanged;

    public void NotifyStateChanged(IncomingTransferState transfer) => StateChanged?.Invoke(this, transfer);

    public void RaiseStateChanged(IncomingTransferState transfer) => StateChanged?.Invoke(this, transfer);

    /// <summary>移除已终止且用户已处理的任务。</summary>
    public bool Forget(string transferId) => _transfers.TryRemove(transferId, out _);

    private static SimpleOperationResponse Snapshot(IncomingTransferState transfer) => new()
    {
        Success = true,
        TransferId = transfer.TransferId,
        State = transfer.State.ToWireString(),
        ErrorCode = transfer.ErrorCode,
        Message = transfer.ErrorMessage,
    };

    private static CreateTransferResponse Fail(string code, string message) => new()
    {
        Success = false,
        State = TransferState.Failed.ToWireString(),
        ErrorCode = code,
        Message = message,
    };

    private static ChunkUploadResponse ChunkFail(string transferId, int chunkIndex, string code, string message) =>
        new()
        {
            Success = false,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            ErrorCode = code,
            Message = message,
        };

    private static ConflictPolicy ParsePolicy(string? value) => value?.ToLowerInvariant() switch
    {
        "overwrite" => ConflictPolicy.Overwrite,
        "skip" => ConflictPolicy.Skip,
        _ => ConflictPolicy.Rename,
    };
}
