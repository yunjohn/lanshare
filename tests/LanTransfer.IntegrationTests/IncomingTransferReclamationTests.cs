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
        _trust = trust;

        // 保留期与扫描间隔都设为 0：一次登记就能触发回收
        _registry = new IncomingTransferRegistry(_settings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            _repository, trust, NullLogger<IncomingTransferRegistry>.Instance,
            terminalRetention: TimeSpan.Zero, sweepInterval: TimeSpan.Zero);
    }

    private readonly TrustStore _trust;

    /// <summary>按指定保留期另造一个注册表（共用同一份配置与数据库）。</summary>
    private IncomingTransferRegistry CreateRegistry(TimeSpan terminalRetention, TimeSpan staleRetention) =>
        new(_settings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            _repository, _trust, NullLogger<IncomingTransferRegistry>.Instance,
            terminalRetention: terminalRetention, sweepInterval: TimeSpan.Zero,
            staleRetention: staleRetention);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private Task<(CreateTransferResponse Response, IncomingTransferState? Transfer)> RegisterAsync(
        string transferId) => RegisterAsync(_registry, transferId);

    private static Task<(CreateTransferResponse Response, IncomingTransferState? Transfer)> RegisterAsync(
        IncomingTransferRegistry registry, string transferId) =>
        registry.CreateOrGetFileAsync(new CreateTransferRequest
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

        // 未进入终态（等待确认），即使终态保留期为 0 也不能回收
        await RegisterAsync(Guid.NewGuid().ToString());

        Assert.NotNull(_registry.Find(active));
    }

    /// <summary>
    /// 非终态但长期无活动的传输同样必须回收。
    ///
    /// <para>
    /// 触发条件：发送端在上传途中崩溃 / 断网 / 被强杀。接收端这条记录没有任何超时能把它推进到
    /// 终态（收分块不设超时），于是它永远停在 Transferring —— 第九轮的回收只清终态条目，
    /// 这类条目会一条条漏下去（每条都带着位图、临时文件路径与锁对象常驻内存）。
    /// </para>
    /// <para>
    /// 回收是安全的：发送端重连后用同一个 transferId 重新登记，
    /// 断点由磁盘上的 .part 元数据 + 数据库位图恢复（<c>RestoreProgressAsync</c> 就是为此存在）。
    /// 这里把「无活动保留期」设为 0 来代表「已经闲置 24 小时」，不必真的等一天。
    /// </para>
    /// </summary>
    [Fact]
    public async Task StaleTransferringTransfer_IsReclaimed_ButHistoryIsKept()
    {
        var registry = CreateRegistry(terminalRetention: TimeSpan.FromMinutes(30),
            staleRetention: TimeSpan.Zero);

        var abandoned = Guid.NewGuid().ToString();
        await RegisterAsync(registry, abandoned);

        // 用户确认过、正在接收 —— 然后发送端就没了下文
        registry.RespondToApproval(abandoned, approve: true);
        registry.SetState(abandoned, TransferState.Transferring);

        var transfer = registry.Find(abandoned);
        Assert.NotNull(transfer);
        Assert.Equal(TransferState.Transferring, transfer!.State);
        Assert.False(transfer.State.IsTerminal());
        Assert.True(transfer.Approved);

        // 再来一个登记 → 顺带回收
        await RegisterAsync(registry, Guid.NewGuid().ToString());

        Assert.Null(registry.Find(abandoned));

        // 历史记录必须保留（用户要在历史页看到这次中断的传输）
        var history = await _repository.GetTransferAsync(abandoned);
        Assert.NotNull(history);
    }

    [Fact]
    public async Task ActiveTransferringTransfer_IsNotReclaimed_WithinRetentionWindow()
    {
        // 对照用例：保留期没到就不能回收，否则正在传输的条目会被自己人删掉
        var registry = CreateRegistry(terminalRetention: TimeSpan.FromMinutes(30),
            staleRetention: TimeSpan.FromHours(24));

        var active = Guid.NewGuid().ToString();
        await RegisterAsync(registry, active);

        registry.RespondToApproval(active, approve: true);
        registry.SetState(active, TransferState.Transferring);

        await RegisterAsync(registry, Guid.NewGuid().ToString());

        Assert.NotNull(registry.Find(active));
    }
}
