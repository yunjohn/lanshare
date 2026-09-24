using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Security.Pairing;

/// <summary>
/// 设备配对。两端基于「排序后的两个 DeviceId + 配对会话 ID」推导同一个 6 位验证码，
/// 因此用户只需肉眼比对两端显示的数字是否一致。
/// </summary>
public sealed class PairingService : IPairingService
{
    private readonly ITrustStore _trustStore;
    private readonly IIdentityService _identity;
    private readonly ILogger<PairingService> _logger;
    private readonly ConcurrentDictionary<string, PairingSession> _sessions = new();

    public PairingService(ITrustStore trustStore, IIdentityService identity, ILogger<PairingService> logger)
    {
        _trustStore = trustStore;
        _identity = identity;
        _logger = logger;
    }

    public PairingSession CreateSession(string remoteDeviceId, string remoteDeviceName, string remoteFingerprint)
    {
        var session = new PairingSession
        {
            RemoteDeviceId = remoteDeviceId,
            RemoteDeviceName = remoteDeviceName,
            RemoteFingerprint = remoteFingerprint,
        };

        session.VerificationCode = DeriveVerificationCode(session.PairingSessionId, _identity.DeviceId,
            _identity.CertificateFingerprint, remoteDeviceId, remoteFingerprint);

        _sessions[session.PairingSessionId] = session;

        _logger.LogInformation("创建配对会话 {SessionId} 与设备 {Device} ({DeviceId})",
            session.PairingSessionId, remoteDeviceName, remoteDeviceId);

        return session;
    }

    /// <summary>
    /// 验证码推导：对「排序后的 (DeviceId, 证书指纹) 对 + 会话 ID」做 SHA-256，取前 4 字节映射到 6 位数字。
    ///
    /// 为什么必须把**证书指纹**一起纳入：DeviceId 与 sessionId 都是公开值
    /// （UDP 广播与 GET /device 都会给出 DeviceId，会话 ID 由发起方选定），
    /// 只凭它们推导等于对外公开验证码 —— 中继型中间人可以为两端各挑一个会话 ID，
    /// 让两个屏幕显示同一个数字，从而通过「肉眼比对」这道唯一的人工闸门。
    /// 一旦把两端证书指纹纳入，中继者的两条 TLS 连接指纹不同，两端显示的验证码必然不同，用户一眼就能发现。
    /// </summary>
    public string DeriveVerificationCode(string pairingSessionId, string localDeviceId,
        string localFingerprint, string remoteDeviceId, string remoteFingerprint)
    {
        // 按 DeviceId 排序，保证两端拼出的字符串完全一致
        var localFirst = string.CompareOrdinal(localDeviceId, remoteDeviceId) <= 0;

        var firstId = localFirst ? localDeviceId : remoteDeviceId;
        var firstFp = localFirst ? localFingerprint : remoteFingerprint;
        var secondId = localFirst ? remoteDeviceId : localDeviceId;
        var secondFp = localFirst ? remoteFingerprint : localFingerprint;

        var payload = $"{Normalize(firstId)}|{Normalize(firstFp)}|{Normalize(secondId)}|" +
                      $"{Normalize(secondFp)}|{pairingSessionId}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        var value = ((uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3]) % 1_000_000u;

        return value.ToString("D6");

        // 指纹大小写在不同来源间可能不一致，统一成大写再参与推导
        static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    public async Task ConfirmAsync(PairingSession session, CancellationToken cancellationToken = default)
    {
        await _trustStore.TrustAsync(session.RemoteDeviceId, session.RemoteDeviceName,
            session.RemoteFingerprint, cancellationToken).ConfigureAwait(false);

        session.Confirmed = true;
        _sessions.TryRemove(session.PairingSessionId, out _);

        _logger.LogInformation("配对已确认: {Device} ({DeviceId})", session.RemoteDeviceName,
            session.RemoteDeviceId);
    }

    public void Cancel(string pairingSessionId)
    {
        if (_sessions.TryRemove(pairingSessionId, out var session))
            _logger.LogInformation("配对已取消: {Device}", session.RemoteDeviceName);
    }

    public PairingSession? GetSession(string pairingSessionId) =>
        _sessions.TryGetValue(pairingSessionId, out var session) ? session : null;
}
