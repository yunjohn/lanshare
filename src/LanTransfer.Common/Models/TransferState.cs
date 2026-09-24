namespace LanTransfer.Common.Models;

/// <summary>
/// 传输状态机。所有模块（Core / Network / Storage / UI）必须统一使用本枚举，
/// 禁止任何模块自定义另一套状态。
/// </summary>
public enum TransferState
{
    /// <summary>已创建，尚未进入队列。</summary>
    Pending = 0,

    /// <summary>排队中，等待其它传输任务释放并发额度。</summary>
    Queued = 1,

    /// <summary>等待接收端用户确认。</summary>
    WaitingApproval = 2,

    /// <summary>准备中（建立目录、预分配空间、计算摘要）。</summary>
    Preparing = 3,

    /// <summary>正在传输。</summary>
    Transferring = 4,

    /// <summary>已暂停（当前 Chunk 允许完成）。</summary>
    Paused = 5,

    /// <summary>全部 Chunk 完成，正在做 SHA-256 校验。</summary>
    Verifying = 6,

    /// <summary>已完成且校验通过。</summary>
    Completed = 7,

    /// <summary>接收端拒绝。</summary>
    Rejected = 8,

    /// <summary>用户取消。</summary>
    Cancelled = 9,

    /// <summary>失败（网络、磁盘、IO 等）。</summary>
    Failed = 10,

    /// <summary>校验失败，文件不得标记为成功。</summary>
    VerificationFailed = 11,
}

public static class TransferStateExtensions
{
    /// <summary>是否为终态（不会再自行变化）。</summary>
    public static bool IsTerminal(this TransferState state) => state is
        TransferState.Completed or
        TransferState.Rejected or
        TransferState.Cancelled or
        TransferState.Failed or
        TransferState.VerificationFailed;

    /// <summary>是否可继续/恢复。</summary>
    public static bool IsResumable(this TransferState state) => state is
        TransferState.Paused or
        TransferState.Failed or
        TransferState.Cancelled or
        TransferState.Pending or
        TransferState.Queued;

    /// <summary>是否为活动状态（占用并发额度）。</summary>
    public static bool IsActive(this TransferState state) => state is
        TransferState.Preparing or
        TransferState.Transferring or
        TransferState.Verifying or
        TransferState.WaitingApproval;

    public static string ToWireString(this TransferState state) => state switch
    {
        TransferState.Pending => "pending",
        TransferState.Queued => "queued",
        TransferState.WaitingApproval => "waiting-approval",
        TransferState.Preparing => "preparing",
        TransferState.Transferring => "transferring",
        TransferState.Paused => "paused",
        TransferState.Verifying => "verifying",
        TransferState.Completed => "completed",
        TransferState.Rejected => "rejected",
        TransferState.Cancelled => "cancelled",
        TransferState.Failed => "failed",
        TransferState.VerificationFailed => "verification-failed",
        _ => "unknown",
    };

    public static TransferState FromWireString(string? value) => value switch
    {
        "pending" => TransferState.Pending,
        "queued" => TransferState.Queued,
        "waiting-approval" => TransferState.WaitingApproval,
        "preparing" => TransferState.Preparing,
        "transferring" => TransferState.Transferring,
        "paused" => TransferState.Paused,
        "verifying" => TransferState.Verifying,
        "completed" => TransferState.Completed,
        "rejected" => TransferState.Rejected,
        "cancelled" => TransferState.Cancelled,
        "failed" => TransferState.Failed,
        "verification-failed" => TransferState.VerificationFailed,
        _ => TransferState.Failed,
    };
}
