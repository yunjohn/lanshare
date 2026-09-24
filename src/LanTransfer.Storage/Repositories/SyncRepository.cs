using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Storage.Database;
using Microsoft.Data.Sqlite;

namespace LanTransfer.Storage.Repositories;

/// <summary>同步关系 / 同步基线（元数据） / 冲突记录 仓储。</summary>
public sealed class SyncRepository : ISyncRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseInitializer _initializer;

    public SyncRepository(SqliteConnectionFactory factory, DatabaseInitializer initializer)
    {
        _factory = factory;
        _initializer = initializer;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _initializer.InitializeAsync(cancellationToken);

    // ---------------------------------------------------------------- SyncPairs

    public async Task<IReadOnlyList<SyncPair>> GetPairsAsync(CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<SyncPair>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PairSelect + " ORDER BY CreatedAt ASC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadPair(reader));

        return result;
    }

    public async Task<SyncPair?> GetPairAsync(string syncPairId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PairSelect + " WHERE SyncPairId = $id;";
        command.Parameters.AddWithValue("$id", syncPairId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPair(reader) : null;
    }

    public async Task UpsertPairAsync(SyncPair pair, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncPairs
                (SyncPairId, Name, LocalPath, RemoteDeviceId, RemoteDeviceName, RemotePath,
                 Mode, Enabled, Status, IsInitiator, LastError, CreatedAt, LastSyncAt, LastScanAt)
            VALUES
                ($id, $name, $local, $remoteId, $remoteName, $remotePath,
                 $mode, $enabled, $status, $initiator, $error, $createdAt, $lastSyncAt, $lastScanAt)
            ON CONFLICT(SyncPairId) DO UPDATE SET
                Name             = excluded.Name,
                LocalPath        = excluded.LocalPath,
                RemoteDeviceId   = excluded.RemoteDeviceId,
                RemoteDeviceName = excluded.RemoteDeviceName,
                RemotePath       = excluded.RemotePath,
                Mode             = excluded.Mode,
                Enabled          = excluded.Enabled,
                Status           = excluded.Status,
                LastError        = excluded.LastError,
                LastSyncAt       = excluded.LastSyncAt,
                LastScanAt       = excluded.LastScanAt;
            """;

        command.Parameters.AddWithValue("$id", pair.SyncPairId);
        command.Parameters.AddWithValue("$name", pair.Name ?? string.Empty);
        command.Parameters.AddWithValue("$local", pair.LocalPath ?? string.Empty);
        command.Parameters.AddWithValue("$remoteId", pair.RemoteDeviceId ?? string.Empty);
        command.Parameters.AddWithValue("$remoteName", pair.RemoteDeviceName ?? string.Empty);
        command.Parameters.AddWithValue("$remotePath", pair.RemotePath ?? string.Empty);
        command.Parameters.AddWithValue("$mode", (int)pair.Mode);
        command.Parameters.AddWithValue("$enabled", pair.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)pair.Status);
        command.Parameters.AddWithValue("$initiator", pair.IsInitiator ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)pair.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", pair.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastSyncAt",
            pair.LastSyncAt.HasValue ? pair.LastSyncAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$lastScanAt",
            pair.LastScanAt.HasValue ? pair.LastScanAt.Value.ToString("O") : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM SyncConflicts WHERE SyncPairId = $id;
            DELETE FROM SyncEntries   WHERE SyncPairId = $id;
            DELETE FROM SyncPairs     WHERE SyncPairId = $id;
            """;
        command.Parameters.AddWithValue("$id", syncPairId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- SyncEntries

    public async Task<IReadOnlyList<SyncEntry>> GetEntriesAsync(string syncPairId,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<SyncEntry>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE SyncPairId = $id;";
        command.Parameters.AddWithValue("$id", syncPairId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadEntry(reader));

        return result;
    }

    public async Task<SyncEntry?> GetEntryAsync(string syncPairId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE SyncPairId = $id AND RelativePath = $path;";
        command.Parameters.AddWithValue("$id", syncPairId);
        command.Parameters.AddWithValue("$path", relativePath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    public async Task UpsertEntriesAsync(IEnumerable<SyncEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in entries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO SyncEntries
                    (SyncPairId, RelativePath, FileId, IsDirectory, FileSize, LastWriteTimeUtc, Sha256,
                     Version, LocalVersion, RemoteVersion, State, Deleted, DeletedAt, DeletePropagated, LastSyncedAt)
                VALUES
                    ($pairId, $path, $fileId, $isDir, $size, $lastWrite, $sha256,
                     $version, $localVersion, $remoteVersion, $state, $deleted, $deletedAt, $propagated, $lastSynced)
                ON CONFLICT(SyncPairId, RelativePath) DO UPDATE SET
                    FileId           = excluded.FileId,
                    IsDirectory      = excluded.IsDirectory,
                    FileSize         = excluded.FileSize,
                    LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                    Sha256           = excluded.Sha256,
                    Version          = excluded.Version,
                    LocalVersion     = excluded.LocalVersion,
                    RemoteVersion    = excluded.RemoteVersion,
                    State            = excluded.State,
                    Deleted          = excluded.Deleted,
                    DeletedAt        = excluded.DeletedAt,
                    DeletePropagated = excluded.DeletePropagated,
                    LastSyncedAt     = excluded.LastSyncedAt;
                """;

            command.Parameters.AddWithValue("$pairId", entry.SyncPairId);
            command.Parameters.AddWithValue("$path", entry.RelativePath);
            command.Parameters.AddWithValue("$fileId", entry.FileId ?? string.Empty);
            command.Parameters.AddWithValue("$isDir", entry.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$size", entry.FileSize);
            command.Parameters.AddWithValue("$lastWrite", entry.LastWriteTimeUtc.ToString("O"));
            command.Parameters.AddWithValue("$sha256", entry.Sha256 ?? string.Empty);
            command.Parameters.AddWithValue("$version", entry.Version);
            command.Parameters.AddWithValue("$localVersion", entry.LocalVersion);
            command.Parameters.AddWithValue("$remoteVersion", entry.RemoteVersion);
            command.Parameters.AddWithValue("$state", (int)entry.State);
            command.Parameters.AddWithValue("$deleted", entry.Deleted ? 1 : 0);
            command.Parameters.AddWithValue("$deletedAt",
                entry.DeletedAt.HasValue ? entry.DeletedAt.Value.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$propagated", entry.DeletePropagated ? 1 : 0);
            command.Parameters.AddWithValue("$lastSynced",
                entry.LastSyncedAt.HasValue ? entry.LastSyncedAt.Value.ToString("O") : DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEntryAsync(string syncPairId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncEntries WHERE SyncPairId = $id AND RelativePath = $path;";
        command.Parameters.AddWithValue("$id", syncPairId);
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Conflicts

    public async Task<IReadOnlyList<SyncConflictRecord>> GetConflictsAsync(string syncPairId,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<SyncConflictRecord>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ConflictSelect + " WHERE SyncPairId = $id ORDER BY CreatedAt DESC;";
        command.Parameters.AddWithValue("$id", syncPairId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadConflict(reader));

        return result;
    }

    public async Task UpsertConflictAsync(SyncConflictRecord conflict,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncConflicts
                (ConflictId, SyncPairId, RelativePath, LocalSha256, RemoteSha256, ConflictCopyPath,
                 Description, Resolved, CreatedAt, ResolvedAt)
            VALUES
                ($id, $pairId, $path, $local, $remote, $copy, $description, $resolved, $createdAt, $resolvedAt)
            ON CONFLICT(ConflictId) DO UPDATE SET
                LocalSha256      = excluded.LocalSha256,
                RemoteSha256     = excluded.RemoteSha256,
                ConflictCopyPath = excluded.ConflictCopyPath,
                Description      = excluded.Description,
                Resolved         = excluded.Resolved,
                ResolvedAt       = excluded.ResolvedAt;
            """;

        command.Parameters.AddWithValue("$id", conflict.ConflictId);
        command.Parameters.AddWithValue("$pairId", conflict.SyncPairId);
        command.Parameters.AddWithValue("$path", conflict.RelativePath);
        command.Parameters.AddWithValue("$local", conflict.LocalSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$remote", conflict.RemoteSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$copy", (object?)conflict.ConflictCopyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", conflict.Description ?? string.Empty);
        command.Parameters.AddWithValue("$resolved", conflict.Resolved ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", conflict.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$resolvedAt",
            conflict.ResolvedAt.HasValue ? conflict.ResolvedAt.Value.ToString("O") : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveConflictAsync(string conflictId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyncConflicts SET Resolved = 1, ResolvedAt = $resolvedAt WHERE ConflictId = $id;
            """;
        command.Parameters.AddWithValue("$id", conflictId);
        command.Parameters.AddWithValue("$resolvedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeTombstonesAsync(DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // 只有「已确认对端收到删除」且超过保留期的墓碑才允许清理
        command.CommandText = """
            DELETE FROM SyncEntries
            WHERE Deleted = 1
              AND DeletePropagated = 1
              AND DeletedAt IS NOT NULL
              AND DeletedAt < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 映射

    private const string PairSelect = """
        SELECT SyncPairId, Name, LocalPath, RemoteDeviceId, RemoteDeviceName, RemotePath,
               Mode, Enabled, Status, IsInitiator, LastError, CreatedAt, LastSyncAt, LastScanAt
        FROM SyncPairs
        """;

    private const string EntrySelect = """
        SELECT SyncPairId, RelativePath, FileId, IsDirectory, FileSize, LastWriteTimeUtc, Sha256,
               Version, LocalVersion, RemoteVersion, State, Deleted, DeletedAt, DeletePropagated, LastSyncedAt
        FROM SyncEntries
        """;

    private const string ConflictSelect = """
        SELECT ConflictId, SyncPairId, RelativePath, LocalSha256, RemoteSha256, ConflictCopyPath,
               Description, Resolved, CreatedAt, ResolvedAt
        FROM SyncConflicts
        """;

    private static SyncPair ReadPair(SqliteDataReader reader) => new()
    {
        SyncPairId = reader.GetString(0),
        Name = reader.GetString(1),
        LocalPath = reader.GetString(2),
        RemoteDeviceId = reader.GetString(3),
        RemoteDeviceName = reader.GetString(4),
        RemotePath = reader.GetString(5),
        Mode = (SyncMode)reader.GetInt32(6),
        Enabled = reader.GetInt32(7) != 0,
        Status = (SyncStatus)reader.GetInt32(8),
        IsInitiator = reader.GetInt32(9) != 0,
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt = DeviceRepository.ParseDate(reader.GetString(11)),
        LastSyncAt = reader.IsDBNull(12) ? null : DeviceRepository.ParseDate(reader.GetString(12)),
        LastScanAt = reader.IsDBNull(13) ? null : DeviceRepository.ParseDate(reader.GetString(13)),
    };

    private static SyncEntry ReadEntry(SqliteDataReader reader) => new()
    {
        SyncPairId = reader.GetString(0),
        RelativePath = reader.GetString(1),
        FileId = reader.GetString(2),
        IsDirectory = reader.GetInt32(3) != 0,
        FileSize = reader.GetInt64(4),
        LastWriteTimeUtc = DeviceRepository.ParseDate(reader.GetString(5)),
        Sha256 = reader.GetString(6),
        Version = reader.GetInt64(7),
        LocalVersion = reader.GetInt64(8),
        RemoteVersion = reader.GetInt64(9),
        State = (SyncEntryState)reader.GetInt32(10),
        Deleted = reader.GetInt32(11) != 0,
        DeletedAt = reader.IsDBNull(12) ? null : DeviceRepository.ParseDate(reader.GetString(12)),
        DeletePropagated = reader.GetInt32(13) != 0,
        LastSyncedAt = reader.IsDBNull(14) ? null : DeviceRepository.ParseDate(reader.GetString(14)),
    };

    private static SyncConflictRecord ReadConflict(SqliteDataReader reader) => new()
    {
        ConflictId = reader.GetString(0),
        SyncPairId = reader.GetString(1),
        RelativePath = reader.GetString(2),
        LocalSha256 = reader.GetString(3),
        RemoteSha256 = reader.GetString(4),
        ConflictCopyPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        Description = reader.GetString(6),
        Resolved = reader.GetInt32(7) != 0,
        CreatedAt = DeviceRepository.ParseDate(reader.GetString(8)),
        ResolvedAt = reader.IsDBNull(9) ? null : DeviceRepository.ParseDate(reader.GetString(9)),
    };
}
