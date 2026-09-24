using LanTransfer.Common.Models;

namespace LanTransfer.Core.Interfaces;

/// <summary>接收端收到新的传输请求（等待用户确认）或状态发生变化。</summary>
public sealed class IncomingTransferEventArgs : EventArgs
{
    public string TransferId { get; init; } = string.Empty;
    public string RemoteDeviceId { get; init; } = string.Empty;
    public string RemoteDeviceName { get; init; } = string.Empty;
    public string RootName { get; init; } = string.Empty;
    public TransferType TransferType { get; init; }
    public long TotalSize { get; init; }
    public long TransferredSize { get; init; }
    public int TotalFiles { get; init; }
    public string FirstFileName { get; init; } = string.Empty;
    public bool IsTrusted { get; init; }

    /// <summary>当前状态（接收端为唯一权威来源）。</summary>
    public TransferState State { get; init; } = TransferState.WaitingApproval;

    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>接收端文件落盘完成。</summary>
public sealed class IncomingFileCompletedEventArgs : EventArgs
{
    public string TransferId { get; init; } = string.Empty;
    public int FileIndex { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string SavedPath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public bool Verified { get; init; }
}

/// <summary>对端请求配对，需要本机用户比对验证码。</summary>
public sealed class PairingRequestedEventArgs : EventArgs
{
    public string PairingSessionId { get; init; } = string.Empty;
    public string RemoteDeviceId { get; init; } = string.Empty;
    public string RemoteDeviceName { get; init; } = string.Empty;
    public string RemoteFingerprint { get; init; } = string.Empty;

    /// <summary>本机根据会话推导出的验证码，应与对端显示一致。</summary>
    public string VerificationCode { get; init; } = string.Empty;
}

/// <summary>配对会话（两端显示同一验证码）。</summary>
public interface IPairingService
{
    /// <summary>生成本机侧配对会话（含 6 位验证码）。</summary>
    PairingSession CreateSession(string remoteDeviceId, string remoteDeviceName, string remoteFingerprint);

    /// <summary>
    /// 根据对端会话 ID 与两端证书指纹推导验证码（两端算法一致，必然相同）。
    /// 指纹必须参与推导，否则验证码只是公开值的摘要，中继型中间人可以让两端显示同一个数字。
    /// </summary>
    string DeriveVerificationCode(string pairingSessionId, string localDeviceId, string localFingerprint,
        string remoteDeviceId, string remoteFingerprint);

    /// <summary>确认配对，写入信任存储。</summary>
    Task ConfirmAsync(PairingSession session, CancellationToken cancellationToken = default);

    /// <summary>取消配对。</summary>
    void Cancel(string pairingSessionId);

    PairingSession? GetSession(string pairingSessionId);
}

public sealed class PairingSession
{
    public string PairingSessionId { get; set; } = Guid.NewGuid().ToString();
    public string RemoteDeviceId { get; set; } = string.Empty;
    public string RemoteDeviceName { get; set; } = string.Empty;
    public string RemoteFingerprint { get; set; } = string.Empty;
    public string VerificationCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Confirmed { get; set; }
}

/// <summary>本机身份：DeviceId + 自签名 TLS 证书。</summary>
public interface IIdentityService
{
    string DeviceId { get; }

    string DeviceName { get; set; }

    /// <summary>自签名证书（含私钥），进程内共享。</summary>
    System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }

    /// <summary>证书 SHA-256 指纹（十六进制大写）。</summary>
    string CertificateFingerprint { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>信任存储：已配对设备与证书指纹。</summary>
public interface ITrustStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> IsTrustedAsync(string deviceId, string? fingerprint,
        CancellationToken cancellationToken = default);

    Task<TrustState> EvaluateAsync(string deviceId, string? fingerprint,
        CancellationToken cancellationToken = default);

    Task TrustAsync(string deviceId, string deviceName, string fingerprint,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default);
}
