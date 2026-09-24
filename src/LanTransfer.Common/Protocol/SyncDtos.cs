using System.Text.Json.Serialization;

namespace LanTransfer.Common.Protocol;

/// <summary>同步关系中的单个文件清单项（用于两端比对，不含完整本地路径）。</summary>
public sealed class SyncManifestEntry
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("lastWriteTimeUtc")]
    public DateTimeOffset LastWriteTimeUtc { get; set; }

    /// <summary>仅在 size/mtime 发生变化时才提供 SHA-256，避免每次全量 Hash。</summary>
    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    [JsonPropertyName("version")]
    public long Version { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
}

/// <summary>请求创建同步关系（POST /api/v1/sync/pairs）。</summary>
public sealed class SyncRequest
{
    [JsonPropertyName("syncPairId")]
    public string SyncPairId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("remoteDeviceId")]
    public string RemoteDeviceId { get; set; } = string.Empty;

    [JsonPropertyName("remoteDeviceName")]
    public string RemoteDeviceName { get; set; } = string.Empty;

    /// <summary>发起方希望在本机使用的目录（仅用于向对端展示，对端不会使用该路径）。</summary>
    [JsonPropertyName("requesterLocalPath")]
    public string RequesterLocalPath { get; set; } = string.Empty;

    /// <summary>对端应使用的目录（对端可修改）。</summary>
    [JsonPropertyName("targetRemotePath")]
    public string TargetRemotePath { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "two-way";

    [JsonPropertyName("certificateFingerprint")]
    public string CertificateFingerprint { get; set; } = string.Empty;
}

public sealed class SyncRequestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("syncPairId")]
    public string SyncPairId { get; set; } = string.Empty;

    /// <summary>接收端实际使用的本地目录（可能被用户修改）。</summary>
    [JsonPropertyName("resolvedLocalPath")]
    public string? ResolvedLocalPath { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>请求对端删除同步目录中的文件（POST /api/v1/sync/pairs/{id}/delete）。</summary>
public sealed class SyncDeleteRequest
{
    [JsonPropertyName("relativePaths")]
    public List<string> RelativePaths { get; set; } = new();
}

/// <summary>同步清单交换（POST /api/v1/sync/pairs/{id}/manifest）。</summary>
public sealed class SyncManifestRequest
{
    [JsonPropertyName("syncPairId")]
    public string SyncPairId { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<SyncManifestEntry> Entries { get; set; } = new();

    /// <summary>true 表示仅请求对端清单，不上传本地清单。</summary>
    [JsonPropertyName("manifestOnly")]
    public bool ManifestOnly { get; set; }

    [JsonPropertyName("fullScan")]
    public bool FullScan { get; set; }
}

public sealed class SyncManifestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("syncPairId")]
    public string SyncPairId { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<SyncManifestEntry> Entries { get; set; } = new();

    /// <summary>
    /// 对端本轮读不到（被占用/无权限）的相对路径。请求方必须把命中的条目视为「状态未知」并跳过，
    /// 否则会按「基线里有、对端清单里没有 = 对端已删除」的规则删掉自己的副本。
    /// </summary>
    [JsonPropertyName("unreadablePaths")]
    public List<string> UnreadablePaths { get; set; } = new();

    /// <summary>对端同步根整体不可读（不存在/无法枚举）：请求方必须放弃本轮，绝不能按空清单处理。</summary>
    [JsonPropertyName("rootUnreadable")]
    public bool RootUnreadable { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
