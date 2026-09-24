using System.Text.Json.Serialization;

namespace LanTransfer.Common.Protocol;

/// <summary>
/// 统一错误码。任何 API 失败都必须返回其中之一，禁止各接口自定义结构。
/// </summary>
public static class ErrorCodes
{
    public const string None = "NONE";
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string ProtocolIncompatible = "PROTOCOL_INCOMPATIBLE";
    public const string DeviceNotTrusted = "DEVICE_NOT_TRUSTED";
    public const string IdentityChanged = "DEVICE_IDENTITY_CHANGED";
    public const string PairingRejected = "PAIRING_REJECTED";
    public const string InvalidPairingCode = "INVALID_PAIRING_CODE";
    public const string TransferNotFound = "TRANSFER_NOT_FOUND";
    public const string FileNotFound = "FILE_NOT_FOUND";

    /// <summary>接收端按「跳过」重名策略拒绝该文件（目标已存在，未被覆盖）。</summary>
    public const string FileExistsSkipped = "FILE_EXISTS_SKIPPED";

    /// <summary>
    /// 接收端「等待用户确认」的传输过多，拒绝登记新的传输。
    /// 本接口对未配对设备开放，没有这道闸门时任何人都能用脚本换着 transferId 刷确认框。
    /// </summary>
    public const string TooManyPendingRequests = "TOO_MANY_PENDING_REQUESTS";
    public const string TransferStateConflict = "TRANSFER_STATE_CONFLICT";
    public const string ChunkIndexOutOfRange = "CHUNK_INDEX_OUT_OF_RANGE";
    public const string ChunkSizeMismatch = "CHUNK_SIZE_MISMATCH";
    public const string InsufficientDiskSpace = "INSUFFICIENT_DISK_SPACE";
    public const string PathEscapeDetected = "PATH_ESCAPE_DETECTED";
    public const string InvalidFileName = "INVALID_FILE_NAME";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string SourceChangedDuringTransfer = "SOURCE_CHANGED_DURING_TRANSFER";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string RejectedByUser = "REJECTED_BY_USER";
    public const string CancelledByUser = "CANCELLED_BY_USER";
    public const string NetworkUnreachable = "NETWORK_UNREACHABLE";
    public const string Timeout = "TIMEOUT";
    public const string FirewallSuspected = "FIREWALL_SUSPECTED";
    public const string SyncPairNotFound = "SYNC_PAIR_NOT_FOUND";
    public const string SyncPairError = "SYNC_PAIR_ERROR";
    public const string SyncConflict = "SYNC_CONFLICT";
    public const string SyncRejected = "SYNC_REJECTED";
}

/// <summary>统一错误响应体。</summary>
public sealed class ApiErrorResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = false;

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = ErrorCodes.InternalError;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>可选补充信息（例如磁盘剩余空间、冲突文件路径）。</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Details { get; set; }

    public static ApiErrorResponse Create(string errorCode, string message,
        Dictionary<string, string>? details = null) => new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Details = details,
        };
}

/// <summary>发现协议报文类型。</summary>
public static class DiscoveryMessageTypes
{
    public const string Discover = "discover";
    public const string DiscoverResponse = "discover-response";
}

/// <summary>UDP 发现请求 / 响应报文。</summary>
public sealed class DiscoveryMessage
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = Common.Constants.AppConstants.ProtocolName;

    [JsonPropertyName("type")]
    public string Type { get; set; } = DiscoveryMessageTypes.Discover;

    [JsonPropertyName("version")]
    public int Version { get; set; } = Common.Constants.AppConstants.ProtocolVersion;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = Common.Constants.AppConstants.AppVersion;

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>GET /api/v1/health 响应。</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Common.Constants.AppConstants.ProtocolVersion;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = Common.Constants.AppConstants.AppVersion;

    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>GET /api/v1/device 响应。</summary>
public sealed class DeviceResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = Common.Constants.AppConstants.AppVersion;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Common.Constants.AppConstants.ProtocolVersion;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("certificateFingerprint")]
    public string CertificateFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = string.Empty;
}

/// <summary>POST /api/v1/pair 请求。</summary>
public sealed class PairRequest
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = Common.Constants.AppConstants.AppVersion;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Common.Constants.AppConstants.ProtocolVersion;

    [JsonPropertyName("certificateFingerprint")]
    public string CertificateFingerprint { get; set; } = string.Empty;

    /// <summary>配对会话 ID（由发起方生成，两端据此显示同一验证码）。</summary>
    [JsonPropertyName("pairingSessionId")]
    public string PairingSessionId { get; set; } = string.Empty;

    /// <summary>本机根据会话 ID 计算出的 6 位验证码。</summary>
    [JsonPropertyName("verificationCode")]
    public string VerificationCode { get; set; } = string.Empty;
}

/// <summary>POST /api/v1/pair 响应。</summary>
public sealed class PairResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("verificationCode")]
    public string VerificationCode { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("certificateFingerprint")]
    public string CertificateFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>POST /api/v1/transfers 请求。</summary>
public sealed class CreateTransferRequest
{
    [JsonPropertyName("transferType")]
    public string TransferType { get; set; } = "file";

    /// <summary>发送端协议版本。不兼容时接收端必须明确拒绝，而不是尝试降级。</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Common.Constants.AppConstants.ProtocolVersion;

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; } = Common.Constants.AppConstants.DefaultChunkSize;

    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }

    [JsonPropertyName("fileIndex")]
    public int FileIndex { get; set; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; } = 1;

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("rootName")]
    public string RootName { get; set; } = string.Empty;

    [JsonPropertyName("conflictPolicy")]
    public string ConflictPolicy { get; set; } = "rename";

    [JsonPropertyName("lastWriteTimeUtc")]
    public DateTimeOffset? LastWriteTimeUtc { get; set; }

    /// <summary>
    /// 同步关系 ID。非空时接收端把文件写入该同步关系的目录（而不是默认下载目录）。
    /// 接收端只接受已登记且已获用户授权的同步关系 ID。
    /// </summary>
    [JsonPropertyName("syncPairId")]
    public string? SyncPairId { get; set; }
}

/// <summary>POST /api/v1/transfers 响应。</summary>
public sealed class CreateTransferResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "waiting-approval";

    [JsonPropertyName("resolvedFileName")]
    public string? ResolvedFileName { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Details { get; set; }
}

/// <summary>GET /api/v1/transfers/{id} 响应（断点续传的核心）。</summary>
public sealed class TransferStatusResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "pending";

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; }

    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }

    [JsonPropertyName("completedChunks")]
    public List<int> CompletedChunks { get; set; } = new();

    [JsonPropertyName("receivedBytes")]
    public long ReceivedBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>PUT chunk 响应。</summary>
public sealed class ChunkUploadResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("completedChunks")]
    public int CompletedChunks { get; set; }

    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "transferring";

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>POST complete 请求 / 响应。</summary>
public sealed class CompleteTransferRequest
{
    [JsonPropertyName("fileIndex")]
    public int FileIndex { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class CompleteTransferResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "completed";

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("savedPath")]
    public string? SavedPath { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>通用操作（approve / reject / pause / resume / cancel）响应。</summary>
public sealed class SimpleOperationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
