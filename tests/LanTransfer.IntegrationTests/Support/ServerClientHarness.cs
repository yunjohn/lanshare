using System.Net;
using System.Net.Sockets;
using LanTransfer.Common.Constants;
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

namespace LanTransfer.IntegrationTests.Support;

/// <summary>
/// 端到端测试用的「接收端 + 发送端」最小装配：真起 Kestrel（双向 TLS），
/// 走真实 HTTP 请求，不做任何内部方法直调。
///
/// <para>
/// 抽出来是因为「安全边界」类用例越来越多（自动接收、远程 approve/reject、远程 resume…），
/// 每加一条就复制 100 行装配代码既容易抄错，也不容易看出用例之间装配的差异。
/// </para>
/// </summary>
internal sealed class ServerClientHarness : IAsyncDisposable
{
    private readonly string _directory;

    private ServerClientHarness(string directory, int port, string serverDownloadPath, string clientSourceRoot,
        TransferServer server, TransferHttpClient client, string clientDeviceId, string clientFingerprint)
    {
        _directory = directory;
        Port = port;
        ServerDownloadPath = serverDownloadPath;
        ClientSourceRoot = clientSourceRoot;
        Server = server;
        Client = client;
        ClientDeviceId = clientDeviceId;
        ClientFingerprint = clientFingerprint;
    }

    public int Port { get; }

    /// <summary>接收端的下载目录（断言「文件有没有真的落盘」用）。</summary>
    public string ServerDownloadPath { get; }

    /// <summary>发送端用来放源文件的目录。</summary>
    public string ClientSourceRoot { get; }

    public TransferServer Server { get; }

    public TransferHttpClient Client { get; }

    public string ClientDeviceId { get; }

    public string ClientFingerprint { get; }

    /// <summary>
    /// 收到确认请求时如何模拟本机用户：<c>null</c> = 不回答（保持 WaitingApproval），
    /// <c>true</c> = 点「接收」，<c>false</c> = 点「拒绝」。
    /// </summary>
    public bool? AutoRespondToPrompts { get; set; }

    private int _promptCount;

    /// <summary>本机一共弹出了几次确认框。</summary>
    public int PromptCount => Volatile.Read(ref _promptCount);

    public static async Task<ServerClientHarness> StartAsync(string root, bool autoAccept = false,
        bool trustClient = false, bool? autoRespondToPrompts = null)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var serverDirectory = Path.Combine(directory, "server");
        var clientDirectory = Path.Combine(directory, "client");
        var downloadDirectory = Path.Combine(directory, "downloads");
        var sourceDirectory = Path.Combine(directory, "source");

        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(clientDirectory);
        Directory.CreateDirectory(downloadDirectory);
        Directory.CreateDirectory(sourceDirectory);

        var port = GetFreeTcpPort();

        var serverSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(serverDirectory, "settings.json"));
        await serverSettings.LoadAsync();
        await serverSettings.ApplyAsync(NewSettings(serverSettings, "SERVER-PC", downloadDirectory, port,
            autoAccept));

        var database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(serverDirectory, "server.db"));
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(database, initializer);

        var serverIdentity = new IdentityService(serverSettings, NullLogger<IdentityService>.Instance,
            serverDirectory);
        await serverIdentity.InitializeAsync();

        var trust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);
        await trust.InitializeAsync();

        var clientSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(clientDirectory, "settings.json"));
        await clientSettings.LoadAsync();
        await clientSettings.ApplyAsync(NewSettings(clientSettings, "CLIENT-PC", clientDirectory, port,
            autoAccept: false));

        var clientIdentity = new IdentityService(clientSettings, NullLogger<IdentityService>.Instance,
            clientDirectory);
        await clientIdentity.InitializeAsync();

        if (trustClient)
        {
            // 与真实配对写入的内容一致：「设备 Id ↔ 证书指纹」
            await trust.TrustAsync(clientIdentity.DeviceId, "CLIENT-PC",
                clientIdentity.CertificateFingerprint);
        }

        var client = new TransferHttpClient(new PeerCertificateRegistry(), clientIdentity,
            NullLogger<TransferHttpClient>.Instance);

        var devices = new DeviceManager(deviceRepository, serverIdentity, serverSettings, client,
            new PeerCertificateRegistry(), NullLogger<DeviceManager>.Instance);

        var registry = new IncomingTransferRegistry(serverSettings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            new TransferRepository(database, initializer), trust,
            NullLogger<IncomingTransferRegistry>.Instance);

        var pairing = new PairingService(trust, serverIdentity, NullLogger<PairingService>.Instance);

        var server = new TransferServer(serverSettings, serverIdentity, devices, trust, pairing, registry,
            NullLogger<TransferServer>.Instance);

        var harness = new ServerClientHarness(directory, port, downloadDirectory, sourceDirectory, server,
            client, clientIdentity.DeviceId, clientIdentity.CertificateFingerprint)
        {
            AutoRespondToPrompts = autoRespondToPrompts,
        };

        server.IncomingTransferRequested += (_, e) =>
        {
            Interlocked.Increment(ref harness._promptCount);

            // 默认（null）不回答：调用方要看的就是「用户还没确认时能不能写盘」
            if (harness.AutoRespondToPrompts is { } approve)
                server.RespondToApproval(e.TransferId, approve, rememberDevice: false);
        };

        await server.StartAsync();
        Assert.True(server.IsRunning, $"接收端启动失败：{server.LastError}");

        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        try { Client.Dispose(); } catch { /* 忽略 */ }
        try { await Server.StopAsync(); } catch { /* 忽略 */ }
        try { await Server.DisposeAsync(); } catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 便利方法

    /// <summary>在发送端创建一个小源文件，返回其绝对路径。</summary>
    public string CreateSourceFile(string name, int size, byte seed = 0x5A)
    {
        var path = Path.Combine(ClientSourceRoot, name);
        var content = new byte[size];

        for (var i = 0; i < size; i++) content[i] = (byte)(seed + i % 251);

        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>构造一个「单文件、单分块」的建传输请求（哈希取自真实源文件）。</summary>
    public CreateTransferRequest NewRequest(string transferId, string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)));

        return new CreateTransferRequest
        {
            TransferType = "file",
            ProtocolVersion = AppConstants.ProtocolVersion,
            TransferId = transferId,
            FileName = info.Name,
            RelativePath = string.Empty,
            FileSize = info.Length,
            Sha256 = sha,
            ChunkSize = AppConstants.DefaultChunkSize,
            TotalChunks = 1,
            FileIndex = 0,
            TotalFiles = 1,
            TotalSize = info.Length,
            RootName = info.Name,
            ConflictPolicy = "rename",
        };
    }

    /// <summary>把一个文件完整推给接收端：建传输 → 传分块 → 校验完成。</summary>
    public async Task<(CreateTransferResponse Create, ChunkUploadResponse Chunk, CompleteTransferResponse Complete)>
        PushFileAsync(string transferId, string sourcePath)
    {
        var create = await Client.CreateTransferAsync(IPAddress.Loopback.ToString(), Port,
            NewRequest(transferId, sourcePath));

        // 审批是异步推进的（确认框上点「接收」之后状态才会变成 transferring），
        // 「建传输」的响应里状态很可能还是 waiting-approval —— 不等一下就是竞态。
        if (string.Equals(create.State, "waiting-approval", StringComparison.OrdinalIgnoreCase))
            await WaitForStateAsync(transferId, "transferring");

        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        using var stream = new MemoryStream(bytes);

        var chunk = await Client.UploadChunkAsync(IPAddress.Loopback.ToString(), Port, transferId, 0, 0,
            stream, bytes.Length);

        var complete = await Client.CompleteFileAsync(IPAddress.Loopback.ToString(), Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        return (create, chunk, complete);
    }

    /// <summary>轮询等待接收端状态变为期望值（审批、校验都是异步推进的）。</summary>
    public async Task<string> WaitForStateAsync(string transferId, string expected, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var state = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var status = await Client.GetTransferStatusAsync(IPAddress.Loopback.ToString(), Port, transferId, 0);
            state = status.State;

            if (string.Equals(state, expected, StringComparison.OrdinalIgnoreCase)) return state;

            await Task.Delay(25);
        }

        Assert.Fail($"等待传输 {transferId} 进入 {expected} 超时（当前 {state}）");
        return state;
    }

    public string DownloadedPath(string fileName) => Path.Combine(ServerDownloadPath, fileName);

    private static AppSettings NewSettings(SettingsService service, string name, string downloadPath,
        int port, bool autoAccept) => new()
    {
        DeviceId = service.Current.DeviceId,
        DeviceName = name,
        DiscoveryPort = AppConstants.DefaultDiscoveryPort,
        TransferPort = port,
        DownloadPath = downloadPath,
        ChunkSize = AppConstants.DefaultChunkSize,
        AutoAcceptTrustedDevice = autoAccept,
    };

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
