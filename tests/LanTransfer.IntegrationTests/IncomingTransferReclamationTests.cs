using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using LanTransfer.Core.Interfaces;
using LanTransfer.Core.Transfers;
using LanTransfer.Network.Server;
using LanTransfer.Security.Trust;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 接收端注册表的资源回收回归测试。
/// 历史缺陷：终态（已完成/已取消/已拒绝/失败）传输的内存状态永不回收，
/// 长时间运行会持续累积（内存与失效会话），跨会话累积也没有上限。
/// </summary>
public sealed class IncomingTransferReclamationTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly SqliteConnectionFactory _database;
    private readonly TransferRepository _repository;
    private readonly IncomingTransferRegistry _registry;

    public IncomingTransferReclamationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-reclaim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"));
        _settings.LoadAsync().GetAwaiter().GetResult();
        _settings.ApplyAsync(new AppSettings
        {
            DeviceId = _settings.Current.DeviceId,
            DeviceName = "RECEIVER-PC",
            TransferPort = AppConstants.DefaultTransferPort,
            DownloadPath = Path.Combine(_root, "downloads"),
            ChunkSize = AppConstants.DefaultChunkSize,
        }).GetAwaiter().GetResult();

        Directory.CreateDirectory(_settings.Current.DownloadPath);

        _database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(_root, "reclaim.db"));
        var initializer = new DatabaseInitializer(_database, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(_database, initializer);
        _repository = new TransferRepository(_database, initializer);

        var trust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);

        // 保留期与扫描间隔都设为 0：一次登记就能触发回收
        _registry = new IncomingTransferRegistry(_settings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            _repository, trust, NullLogger<IncomingTransferRegistry>.Instance,
            terminalRetention: TimeSpan.Zero, sweepInterval: TimeSpan.Zero);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private Task<(CreateTransferResponse Response, IncomingTransferState? Transfer)> RegisterAsync(
        string transferId) =>
        _registry.CreateOrGetFileAsync(new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = $"{transferId}.bin",
            RelativePath = string.Empty,
            FileSize = 1024,
            Sha256 = new string('a', 64),
            ChunkSize = AppConstants.DefaultChunkSize,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = 1024,
            RootName = $"{transferId}.bin",
            ConflictPolicy = "rename",
        }, "peer-device-1", "PEER-PC", isTrusted: false, CancellationToken.None);

    [Fact]
    public async Task TerminalTransfer_MemoryStateIsReclaimed_ButHistoryIsKept()
    {
        var cancelled = Guid.NewGuid().ToString();
        await RegisterAsync(cancelled);

        Assert.NotNull(_registry.Find(cancelled));

        // 进入终态（模拟用户取消）
        _registry.SetState(cancelled, TransferState.Cancelled, ErrorCodes.CancelledByUser, "测试取消");
        Assert.Equal(TransferState.Cancelled, _registry.Find(cancelled)!.State);

        // 再登记另一个传输 → 顺带回收（保留期为 0）
        await RegisterAsync(Guid.NewGuid().ToString());

        Assert.Null(_registry.Find(cancelled));

        // 数据库里的历史记录必须保留：用户要在历史页看到这次传输
        var history = await _repository.GetTransferAsync(cancelled);
        Assert.NotNull(history);
        Assert.Equal(TransferState.Cancelled, history!.State);
    }

    [Fact]
    public async Task ActiveTransfer_IsNotReclaimed()
    {
        var active = Guid.NewGuid().ToString();
        await RegisterAsync(active);

        // 未进入终态（等待确认），即使保留期为 0 也不能回收
        await RegisterAsync(Guid.NewGuid().ToString());

        Assert.NotNull(_registry.Find(active));
    }
}
