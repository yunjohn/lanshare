using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Security.Trust;

/// <summary>
/// 信任存储。以 DeviceId 为主键，记录证书指纹；
/// 已信任设备的证书指纹发生变化时必须提示重新确认，不得静默接受。
/// </summary>
public sealed class TrustStore : ITrustStore
{
    private readonly IDeviceRepository _repository;
    private readonly ILogger<TrustStore> _logger;

    public TrustStore(IDeviceRepository repository, ILogger<TrustStore> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _repository.InitializeAsync(cancellationToken);

    public async Task<bool> IsTrustedAsync(string deviceId, string? fingerprint,
        CancellationToken cancellationToken = default) =>
        await EvaluateAsync(deviceId, fingerprint, cancellationToken).ConfigureAwait(false) == TrustState.Trusted;

    public async Task<TrustState> EvaluateAsync(string deviceId, string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return TrustState.Unknown;

        var device = await _repository.GetAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null) return TrustState.Unknown;

        if (device.TrustState == TrustState.Revoked) return TrustState.Revoked;

        if (device.TrustState != TrustState.Trusted) return device.TrustState;

        // 已信任：必须校验证书指纹
        if (string.IsNullOrWhiteSpace(fingerprint) ||
            string.IsNullOrWhiteSpace(device.CertificateFingerprint))
        {
            _logger.LogWarning("已信任设备 {DeviceId} 缺少证书指纹，无法校验。", deviceId);
            return TrustState.IdentityChanged;
        }

        if (!string.Equals(device.CertificateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("设备身份发生变化: {DeviceId} 记录={Recorded} 实际={Actual}",
                deviceId, device.CertificateFingerprint, fingerprint);
            return TrustState.IdentityChanged;
        }

        return TrustState.Trusted;
    }

    /// <summary>
    /// 把设备标记为可信。
    ///
    /// 安全约束（此前缺失，可被用来冒充可信设备）：
    /// 1. 指纹不能为空——写入「Trusted + 空指纹」会让该设备此后永久被判 IdentityChanged，
    ///    界面却仍显示「已信任」，用户无法自解；
    /// 2. 已信任设备的指纹**不允许被静默覆盖**：指纹变化意味着身份变了（重装程序、被冒充），
    ///    必须由用户重新确认，不能因为一次配对请求就把可信身份换掉——
    ///    否则攻击者用自己的证书 + 别人的 deviceId 发一次 /pair，
    ///    用户点一次「确认」就会把真正对端的指纹覆盖掉，从此真对端被拒、攻击者畅通无阻。
    /// </summary>
    public async Task TrustAsync(string deviceId, string deviceName, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId 不能为空。", nameof(deviceId));

        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("证书指纹不能为空，拒绝写入无法校验的信任记录。", nameof(fingerprint));

        var existing = await _repository.GetAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (existing is { TrustState: TrustState.Trusted } &&
            !string.IsNullOrWhiteSpace(existing.CertificateFingerprint) &&
            !string.Equals(existing.CertificateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "拒绝覆盖已信任设备的证书指纹: {DeviceId} 记录={Recorded} 本次={Actual}；" +
                "需要用户重新确认身份后才能替换。",
                deviceId, existing.CertificateFingerprint, fingerprint);

            throw new InvalidOperationException(
                $"设备 {deviceId} 已是可信设备但证书指纹不同（{existing.CertificateFingerprint} → {fingerprint}），" +
                "已拒绝静默替换，请先解除信任并重新配对。");
        }

        var device = existing ?? new DeviceInfo
        {
            DeviceId = deviceId,
            FirstSeen = DateTimeOffset.UtcNow,
        };

        device.DeviceName = string.IsNullOrWhiteSpace(deviceName) ? device.DeviceName : deviceName;
        device.CertificateFingerprint = fingerprint;
        device.TrustState = TrustState.Trusted;
        device.LastSeen = DateTimeOffset.UtcNow;

        await _repository.UpsertAsync(device, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("设备已标记为可信: {Name} ({DeviceId}) 指纹={Fingerprint}",
            device.DeviceName, deviceId, fingerprint);
    }

    public async Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateTrustAsync(deviceId, TrustState.Revoked, null, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("已解除设备信任: {DeviceId}", deviceId);
    }
}
