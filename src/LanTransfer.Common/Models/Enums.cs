namespace LanTransfer.Common.Models;

/// <summary>传输方向。</summary>
public enum TransferDirection
{
    /// <summary>本机发送。</summary>
    Send = 0,

    /// <summary>本机接收。</summary>
    Receive = 1,
}

/// <summary>传输类型。</summary>
public enum TransferType
{
    /// <summary>单个文件。</summary>
    File = 0,

    /// <summary>文件夹（多文件，保留相对路径）。</summary>
    Folder = 1,
}

/// <summary>设备信任状态。</summary>
public enum TrustState
{
    /// <summary>从未配对过。</summary>
    Unknown = 0,

    /// <summary>已配对，等待用户确认验证码。</summary>
    Pending = 1,

    /// <summary>已信任（证书指纹已记录）。</summary>
    Trusted = 2,

    /// <summary>用户主动解除信任。</summary>
    Revoked = 3,

    /// <summary>证书指纹发生变化，必须重新确认身份。</summary>
    IdentityChanged = 4,
}

/// <summary>设备在线状态。</summary>
public enum OnlineState
{
    Offline = 0,
    Online = 1,
}

/// <summary>同步模式。</summary>
public enum SyncMode
{
    /// <summary>双向同步 A ⇄ B。</summary>
    TwoWay = 0,

    /// <summary>仅 A → B。</summary>
    SendOnly = 1,

    /// <summary>仅 B → A。</summary>
    ReceiveOnly = 2,
}

/// <summary>同步任务状态。</summary>
public enum SyncStatus
{
    Idle = 0,
    Scanning = 1,
    Syncing = 2,
    Paused = 3,
    Conflict = 4,
    Error = 5,
    WaitingApproval = 6,
}

/// <summary>单个同步文件的同步状态。</summary>
public enum SyncEntryState
{
    /// <summary>两端一致。</summary>
    InSync = 0,

    /// <summary>待从本机推送到对端。</summary>
    PendingLocalToRemote = 1,

    /// <summary>待从对端拉取到本机。</summary>
    PendingRemoteToLocal = 2,

    /// <summary>冲突，等待用户裁决。</summary>
    Conflict = 3,

    /// <summary>正在传输。</summary>
    Transferring = 4,

    /// <summary>已删除（墓碑）。</summary>
    Deleted = 5,

    /// <summary>错误。</summary>
    Error = 6,
}

/// <summary>文件重名处理策略。</summary>
public enum ConflictPolicy
{
    /// <summary>覆盖已存在文件。</summary>
    Overwrite = 0,

    /// <summary>自动重命名（默认）。</summary>
    Rename = 1,

    /// <summary>跳过该文件。</summary>
    Skip = 2,
}

/// <summary>
/// 点击主窗口关闭按钮时的行为。作为持久化配置项，用户选择一次后即成为默认行为。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(Serialization.TolerantEnumConverter<CloseWindowAction>))]
public enum CloseWindowAction
{
    /// <summary>每次关闭都询问（默认）。</summary>
    Ask = 0,

    /// <summary>直接退出程序，停止接收、传输与实时同步。</summary>
    Exit = 1,

    /// <summary>最小化到系统托盘，继续在后台运行。</summary>
    MinimizeToTray = 2,
}
