using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;

namespace LanTransfer.Core.Interfaces;

/// <summary>流式 SHA-256 计算。严禁把整个文件读入内存。</summary>
public interface IHashService
{
    /// <summary>计算文件 SHA-256（十六进制小写）。</summary>
    Task<string> ComputeFileHashAsync(string filePath, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>计算流的 SHA-256。</summary>
    Task<string> ComputeStreamHashAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>计算字节数组 SHA-256（仅用于小数据，例如配对验证码）。</summary>
    string ComputeHash(ReadOnlySpan<byte> data);

    /// <summary>验证文件哈希。</summary>
    Task<bool> VerifyFileHashAsync(string filePath, string expectedSha256,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>分块计算与断点续传元数据。</summary>
public interface IChunkManager
{
    /// <summary>按文件大小与分块大小计算分块数量（0 字节文件为 1 个空块）。</summary>
    int CalculateTotalChunks(long fileSize, int chunkSize);

    /// <summary>获取指定块在文件中的偏移与长度。</summary>
    (long Offset, int Length) GetChunkRange(long fileSize, int chunkSize, int chunkIndex);

    /// <summary>写入单块数据到 .part 文件（随机访问写入，支持乱序续传）。</summary>
    Task WriteChunkAsync(string partFilePath, long offset, Stream source, int length,
        CancellationToken cancellationToken = default);

    /// <summary>读取单块数据（发送端）。</summary>
    Task<int> ReadChunkAsync(string sourceFilePath, long offset, int length, Memory<byte> buffer,
        CancellationToken cancellationToken = default);

    /// <summary>读取断点续传元数据。</summary>
    Task<PartMetadata?> ReadMetadataAsync(string partFilePath, CancellationToken cancellationToken = default);

    /// <summary>写入断点续传元数据。</summary>
    Task WriteMetadataAsync(string partFilePath, PartMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>删除 .part 与元数据。</summary>
    Task DeletePartFilesAsync(string partFilePath, CancellationToken cancellationToken = default);
}

/// <summary>.part 同目录下的续传元数据。</summary>
public sealed class PartMetadata
{
    public string TransferId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public List<int> CompletedChunks { get; set; } = new();
    public string Sha256 { get; set; } = string.Empty;
    public string RemoteDeviceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>本地待发送文件扫描结果。</summary>
public interface IFileScanner
{
    /// <summary>扫描单个文件。</summary>
    TransferFileDescriptor ScanFile(string filePath);

    /// <summary>扫描文件夹（保留目录结构，跳过重解析点）。</summary>
    IReadOnlyList<TransferFileDescriptor> ScanFolder(string folderPath);

    /// <summary>展开用户选择的路径（文件 + 文件夹）为发送清单。</summary>
    IReadOnlyList<ScannedEntry> ScanSelection(IEnumerable<string> paths);
}

/// <summary>发送清单中的一项：源路径 + 相对路径。</summary>
public sealed class ScannedEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }
}

/// <summary>路径安全解析。所有落盘路径必须经过本接口。</summary>
public interface ISafePathResolver
{
    /// <summary>把远程相对路径解析到接收根目录下；若发生路径穿越则抛出/返回失败。</summary>
    bool TryResolve(string downloadRoot, string relativePath, out string fullPath);

    /// <summary>规范化相对路径（去掉盘符、前导分隔符、.. 段）。</summary>
    string NormalizeRelativePath(string relativePath);

    /// <summary>校验文件名是否合法（Windows 保留名、非法字符）。</summary>
    bool IsValidFileName(string fileName, out string? reason);

    /// <summary>在目标已存在文件时按策略生成最终文件名。</summary>
    string ResolveConflictName(string targetPath, ConflictPolicy policy, string? deviceTag = null);

    /// <summary>获取指定路径所在磁盘的可用空间。</summary>
    long GetAvailableFreeSpace(string path);
}

/// <summary>接收端传输服务器（Kestrel）。</summary>
public interface ITransferServer : IAsyncDisposable
{
    bool IsRunning { get; }

    int Port { get; }

    string? LastError { get; }

    string CertificateFingerprint { get; }

    event EventHandler<IncomingTransferEventArgs>? IncomingTransferRequested;

    event EventHandler<IncomingTransferEventArgs>? IncomingTransferStateChanged;

    event EventHandler<IncomingFileCompletedEventArgs>? IncomingFileCompleted;

    /// <summary>对端发起设备配对，需要本机用户确认验证码。</summary>
    event EventHandler<PairingRequestedEventArgs>? PairingRequested;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>用户对接收请求的应答。</summary>
    void RespondToApproval(string transferId, bool approve, bool rememberDevice);

    /// <summary>用户对配对请求的应答。</summary>
    void RespondToPairing(string pairingSessionId, bool accept);

    /// <summary>接收端暂停（后续 Chunk 一律拒绝，已写入内容保留）。</summary>
    Task<SimpleOperationResponse> PauseIncomingAsync(string transferId,
        CancellationToken cancellationToken = default);

    /// <summary>接收端恢复。</summary>
    Task<SimpleOperationResponse> ResumeIncomingAsync(string transferId,
        CancellationToken cancellationToken = default);

    /// <summary>接收端取消；deletePartial 为 true 时同时删除 .part 与元数据。</summary>
    Task<SimpleOperationResponse> CancelIncomingAsync(string transferId, bool deletePartial,
        CancellationToken cancellationToken = default);
}

/// <summary>发送端 HTTP 客户端。</summary>
public interface ITransferClient
{
    Task<HealthResponse> PingAsync(string host, int port, CancellationToken cancellationToken = default);

    Task<DeviceResponse> GetDeviceInfoAsync(string host, int port, CancellationToken cancellationToken = default);

    Task<PairResponse> PairAsync(string host, int port, PairRequest request,
        CancellationToken cancellationToken = default);

    Task<CreateTransferResponse> CreateTransferAsync(string host, int port, CreateTransferRequest request,
        CancellationToken cancellationToken = default);

    Task<TransferStatusResponse> GetTransferStatusAsync(string host, int port, string transferId,
        int fileIndex, CancellationToken cancellationToken = default);

    Task<ChunkUploadResponse> UploadChunkAsync(string host, int port, string transferId, int fileIndex,
        int chunkIndex, Stream chunkStream, int length, CancellationToken cancellationToken = default);

    Task<CompleteTransferResponse> CompleteFileAsync(string host, int port, string transferId,
        CompleteTransferRequest request, CancellationToken cancellationToken = default);

    Task<SimpleOperationResponse> ApproveAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default);

    Task<SimpleOperationResponse> RejectAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default);

    Task<SimpleOperationResponse> PauseAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default);

    Task<SimpleOperationResponse> ResumeAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default);

    Task<SimpleOperationResponse> CancelAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default);

    Task<SyncRequestResponse> RequestSyncAsync(string host, int port, SyncRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>上传同步文件内容（PUT /sync/pairs/{id}/content?relativePath=...）。</summary>
    Task<SimpleOperationResponse> UploadSyncFileAsync(string host, int port, string syncPairId,
        string relativePath, Stream content, long length, CancellationToken cancellationToken = default);

    /// <summary>下载同步文件内容到指定流（GET /sync/pairs/{id}/content?relativePath=...）。</summary>
    Task<bool> DownloadSyncFileAsync(string host, int port, string syncPairId, string relativePath,
        Stream destination, CancellationToken cancellationToken = default);

    /// <summary>请求对端删除同步目录中的文件（POST /sync/pairs/{id}/delete）。</summary>
    Task<SimpleOperationResponse> DeleteSyncFilesAsync(string host, int port, string syncPairId,
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>交换同步清单（POST /sync/pairs/{id}/manifest）。</summary>
    Task<SyncManifestResponse> ExchangeManifestAsync(string host, int port, SyncManifestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>通知同步关系发起端：对端目录发生变化，应立即执行一轮同步。</summary>
    Task<SimpleOperationResponse> NotifySyncChangedAsync(string host, int port, string syncPairId,
        CancellationToken cancellationToken = default);
}

/// <summary>发送任务参数。同步引擎通过 RelativePaths + SyncPairId 让接收端落到同步目录内。</summary>
public sealed class SendTransferOptions
{
    public DeviceInfo Target { get; set; } = new();

    /// <summary>本机源文件绝对路径。</summary>
    public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();

    /// <summary>与 <see cref="Paths"/> 一一对应的接收端相对路径（含文件名）。为空时按普通传输处理。</summary>
    public IReadOnlyList<string>? RelativePaths { get; set; }

    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Rename;

    /// <summary>同步关系 ID。接收端据此把文件写入该同步关系的目录，而不是默认下载目录。</summary>
    public string? SyncPairId { get; set; }
}

/// <summary>传输管理器：UI 唯一的传输入口。</summary>
public interface ITransferManager
{
    IReadOnlyList<TransferRecord> Transfers { get; }

    event EventHandler<TransferProgressSnapshot>? ProgressChanged;

    event EventHandler<TransferRecord>? TransferAdded;

    event EventHandler<TransferRecord>? TransferUpdated;

    /// <summary>创建发送任务（支持多文件 / 文件夹）。</summary>
    Task<TransferRecord> CreateSendTransferAsync(DeviceInfo target, IReadOnlyList<string> paths,
        ConflictPolicy conflictPolicy = ConflictPolicy.Rename, CancellationToken cancellationToken = default);

    /// <summary>创建发送任务（可指定接收端的相对路径与同步关系，供同步引擎使用）。</summary>
    Task<TransferRecord> CreateSendTransferAsync(SendTransferOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>恢复未完成的传输（断点续传）。</summary>
    Task ResumeAsync(string transferId, CancellationToken cancellationToken = default);

    Task PauseAsync(string transferId, CancellationToken cancellationToken = default);

    Task CancelAsync(string transferId, CancellationToken cancellationToken = default);

    /// <summary>删除未完成文件（.part + 元数据 + 记录）。</summary>
    Task DeleteIncompleteAsync(string transferId, CancellationToken cancellationToken = default);

    Task LoadHistoryAsync(CancellationToken cancellationToken = default);

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>程序启动时自动恢复上次中断的任务。</summary>
    Task AutoResumePendingAsync(CancellationToken cancellationToken = default);
}
