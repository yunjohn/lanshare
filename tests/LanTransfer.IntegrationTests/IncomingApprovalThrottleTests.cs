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
/// 接收端「待用户确认」传输的闸门回归测试。
///
/// <para>
/// 历史缺陷：/transfers 对未配对设备也开放（只拒绝「身份已变化」的设备），而每登记一个新传输
/// 都会弹一个模态确认框、并留下一个最长等 10 分钟的审批任务。没有上限时，局域网里任何人都能用
/// 脚本换着 transferId 刷屏：界面被确认框淹没、内存里攒下大量待办。
/// </para>
///
/// <para>
/// 注意本测试类的注册表用「保留期 30 分钟 + 扫描间隔 0」构造：
/// 扫描每次登记都会跑（能验证它没误删活动传输），但不会回收本用例里刚变终态的条目，
/// 否则「超时后额度是否释放」这条用例会因为条目被回收而变成假通过。
/// </para>
/// </summary>
public sealed class IncomingApprovalThrottleTests : IDisposable
{
    /// <summary>与 <see cref="IncomingTransferRegistry"/> 里的每设备上限保持一致（3）。</summary>
    private const int PerDeviceLimit = 3;

    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly SqliteConnectionFactory _database;
    private readonly TransferRepository _repository;
    private readonly IncomingTransferRegistry _registry;

    public IncomingApprovalThrottleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-throttle-" + Guid.NewGuid().ToString("N"));
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
            Path.Combine(_root, "throttle.db"));
        var initializer = new DatabaseInitializer(_database, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(_database, initializer);
        _repository = new TransferRepository(_database, initializer);

        var trust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);

        _registry = new IncomingTransferRegistry(_settings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            _repository, trust, NullLogger<IncomingTransferRegistry>.Instance,
            terminalRetention: TimeSpan.FromMinutes(30), sweepInterval: TimeSpan.Zero);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private Task<(CreateTransferResponse Response, IncomingTransferState? Transfer)> RegisterAsync(
        string transferId, string deviceId = "peer-device-1", int fileIndex = 0) =>
        _registry.CreateOrGetFileAsync(new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = $"{transferId}-{fileIndex}.bin",
            RelativePath = string.Empty,
            FileSize = 1024,
            Sha256 = new string('a', 64),
            ChunkSize = AppConstants.DefaultChunkSize,
            TotalChunks = 1,
            FileIndex = fileIndex,
            TotalFiles = 1,
            TotalSize = 1024,
            RootName = $"{transferId}.bin",
            ConflictPolicy = "rename",
        }, deviceId, "PEER-PC", isTrusted: false, CancellationToken.None);

    [Fact]
    public async Task SameDevice_PendingApprovalsAreCapped()
    {
        for (var i = 0; i < PerDeviceLimit; i++)
        {
            var (ok, _) = await RegisterAsync($"pending-{i}");
            Assert.True(ok.Success);
        }

        var (rejected, transfer) = await RegisterAsync("one-too-many");

        Assert.False(rejected.Success);
        Assert.Equal(ErrorCodes.TooManyPendingRequests, rejected.ErrorCode);
        Assert.Null(transfer);

        // 闸门是按设备算的：另一台设备不受影响
        var (otherDevice, _) = await RegisterAsync("from-other-device", "peer-device-2");
        Assert.True(otherDevice.Success);
    }

    [Fact]
    public async Task MultiFileTransfer_IsNotBlockedByItsOwnPendingFiles()
    {
        // 发送端是「按文件」调用 /transfers 的：同一个 transferId 传 5 个文件就是 5 次调用。
        // 只有「新传输」才该被闸门判定，否则一个目录传到第 4 个文件就会把自己挡住。
        var transferId = Guid.NewGuid().ToString();

        for (var index = 0; index < 5; index++)
        {
            var (response, _) = await RegisterAsync(transferId, fileIndex: index);
            Assert.True(response.Success);
        }
    }

    [Fact]
    public async Task AnsweringThePrompt_FreesTheQuota()
    {
        var ids = new List<string>();

        for (var i = 0; i < PerDeviceLimit; i++)
        {
            var id = $"pending-{i}";
            ids.Add(id);
            await RegisterAsync(id);
        }

        var (blocked, _) = await RegisterAsync("blocked");
        Assert.False(blocked.Success);

        // 用户在接收端点了「拒绝」（或者点了「允许」）→ 额度立刻释放
        _registry.RespondToApproval(ids[0], approve: false);

        var (accepted, _) = await RegisterAsync("after-answer");
        Assert.True(accepted.Success);
    }

    [Fact]
    public async Task TimedOutApproval_DoesNotLeakQuotaForever()
    {
        var ids = new List<string>();

        for (var i = 0; i < PerDeviceLimit; i++)
        {
            var id = $"pending-{i}";
            ids.Add(id);
            await RegisterAsync(id);
        }

        // 模拟「等待用户确认超时」：审批流程超时后只把状态置为 Failed，
        // 那个 TaskCompletionSource 永远不会完成。若配额只按「审批任务未完成」计数，
        // 几台设备各超时几次就会把额度永久占满，之后谁也别想再发文件。
        foreach (var id in ids)
        {
            _registry.SetState(id, TransferState.Failed, ErrorCodes.Timeout, "等待用户确认超时。");
        }

        var (accepted, _) = await RegisterAsync("after-timeout");
        Assert.True(accepted.Success);
    }

    [Fact]
    public async Task ApprovalFlow_IsStartedOnlyOncePerTransfer()
    {
        // 同一传输只允许启动一个审批等待流程：否则 N 个文件的传输会挂上 N 个
        // WaitAsync 任务，每个都带一个 10 分钟定时器，全都在等同一个应答。
        await RegisterAsync("multi-file");

        var transfer = _registry.Find("multi-file");
        Assert.NotNull(transfer);

        Assert.True(transfer!.TryBeginApprovalFlow());
        Assert.False(transfer.TryBeginApprovalFlow());
        Assert.False(transfer.TryBeginApprovalFlow());
    }
}
