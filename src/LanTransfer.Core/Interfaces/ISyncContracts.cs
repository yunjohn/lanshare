using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;

namespace LanTransfer.Core.Interfaces;

/// <summary>对端请求创建同步关系，需要本机用户授权。</summary>
public sealed class SyncRequestEventArgs : EventArgs
{
    public string SyncPairId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string RemoteDeviceId { get; init; } = string.Empty;
    public string RemoteDeviceName { get; init; } = string.Empty;
    public string RequestedRemotePath { get; init; } = string.Empty;
    public string SuggestedLocalPath { get; init; } = string.Empty;
    public SyncMode Mode { get; init; } = SyncMode.TwoWay;
}

/// <summary>
/// 同步服务端契约。由 LanTransfer.Sync 的 SyncEngine 实现，
/// TransferServer 通过它暴露 /api/v1/sync/* 端点。
/// 未经用户授权不得自动创建长期同步关系。
/// </summary>
public interface ISyncServerHandler
{
    event EventHandler<SyncRequestEventArgs>? SyncRequested;

    /// <summary>创建同步关系请求（需本机用户确认）。</summary>
    Task<SyncRequestResponse> HandleSyncRequestAsync(SyncRequest request, string remoteDeviceId,
        string remoteDeviceName, CancellationToken cancellationToken = default);

    /// <summary>交换同步清单。</summary>
    Task<SyncManifestResponse> HandleManifestAsync(SyncManifestRequest request, string remoteDeviceId,
        CancellationToken cancellationToken = default);

    /// <summary>接收对端的文件变化通知；只有同步关系发起端会据此调度同步。</summary>
    Task<SimpleOperationResponse> HandleChangeNotificationAsync(string syncPairId, string remoteDeviceId,
        CancellationToken cancellationToken = default);

    /// <summary>读取同步目录中的文件内容（流式）。</summary>
    Task<SyncContentResult> OpenReadAsync(string syncPairId, string relativePath, string remoteDeviceId,
        CancellationToken cancellationToken = default);

    /// <summary>写入同步目录中的文件（流式，先写 .part 再原子改名）。</summary>
    Task<SimpleOperationResponse> WriteAsync(string syncPairId, string relativePath, Stream content,
        string remoteDeviceId, CancellationToken cancellationToken = default);

    /// <summary>删除同步目录中的文件（用于删除传播）。</summary>
    Task<SimpleOperationResponse> DeleteAsync(string syncPairId, IReadOnlyList<string> relativePaths,
        string remoteDeviceId, CancellationToken cancellationToken = default);

    /// <summary>用户对同步请求的应答。</summary>
    void RespondToSyncRequest(string syncPairId, bool accept, string? resolvedLocalPath);
}

/// <summary>同步文件读取结果。</summary>
public sealed class SyncContentResult
{
    public bool Success { get; init; }
    public Stream? Content { get; init; }
    public long Length { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static SyncContentResult Fail(string code, string message) =>
        new() { Success = false, ErrorCode = code, Message = message };
}

/// <summary>
/// 把 SyncPairId 映射为本机同步目录。接收端据此把文件写入同步目录而不是默认下载目录；
/// 只有已登记且已获用户授权的同步关系才会返回路径，避免远程任意指定写入位置。
/// </summary>
public interface ISyncPathProvider
{
    Task<string?> GetLocalPathAsync(string syncPairId, CancellationToken cancellationToken = default);
}
