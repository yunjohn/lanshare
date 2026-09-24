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

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 配对回归测试。
///
/// 历史缺陷：<see cref="TransferHttpClient"/> 在构造函数里快照 <c>identity.Certificate</c>，
/// 而 App 的启动顺序是「先解析全部服务（含 HttpClient）→ 再 InitializeAsync 身份」，
/// 那一刻取证书会抛「身份服务尚未初始化」并被 catch 吞掉，于是客户端证书永久缺失。
/// 后果：对端 /pair 以 400「未收到客户端证书」拒绝，且拒绝发生在抛出 PairingRequested
/// 之前 —— 表现为「一端弹出配对确认，另一端毫无反应」。
///
/// 本测试固定「先造 HttpClient、后初始化身份」的顺序，断言配对能成功、
/// 且对端拿到的证书指纹与本机一致（即证书确实被出示了）。
/// </summary>
public sealed class PairingClientCertificateTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly int _port;

    private SettingsService _serverSettings = null!;
    private SettingsService _clientSettings = null!;
    private IdentityService _serverIdentity = null!;
    private IdentityService _clientIdentity = null!;
    private TrustStore _serverTrust = null!;
    private TransferServer _server = null!;
    private TransferHttpClient _client = null!;
    private PairingService _serverPairing = null!;
    private DeviceManager _serverDevices = null!;

    public PairingClientCertificateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _port = GetFreeTcpPort();
    }

    public async Task InitializeAsync()
    {
        await StartServerAsync();
        await CreateClientBeforeIdentityIsInitializedAsync();
    }

    public async Task DisposeAsync()
    {
        try { _client.Dispose(); } catch { /* 忽略 */ }
        try { await _server.StopAsync(); } catch { /* 忽略 */ }
        try { await _server.DisposeAsync(); } catch { /* 忽略 */ }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 用例

    [Fact]
    public async Task Pair_succeeds_and_peer_receives_certificate_fingerprint()
    {
        PairingRequestedEventArgs? seen = null;

        // 模拟对端用户在配对弹窗上点击「验证码一致，确认配对」
        _server.PairingRequested += (_, e) =>
        {
            seen = e;
            _server.RespondToPairing(e.PairingSessionId, true);
        };

        var sessionId = Guid.NewGuid().ToString();
        var request = new PairRequest
        {
            DeviceId = _clientIdentity.DeviceId,
            DeviceName = "CLIENT-PC",
            CertificateFingerprint = _clientIdentity.CertificateFingerprint,
            PairingSessionId = sessionId,
            VerificationCode = _serverPairing.DeriveVerificationCode(
                sessionId, _clientIdentity.DeviceId, _clientIdentity.CertificateFingerprint,
                _serverIdentity.DeviceId, _serverIdentity.CertificateFingerprint),
            ProtocolVersion = AppConstants.ProtocolVersion,
        };

        await _serverDevices.ReportSeenAsync(new LanTransfer.Common.Models.DeviceInfo
        {
            DeviceId = _clientIdentity.DeviceId,
            DeviceName = "CLIENT-PC",
            IpAddress = IPAddress.Loopback.ToString(),
            Port = _port,
            TrustState = LanTransfer.Common.Models.TrustState.Unknown,
            OnlineState = LanTransfer.Common.Models.OnlineState.Online,
        });

        var response = await _client.PairAsync(IPAddress.Loopback.ToString(), _port, request);

        Assert.True(response.Success,
            $"配对失败：{response.ErrorCode} {response.Message}（客户端证书很可能没有被出示）");
        Assert.True(response.Accepted);

        // 对端确实弹出了确认框，并且看到了本机的证书指纹
        Assert.NotNull(seen);
        Assert.Equal(_clientIdentity.CertificateFingerprint, seen!.RemoteFingerprint);
        Assert.Equal(_clientIdentity.DeviceId, seen.RemoteDeviceId);

        // 双方各自把对方记为可信
        Assert.True(await _serverTrust.IsTrustedAsync(_clientIdentity.DeviceId,
            _clientIdentity.CertificateFingerprint));
        Assert.Equal(LanTransfer.Common.Models.TrustState.Trusted,
            _serverDevices.Find(_clientIdentity.DeviceId)!.TrustState);

        // 后续 UDP/HTTPS 心跳只携带在线信息，不能用 Unknown 覆盖刚建立的信任关系。
        await _serverDevices.ReportSeenAsync(new LanTransfer.Common.Models.DeviceInfo
        {
            DeviceId = _clientIdentity.DeviceId,
            DeviceName = "CLIENT-PC",
            IpAddress = IPAddress.Loopback.ToString(),
            Port = _port,
            TrustState = LanTransfer.Common.Models.TrustState.Unknown,
            OnlineState = LanTransfer.Common.Models.OnlineState.Online,
        });

        Assert.True(await _serverTrust.IsTrustedAsync(_clientIdentity.DeviceId,
            _clientIdentity.CertificateFingerprint));
        Assert.Equal(LanTransfer.Common.Models.TrustState.Trusted,
            _serverDevices.Find(_clientIdentity.DeviceId)!.TrustState);
    }

    [Fact]
    public async Task Pair_rejects_incompatible_protocol_before_showing_prompt()
    {
        var promptRaised = false;
        _server.PairingRequested += (_, _) => promptRaised = true;

        var sessionId = Guid.NewGuid().ToString();
        var response = await _client.PairAsync(IPAddress.Loopback.ToString(), _port, new PairRequest
        {
            DeviceId = _clientIdentity.DeviceId,
            DeviceName = "CLIENT-PC",
            CertificateFingerprint = _clientIdentity.CertificateFingerprint,
            PairingSessionId = sessionId,
            VerificationCode = _serverPairing.DeriveVerificationCode(
                sessionId, _clientIdentity.DeviceId, _clientIdentity.CertificateFingerprint,
                _serverIdentity.DeviceId, _serverIdentity.CertificateFingerprint),
            ProtocolVersion = AppConstants.ProtocolVersion + 1,
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ProtocolIncompatible, response.ErrorCode);
        Assert.False(promptRaised);
    }

    [Fact]
    public async Task Client_certificate_is_present_on_ordinary_requests()
    {
        // 普通请求（不含配对）也应带上证书，否则服务端无法判定设备可信
        var info = await _client.GetDeviceInfoAsync(IPAddress.Loopback.ToString(), _port);

        Assert.False(string.IsNullOrWhiteSpace(info.DeviceId));
        Assert.Equal(_serverIdentity.DeviceId, info.DeviceId);
        Assert.Equal(_serverIdentity.CertificateFingerprint, info.CertificateFingerprint);
    }

    // ---------------------------------------------------------------- 装配

    private async Task StartServerAsync()
    {
        var dir = Path.Combine(_root, "server");
        Directory.CreateDirectory(dir);

        _serverSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await _serverSettings.LoadAsync();
        await _serverSettings.ApplyAsync(NewSettings(_serverSettings, "SERVER-PC", dir, _port));

        var database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(dir, "server.db"));
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        var deviceRepository = new DeviceRepository(database, initializer);
        var transferRepository = new TransferRepository(database, initializer);

        _serverIdentity = new IdentityService(_serverSettings, NullLogger<IdentityService>.Instance, dir);
        await _serverIdentity.InitializeAsync();

        _serverTrust = new TrustStore(deviceRepository, NullLogger<TrustStore>.Instance);
        await _serverTrust.InitializeAsync();

        _serverPairing = new PairingService(_serverTrust, _serverIdentity,
            NullLogger<PairingService>.Instance);

        var serverHttpClient = new TransferHttpClient(new PeerCertificateRegistry(), _serverIdentity,
            NullLogger<TransferHttpClient>.Instance);

        _serverDevices = new DeviceManager(deviceRepository, _serverIdentity, _serverSettings,
            serverHttpClient, new PeerCertificateRegistry(), NullLogger<DeviceManager>.Instance);

        var registry = new IncomingTransferRegistry(_serverSettings, new SafePathResolver(),
            new ChunkManager(NullLogger<ChunkManager>.Instance),
            new HashService(NullLogger<HashService>.Instance),
            transferRepository, _serverTrust, NullLogger<IncomingTransferRegistry>.Instance);

        _server = new TransferServer(_serverSettings, _serverIdentity, _serverDevices, _serverTrust,
            _serverPairing, registry, NullLogger<TransferServer>.Instance);

        await _server.StartAsync();
        Assert.True(_server.IsRunning, $"接收端启动失败：{_server.LastError}");
    }

    /// <summary>
    /// 复刻 App 的真实启动顺序：先构造 HttpClient，之后才初始化身份服务。
    /// </summary>
    private async Task CreateClientBeforeIdentityIsInitializedAsync()
    {
        var dir = Path.Combine(_root, "client");
        Directory.CreateDirectory(dir);

        _clientSettings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await _clientSettings.LoadAsync();
        await _clientSettings.ApplyAsync(NewSettings(_clientSettings, "CLIENT-PC", dir, _port));

        _clientIdentity = new IdentityService(_clientSettings, NullLogger<IdentityService>.Instance, dir);

        // 故意不 await InitializeAsync()：此时 identity.Certificate 会抛异常
        _client = new TransferHttpClient(new PeerCertificateRegistry(), _clientIdentity,
            NullLogger<TransferHttpClient>.Instance);

        await _clientIdentity.InitializeAsync();
    }

    private static AppSettings NewSettings(SettingsService service, string name, string downloadPath,
        int port) => new()
    {
        DeviceId = service.Current.DeviceId,
        DeviceName = name,
        DiscoveryPort = AppConstants.DefaultDiscoveryPort,
        TransferPort = port,
        DownloadPath = downloadPath,
        ChunkSize = 1024 * 1024,
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
}
