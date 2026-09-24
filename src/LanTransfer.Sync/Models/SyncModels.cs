namespace LanTransfer.Sync.Models;

/// <summary>同步规划出的单个动作类型。</summary>
public enum SyncActionType
{
    /// <summary>无需处理（两端一致）。</summary>
    None = 0,

    /// <summary>本机 → 对端（新增或修改）。</summary>
    Upload = 1,

    /// <summary>对端 → 本机（新增或修改）。</summary>
    Download = 2,

    /// <summary>删除对端文件（本机已删除，对端未修改）。</summary>
    DeleteRemote = 3,

    /// <summary>删除本机文件（对端已删除，本机未修改）。</summary>
    DeleteLocal = 4,

    /// <summary>仅更新基线（两端修改结果一致，或两端都已删除）。</summary>
    UpdateBaseline = 5,

    /// <summary>冲突：双方都修改且结果不同，禁止自动覆盖。</summary>
    Conflict = 6,

    /// <summary>删除/修改冲突：一端删除、另一端修改，保留被修改的文件并记录冲突。</summary>
    DeleteModifyConflict = 7,

    /// <summary>仅创建目录。</summary>
    CreateDirectory = 8,
}

/// <summary>规划结果中的一项。</summary>
public sealed class SyncAction
{
    public string RelativePath { get; set; } = string.Empty;
    public SyncActionType Type { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>冲突时保存对端版本的目标相对路径。</summary>
    public string? ConflictCopyRelativePath { get; set; }

    public long FileSize { get; set; }
    public bool IsDirectory { get; set; }

    /// <summary>
    /// 计划中该动作对应的内容哈希：
    /// Download/Conflict = 对端清单里的哈希；Upload = 本机扫描得到的哈希。
    /// 用途：冲突去重（同一 path+本地哈希+对端哈希只处理一次），以及把基线写成
    /// 「对端实际声明的哈希」而不是事后重新读取的结果（避免上传期间文件被改而记错基线）。
    /// </summary>
    public string? Sha256 { get; set; }

    public override string ToString() => $"{Type} {RelativePath}（{Reason}）";
}

/// <summary>一次同步计划的完整结果。</summary>
public sealed class SyncPlan
{
    public string SyncPairId { get; set; } = string.Empty;
    public List<SyncAction> Actions { get; } = new();
    public List<SyncAction> Conflicts { get; } = new();

    /// <summary>本轮因本机读不到（被占用等）而整条跳过、状态未知的路径。</summary>
    public List<string> SkippedUnreadable { get; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public IEnumerable<SyncAction> Uploads =>
        Actions.Where(a => a.Type == SyncActionType.Upload);

    public IEnumerable<SyncAction> Downloads =>
        Actions.Where(a => a.Type == SyncActionType.Download);

    public IEnumerable<SyncAction> RemoteDeletions =>
        Actions.Where(a => a.Type == SyncActionType.DeleteRemote);

    public IEnumerable<SyncAction> LocalDeletions =>
        Actions.Where(a => a.Type == SyncActionType.DeleteLocal);

    public bool HasWork => Actions.Any(a => a.Type is not SyncActionType.None);
}

/// <summary>本地扫描得到的清单项（与协议 DTO 解耦，便于单测）。</summary>
public sealed class LocalFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }

    /// <summary>可能为 null：只有 size/mtime 变化时才计算 SHA-256。</summary>
    public string? Sha256 { get; set; }
}

/// <summary>
/// 一次目录扫描的结果。
///
/// <see cref="UnreadablePaths"/> 是本轮「读不到」的相对路径（文件被其它程序独占、
/// 目录无权限、摘要计算失败等）。必须把它与「文件确实不存在」区分开：
/// 状态未知的条目既不能同步，**更不能被当成删除**——否则一个被打开的文件
/// 会让规划器生成 DeleteRemote，把对端的副本删掉。
/// 路径可能是目录：其下所有条目的状态都视为未知。
/// </summary>
public sealed class DirectoryScanResult
{
    public IReadOnlyList<LocalFileEntry> Entries { get; init; } = Array.Empty<LocalFileEntry>();

    public IReadOnlySet<string> UnreadablePaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 根目录整体读不到（不存在、无权限、IO 错误）。此时 <see cref="Entries"/> 必然为空，
    /// 它**绝不能**被当作「目录里什么都没有」——那会让规划器把两端所有文件判成已删除。
    /// 调用方（SyncEngine）必须在进入规划器之前直接中止本轮。
    /// </summary>
    public bool RootUnreadable { get; init; }

    public bool HasUnreadable => UnreadablePaths.Count > 0 || RootUnreadable;

    /// <summary>该相对路径是否落在「本轮读不到」的范围内（含读不到的目录之下的所有条目）。</summary>
    public bool IsUnreadable(string relativePath) => IsWithin(relativePath, UnreadablePaths);

    /// <summary>路径前缀判定：命中集合本身，或位于集合中某个目录之下。</summary>
    public static bool IsWithin(string relativePath, IReadOnlySet<string> paths)
    {
        if (paths.Count == 0) return false;

        // 空字符串代表「根都读不到」= 整棵树状态未知。
        // 必须在 Contains 之前单独判断：前缀循环对空串永远不成立，漏掉这一条会让护栏完全失效。
        if (paths.Contains(string.Empty)) return true;

        if (paths.Contains(relativePath)) return true;

        var index = relativePath.LastIndexOf('\\');
        while (index > 0)
        {
            if (paths.Contains(relativePath[..index])) return true;
            index = relativePath.LastIndexOf('\\', index - 1);
        }

        return false;
    }
}

/// <summary>同步执行结果。</summary>
public sealed class SyncRunResult
{
    public string SyncPairId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int DeletedLocal { get; set; }
    public int DeletedRemote { get; set; }
    public int Conflicts { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>本轮因文件被占用/读不到而跳过的条目数（稍后会自动重试）。</summary>
    public int Skipped { get; set; }

    /// <summary>本轮因对端或本机占用等原因执行失败的条目数（稍后会自动重试）。</summary>
    public int Failed { get; set; }

    /// <summary>是否还有被跳过/失败的条目等待重试。</summary>
    public bool HasPendingRetry => Skipped > 0 || Failed > 0;
}
