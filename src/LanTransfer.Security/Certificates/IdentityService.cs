using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LanTransfer.Common.Constants;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Security.Certificates;

/// <summary>
/// 本机身份：DeviceId（首次生成后永久保存） + 自签名 TLS 证书。
/// 证书不需要安装到 Windows 根证书存储，由应用自行维护信任；
/// 私钥口令使用 Windows DPAPI（CurrentUser 作用域）保护，仅本机可解。
/// </summary>
public sealed class IdentityService : IIdentityService
{
    private const int CertificateValidYears = 10;

    private readonly ISettingsService _settings;
    private readonly ILogger<IdentityService> _logger;
    private readonly string _certificateFile;
    private readonly string _keyFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private X509Certificate2? _certificate;

    /// <param name="settings">配置服务（提供 DeviceId / DeviceName）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="identityDirectory">
    /// 证书与口令的存放目录。默认 %LOCALAPPDATA%\LanTransfer；
    /// 显式传入便于测试与便携部署，避免硬编码路径。
    /// </param>
    public IdentityService(ISettingsService settings, ILogger<IdentityService> logger,
        string? identityDirectory = null)
    {
        _settings = settings;
        _logger = logger;

        var directory = identityDirectory ?? AppPaths.RootDirectory;
        _certificateFile = Path.Combine(directory, "identity.pfx");
        _keyFile = Path.Combine(directory, "identity.key");
    }

    /// <summary>当前证书文件绝对路径。</summary>
    public string CertificateFilePath => _certificateFile;

    public string DeviceId => _settings.Current.DeviceId;

    public string DeviceName
    {
        get => _settings.Current.DeviceName;
        set => _settings.Current.DeviceName = value;
    }

    public X509Certificate2 Certificate =>
        _certificate ?? throw new InvalidOperationException("身份服务尚未初始化。");

    public string CertificateFingerprint { get; private set; } = string.Empty;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_certificate is not null) return;

            EnsureDirectory();

            _certificate = LoadOrCreateCertificate();
            CertificateFingerprint = ComputeFingerprint(_certificate);

            _logger.LogInformation("本机身份就绪: DeviceId={DeviceId} 证书指纹={Fingerprint}",
                DeviceId, CertificateFingerprint);
        }
        finally
        {
            _gate.Release();
        }
    }

    private X509Certificate2 LoadOrCreateCertificate()
    {
        if (File.Exists(_certificateFile) && File.Exists(_keyFile))
        {
            try
            {
                var password = UnprotectPassword();
                var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                    _certificateFile,
                    password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

                if (loaded.HasPrivateKey && !IsExpiringSoon(loaded))
                {
                    _logger.LogInformation("已加载本机自签名证书，有效期至 {NotAfter:u}", loaded.NotAfter);
                    return loaded;
                }

                _logger.LogWarning("本机证书缺少私钥或即将过期，将重新生成。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载本机证书失败，将重新生成。");
            }
        }

        return CreateAndPersistCertificate();
    }

    private X509Certificate2 CreateAndPersistCertificate()
    {
        using var rsa = RSA.Create(2048);

        var subject = new X500DistinguishedName($"CN={Sanitize(DeviceName)}");

        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        // 同时具备 serverAuth 与 clientAuth，用于双向 TLS：服务端据此确认对端身份
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // serverAuth
                new("1.3.6.1.5.5.7.3.2"), // clientAuth
            }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);

        foreach (var address in NetworkHelper.GetLocalIPv4Addresses())
        {
            if (IPAddress.TryParse(address, out var parsed))
                sanBuilder.AddIpAddress(parsed);
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(CertificateValidYears);

        using var generated = request.CreateSelfSigned(notBefore, notAfter);

        // 通过 PFX 往返一次，确保私钥可持久化使用
        var password = GeneratePassword();
        var pfxBytes = generated.Export(X509ContentType.Pfx, password);
        var certificate = X509CertificateLoader.LoadPkcs12(pfxBytes, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

        File.WriteAllBytes(_certificateFile, pfxBytes);
        File.WriteAllBytes(_keyFile, ProtectPassword(password));

        TryRestrictFilePermissions(_certificateFile);
        TryRestrictFilePermissions(_keyFile);

        _logger.LogInformation("已生成新的自签名证书，有效期至 {NotAfter:u}", certificate.NotAfter);
        return certificate;
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_certificateFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public static string ComputeFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static bool IsExpiringSoon(X509Certificate2 certificate) =>
        certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30);

    private static string Sanitize(string name) =>
        new(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static byte[] ProtectPassword(string password) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);

    private string UnprotectPassword()
    {
        var protectedBytes = File.ReadAllBytes(_keyFile);
        var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private void TryRestrictFilePermissions(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(true, false);
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().Name,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow);
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // 权限收紧失败不影响功能，但必须留痕
            _logger.LogWarning(ex, "收紧文件权限失败: {Path}", path);
        }
    }
}
