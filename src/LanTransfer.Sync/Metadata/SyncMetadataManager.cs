using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Models;

namespace LanTransfer.Sync.Metadata;

/// <summary>
/// 同步基线（元数据）管理。基线代表「双方最后一次确认一致的状态」，
/// 是双向同步判断的唯一依据；RelativePath 是两端建立对应关系的核心标识。
/// </summary>
public sealed class SyncMetadataManager
{
    private readonly ISyncRepository _repository;

    public SyncMetadataManager(ISyncRepository repository) => _repository = repository;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _repository.InitializeAsync(cancellationToken);

    /// <summary>加载某个同步关系的全部基线（含墓碑）。</summary>
    public async Task<Dictionary<string, SyncEntry>> LoadBaselineAsync(string syncPairId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _repository.GetEntriesAsync(syncPairId, cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            map[entry.RelativePath] = entry;

        return map;
    }

    /// <summary>把一批条目写回基线。</summary>
    public Task SaveAsync(IEnumerable<SyncEntry> entries, CancellationToken cancellationToken = default)
        => _repository.UpsertEntriesAsync(entries, cancellationToken);

    public Task SaveAsync(SyncEntry entry, CancellationToken cancellationToken = default)
        => _repository.UpsertEntriesAsync(new[] { entry }, cancellationToken);

    /// <summary>把基线更新为「与本地一致」。</summary>
    public SyncEntry BuildBaseline(string syncPairId, string relativePath, string fileId, bool isDirectory,
        long fileSize, DateTimeOffset lastWriteTimeUtc, string sha256, long version,
        DateTimeOffset? lastSyncedAt = null) => new()
        {
            SyncPairId = syncPairId,
            RelativePath = relativePath,
            FileId = string.IsNullOrEmpty(fileId) ? Guid.NewGuid().ToString() : fileId,
            IsDirectory = isDirectory,
            FileSize = fileSize,
            LastWriteTimeUtc = lastWriteTimeUtc,
            Sha256 = sha256,
            Version = version,
            LocalVersion = version,
            RemoteVersion = version,
            State = SyncEntryState.InSync,
            Deleted = false,
            LastSyncedAt = lastSyncedAt ?? DateTimeOffset.UtcNow,
        };

    /// <summary>生成删除墓碑。删除不能直接移除数据库记录，否则另一端无法区分「缺失」与「被删除」。</summary>
    public SyncEntry BuildTombstone(string syncPairId, string relativePath, long nextVersion,
        bool propagated, DateTimeOffset? deletedAt = null)
    {
        var now = deletedAt ?? DateTimeOffset.UtcNow;

        return new SyncEntry
        {
            SyncPairId = syncPairId,
            RelativePath = relativePath,
            FileId = Guid.NewGuid().ToString(),
            IsDirectory = false,
            FileSize = 0,
            LastWriteTimeUtc = now,
            Sha256 = string.Empty,
            Version = nextVersion,
            LocalVersion = nextVersion,
            RemoteVersion = nextVersion,
            State = SyncEntryState.Deleted,
            Deleted = true,
            DeletedAt = now,
            DeletePropagated = propagated,
            LastSyncedAt = now,
        };
    }

    /// <summary>清理超过保留期且双方均已确认删除的墓碑。</summary>
    public Task<int> PurgeTombstonesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        => _repository.PurgeTombstonesAsync(olderThan, cancellationToken);

    /// <summary>把内存扫描结果转换为协议清单。</summary>
    public static List<Common.Protocol.SyncManifestEntry> ToManifest(IEnumerable<LocalFileEntry> entries,
        IReadOnlyDictionary<string, SyncEntry> baseline)
    {
        var result = new List<Common.Protocol.SyncManifestEntry>();

        foreach (var entry in entries)
        {
            baseline.TryGetValue(entry.RelativePath, out var known);

            result.Add(new Common.Protocol.SyncManifestEntry
            {
                RelativePath = entry.RelativePath,
                FileId = known?.FileId ?? string.Empty,
                IsDirectory = entry.IsDirectory,
                FileSize = entry.FileSize,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                Sha256 = entry.Sha256,
                Version = known?.Version ?? 0,
                Deleted = false,
            });
        }

        return result;
    }

    /// <summary>
    /// 生成本机清单（含「读不到」的条目）。
    ///
    /// 对本轮读不到的路径，用**基线状态**占位——等价于告诉对端「这一项没有变化」。
    /// 否则对端收到不含该路径的清单后，会按下「基线里有、对端清单里没有 = 对端删除了」
    /// 的规则，把它自己的副本删掉。
    /// </summary>
    public static List<Common.Protocol.SyncManifestEntry> ToManifest(DirectoryScanResult scan,
        IReadOnlyDictionary<string, SyncEntry> baseline)
    {
        var result = ToManifest(scan.Entries, baseline);

        if (!scan.HasUnreadable) return result;

        var included = new HashSet<string>(result.Select(e => e.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (path, entry) in baseline)
        {
            if (included.Contains(path)) continue;
            if (!DirectoryScanResult.IsWithin(path, scan.UnreadablePaths)) continue;

            result.Add(new Common.Protocol.SyncManifestEntry
            {
                RelativePath = path,
                FileId = entry.FileId,
                IsDirectory = entry.IsDirectory,
                FileSize = entry.FileSize,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                Sha256 = entry.Sha256,
                Version = entry.Version,
                Deleted = entry.Deleted,
            });
        }

        return result;
    }
}
