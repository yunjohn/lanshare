using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Sync.Conflict;

/// <summary>
/// 冲突处理。默认策略：保留双方文件，禁止默认覆盖任意一边。
/// 本机版本保留原名，对端版本落盘为 name (conflict-设备-时间戳).ext，
/// 并在数据库中留下冲突记录，由用户在同步页面裁决。
/// </summary>
public sealed class ConflictResolver
{
    private readonly ISyncRepository _repository;
    private readonly ISafePathResolver _paths;
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(ISyncRepository repository, ISafePathResolver paths,
        ILogger<ConflictResolver> logger)
    {
        _repository = repository;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>确保冲突副本路径不与已有文件冲突。</summary>
    public string ReserveConflictCopyPath(string rootPath, string conflictCopyRelativePath)
    {
        if (!_paths.TryResolve(rootPath, conflictCopyRelativePath, out var fullPath))
            throw new InvalidOperationException($"冲突副本路径非法：{conflictCopyRelativePath}");

        var resolved = _paths.ResolveConflictName(fullPath, Common.Models.ConflictPolicy.Rename);

        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        return resolved;
    }

    /// <summary>记录一条冲突。</summary>
    public async Task RecordAsync(string syncPairId, string relativePath, string localSha256,
        string remoteSha256, string? conflictCopyPath, string description,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpsertConflictAsync(new SyncConflictRecord
        {
            ConflictId = Guid.NewGuid().ToString(),
            SyncPairId = syncPairId,
            RelativePath = relativePath,
            LocalSha256 = localSha256,
            RemoteSha256 = remoteSha256,
            ConflictCopyPath = conflictCopyPath,
            Description = description,
            Resolved = false,
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("同步冲突: {Path}（{Description}）", relativePath, description);
    }

    public Task<IReadOnlyList<SyncConflictRecord>> GetOpenConflictsAsync(string syncPairId,
        CancellationToken cancellationToken = default)
        => _repository.GetConflictsAsync(syncPairId, cancellationToken);

    public Task ResolveAsync(string conflictId, CancellationToken cancellationToken = default)
        => _repository.ResolveConflictAsync(conflictId, cancellationToken);
}
