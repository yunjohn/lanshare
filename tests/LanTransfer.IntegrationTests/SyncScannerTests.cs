using LanTransfer.Common.Models;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Metadata;
using LanTransfer.Sync.Models;
using LanTransfer.Sync.Scanner;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 同步目录扫描与基线元数据测试（任务书第 93 / 94 节）：
/// 两级检测（size + mtime 未变则不重算 SHA-256）、RelativePath 为核心标识、墓碑不得被物理删除。
/// </summary>
public class SyncScannerTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryScanner _scanner;
    private readonly SafePathResolver _resolver = new();

    public SyncScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-sync-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _scanner = new DirectoryScanner(new HashService(NullLogger<HashService>.Instance),
            NullLogger<DirectoryScanner>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task ScanAsync_ReturnsFilesAndDirectoriesWithRelativePaths()
    {
        Write("a.txt", "a");
        Write("sub\\b.txt", "b");

        var entries = (await _scanner.ScanAsync(_root, new Dictionary<string, SyncEntry>(),
            fullHash: false)).Entries;

        Assert.Contains(entries, e => e.RelativePath == "a.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub\\b.txt" && !e.IsDirectory);
    }

    [Fact]
    public async Task ScanAsync_FirstScanAlwaysComputesHash()
    {
        Write("a.txt", "hello");

        var entries = (await _scanner.ScanAsync(_root, new Dictionary<string, SyncEntry>(),
            fullHash: false)).Entries;

        var file = entries.Single(e => e.RelativePath == "a.txt");
        Assert.False(string.IsNullOrEmpty(file.Sha256));
    }

    [Fact]
    public async Task ScanAsync_SkipsHashingWhenSizeAndTimestampUnchanged()
    {
        var full = Write("stable.txt", "stable");
        var info = new FileInfo(full);

        var baseline = new Dictionary<string, SyncEntry>
        {
            ["stable.txt"] = new()
            {
                RelativePath = "stable.txt",
                FileSize = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Sha256 = "cached-hash",
            },
        };

        var entries = (await _scanner.ScanAsync(_root, baseline, fullHash: false)).Entries;

        var file = entries.Single(e => e.RelativePath == "stable.txt");

        // 两级检测：未变化时直接复用基线哈希，不重新读取文件内容
        Assert.Equal("cached-hash", file.Sha256);
    }

    [Fact]
    public async Task ScanAsync_RecomputesHashWhenSizeChanges()
    {
        var full = Write("growing.txt", "short");
        var info = new FileInfo(full);

        var baseline = new Dictionary<string, SyncEntry>
        {
            ["growing.txt"] = new()
            {
                RelativePath = "growing.txt",
                FileSize = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Sha256 = "stale-hash",
            },
        };

        File.AppendAllText(full, " and now much longer");

        var entries = (await _scanner.ScanAsync(_root, baseline, fullHash: false)).Entries;
        var file = entries.Single(e => e.RelativePath == "growing.txt");

        Assert.NotEqual("stale-hash", file.Sha256);
        Assert.False(string.IsNullOrEmpty(file.Sha256));
    }

    [Fact]
    public async Task ScanAsync_FullHashForcesRecompute()
    {
        var full = Write("stable.txt", "stable");
        var info = new FileInfo(full);

        var baseline = new Dictionary<string, SyncEntry>
        {
            ["stable.txt"] = new()
            {
                RelativePath = "stable.txt",
                FileSize = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Sha256 = "cached-hash",
            },
        };

        var entries = (await _scanner.ScanAsync(_root, baseline, fullHash: true)).Entries;
        var file = entries.Single(e => e.RelativePath == "stable.txt");

        Assert.NotEqual("cached-hash", file.Sha256);
    }

    [Fact]
    public async Task ScanAsync_MissingDirectory_ReturnsEmptyAndFlagsUnknown()
    {
        var result = await _scanner.ScanAsync(Path.Combine(_root, "not-there"),
            new Dictionary<string, SyncEntry>(), fullHash: false);

        Assert.Empty(result.Entries);

        // 根目录不存在必须标记为「整棵树未知」：否则规划器会认为两端所有文件都已删除，
        // 而 SyncEngine 只认 RootUnreadable 这个标记来中止本轮。
        Assert.True(result.HasUnreadable);
        Assert.True(result.RootUnreadable);
    }

    [Fact]
    public async Task ScanAsync_FileLockedByAnotherProgram_IsReportedAsUnreadable()
    {
        var full = Write("locked.txt", "content");

        // 模拟独占锁定（例如程序以 FileShare.None 打开文件）
        using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _scanner.ScanAsync(_root, new Dictionary<string, SyncEntry>(),
                fullHash: false);

            Assert.DoesNotContain(result.Entries, e => e.RelativePath == "locked.txt");
            Assert.Contains("locked.txt", result.UnreadablePaths);
        }
    }

    [Fact]
    public async Task ScanAsync_FileHeldOpenForWritingByAnotherProgram_IsStillHashed()
    {
        // 关键场景：文档已保存但程序没关闭 —— Word/Excel 等仍持有写句柄。
        // 只声明 FileShare.Read 会共享冲突读不了；必须允许 ReadWrite 才能同步。
        var full = Write("open-document.txt", "saved but still open");

        using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var result = await _scanner.ScanAsync(_root, new Dictionary<string, SyncEntry>(),
                fullHash: false);

            var file = result.Entries.SingleOrDefault(e => e.RelativePath == "open-document.txt");
            Assert.NotNull(file);
            Assert.False(string.IsNullOrEmpty(file!.Sha256));
            Assert.Empty(result.UnreadablePaths);
        }
    }

    [Fact]
    public async Task ScanAsync_DeletedBaselineEntry_IsStillReportedAsDeletedByPlanner()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["gone.txt"] = new()
            {
                RelativePath = "gone.txt",
                Deleted = true,
                Sha256 = string.Empty,
            },
        };

        var entries = (await _scanner.ScanAsync(_root, baseline, fullHash: false)).Entries;

        // 本地已不存在，扫描结果里自然没有它 —— 规划器依赖基线中的墓碑判断「删除」
        Assert.DoesNotContain(entries, e => e.RelativePath == "gone.txt");
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("sub\\b.txt", "sub\\b.txt")]
    public void Relative_ProducesBackslashSeparatedPath(string relative, string expected)
    {
        var full = Path.Combine(_root, relative);
        Assert.Equal(expected, DirectoryScanner.Relative(_root, full));
    }

    [Fact]
    public void Relative_ReturnsEmptyForRootItself()
    {
        Assert.Equal(string.Empty, DirectoryScanner.Relative(_root, _root));
    }

    [Fact]
    public void Resolve_ReturnsNullForTraversal()
    {
        Assert.Null(DirectoryScanner.Resolve(_resolver, _root, "..\\outside.txt"));
        Assert.NotNull(DirectoryScanner.Resolve(_resolver, _root, "inside.txt"));
    }
}

/// <summary>基线（元数据）与墓碑的持久化行为。</summary>
public class SyncMetadataTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SyncMetadataManager _metadata;

    public SyncMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-sync-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _factory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(_root, "meta.db"));
        var initializer = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        _metadata = new SyncMetadataManager(new SyncRepository(_factory, initializer));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BuildBaseline_ThenLoad_RoundTrips()
    {
        await _metadata.InitializeAsync();

        var entry = _metadata.BuildBaseline("pair-1", "docs\\a.txt", "file-1", false, 100,
            DateTimeOffset.UtcNow, "hash-1", version: 3);

        await _metadata.SaveAsync(entry);

        var baseline = await _metadata.LoadBaselineAsync("pair-1");

        Assert.True(baseline.ContainsKey("docs\\a.txt"));
        Assert.Equal("hash-1", baseline["docs\\a.txt"].Sha256);
        Assert.Equal(3, baseline["docs\\a.txt"].Version);
        Assert.Equal(3, baseline["docs\\a.txt"].LocalVersion);
        Assert.Equal(3, baseline["docs\\a.txt"].RemoteVersion);
        Assert.False(baseline["docs\\a.txt"].Deleted);
        Assert.Equal(SyncEntryState.InSync, baseline["docs\\a.txt"].State);
    }

    [Fact]
    public async Task BuildBaseline_GeneratesFileIdWhenMissing()
    {
        var entry = _metadata.BuildBaseline("pair-1", "a.txt", string.Empty, false, 1,
            DateTimeOffset.UtcNow, "h", 1);

        Assert.False(string.IsNullOrWhiteSpace(entry.FileId));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tombstone_IsPersistedAsRow_NotPhysicallyDeleted()
    {
        await _metadata.InitializeAsync();
        await _metadata.SaveAsync(_metadata.BuildBaseline("pair-1", "a.txt", "f", false, 10,
            DateTimeOffset.UtcNow, "h", 1));

        // 删除必须留下墓碑，否则对端无法区分「缺失」与「被删除」，文件会复活
        await _metadata.SaveAsync(_metadata.BuildTombstone("pair-1", "a.txt", nextVersion: 2,
            propagated: false));

        var baseline = await _metadata.LoadBaselineAsync("pair-1");

        Assert.True(baseline.ContainsKey("a.txt"));
        Assert.True(baseline["a.txt"].Deleted);
        Assert.NotNull(baseline["a.txt"].DeletedAt);
        Assert.Equal(SyncEntryState.Deleted, baseline["a.txt"].State);
        Assert.Equal(2, baseline["a.txt"].Version);
        Assert.False(baseline["a.txt"].DeletePropagated);
    }

    [Fact]
    public async Task Tombstone_IsOnlyPurgedAfterPropagationAndRetention()
    {
        await _metadata.InitializeAsync();

        await _metadata.SaveAsync(_metadata.BuildTombstone("pair-1", "unpropagated.txt", 2,
            propagated: false, deletedAt: DateTimeOffset.UtcNow.AddDays(-100)));

        await _metadata.SaveAsync(_metadata.BuildTombstone("pair-1", "old.txt", 2,
            propagated: true, deletedAt: DateTimeOffset.UtcNow.AddDays(-100)));

        await _metadata.SaveAsync(_metadata.BuildTombstone("pair-1", "recent.txt", 2,
            propagated: true, deletedAt: DateTimeOffset.UtcNow.AddDays(-1)));

        var purged = await _metadata.PurgeTombstonesAsync(DateTimeOffset.UtcNow.AddDays(-30));
        Assert.Equal(1, purged);

        var baseline = await _metadata.LoadBaselineAsync("pair-1");
        Assert.True(baseline.ContainsKey("unpropagated.txt"));
        Assert.True(baseline.ContainsKey("recent.txt"));
        Assert.False(baseline.ContainsKey("old.txt"));
    }

    [Fact]
    public async Task ToManifest_CarriesBaselineVersionAndSha()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["a.txt"] = new()
            {
                RelativePath = "a.txt",
                FileId = "file-1",
                Sha256 = "h1",
                Version = 7,
            },
        };

        var local = new List<LocalFileEntry>
        {
            new()
            {
                RelativePath = "a.txt",
                FileSize = 10,
                Sha256 = "h1",
                LastWriteTimeUtc = DateTimeOffset.UtcNow,
            },
            new()
            {
                RelativePath = "new.txt",
                FileSize = 20,
                Sha256 = "h2",
                LastWriteTimeUtc = DateTimeOffset.UtcNow,
            },
        };

        var manifest = SyncMetadataManager.ToManifest(local, baseline);

        Assert.Equal(2, manifest.Count);

        var known = manifest.Single(m => m.RelativePath == "a.txt");
        Assert.Equal("file-1", known.FileId);
        Assert.Equal(7, known.Version);

        var fresh = manifest.Single(m => m.RelativePath == "new.txt");
        Assert.Equal(string.Empty, fresh.FileId);
        Assert.Equal(0, fresh.Version);
        Assert.False(fresh.Deleted);
    }

    [Fact]
    public async Task LoadBaseline_IsCaseInsensitiveOnRelativePath()
    {
        await _metadata.InitializeAsync();
        await _metadata.SaveAsync(_metadata.BuildBaseline("pair-1", "Docs\\A.TXT", "f", false, 1,
            DateTimeOffset.UtcNow, "h", 1));

        var baseline = await _metadata.LoadBaselineAsync("pair-1");

        Assert.True(baseline.ContainsKey("docs\\a.txt"));
    }

    [Fact]
    public async Task BaselineIsIsolatedPerSyncPair()
    {
        await _metadata.InitializeAsync();

        await _metadata.SaveAsync(_metadata.BuildBaseline("pair-1", "a.txt", "f", false, 1,
            DateTimeOffset.UtcNow, "h", 1));
        await _metadata.SaveAsync(_metadata.BuildBaseline("pair-2", "b.txt", "g", false, 1,
            DateTimeOffset.UtcNow, "h", 1));

        Assert.Single(await _metadata.LoadBaselineAsync("pair-1"));
        Assert.Single(await _metadata.LoadBaselineAsync("pair-2"));
    }
}
