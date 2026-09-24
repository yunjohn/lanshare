using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using LanTransfer.Core.Interfaces;
using LanTransfer.Core.Transfers;
using LanTransfer.Network.Client;
using LanTransfer.Network.Server;
using LanTransfer.Security.Certificates;
using LanTransfer.Security.Pairing;
using LanTransfer.Security.Trust;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 双机传输端到端测试：在同一台机器上用真实 Kestrel + 双向 TLS 起一个接收端，
/// 再用真实 HttpClient 作为发送端，走完「建立传输 → 用户确认 → 上传 Chunk → SHA-256 校验 → 落盘」全流程。
/// 覆盖任务书第 58~61 节要求的 1 KB / 10 MB 完整性测试与断点续传测试。
/// </summary>
public sealed class LoopbackTransferTests : IAsyncLifetime
{
    private const int MiB = 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly int _port;

    private SettingsService _serverSettings = null!;
    private SettingsService _clientSettings = null!;
    private IdentityService _serverIdentity = null!;
    private IdentityService _clientIdentity = null!;
    private TrustStore _serverTrust = null!;
    private SqliteConnectionFactory _serverDatabase = null!;
    private TransferServer _server = null!;
    private TransferHttpClient _client = null!;
    private string _downloadRoot = null!;
    private string _sourceRoot = null!;

    public LoopbackTransferTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _port = GetFreeTcpPort();
    }

    // ---------------------------------------------------------------- 生命周期

    public async Task InitializeAsync()
    {
        _downloadRoot = Path.Combine(_root, "downloads");
        _sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(_downloadRoot);
        Directory.CreateDirectory(_sourceRoot);

        await StartServerAsync();
        await CreateClientAsync();
    }

    public async Task DisposeAsync()
    {
        try { _client.Dispose(); } catch { /* 忽略 */ }

        try { await _server.StopAsync(); } catch { /* 忽略 */ }
        try { await _server.DisposeAsync(); } catch { /* 忽略 */ }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private async Task StartServerAsync()
    {
        var dir = Path.Combine(_root, "server");
        Directory.CreateDirectory(dir);

        _serverSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await _serverSettings.LoadAsync();
        await _serverSettings.ApplyAsync(NewSettings(_serverSettings, "SERVER-PC", _downloadRoot, _port));

        _serverDatabase = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(dir, "server.db"));
        var initializer = new DatabaseInitializer(_serverDatabase, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(_serverDatabase, initializer);
        var transferRepository = new TransferRepository(_serverDatabase, initializer);

        _serverIdentity = new IdentityService(_serverSettings, NullLogger<IdentityService>.Instance, dir);
        await _serverIdentity.InitializeAsync();

        _serverTrust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);
        await _serverTrust.InitializeAsync();

        var serverHttpClient = new TransferHttpClient(new PeerCertificateRegistry(), _serverIdentity,
            NullLogger<TransferHttpClient>.Instance);

        var deviceManager = new DeviceManager(deviceRepository, _serverIdentity, _serverSettings,
            serverHttpClient, new PeerCertificateRegistry(), NullLogger<DeviceManager>.Instance);

        var registry = new IncomingTransferRegistry(_serverSettings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            transferRepository, _serverTrust, NullLogger<IncomingTransferRegistry>.Instance);

        _server = new TransferServer(_serverSettings, _serverIdentity, deviceManager, _serverTrust,
            new PairingService(_serverTrust, _serverIdentity, NullLogger<PairingService>.Instance),
            registry, NullLogger<TransferServer>.Instance);

        // 模拟用户在接收确认弹窗上点击「接收」
        _server.IncomingTransferRequested += (_, e) => _server.RespondToApproval(e.TransferId, true, false);

        await _server.StartAsync();

        Assert.True(_server.IsRunning, $"接收端启动失败：{_server.LastError}");
        Assert.Equal(_port, _server.Port);
    }

    private async Task CreateClientAsync()
    {
        var dir = Path.Combine(_root, "client");
        Directory.CreateDirectory(dir);

        _clientSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await _clientSettings.LoadAsync();
        await _clientSettings.ApplyAsync(NewSettings(_clientSettings, "CLIENT-PC",
            Path.Combine(dir, "downloads"), _port));

        _clientIdentity = new IdentityService(_clientSettings, NullLogger<IdentityService>.Instance, dir);
        await _clientIdentity.InitializeAsync();

        _client = new TransferHttpClient(new PeerCertificateRegistry(), _clientIdentity,
            NullLogger<TransferHttpClient>.Instance);
    }

    private static AppSettings NewSettings(SettingsService service, string name, string downloadPath,
        int port) => new()
    {
        DeviceId = service.Current.DeviceId,
        DeviceName = name,
        DiscoveryPort = AppConstants.DefaultDiscoveryPort,
        TransferPort = port,
        DownloadPath = downloadPath,
        ChunkSize = MiB,
        AutoAcceptTrustedDevice = false,
    };

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---------------------------------------------------------------- 辅助

    private string CreateSourceFile(string name, int size)
    {
        var path = Path.Combine(_sourceRoot, name);
        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static string Sha256Of(string filePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();

    private async Task<(string TransferId, string ExpectedSha, long Size)> SendFileAsync(string sourcePath,
        string relativePath, string? conflictPolicy = null)
    {
        var info = new FileInfo(sourcePath);
        var sha = Sha256Of(sourcePath);
        var chunkSize = MiB;
        var totalChunks = (int)((info.Length + chunkSize - 1) / chunkSize);

        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = info.Name,
            RelativePath = relativePath,
            FileSize = info.Length,
            Sha256 = sha,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = info.Length,
            RootName = info.Name,
            ConflictPolicy = conflictPolicy ?? "rename",
        });

        Assert.True(create.Success, $"{create.ErrorCode}: {create.Message}");
        Assert.Equal(transferId, create.TransferId);

        // 等待接收端用户确认
        var status = await WaitForStateAsync(transferId, TransferState.Transferring);

        // 只上传接收端缺少的 Chunk（这正是断点续传的核心）
        var completed = status.CompletedChunks.ToHashSet();

        await using (var stream = File.OpenRead(sourcePath))
        {
            for (var index = 0; index < totalChunks; index++)
            {
                if (completed.Contains(index)) continue;

                var offset = (long)index * chunkSize;
                var length = (int)Math.Min(chunkSize, info.Length - offset);

                stream.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, length - read));
                    if (n <= 0) break;
                    read += n;
                }

                using var chunk = new MemoryStream(buffer, writable: false);
                var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, index,
                    chunk, length);

                Assert.True(upload.Success, $"Chunk {index} 上传失败：{upload.ErrorCode} {upload.Message}");
                Assert.Equal(index, upload.ChunkIndex);
            }
        }

        var complete = await _client.CompleteFileAsync("127.0.0.1", _port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        Assert.True(complete.Success, $"{complete.ErrorCode}: {complete.Message}");
        Assert.True(complete.Verified, "接收端 SHA-256 校验未通过");
        Assert.NotNull(complete.SavedPath);

        return (transferId, sha, info.Length);
    }

    private async Task<TransferStatusResponse> WaitForStateAsync(string transferId, TransferState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        TransferStatusResponse? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await _client.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);

            if (TransferStateExtensions.FromWireString(last.State) == expected) return last;

            await Task.Delay(50);
        }

        Assert.Fail($"等待状态 {expected} 超时，最后状态：{last?.State}");
        return last!;
    }

    // ---------------------------------------------------------------- 基础接口

    [Fact]
    public async Task Health_ReturnsCompatibleProtocolVersion()
    {
        var health = await _client.PingAsync("127.0.0.1", _port);

        Assert.True(health.Success);
        Assert.Equal("ok", health.Status);
        Assert.Equal(AppConstants.ProtocolVersion, health.ProtocolVersion);
        Assert.Equal(AppConstants.AppVersion, health.AppVersion);
    }

    [Fact]
    public async Task Device_ReturnsServerIdentityAndFingerprint()
    {
        var device = await _client.GetDeviceInfoAsync("127.0.0.1", _port);

        Assert.True(device.Success);
        Assert.Equal(_serverSettings.Current.DeviceId, device.DeviceId);
        Assert.Equal("SERVER-PC", device.DeviceName);
        Assert.Equal(_port, device.Port);
        Assert.Equal(_serverIdentity.CertificateFingerprint, device.CertificateFingerprint);
    }

    [Fact]
    public async Task CertificateFingerprint_IsRecordedOnFirstContact()
    {
        var registry = new PeerCertificateRegistry();

        using var client = new TransferHttpClient(registry, _clientIdentity,
            NullLogger<TransferHttpClient>.Instance);

        var health = await client.PingAsync("127.0.0.1", _port);

        Assert.True(health.Success);
        Assert.Equal(_serverIdentity.CertificateFingerprint, registry.GetExpected("127.0.0.1"));
    }

    [Fact]
    public async Task CertificateFingerprint_PinningRejectsChangedCertificate()
    {
        var registry = new PeerCertificateRegistry();

        // 模拟「曾经连接过该主机，但对方证书已变化」
        registry.Remember("127.0.0.1", new string('F', 64));

        using var pinned = new TransferHttpClient(registry, _clientIdentity,
            NullLogger<TransferHttpClient>.Instance);

        // 指纹不匹配必须拒绝连接（防中间人替换证书）
        await Assert.ThrowsAnyAsync<Exception>(() => pinned.PingAsync("127.0.0.1", _port));

        // 且绝不能把新指纹静默覆盖写回
        Assert.Equal(new string('F', 64), registry.GetExpected("127.0.0.1"));
    }

    // ---------------------------------------------------------------- 完整性

    [Fact]
    public async Task Transfer_OneKilobyte_IsSavedAndVerified()
    {
        var source = CreateSourceFile("small-1kb.bin", 1024);

        var (_, expectedSha, size) = await SendFileAsync(source, string.Empty);

        var saved = Path.Combine(_downloadRoot, "small-1kb.bin");
        Assert.True(File.Exists(saved), "接收端未生成目标文件");
        Assert.Equal(size, new FileInfo(saved).Length);
        Assert.Equal(expectedSha, Sha256Of(saved));

        // 落盘后不得残留 .part 与元数据
        Assert.False(File.Exists(saved + ".part"));
        Assert.False(File.Exists(saved + ".part.json"));
    }

    [Fact]
    public async Task Transfer_TenMegabytes_IsStreamedAndVerified()
    {
        var source = CreateSourceFile("medium-10mb.bin", 10 * MiB);

        var (_, expectedSha, size) = await SendFileAsync(source, string.Empty);

        var saved = Path.Combine(_downloadRoot, "medium-10mb.bin");
        Assert.Equal(size, new FileInfo(saved).Length);
        Assert.Equal(expectedSha, Sha256Of(saved));
    }

    [Fact]
    public async Task Transfer_ExactChunkBoundary_ProducesSingleChunk()
    {
        var source = CreateSourceFile("exact-1mb.bin", MiB);

        var (transferId, expectedSha, _) = await SendFileAsync(source, string.Empty);

        var status = await _client.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);
        Assert.Equal(1, status.TotalChunks);

        Assert.Equal(expectedSha, Sha256Of(Path.Combine(_downloadRoot, "exact-1mb.bin")));
    }

    [Fact]
    public async Task Transfer_NestedRelativePath_KeepsDirectoryStructure()
    {
        var source = CreateSourceFile("nested.bin", 4096);

        await SendFileAsync(source, "docs\\sub");

        var saved = Path.Combine(_downloadRoot, "docs", "sub", "nested.bin");
        Assert.True(File.Exists(saved), $"期望文件不存在：{saved}");
    }

    /// <summary>
    /// 大文件必须「流式」走完整链路（分块上传 + 接收落盘 + 校验），进程托管堆增长远小于文件本身。
    ///
    /// 这条用例替代了原来那条被环境变量静默 return 的「500 MB 测试」——
    /// 静默返回在 xunit 里记为「通过」，是典型的假绿灯（文档却宣称它断言了内存增量）。
    /// 现在默认跑 128 MiB，设置 LANTRANSFER_LARGE_TESTS=1 可放大到 500 MB（同一套断言）。
    /// </summary>
    [Fact]
    public async Task Transfer_LargeFile_IsStreamedWithoutLoadingIntoMemory()
    {
        var size = Environment.GetEnvironmentVariable("LANTRANSFER_LARGE_TESTS") == "1"
            ? 500 * MiB
            : 128 * MiB;

        var source = CreateSourceFile("large-stream.bin", size);

        // 先跑一轮预热，避免 JIT/初始化噪声算进增量
        var warmup = CreateSourceFile("stream-warmup.bin", MiB);
        await SendFileAsync(warmup, string.Empty);

        var before = GC.GetTotalMemory(forceFullCollection: true);

        var (_, expectedSha, _) = await SendFileAsync(source, string.Empty);

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var growth = after - before;

        var saved = Path.Combine(_downloadRoot, "large-stream.bin");
        Assert.Equal(size, new FileInfo(saved).Length);
        Assert.Equal(expectedSha, Sha256Of(saved));

        // 托管堆增量应远小于文件大小（整文件读入内存会让它至少涨到文件大小量级）
        Assert.True(growth < size / 4,
            $"托管堆增长 {growth / MiB} MiB，疑似把大文件读进了内存（文件 {size / MiB} MiB）");
    }

    // ---------------------------------------------------------------- 断点续传

    [Fact]
    public async Task Resume_UploadsOnlyMissingChunksAndStillVerifies()
    {
        var source = CreateSourceFile("resume-5mb.bin", 5 * MiB);
        var info = new FileInfo(source);
        var sha = Sha256Of(source);
        var totalChunks = 5;

        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferId = transferId,
            FileName = info.Name,
            RelativePath = string.Empty,
            FileSize = info.Length,
            Sha256 = sha,
            ChunkSize = MiB,
            TotalChunks = totalChunks,
            TotalSize = info.Length,
            RootName = info.Name,
        });
        Assert.True(create.Success, create.Message);

        await WaitForStateAsync(transferId, TransferState.Transferring);

        // 第 1 轮：只上传偶数块，模拟「传到一半断线」
        await UploadChunkRangeAsync(source, transferId, new[] { 0, 2, 4 });

        var midStatus = await _client.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);
        Assert.Equal(new[] { 0, 2, 4 }, midStatus.CompletedChunks.OrderBy(i => i));
        Assert.Equal(3 * MiB, midStatus.ReceivedBytes);

        // 第 2 轮：查询状态后只补缺失的块（真正的断点续传）
        await UploadChunkRangeAsync(source, transferId, new[] { 1, 3 });

        var complete = await _client.CompleteFileAsync("127.0.0.1", _port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        Assert.True(complete.Success, $"{complete.ErrorCode}: {complete.Message}");
        Assert.True(complete.Verified);

        var saved = Path.Combine(_downloadRoot, "resume-5mb.bin");
        Assert.Equal(info.Length, new FileInfo(saved).Length);
        Assert.Equal(sha, Sha256Of(saved));
    }

    private async Task UploadChunkRangeAsync(string sourcePath, string transferId, IEnumerable<int> indices)
    {
        var info = new FileInfo(sourcePath);

        await using var stream = File.OpenRead(sourcePath);

        foreach (var index in indices)
        {
            var offset = (long)index * MiB;
            var length = (int)Math.Min(MiB, info.Length - offset);

            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, length - read));
                if (n <= 0) break;
                read += n;
            }

            using var chunk = new MemoryStream(buffer);
            var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, index, chunk, length);
            Assert.True(upload.Success, $"Chunk {index} 上传失败：{upload.ErrorCode} {upload.Message}");
        }
    }

    // ---------------------------------------------------------------- 安全与拒绝路径

    [Fact]
    public async Task PathTraversal_IsRejectedOverTheWire()
    {
        var source = CreateSourceFile("evil.bin", 512);

        var response = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "evil.bin",
            RelativePath = "..\\..\\Windows",
            FileSize = new FileInfo(source).Length,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            TotalSize = new FileInfo(source).Length,
            RootName = "evil.bin",
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.PathEscapeDetected, response.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "Windows", "evil.bin")));
    }

    [Fact]
    public async Task InvalidFileName_IsRejectedOverTheWire()
    {
        var source = CreateSourceFile("reserved.bin", 128);

        var response = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "CON",
            RelativePath = string.Empty,
            FileSize = 128,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            TotalSize = 128,
            RootName = "CON",
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidFileName, response.ErrorCode);
    }

    [Fact]
    public async Task IncompatibleProtocolVersion_IsRejected()
    {
        var source = CreateSourceFile("proto.bin", 64);

        var response = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            ProtocolVersion = AppConstants.ProtocolVersion + 99,
            TransferId = Guid.NewGuid().ToString(),
            FileName = "proto.bin",
            FileSize = 64,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            TotalSize = 64,
            RootName = "proto.bin",
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ProtocolIncompatible, response.ErrorCode);
    }

    [Fact]
    public async Task HashMismatch_IsDetectedAndFileIsNotPromoted()
    {
        var source = CreateSourceFile("tampered.bin", 2048);

        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferId = transferId,
            FileName = "tampered.bin",
            FileSize = 2048,
            Sha256 = new string('a', 64),
            ChunkSize = MiB,
            TotalChunks = 1,
            TotalSize = 2048,
            RootName = "tampered.bin",
        });
        Assert.True(create.Success, create.Message);

        await WaitForStateAsync(transferId, TransferState.Transferring);

        await using (var stream = File.OpenRead(source))
        {
            var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, 0, stream, 2048);
            Assert.True(upload.Success, upload.Message);
        }

        // 发送端声称的哈希与实际内容不符 → 校验必须失败
        var complete = await _client.CompleteFileAsync("127.0.0.1", _port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = new string('a', 64) });

        Assert.False(complete.Success);
        Assert.False(complete.Verified);

        var saved = Path.Combine(_downloadRoot, "tampered.bin");
        Assert.False(File.Exists(saved), "校验失败的文件不得出现在接收目录中");
    }

    [Fact]
    public async Task RejectedTransfer_IsNotWrittenToDisk()
    {
        var source = CreateSourceFile("rejected.bin", 4096);
        var transferId = Guid.NewGuid().ToString();

        // 先摘掉自动批准，改为拒绝
        void Reject(object? _, IncomingTransferEventArgs e)
            => _server.RespondToApproval(e.TransferId, approve: false, rememberDevice: false);

        _server.IncomingTransferRequested += Reject;

        try
        {
            var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
            {
                TransferId = transferId,
                FileName = "rejected.bin",
                FileSize = new FileInfo(source).Length,
                Sha256 = Sha256Of(source),
                ChunkSize = MiB,
                TotalChunks = 1,
                TotalSize = new FileInfo(source).Length,
                RootName = "rejected.bin",
            });

            Assert.True(create.Success, create.Message);

            var status = await WaitForStateAsync(transferId, TransferState.Rejected);
            Assert.Equal("rejected", status.State);

            Assert.False(File.Exists(Path.Combine(_downloadRoot, "rejected.bin")));
        }
        finally
        {
            _server.IncomingTransferRequested -= Reject;
        }
    }

    [Fact]
    public async Task DuplicateFileName_IsRenamedInsteadOfOverwritten()
    {
        var source = CreateSourceFile("dup.bin", 1024);
        var firstSha = Sha256Of(source);

        await SendFileAsync(source, string.Empty);

        // 用不同内容覆盖源文件后再次发送同名文件（默认 rename 策略）
        var replacement = new byte[1024];
        new Random(4242).NextBytes(replacement);
        await File.WriteAllBytesAsync(source, replacement);
        var secondSha = Sha256Of(source);

        Assert.NotEqual(firstSha, secondSha);

        await SendFileAsync(source, string.Empty, conflictPolicy: "rename");

        var original = Path.Combine(_downloadRoot, "dup.bin");
        var renamed = Path.Combine(_downloadRoot, "dup (1).bin");

        Assert.True(File.Exists(original));
        Assert.True(File.Exists(renamed), "重名文件应自动重命名为 dup (1).bin，而不是覆盖原文件");

        Assert.Equal(firstSha, Sha256Of(original));
        Assert.Equal(secondSha, Sha256Of(renamed));
    }

    /// <summary>
    /// 回归测试（数据安全）：「跳过」重名策略下目标已存在时，接收端必须拒绝该文件，
    /// 绝不能像「覆盖」那样先删掉已有文件再改名。
    /// </summary>
    [Fact]
    public async Task SkipPolicy_ExistingTarget_IsRejectedInsteadOfOverwritten()
    {
        var source = CreateSourceFile("keep.bin", 2048);

        var originalBytes = new byte[2048];
        new Random(7).NextBytes(originalBytes);

        // 接收目录里已存在同名文件，且内容与源文件不同
        var existing = Path.Combine(_downloadRoot, "keep.bin");
        await File.WriteAllBytesAsync(existing, originalBytes);
        var existingSha = Sha256Of(existing);

        var info = new FileInfo(source);
        var sha = Sha256Of(source);
        Assert.NotEqual(existingSha, sha);

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = Guid.NewGuid().ToString(),
            FileName = info.Name,
            RelativePath = string.Empty,
            FileSize = info.Length,
            Sha256 = sha,
            ChunkSize = MiB,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = info.Length,
            RootName = info.Name,
            ConflictPolicy = "skip",
        });

        Assert.False(create.Success, "「跳过」策略下目标已存在，应拒绝该文件");
        Assert.Equal(ErrorCodes.FileExistsSkipped, create.ErrorCode);

        // 已有文件必须原封不动，且不得留下 .part 残骸
        Assert.True(File.Exists(existing));
        Assert.Equal(existingSha, Sha256Of(existing));
        Assert.False(File.Exists(existing + AppConstants.PartExtension));
    }

    /// <summary>
    /// 安全回归：入站传输的状态查询 / 取消等端点只能由「创建该传输的设备」调用。
    /// 此前完全不校验调用方，局域网内任意主机都能查询、暂停、取消他人的传输。
    /// </summary>
    [Fact]
    public async Task OtherDevice_CannotOperateForeignTransfer()
    {
        var source = CreateSourceFile("owned.bin", 1024);
        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = Path.GetFileName(source),
            RelativePath = string.Empty,
            FileSize = new FileInfo(source).Length,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = new FileInfo(source).Length,
            RootName = Path.GetFileName(source),
            ConflictPolicy = "rename",
        });

        Assert.True(create.Success, create.Message);

        // 另一台「设备」：不同 DeviceId + 不同证书
        var otherDir = Path.Combine(_root, "intruder");
        Directory.CreateDirectory(otherDir);

        var otherSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(otherDir, "settings.json"));
        await otherSettings.LoadAsync();
        await otherSettings.ApplyAsync(NewSettings(otherSettings, "INTRUDER-PC", otherDir, _port));

        var otherIdentity = new IdentityService(otherSettings, NullLogger<IdentityService>.Instance, otherDir);
        await otherIdentity.InitializeAsync();

        using var intruder = new TransferHttpClient(new PeerCertificateRegistry(), otherIdentity,
            NullLogger<TransferHttpClient>.Instance);

        Assert.False(string.Equals(otherIdentity.DeviceId, _clientSettings.Current.DeviceId,
            StringComparison.OrdinalIgnoreCase));

        var status = await intruder.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);
        Assert.False(status.Success);

        var cancel = await intruder.CancelAsync("127.0.0.1", _port, transferId);
        Assert.False(cancel.Success);

        // 属主自己的取消必须仍然可用（确认没有把正常路径一起挡掉）
        var ownerCancel = await _client.CancelAsync("127.0.0.1", _port, transferId);
        Assert.True(ownerCancel.Success, ownerCancel.Message);
    }

    /// <summary>
    /// 安全回归：/approve 不得由网络调用 —— 接收确认是本机用户界面上的决定。
    /// 此前任意主机都能把自己的传输置为「已批准」，绕过配对与用户确认直接写盘。
    /// </summary>
    [Fact]
    public async Task RemoteApprove_IsRejected()
    {
        var source = CreateSourceFile("approve.bin", 512);
        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = Path.GetFileName(source),
            RelativePath = string.Empty,
            FileSize = new FileInfo(source).Length,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = new FileInfo(source).Length,
            RootName = Path.GetFileName(source),
            ConflictPolicy = "rename",
        });

        Assert.True(create.Success, create.Message);

        var approve = await _client.ApproveAsync("127.0.0.1", _port, transferId);
        Assert.False(approve.Success);
        // 必须是被「明确拒绝」（403/UNAUTHORIZED），而不是碰巧落进未知操作分支
        Assert.Equal(ErrorCodes.Unauthorized, approve.ErrorCode);

        var reject = await _client.RejectAsync("127.0.0.1", _port, transferId);
        Assert.False(reject.Success);
        Assert.Equal(ErrorCodes.Unauthorized, reject.ErrorCode);
    }

    /// <summary>
    /// 安全回归：/pair 的 deviceId 必须与请求身份一致。
    /// 否则攻击者用自己的证书 + 别人的 deviceId 就能把「可信设备」的指纹改写成自己的。
    /// </summary>
    [Fact]
    public async Task Pair_WithSpoofedDeviceId_IsRejected()
    {
        var pairing = new PairingService(_serverTrust, _serverIdentity, NullLogger<PairingService>.Instance);

        const string spoofed = "11111111-2222-3333-4444-555555555555";
        var sessionId = Guid.NewGuid().ToString();

        var response = await _client.PairAsync("127.0.0.1", _port, new PairRequest
        {
            DeviceId = spoofed,
            DeviceName = "SPOOFED-PC",
            CertificateFingerprint = _clientIdentity.CertificateFingerprint,
            PairingSessionId = sessionId,
            // 用被冒充的 deviceId 推导验证码，确保是「身份绑定」这一层把请求拦下
            VerificationCode = pairing.DeriveVerificationCode(sessionId, spoofed,
                _clientIdentity.CertificateFingerprint, _serverIdentity.DeviceId,
                _serverIdentity.CertificateFingerprint),
            ProtocolVersion = AppConstants.ProtocolVersion,
        });

        Assert.False(response.Success);
        Assert.False(await _serverTrust.IsTrustedAsync(spoofed, _clientIdentity.CertificateFingerprint));
    }

    /// <summary>
    /// 回归测试：取消后的传输必须能「继续」。
    /// 此前接收端 SetState 对终态一律拒绝、且发送端恢复时从不通知接收端复位，
    /// 于是被取消的任务永远无法恢复（发送端只会收到「该传输已被终止」）。
    /// </summary>
    [Fact]
    public async Task CancelledTransfer_CanBeResumedByOwner()
    {
        var source = CreateSourceFile("resume.bin", 1024);
        var transferId = Guid.NewGuid().ToString();

        var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = Path.GetFileName(source),
            RelativePath = string.Empty,
            FileSize = new FileInfo(source).Length,
            Sha256 = Sha256Of(source),
            ChunkSize = MiB,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = new FileInfo(source).Length,
            RootName = Path.GetFileName(source),
            ConflictPolicy = "rename",
        });

        Assert.True(create.Success, create.Message);

        var cancel = await _client.CancelAsync("127.0.0.1", _port, transferId);
        Assert.True(cancel.Success, cancel.Message);
        Assert.Equal(TransferState.Cancelled, TransferStateExtensions.FromWireString(cancel.State));

        // 属主显式「继续」：接收端必须允许 Cancelled → Transferring
        var resume = await _client.ResumeAsync("127.0.0.1", _port, transferId);
        Assert.True(resume.Success, resume.Message);
        Assert.Equal(TransferState.Transferring, TransferStateExtensions.FromWireString(resume.State));

        // 恢复后可以继续接收分块
        var payload = new byte[1024];
        using var chunk = new MemoryStream(payload, writable: false);
        var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, 0, chunk, payload.Length);
        Assert.True(upload.Success, $"{upload.ErrorCode}: {upload.Message}");
    }

    /// <summary>
    /// 回归测试：两台设备（或同一设备两个传输）同时发**同名文件**到同一目录时，
    /// 各自的临时文件必须互不干扰，先完成的文件也不能被后完成的删掉覆盖。
    /// 历史缺陷：临时文件名固定为 `name.part`，两个传输共用一个临时文件；
    /// 登记时两边都解析到同一个最终名，提交时后完成的会把先完成的删掉。
    /// </summary>
    [Fact]
    public async Task ConcurrentSameNameTransfers_DoNotClashOrOverwrite()
    {
        var payloadA = new byte[1024];
        var payloadB = new byte[1024];
        new Random(111).NextBytes(payloadA);
        new Random(222).NextBytes(payloadB);

        var dirA = Path.Combine(_root, "clash-a");
        var dirB = Path.Combine(_root, "clash-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var sourceA = Path.Combine(dirA, "clash.bin");
        var sourceB = Path.Combine(dirB, "clash.bin");
        await File.WriteAllBytesAsync(sourceA, payloadA);
        await File.WriteAllBytesAsync(sourceB, payloadB);

        var transferA = Guid.NewGuid().ToString();
        var transferB = Guid.NewGuid().ToString();

        foreach (var (id, source) in new[] { (transferA, sourceA), (transferB, sourceB) })
        {
            var create = await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
            {
                TransferType = "file",
                ProtocolVersion = AppConstants.ProtocolVersion,
                TransferId = id,
                FileName = "clash.bin",
                RelativePath = string.Empty,
                FileSize = 1024,
                Sha256 = Sha256Of(source),
                ChunkSize = MiB,
                TotalChunks = 1,
                FileIndex = 0,
                TotalFiles = 1,
                TotalSize = 1024,
                RootName = "clash.bin",
                ConflictPolicy = "rename",
            });

            Assert.True(create.Success, $"{create.ErrorCode}: {create.Message}");
        }

        // 两个传输交错上传分块
        foreach (var id in new[] { transferA, transferB })
        {
            var bytes = id == transferA ? payloadA : payloadB;
            using var chunk = new MemoryStream(bytes, writable: false);
            var upload = await _client.UploadChunkAsync("127.0.0.1", _port, id, 0, 0, chunk, bytes.Length);
            Assert.True(upload.Success, $"{upload.ErrorCode}: {upload.Message}");
        }

        var completeA = await _client.CompleteFileAsync("127.0.0.1", _port, transferA,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = Sha256Of(sourceA) });
        Assert.True(completeA.Success, $"{completeA.ErrorCode}: {completeA.Message}");

        var completeB = await _client.CompleteFileAsync("127.0.0.1", _port, transferB,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = Sha256Of(sourceB) });
        Assert.True(completeB.Success, $"{completeB.ErrorCode}: {completeB.Message}");

        // 先完成的 clash.bin 必须还在，后完成的退让成 clash (1).bin
        var first = Path.Combine(_downloadRoot, "clash.bin");
        var second = Path.Combine(_downloadRoot, "clash (1).bin");

        Assert.True(File.Exists(first), "先完成的文件被后完成的删掉了");
        Assert.True(File.Exists(second), "后完成的文件没有退让重命名，可能覆盖了先完成的文件");

        var contents = new[] { await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second) };
        Assert.Contains(contents, c => c.SequenceEqual(payloadA));
        Assert.Contains(contents, c => c.SequenceEqual(payloadB));
    }

    /// <summary>
    /// 回归测试：临时文件被删掉（清理/异常退出）后，数据库里残留的「已完成分块」位图必须被重置，
    /// 否则接收端以为分块都到齐了、永远不再收，最终必然校验失败且无法恢复。
    /// </summary>
    [Fact]
    public async Task MissingPartFile_DiscardsStaleChunkBitmap()
    {
        var payload = new byte[2 * MiB];
        new Random(333).NextBytes(payload);

        var dir = Path.Combine(_root, "stale-bitmap");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "stale.bin");
        await File.WriteAllBytesAsync(source, payload);

        var transferId = Guid.NewGuid().ToString();

        async Task<CreateTransferResponse> RegisterAsync() =>
            await _client.CreateTransferAsync("127.0.0.1", _port, new CreateTransferRequest
            {
                TransferType = "file",
                ProtocolVersion = AppConstants.ProtocolVersion,
                TransferId = transferId,
                FileName = "stale.bin",
                RelativePath = string.Empty,
                FileSize = payload.Length,
                Sha256 = Sha256Of(source),
                ChunkSize = MiB,
                TotalChunks = 2,
                FileIndex = 0,
                TotalFiles = 1,
                TotalSize = payload.Length,
                RootName = "stale.bin",
                ConflictPolicy = "rename",
            });

        var create = await RegisterAsync();
        Assert.True(create.Success, create.Message);

        // 只传第 0 块，让数据库位图与临时文件都记下「块 0 已完成」
        using (var chunk = new MemoryStream(payload, 0, MiB, writable: false))
        {
            var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, 0, chunk, MiB);
            Assert.True(upload.Success, upload.Message);
        }

        var beforeStatus = await _client.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);
        Assert.Equal(new[] { 0 }, beforeStatus.CompletedChunks.OrderBy(i => i).ToArray());

        // 模拟临时文件被清理掉
        var parts = Directory.GetFiles(_downloadRoot, "*.part", SearchOption.AllDirectories);
        Assert.NotEmpty(parts);
        foreach (var part in parts) File.Delete(part);
        foreach (var meta in Directory.GetFiles(_downloadRoot, "*.part.json", SearchOption.AllDirectories))
            File.Delete(meta);

        // 重新登记同一传输/文件：陈旧的位图必须被丢弃
        var reRegister = await RegisterAsync();
        Assert.True(reRegister.Success, reRegister.Message);

        var afterStatus = await _client.GetTransferStatusAsync("127.0.0.1", _port, transferId, 0);
        Assert.Empty(afterStatus.CompletedChunks);

        // 于是重新上传全部块才能完成 —— 旧实现会跳过块 0 并最终校验失败
        for (var index = 0; index < 2; index++)
        {
            using var chunk = new MemoryStream(payload, index * MiB, MiB, writable: false);
            var upload = await _client.UploadChunkAsync("127.0.0.1", _port, transferId, 0, index, chunk, MiB);
            Assert.True(upload.Success, $"{upload.ErrorCode}: {upload.Message}");
        }

        var complete = await _client.CompleteFileAsync("127.0.0.1", _port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = Sha256Of(source) });

        Assert.True(complete.Success, $"{complete.ErrorCode}: {complete.Message}");
        Assert.True(complete.Verified);
        Assert.Equal(payload.Length, new FileInfo(Path.Combine(_downloadRoot, "stale.bin")).Length);
    }

    [Fact]
    public async Task PortAlreadyInUse_DoesNotCrash_AndReportsLastError()
    {
        var dir = Path.Combine(_root, "conflict");
        Directory.CreateDirectory(dir);

        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await settings.LoadAsync();
        await settings.ApplyAsync(NewSettings(settings, "CONFLICT", Path.Combine(dir, "downloads"), _port));

        var database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(dir, "conflict.db"));
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(database, initializer);
        var transferRepository = new TransferRepository(database, initializer);

        var identity = new IdentityService(settings, NullLogger<IdentityService>.Instance, dir);
        await identity.InitializeAsync();

        var trust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);
        await trust.InitializeAsync();

        var registry = new IncomingTransferRegistry(settings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            transferRepository, trust, NullLogger<IncomingTransferRegistry>.Instance);

        var conflictClient = new TransferHttpClient(new PeerCertificateRegistry(), identity,
            NullLogger<TransferHttpClient>.Instance);

        var deviceManager = new DeviceManager(deviceRepository, identity, settings, conflictClient,
            new PeerCertificateRegistry(), NullLogger<DeviceManager>.Instance);

        // 复用已被主服务占用的端口
        Assert.Equal(_port, settings.Current.TransferPort);

        await using var conflicting = new TransferServer(settings, identity, deviceManager, trust,
            new PairingService(trust, identity, NullLogger<PairingService>.Instance),
            registry, NullLogger<TransferServer>.Instance);

        // 端口被占用只记录 LastError，绝不能抛出导致进程退出
        await conflicting.StartAsync();

        Assert.False(conflicting.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(conflicting.LastError));
        _output.WriteLine($"预期内的端口冲突：{conflicting.LastError}");

        conflictClient.Dispose();
    }
}
