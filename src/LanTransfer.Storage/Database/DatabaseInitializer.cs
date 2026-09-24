using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Storage.Database;

/// <summary>
/// 建表与迁移。全部语句使用 IF NOT EXISTS，重复执行安全。
/// 分块状态使用位图 BLOB 而非「每块一行」，避免十万级行数与巨大 JSON 字符串。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public DatabaseInitializer(SqliteConnectionFactory factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await MigrateSyncEntriesToCaseInsensitiveAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("数据库结构已就绪: {Path}", _factory.DatabasePath);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "初始化数据库失败: {Path}", _factory.DatabasePath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把旧版（大小写敏感主键）的 SyncEntries 迁移成 COLLATE NOCASE，并顺带去掉重复行。
    ///
    /// 只对已存在的旧表生效：CREATE TABLE IF NOT EXISTS 不会改动既有表结构，
    /// 不迁移的话，老用户库里「只改大小写的重命名」留下的重复行会继续产生幽灵条目。
    /// </summary>
    private async Task MigrateSyncEntriesToCaseInsensitiveAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var definition = await ReadTableSqlAsync(connection, "SyncEntries", cancellationToken)
            .ConfigureAwait(false);

        if (definition is null ||
            definition.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogInformation("迁移 SyncEntries 为大小写不敏感主键，并合并重复的相对路径…");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;

        // 用「先把旧表改名 → 再用普通 CREATE TABLE 建回正式表 → 复制数据」的形状，
        // 让新表的建表语句与 SchemaSql 里的完全一致（新装用户与迁移用户得到同一张表）。
        // 没有用「建临时表 → DROP 旧表 → RENAME」，是为了避免依赖 RENAME 后的隐式索引行为。
        command.CommandText = """
            ALTER TABLE SyncEntries RENAME TO SyncEntries_legacy;

            CREATE TABLE SyncEntries (
                SyncPairId        TEXT NOT NULL,
                RelativePath      TEXT NOT NULL COLLATE NOCASE,
                FileId            TEXT NOT NULL DEFAULT '',
                IsDirectory       INTEGER NOT NULL DEFAULT 0,
                FileSize          INTEGER NOT NULL DEFAULT 0,
                LastWriteTimeUtc  TEXT NOT NULL,
                Sha256            TEXT NOT NULL DEFAULT '',
                Version           INTEGER NOT NULL DEFAULT 0,
                LocalVersion      INTEGER NOT NULL DEFAULT 0,
                RemoteVersion     INTEGER NOT NULL DEFAULT 0,
                State             INTEGER NOT NULL DEFAULT 0,
                Deleted           INTEGER NOT NULL DEFAULT 0,
                DeletedAt         TEXT NULL,
                DeletePropagated  INTEGER NOT NULL DEFAULT 0,
                LastSyncedAt      TEXT NULL,
                PRIMARY KEY (SyncPairId, RelativePath)
            );

            -- 只保留「同一路径（忽略大小写）里版本最高」的那一行；
            -- 同版本多行时由新表的大小写不敏感主键再兜一次底。
            INSERT OR REPLACE INTO SyncEntries
                SELECT s.* FROM SyncEntries_legacy s
                WHERE s.Version = (
                    SELECT MAX(x.Version) FROM SyncEntries_legacy x
                    WHERE x.SyncPairId = s.SyncPairId
                      AND x.RelativePath = s.RelativePath COLLATE NOCASE);

            DROP TABLE SyncEntries_legacy;
            CREATE INDEX IF NOT EXISTS IX_SyncEntries_State ON SyncEntries (SyncPairId, State);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SyncEntries 迁移完成");
    }

    private static async Task<string?> ReadTableSqlAsync(SqliteConnection connection, string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Devices (
            DeviceId                TEXT PRIMARY KEY,
            DeviceName              TEXT NOT NULL DEFAULT '',
            IpAddress               TEXT NOT NULL DEFAULT '',
            Port                    INTEGER NOT NULL DEFAULT 0,
            AppVersion              TEXT NOT NULL DEFAULT '',
            ProtocolVersion         INTEGER NOT NULL DEFAULT 0,
            CertificateFingerprint  TEXT NULL,
            TrustState              INTEGER NOT NULL DEFAULT 0,
            FirstSeen               TEXT NOT NULL,
            LastSeen                TEXT NOT NULL,
            IsManual                INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS Transfers (
            TransferId        TEXT PRIMARY KEY,
            Direction         INTEGER NOT NULL DEFAULT 0,
            TransferType      INTEGER NOT NULL DEFAULT 0,
            RemoteDeviceId    TEXT NOT NULL DEFAULT '',
            RemoteDeviceName  TEXT NOT NULL DEFAULT '',
            LocalDeviceId     TEXT NOT NULL DEFAULT '',
            State             INTEGER NOT NULL DEFAULT 0,
            TotalSize         INTEGER NOT NULL DEFAULT 0,
            TransferredSize   INTEGER NOT NULL DEFAULT 0,
            TotalFiles        INTEGER NOT NULL DEFAULT 0,
            CompletedFiles    INTEGER NOT NULL DEFAULT 0,
            RootName          TEXT NOT NULL DEFAULT '',
            DownloadRoot      TEXT NULL,
            ErrorCode         TEXT NULL,
            ErrorMessage      TEXT NULL,
            CreatedAt         TEXT NOT NULL,
            CompletedAt       TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Transfers_CreatedAt ON Transfers (CreatedAt DESC);
        CREATE INDEX IF NOT EXISTS IX_Transfers_State     ON Transfers (State);

        CREATE TABLE IF NOT EXISTS TransferFiles (
            FileId           TEXT NOT NULL,
            TransferId       TEXT NOT NULL,
            FileIndex        INTEGER NOT NULL,
            RelativePath     TEXT NOT NULL DEFAULT '',
            FileName         TEXT NOT NULL DEFAULT '',
            FileSize         INTEGER NOT NULL DEFAULT 0,
            Sha256           TEXT NOT NULL DEFAULT '',
            ChunkSize        INTEGER NOT NULL DEFAULT 0,
            TotalChunks      INTEGER NOT NULL DEFAULT 0,
            CompletedChunks  INTEGER NOT NULL DEFAULT 0,
            State            INTEGER NOT NULL DEFAULT 0,
            TargetPath       TEXT NULL,
            SourcePath       TEXT NULL,
            LastWriteTimeUtc TEXT NULL,
            PRIMARY KEY (TransferId, FileIndex)
        );

        -- 每行保存一个文件的全部分块完成状态位图（BLOB）。
        -- 例如 100 GB / 4 MiB = 25600 个 Chunk，仅需 3200 字节。
        CREATE TABLE IF NOT EXISTS TransferChunks (
            TransferId  TEXT NOT NULL,
            FileIndex   INTEGER NOT NULL,
            TotalChunks INTEGER NOT NULL,
            Bitmap      BLOB NOT NULL,
            UpdatedAt   TEXT NOT NULL,
            PRIMARY KEY (TransferId, FileIndex)
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS SyncPairs (
            SyncPairId       TEXT PRIMARY KEY,
            Name             TEXT NOT NULL DEFAULT '',
            LocalPath        TEXT NOT NULL DEFAULT '',
            RemoteDeviceId   TEXT NOT NULL DEFAULT '',
            RemoteDeviceName TEXT NOT NULL DEFAULT '',
            RemotePath       TEXT NOT NULL DEFAULT '',
            Mode             INTEGER NOT NULL DEFAULT 0,
            Enabled          INTEGER NOT NULL DEFAULT 1,
            Status           INTEGER NOT NULL DEFAULT 0,
            IsInitiator      INTEGER NOT NULL DEFAULT 1,
            LastError        TEXT NULL,
            CreatedAt        TEXT NOT NULL,
            LastSyncAt       TEXT NULL,
            LastScanAt       TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS SyncEntries (
            SyncPairId        TEXT NOT NULL,
            -- 必须 COLLATE NOCASE：Windows 路径大小写不敏感，全应用（扫描器/规划器/基线折叠）
            -- 都按 OrdinalIgnoreCase 处理；若主键按 BINARY 比较，只改大小写的重命名
            -- （readme.md → README.md）会插入第二行，旧行永久残留成幽灵条目
            -- （陈旧哈希/陈旧墓碑），导致多余下载、删除或冲突。
            RelativePath      TEXT NOT NULL COLLATE NOCASE,
            FileId            TEXT NOT NULL DEFAULT '',
            IsDirectory       INTEGER NOT NULL DEFAULT 0,
            FileSize          INTEGER NOT NULL DEFAULT 0,
            LastWriteTimeUtc  TEXT NOT NULL,
            Sha256            TEXT NOT NULL DEFAULT '',
            Version           INTEGER NOT NULL DEFAULT 0,
            LocalVersion      INTEGER NOT NULL DEFAULT 0,
            RemoteVersion     INTEGER NOT NULL DEFAULT 0,
            State             INTEGER NOT NULL DEFAULT 0,
            Deleted           INTEGER NOT NULL DEFAULT 0,
            DeletedAt         TEXT NULL,
            DeletePropagated  INTEGER NOT NULL DEFAULT 0,
            LastSyncedAt      TEXT NULL,
            PRIMARY KEY (SyncPairId, RelativePath)
        );
        CREATE INDEX IF NOT EXISTS IX_SyncEntries_State ON SyncEntries (SyncPairId, State);

        CREATE TABLE IF NOT EXISTS SyncConflicts (
            ConflictId       TEXT PRIMARY KEY,
            SyncPairId       TEXT NOT NULL,
            RelativePath     TEXT NOT NULL,
            LocalSha256      TEXT NOT NULL DEFAULT '',
            RemoteSha256     TEXT NOT NULL DEFAULT '',
            ConflictCopyPath TEXT NULL,
            Description      TEXT NOT NULL DEFAULT '',
            Resolved         INTEGER NOT NULL DEFAULT 0,
            CreatedAt        TEXT NOT NULL,
            ResolvedAt       TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_SyncConflicts_Pair ON SyncConflicts (SyncPairId, Resolved);
        """;
}
