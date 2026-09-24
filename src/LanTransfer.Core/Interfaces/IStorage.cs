using LanTransfer.Common.Models;

namespace LanTransfer.Core.Interfaces;

/// <summary>设备持久化仓储（SQLite）。</summary>
public interface IDeviceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<DeviceInfo?> GetAsync(string deviceId, CancellationToken cancellationToken = default);

    Task UpsertAsync(DeviceInfo device, CancellationToken cancellationToken = default);

    Task UpdateTrustAsync(string deviceId, TrustState trustState, string? fingerprint,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>传输任务持久化仓储（SQLite）。</summary>
public interface ITransferRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertTransferAsync(TransferRecord record, CancellationToken cancellationToken = default);

    Task<TransferRecord?> GetTransferAsync(string transferId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransferRecord>> GetRecentTransfersAsync(int limit,
        CancellationToken cancellationToken = default);

    /// <summary>取出所有未完成的传输（用于启动时自动续传）。</summary>
    Task<IReadOnlyList<TransferRecord>> GetIncompleteTransfersAsync(CancellationToken cancellationToken = default);

    Task DeleteTransferAsync(string transferId, CancellationToken cancellationToken = default);

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    Task UpsertFileAsync(TransferFileRecord file, CancellationToken cancellationToken = default);

    Task<TransferFileRecord?> GetFileAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransferFileRecord>> GetFilesAsync(string transferId,
        CancellationToken cancellationToken = default);

    /// <summary>标记某块已完成（位图持久化）。</summary>
    Task MarkChunkCompletedAsync(string transferId, int fileIndex, int chunkIndex,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetCompletedChunksAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default);

    Task<int> GetCompletedChunkCountAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default);

    Task ClearChunksAsync(string transferId, int fileIndex, CancellationToken cancellationToken = default);

    /// <summary>删除超过保留期的历史记录。</summary>
    Task<int> PurgeHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>同步关系与同步元数据持久化仓储（SQLite）。</summary>
public interface ISyncRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncPair>> GetPairsAsync(CancellationToken cancellationToken = default);

    Task<SyncPair?> GetPairAsync(string syncPairId, CancellationToken cancellationToken = default);

    Task UpsertPairAsync(SyncPair pair, CancellationToken cancellationToken = default);

    Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncEntry>> GetEntriesAsync(string syncPairId, CancellationToken cancellationToken = default);

    Task<SyncEntry?> GetEntryAsync(string syncPairId, string relativePath,
        CancellationToken cancellationToken = default);

    Task UpsertEntriesAsync(IEnumerable<SyncEntry> entries, CancellationToken cancellationToken = default);

    Task DeleteEntryAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(string syncPairId,
        CancellationToken cancellationToken = default);

    Task UpsertConflictAsync(SyncConflictRecord conflict, CancellationToken cancellationToken = default);

    Task ResolveConflictAsync(string conflictId, CancellationToken cancellationToken = default);

    /// <summary>清理超过保留期、且双方均已确认删除的墓碑。</summary>
    Task<int> PurgeTombstonesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>一个同步关系（两端各持一份，SyncPairId 相同）。</summary>
public sealed class SyncPair
{
    public string SyncPairId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>本机目录绝对路径。</summary>
    public string LocalPath { get; set; } = string.Empty;

    public string RemoteDeviceId { get; set; } = string.Empty;
    public string RemoteDeviceName { get; set; } = string.Empty;

    /// <summary>对端目录绝对路径。</summary>
    public string RemotePath { get; set; } = string.Empty;

    public SyncMode Mode { get; set; } = SyncMode.TwoWay;
    public bool Enabled { get; set; } = true;
    public SyncStatus Status { get; set; } = SyncStatus.Idle;
    public bool IsInitiator { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
}

/// <summary>同步元数据（基线）。RelativePath 是两端建立对应关系的核心标识。</summary>
public sealed class SyncEntry
{
    public string SyncPairId { get; set; } = string.Empty;

    /// <summary>相对同步根目录的路径，例如 docs\a.txt。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }

    /// <summary>最后一次双方确认一致的内容哈希（基线）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>基线版本号，每次内容实际变化递增。</summary>
    public long Version { get; set; }

    public long LocalVersion { get; set; }
    public long RemoteVersion { get; set; }

    public SyncEntryState State { get; set; } = SyncEntryState.InSync;

    /// <summary>删除墓碑标记。</summary>
    public bool Deleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>删除是否已被对端确认。</summary>
    public bool DeletePropagated { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
}

/// <summary>同步冲突记录。</summary>
public sealed class SyncConflictRecord
{
    public string ConflictId { get; set; } = string.Empty;
    public string SyncPairId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string LocalSha256 { get; set; } = string.Empty;
    public string RemoteSha256 { get; set; } = string.Empty;
    public string? ConflictCopyPath { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Resolved { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}
