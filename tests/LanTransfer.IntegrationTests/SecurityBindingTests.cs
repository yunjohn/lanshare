using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Interfaces;
using LanTransfer.Network.Client;
using LanTransfer.Security.Certificates;
using LanTransfer.Security.Pairing;
using LanTransfer.Security.Trust;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 安全绑定回归测试：
/// 1) 配对验证码必须绑定双方**证书指纹**，否则中继型中间人可以让两端显示同一个数字；
/// 2) 可信设备的端点必须钉在其已记录的证书指纹上，否则伪造 UDP 报文即可把文件引向攻击者。
/// </summary>
public sealed class SecurityBindingTests : IDisposable
{
    private readonly string _root;

    public SecurityBindingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-secbind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private async Task<IdentityService> CreateIdentityAsync(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await settings.LoadAsync();
        await settings.ApplyAsync(new AppSettings
        {
            DeviceId = settings.Current.DeviceId,
            DeviceName = name,
            DiscoveryPort = AppConstants.DefaultDiscoveryPort,
            TransferPort = AppConstants.DefaultTransferPort,
            DownloadPath = dir,
        });

        var identity = new IdentityService(settings, NullLogger<IdentityService>.Instance, dir);
        await identity.InitializeAsync();
        return identity;
    }

    private static PairingService CreatePairingService(IdentityService identity, ITrustStore trust) =>
        new(trust, identity, NullLogger<PairingService>.Instance);

    /// <summary>
    /// 回归测试：验证码必须与证书指纹相关，且两端推导对称。
    /// 历史缺陷：验证码只由 DeviceId + sessionId 推导，而这两者都是公开值
    /// （UDP 广播/GET /device 给出 DeviceId，sessionId 由发起方选定），
    /// 于是中间人可离线算出任意一端显示的码，也能为两端挑一个相同的码。
    /// </summary>
    [Fact]
    public async Task PairingCode_IsSymmetric_AndBindsCertificateFingerprint()
    {
        var identityA = await CreateIdentityAsync("PC-A");
        var identityB = await CreateIdentityAsync("PC-B");
        var identityM = await CreateIdentityAsync("MITM");

        // 三个身份各自一个 TrustStore（只是构造 PairingService 需要）
        var trust = new TrustStore(new DeviceRepository(
                new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
                    Path.Combine(_root, "trust.db")),
                new DatabaseInitializer(
                    new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
                        Path.Combine(_root, "trust.db")),
                    NullLogger<DatabaseInitializer>.Instance)),
            NullLogger<TrustStore>.Instance);

        var pairingA = CreatePairingService(identityA, trust);
        var pairingB = CreatePairingService(identityB, trust);
        var pairingM = CreatePairingService(identityM, trust);

        var sessionId = Guid.NewGuid().ToString();

        // 正常两端：顺序无关，必然得到同一个码
        var codeFromA = pairingA.DeriveVerificationCode(sessionId,
            identityA.DeviceId, identityA.CertificateFingerprint,
            identityB.DeviceId, identityB.CertificateFingerprint);

        var codeFromB = pairingB.DeriveVerificationCode(sessionId,
            identityB.DeviceId, identityB.CertificateFingerprint,
            identityA.DeviceId, identityA.CertificateFingerprint);

        Assert.Equal(codeFromA, codeFromB);

        // 中继型中间人：对 A 谎称自己就是 B（DeviceId 与指纹都是公开值，可以照抄），
        // 但它连到 B 时**只能出示自己的证书**（TLS 里的指纹伪造不了）。
        // 于是 A 屏幕上是 f(…idB,fpB…)，B 屏幕上是 f(…idA,fpM…)：两端必须不同，用户一眼能看出中间人。
        var codeSeenByA = pairingA.DeriveVerificationCode(sessionId,
            identityA.DeviceId, identityA.CertificateFingerprint,
            identityB.DeviceId, identityB.CertificateFingerprint);

        var codeSeenByB = pairingB.DeriveVerificationCode(sessionId,
            identityA.DeviceId, identityM.CertificateFingerprint,
            identityB.DeviceId, identityB.CertificateFingerprint);

        Assert.NotEqual(codeSeenByA, codeSeenByB);

        // 同一对设备、同一会话，但指纹被替换（伪造）时必须得到不同的码
        var spoofed = pairingA.DeriveVerificationCode(sessionId,
            identityA.DeviceId, identityA.CertificateFingerprint,
            identityB.DeviceId, identityM.CertificateFingerprint);

        Assert.NotEqual(codeFromA, spoofed);
    }

    /// <summary>
    /// 回归测试：伪造 UDP 报文改写可信设备端点后，新端点必须被钉在该设备**已记录的证书指纹**上，
    /// 这样冒充者拿不出对应私钥、TLS 握手会失败，文件不会发给它。
    /// </summary>
    [Fact]
    public async Task TrustedDevice_EndpointChange_PinsNewAddressToRecordedCertificate()
    {
        var dir = Path.Combine(_root, "manager");
        Directory.CreateDirectory(dir);

        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(dir, "settings.json"));
        await settings.LoadAsync();
        await settings.ApplyAsync(new AppSettings
        {
            DeviceId = settings.Current.DeviceId,
            DeviceName = "LOCAL-PC",
            TransferPort = AppConstants.DefaultTransferPort,
            DownloadPath = dir,
        });

        var database = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance,
            Path.Combine(dir, "devices.db"));
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        var repository = new DeviceRepository(database, initializer);

        var identity = new IdentityService(settings, NullLogger<IdentityService>.Instance, dir);
        await identity.InitializeAsync();

        var certificates = new PeerCertificateRegistry();
        var client = new TransferHttpClient(certificates, identity, NullLogger<TransferHttpClient>.Instance);

        await using var manager = new DeviceManager(repository, identity, settings, client, certificates,
            NullLogger<DeviceManager>.Instance);

        const string trustedId = "trusted-device-1";
        const string realIp = "10.0.0.10";
        const string spoofedIp = "10.0.0.99";
        const string realFingerprint = "AAAABBBBCCCCDDDD0000111122223333";

        // 库里已有一台可信设备（含证书指纹）
        await repository.UpsertAsync(new DeviceInfo
        {
            DeviceId = trustedId,
            DeviceName = "PEER-PC",
            IpAddress = realIp,
            Port = AppConstants.DefaultTransferPort,
            TrustState = TrustState.Trusted,
            CertificateFingerprint = realFingerprint,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await manager.InitializeAsync();

        // 启动即钉住真实端点
        Assert.Equal(realFingerprint, certificates.GetExpected(realIp));

        // 攻击者伪造报文：声称自己是该可信设备，但来自另一个 IP
        await manager.ReportSeenAsync(new DeviceInfo
        {
            DeviceId = trustedId,
            DeviceName = "PEER-PC",
            IpAddress = spoofedIp,
            Port = AppConstants.DefaultTransferPort,
            OnlineState = OnlineState.Online,
            LastSeen = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // 关键断言：伪造端点被钉在**真实设备的指纹**上，
        // 因此连过去时对方必须出示该证书 —— 冒充者过不了 TLS 握手。
        Assert.Equal(realFingerprint, certificates.GetExpected(spoofedIp));

        // 信任状态不被 UDP 报文降级
        var device = manager.Find(trustedId);
        Assert.NotNull(device);
        Assert.Equal(TrustState.Trusted, device!.TrustState);
        Assert.Equal(realFingerprint, device.CertificateFingerprint);
    }
}
