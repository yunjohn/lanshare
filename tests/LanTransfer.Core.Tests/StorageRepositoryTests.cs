using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>SQLite 仓储测试：设备、传输（含位图分块）、同步关系与墓碑清理。</summary>
public class StorageRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseInitializer _initializer;
    private readonly DeviceRepository _devices;
    private readonly TransferRepository _transfers;
    private readonly SyncRepository _sync;

    public StorageRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lantransfer-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _factory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(_dir, "test.db"));
        _initializer = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);

        _devices = new DeviceRepository(_factory, _initializer);
        _transfers = new TransferRepository(_factory, _initializer);
        _sync = new SyncRepository(_factory, _initializer);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Database_IsCreatedAndPassesIntegrityCheck()
    {
        await _initializer.InitializeAsync();

        Assert.True(File.Exists(_factory.DatabasePath));
        Assert.True(await _factory.CheckIntegrityAsync());
    }

    [Fact]
    public async Task DeviceRepository_UpsertThenUpdateTrust()
    {
        var device = new DeviceInfo
        {
            DeviceId = "device-1",
            DeviceName = "PC-A",
            IpAddress = "192.168.1.10",
            Port = 39521,
            AppVersion = "1.0.0",
            ProtocolVersion = 1,
            TrustState = TrustState.Unknown,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        };

        await _devices.UpsertAsync(device);

        var loaded = await _devices.GetAsync("device-1");
        Assert.NotNull(loaded);
        Assert.Equal("PC-A", loaded!.DeviceName);
        Assert.Equal("192.168.1.10", loaded.IpAddress);

        await _devices.UpdateTrustAsync("device-1", TrustState.Trusted, "AABBCC");

        var trusted = await _devices.GetAsync("device-1");
        Assert.Equal(TrustState.Trusted, trusted!.TrustState);
        Assert.Equal("AABBCC", trusted.CertificateFingerprint);

        // Upsert 同一 DeviceId 应更新而不是产生重复行
        device.DeviceName = "PC-A-Renamed";
        await _devices.UpsertAsync(device);

        var all = await _devices.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("PC-A-Renamed", all[0].DeviceName);

        await _devices.DeleteAsync("device-1");
        Assert.Empty(await _devices.GetAllAsync());
    }

    [Fact]
    public async Task TransferRepository_PersistsRecordAndFiles()
    {
        var record = new TransferRecord
        {
            TransferId = "t-1",
            Direction = TransferDirection.Receive,
            TransferType = TransferType.Folder,
            RemoteDeviceId = "device-1",
            RemoteDeviceName = "PC-A",
            LocalDeviceId = "device-2",
            State = TransferState.Transferring,
            TotalSize = 1024,
            TransferredSize = 512,
            TotalFiles = 1,
            CompletedFiles = 0,
            RootName = "docs",
            DownloadRoot = Path.Combine(_dir, "downloads"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _transfers.UpsertTransferAsync(record);
        await _transfers.UpsertFileAsync(new TransferFileRecord
        {
            FileId = "f-1",
            TransferId = "t-1",
            FileIndex = 0,
            RelativePath = "docs\\a.txt",
            FileName = "a.txt",
            FileSize = 1024,
            Sha256 = "deadbeef",
            ChunkSize = 256,
            TotalChunks = 4,
            State = TransferState.Transferring,
        });

        var loaded = await _transfers.GetTransferAsync("t-1");
        Assert.NotNull(loaded);
        Assert.Equal(TransferState.Transferring, loaded!.State);
        Assert.Equal(1024, loaded.TotalSize);

        var files = await _transfers.GetFilesAsync("t-1");
        Assert.Single(files);
        Assert.Equal("docs\\a.txt", files[0].RelativePath);
        Assert.Equal(4, files[0].TotalChunks);

        var file = await _transfers.GetFileAsync("t-1", 0);
        Assert.Equal("a.txt", file!.FileName);
    }

    [Fact]
    public async Task TransferRepository_BitmapChunkTracking_ScalesToHundredsOfThousandsOfChunks()
    {
        await _transfers.UpsertTransferAsync(new TransferRecord
        {
            TransferId = "t-big",
            Direction = TransferDirection.Receive,
            RootName = "big.bin",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _transfers.UpsertFileAsync(new TransferFileRecord
        {
            FileId = "f-big",
            TransferId = "t-big",
            FileIndex = 0,
            RelativePath = "big.bin",
            FileName = "big.bin",
            FileSize = 100L * 1024 * 1024 * 1024,
            ChunkSize = 4 * 1024 * 1024,
            TotalChunks = 25600,
        });

        // 乱序标记，覆盖首块 / 末块 / 跨字节边界
        foreach (var index in new[] { 0, 25599, 7, 8, 15, 16, 1023, 1024, 12800 })
            await _transfers.MarkChunkCompletedAsync("t-big", 0, index);

        Assert.Equal(9, await _transfers.GetCompletedChunkCountAsync("t-big", 0));

        var completed = await _transfers.GetCompletedChunksAsync("t-big", 0);
        Assert.Equal(new[] { 0, 7, 8, 15, 16, 1023, 1024, 12800, 25599 }, completed.OrderBy(i => i));

        // 重复标记必须幂等
        await _transfers.MarkChunkCompletedAsync("t-big", 0, 7);
        Assert.Equal(9, await _transfers.GetCompletedChunkCountAsync("t-big", 0));

        await _transfers.ClearChunksAsync("t-big", 0);
        Assert.Equal(0, await _transfers.GetCompletedChunkCountAsync("t-big", 0));
    }

    [Fact]
    public async Task TransferRepository_IncompleteTransfers_ExcludesTerminalAndCancelledStates()
    {
        await _transfers.UpsertTransferAsync(NewRecord("t-done", TransferState.Completed));
        await _transfers.UpsertTransferAsync(NewRecord("t-run", TransferState.Transferring));
        await _transfers.UpsertTransferAsync(NewRecord("t-paused", TransferState.Paused));
        await _transfers.UpsertTransferAsync(NewRecord("t-failed", TransferState.Failed));
        await _transfers.UpsertTransferAsync(NewRecord("t-cancel", TransferState.Cancelled));
        await _transfers.UpsertTransferAsync(NewRecord("t-rejected", TransferState.Rejected));

        var incomplete = await _transfers.GetIncompleteTransfersAsync();

        // 启动时可自动续传的状态
        Assert.Equal(3, incomplete.Count);
        Assert.Contains(incomplete, r => r.TransferId == "t-run");
        Assert.Contains(incomplete, r => r.TransferId == "t-paused");
        Assert.Contains(incomplete, r => r.TransferId == "t-failed");

        // 已完成 / 已拒绝 / 用户主动取消的传输不得在重启后被自动恢复
        Assert.DoesNotContain(incomplete, r => r.TransferId == "t-done");
        Assert.DoesNotContain(incomplete, r => r.TransferId == "t-cancel");
        Assert.DoesNotContain(incomplete, r => r.TransferId == "t-rejected");
    }

    [Fact]
    public async Task TransferRepository_ClearHistory_KeepsUnfinished()
    {
        await _transfers.UpsertTransferAsync(NewRecord("t-done", TransferState.Completed));
        await _transfers.UpsertTransferAsync(NewRecord("t-run", TransferState.Transferring));

        await _transfers.ClearHistoryAsync();

        var remaining = await _transfers.GetRecentTransfersAsync(50);
        Assert.Single(remaining);
        Assert.Equal("t-run", remaining[0].TransferId);
    }

    [Fact]
    public async Task SyncRepository_PairsBaselineAndConflicts()
    {
        await _sync.UpsertPairAsync(new SyncPair
        {
            SyncPairId = "pair-1",
            Name = "文档同步",
            LocalPath = Path.Combine(_dir, "local"),
            RemotePath = "D:\\Shared",
            RemoteDeviceId = "device-1",
            RemoteDeviceName = "PC-A",
            Mode = SyncMode.TwoWay,
            IsInitiator = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var pairs = await _sync.GetPairsAsync();
        Assert.Single(pairs);
        Assert.Equal(SyncMode.TwoWay, pairs[0].Mode);
        Assert.True(pairs[0].IsInitiator);

        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry
            {
                SyncPairId = "pair-1",
                RelativePath = "docs\\a.txt",
                FileSize = 100,
                Sha256 = "aaa",
                Version = 3,
                State = SyncEntryState.InSync,
                LastWriteTimeUtc = DateTimeOffset.UtcNow,
            },
            new SyncEntry
            {
                SyncPairId = "pair-1",
                RelativePath = "docs\\b.txt",
                FileSize = 200,
                Sha256 = "bbb",
                Version = 1,
                Deleted = true,
                DeletedAt = DateTimeOffset.UtcNow.AddDays(-40),
                DeletePropagated = true,
            },
        });

        var entry = await _sync.GetEntryAsync("pair-1", "docs\\a.txt");
        Assert.NotNull(entry);
        Assert.Equal("aaa", entry!.Sha256);
        Assert.Equal(3, entry.Version);

        var entries = await _sync.GetEntriesAsync("pair-1");
        Assert.Equal(2, entries.Count);

        await _sync.UpsertConflictAsync(new SyncConflictRecord
        {
            ConflictId = "c-1",
            SyncPairId = "pair-1",
            RelativePath = "docs\\a.txt",
            LocalSha256 = "aaa",
            RemoteSha256 = "ccc",
            ConflictCopyPath = "docs\\a (conflict-PC-B-20260924-154501).txt",
            Description = "两端同时修改",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var conflicts = await _sync.GetConflictsAsync("pair-1");
        Assert.Single(conflicts);
        Assert.False(conflicts[0].Resolved);

        await _sync.ResolveConflictAsync("c-1");

        var resolved = await _sync.GetConflictsAsync("pair-1");
        Assert.True(resolved[0].Resolved);
        Assert.NotNull(resolved[0].ResolvedAt);

        // 墓碑清理：只清理「已确认对端收到删除」且超过保留期的记录
        var purged = await _sync.PurgeTombstonesAsync(DateTimeOffset.UtcNow.AddDays(-30));
        Assert.Equal(1, purged);

        var after = await _sync.GetEntriesAsync("pair-1");
        Assert.Single(after);
        Assert.Equal("docs\\a.txt", after[0].RelativePath);
    }

    [Fact]
    public async Task SyncRepository_PurgeTombstones_KeepsUnpropagatedDeletions()
    {
        await _sync.UpsertPairAsync(new SyncPair { SyncPairId = "pair-2", Name = "x" });

        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry
            {
                SyncPairId = "pair-2",
                RelativePath = "pending.txt",
                Deleted = true,
                DeletedAt = DateTimeOffset.UtcNow.AddDays(-90),
                DeletePropagated = false,
            },
        });

        Assert.Equal(0, await _sync.PurgeTombstonesAsync(DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.Single(await _sync.GetEntriesAsync("pair-2"));
    }

    [Fact]
    public async Task SyncRepository_DeletePair_RemovesDependents()
    {
        await _sync.UpsertPairAsync(new SyncPair { SyncPairId = "pair-3", Name = "y" });
        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry { SyncPairId = "pair-3", RelativePath = "z.txt" },
        });

        await _sync.DeletePairAsync("pair-3");

        Assert.Empty(await _sync.GetPairsAsync());
        Assert.Empty(await _sync.GetEntriesAsync("pair-3"));
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _initializer.InitializeAsync();
        await _initializer.InitializeAsync();
        await _initializer.InitializeAsync();

        Assert.True(await _factory.CheckIntegrityAsync());
    }

    /// <summary>
    /// 回归测试：SyncEntries 的主键必须大小写不敏感。
    /// Windows 路径大小写不敏感，而全应用（扫描器/规划器/基线折叠）都按 OrdinalIgnoreCase 处理；
    /// 旧表按 BINARY 比较时，"readme.md → README.md" 这样的重命名会插入第二行，
    /// 旧行永久残留成幽灵条目（陈旧哈希/墓碑）→ 多余下载、删除甚至冲突。
    /// </summary>
    [Fact]
    public async Task SyncEntries_TreatsCaseVariantPathsAsSameRow()
    {
        await _initializer.InitializeAsync();

        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry
            {
                SyncPairId = "pair-case",
                RelativePath = "docs\\readme.md",
                Sha256 = "hash-lower",
                Version = 1,
            },
        });

        // 只改大小写的重命名：必须覆盖同一行，而不是新增一行
        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry
            {
                SyncPairId = "pair-case",
                RelativePath = "DOCS\\README.MD",
                Sha256 = "hash-upper",
                Version = 2,
            },
        });

        var entries = await _sync.GetEntriesAsync("pair-case");

        var single = Assert.Single(entries);
        Assert.Equal("hash-upper", single.Sha256);
        Assert.Equal(2, single.Version);
    }

    /// <summary>
    /// 回归测试：老库（大小写敏感主键）启动时必须被迁移，并把只差大小写的重复行合并成一行，
    /// 保留版本较高的那条。不迁移的话老用户会一直带着幽灵条目跑。
    /// </summary>
    [Fact]
    public async Task InitializeAsync_MigratesLegacyCaseSensitiveSyncEntries()
    {
        // 手工造一个「旧版」库：主键没有 COLLATE NOCASE
        await using (var connection = await _factory.OpenAsync())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE SyncEntries (
                    SyncPairId        TEXT NOT NULL,
                    RelativePath      TEXT NOT NULL,
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

                INSERT INTO SyncEntries (SyncPairId, RelativePath, LastWriteTimeUtc, Sha256, Version)
                VALUES ('pair-legacy', 'docs\readme.md', '2026-09-24T00:00:00.0000000+00:00', 'old', 1),
                       ('pair-legacy', 'DOCS\README.MD', '2026-09-24T00:00:00.0000000+00:00', 'new', 5);
                """;
            await create.ExecuteNonQueryAsync();
        }

        // 换一个 initializer 实例（同一个库文件），触发迁移
        var migrated = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        await migrated.InitializeAsync();

        // 表结构确实被重建为大小写不敏感
        await using (var connection = await _factory.OpenAsync())
        {
            await using var ddlCommand = connection.CreateCommand();
            ddlCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='SyncEntries';";
            var ddl = (string?)await ddlCommand.ExecuteScalarAsync();
            Assert.Contains("COLLATE NOCASE", ddl, StringComparison.OrdinalIgnoreCase);
        }

        // 直接数一遍（新连接）
        long rawCount;
        await using (var connection = await _factory.OpenAsync())
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText =
                "SELECT COUNT(*) FROM SyncEntries WHERE SyncPairId = 'pair-legacy';";
            rawCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync());
        }

        var entries = await _sync.GetEntriesAsync("pair-legacy");

        Assert.True(rawCount == 1 && entries.Count == 1,
            $"rawCount={rawCount} repoCount={entries.Count} rows=" +
            string.Join(" | ", entries.Select(e => $"[{e.RelativePath}]={e.Sha256}@{e.Version}")));

        var single = Assert.Single(entries);
        Assert.Equal("new", single.Sha256);
        Assert.Equal(5, single.Version);

        // 迁移后表结构已是 COLLATE NOCASE：再写大小写变体会覆盖同一行
        await _sync.UpsertEntriesAsync(new[]
        {
            new SyncEntry
            {
                SyncPairId = "pair-legacy",
                RelativePath = "Docs\\Readme.md",
                Sha256 = "newest",
                Version = 6,
            },
        });

        Assert.Equal("newest", Assert.Single(await _sync.GetEntriesAsync("pair-legacy")).Sha256);
    }

    private static TransferRecord NewRecord(string id, TransferState state) => new()
    {
        TransferId = id,
        Direction = TransferDirection.Send,
        RootName = id,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
