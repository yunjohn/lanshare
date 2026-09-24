using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using LanTransfer.Core.Interfaces;
using LanTransfer.Network.Client;
using LanTransfer.Security.Certificates;
using LanTransfer.Security.Trust;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using LanTransfer.Sync.Conflict;
using LanTransfer.Sync.Engine;
using LanTransfer.Sync.Metadata;
using LanTransfer.Sync.Models;
using LanTransfer.Sync.Planner;
using LanTransfer.Sync.Scanner;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 被动端（非发起端）清单服务的回归测试。
///
/// 历史缺陷：非发起端从不写基线（HandleManifestAsync / WriteAsync / DeleteAsync 都不落库），
/// 于是它的基线永远为空 —— 每个清单请求都要对整棵树重新做 SHA-256（两级检测失效），
/// 并且「读不到就用基线占位」的保护在被动端完全无从生效。
/// </summary>
public sealed class SyncEngineManifestTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _syncRoot;

    private SettingsService _settings = null!;
    private SqliteConnectionFactory _database = null!;
    private SyncRepository _repository = null!;
    private SyncMetadataManager _metadata = null!;
    private SyncEngine _engine = null!;
    private TransferHttpClient _client = null!;
    private IdentityService _identity = null!;

    private const string PairId = "pair-manifest-1";
    private const string RemoteDeviceId = "remote-device-1";

    public SyncEngineManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-manifest-" + Guid.NewGuid().ToString("N"));
        _syncRoot = Path.Combine(_root, "sync");
        Directory.CreateDirectory(_syncRoot);
    }

    public async Task InitializeAsync()
    {
        _settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"));
        await _settings.LoadAsync();
        await _settings.ApplyAsync(new AppSettings
        {
            DeviceId = _settings.Current.DeviceId,
            DeviceName = "PASSIVE-PC",
            DiscoveryPort = AppConstants.DefaultDiscoveryPort,
            TransferPort = 39999,
            DownloadPath = _syncRoot,
            ChunkSize = AppConstants.DefaultChunkSize,
        });

        _database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(_root, "sync.db"));
        var initializer = new DatabaseInitializer(_database, NullLogger<DatabaseInitializer>.Instance);
        _repository = new SyncRepository(_database, initializer);
        await _repository.InitializeAsync();

        var deviceRepository = new DeviceRepository(_database, initializer);
        _identity = new IdentityService(_settings, NullLogger<IdentityService>.Instance, _root);
        await _identity.InitializeAsync();

        var trust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);
        await trust.InitializeAsync();

        _client = new TransferHttpClient(new PeerCertificateRegistry(), _identity,
            NullLogger<TransferHttpClient>.Instance);

        var devices = new DeviceManager(deviceRepository, _identity, _settings, _client,
            new PeerCertificateRegistry(), NullLogger<DeviceManager>.Instance);

        _metadata = new SyncMetadataManager(_repository);
        var paths = new SafePathResolver();

        _engine = new SyncEngine(_repository, _client, devices, _settings, paths,
            new HashService(NullLogger<HashService>.Instance),
            new DirectoryScanner(new HashService(NullLogger<HashService>.Instance),
                NullLogger<DirectoryScanner>.Instance),
            new SyncPlanner(), _metadata, new ConflictResolver(_repository, paths,
                NullLogger<ConflictResolver>.Instance),
            NullLogger<SyncEngine>.Instance);

        // 被动端：IsInitiator=false，InitializeAsync 不会主动调度同步
        await _repository.UpsertPairAsync(new SyncPair
        {
            SyncPairId = PairId,
            Name = "manifest-test",
            LocalPath = _syncRoot,
            RemotePath = @"D:\remote",
            RemoteDeviceId = RemoteDeviceId,
            RemoteDeviceName = "PEER-PC",
            IsInitiator = false,
            Enabled = true,
            Mode = SyncMode.TwoWay,
        });

        await _engine.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        try { _client.Dispose(); } catch { /* 忽略 */ }
        try { await _engine.DisposeAsync(); } catch { /* 忽略 */ }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public async Task Manifest_PersistsBaseline_SoNextRoundDoesNotRehash()
    {
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "a.txt"), "content-a");
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "b.txt"), "content-b");

        var response = await _engine.HandleManifestAsync(
            new SyncManifestRequest { SyncPairId = PairId, FullScan = false }, RemoteDeviceId);

        Assert.True(response.Success, response.Message);
        Assert.Equal(2, response.Entries.Count(e => !e.IsDirectory));
        Assert.Empty(response.UnreadablePaths);

        // 关键断言：清单返回后，被动端的基线必须已经落库
        var baseline = await _metadata.LoadBaselineAsync(PairId);
        Assert.Equal(2, baseline.Count);
        Assert.Contains("a.txt", baseline.Keys);
        Assert.Contains("b.txt", baseline.Keys);
        Assert.All(baseline.Values, e => Assert.False(string.IsNullOrEmpty(e.Sha256)));

        // 第二次请求仍应返回同样的哈希（此时走两级检测复用基线，不再重算内容摘要）
        var second = await _engine.HandleManifestAsync(
            new SyncManifestRequest { SyncPairId = PairId, FullScan = false }, RemoteDeviceId);

        Assert.True(second.Success);
        foreach (var entry in response.Entries.Where(e => !e.IsDirectory))
        {
            var again = second.Entries.Single(e => e.RelativePath == entry.RelativePath);
            Assert.Equal(entry.Sha256, again.Sha256);
        }

        Assert.Equal(2, (await _metadata.LoadBaselineAsync(PairId)).Count);
    }

    [Fact]
    public async Task Manifest_RejectsUnknownDevice()
    {
        var response = await _engine.HandleManifestAsync(
            new SyncManifestRequest { SyncPairId = PairId }, "someone-else");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.Unauthorized, response.ErrorCode);
    }

    [Fact]
    public async Task Manifest_WhenRootMissing_ReportsRootUnreadable()
    {
        Directory.Delete(_syncRoot, recursive: true);

        var response = await _engine.HandleManifestAsync(
            new SyncManifestRequest { SyncPairId = PairId }, RemoteDeviceId);

        // 根目录不可读时必须明确失败（对端据此放弃本轮），绝不能返回「空清单」
        Assert.False(response.Success);
        Assert.True(response.RootUnreadable);
    }
}
