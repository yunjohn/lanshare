namespace LanTransfer.Common.Models;

/// <summary>单个待传输文件的描述（发送端扫描得到 / 接收端落盘依据）。</summary>
public sealed class TransferFileDescriptor
{
    /// <summary>相对发送根的路径，使用 '\' 分隔；文件在根目录时为空字符串。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件名（含扩展名）。</summary>
    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>发送前计算的 SHA-256（十六进制小写）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int ChunkSize { get; set; }

    public int TotalChunks { get; set; }

    /// <summary>源文件的 LastWriteTimeUtc（用于传输过程中变化检测）。</summary>
    public DateTimeOffset LastWriteTimeUtc { get; set; }
}

/// <summary>一个文件的分块进度。</summary>
public sealed class FileTransferProgress
{
    public int FileIndex { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long TransferredBytes { get; set; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }

    public double Percent => FileSize <= 0
        ? (TotalChunks > 0 && CompletedChunks >= TotalChunks ? 100d : 0d)
        : Math.Clamp(TransferredBytes * 100d / FileSize, 0d, 100d);
}

/// <summary>传输任务的实时进度快照（UI 绑定用，不可变语义）。</summary>
public sealed class TransferProgressSnapshot
{
    public string TransferId { get; set; } = string.Empty;
    public TransferState State { get; set; }
    public long TotalSize { get; set; }
    public long TransferredSize { get; set; }
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;

    /// <summary>最近 2~5 秒滑动平均速度（字节/秒）。</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>预计剩余时间；无法估算时为 null。</summary>
    public TimeSpan? Eta { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public double Percent => TotalSize <= 0
        ? (State == TransferState.Completed ? 100d : 0d)
        : Math.Clamp(TransferredSize * 100d / TotalSize, 0d, 100d);
}

/// <summary>传输任务记录（数据库实体 + UI 列表项）。</summary>
public sealed class TransferRecord
{
    public string TransferId { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public TransferType TransferType { get; set; }
    public string RemoteDeviceId { get; set; } = string.Empty;
    public string RemoteDeviceName { get; set; } = string.Empty;
    public string LocalDeviceId { get; set; } = string.Empty;
    public TransferState State { get; set; } = TransferState.Pending;
    public long TotalSize { get; set; }
    public long TransferredSize { get; set; }
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public string RootName { get; set; } = string.Empty;
    public string? DownloadRoot { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public double Percent => TotalSize <= 0
        ? (State == TransferState.Completed ? 100d : 0d)
        : Math.Clamp(TransferredSize * 100d / TotalSize, 0d, 100d);
}

/// <summary>传输中的单个文件记录（数据库实体）。</summary>
public sealed class TransferFileRecord
{
    public string FileId { get; set; } = string.Empty;
    public string TransferId { get; set; } = string.Empty;
    public int FileIndex { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public TransferState State { get; set; } = TransferState.Pending;
    public string? TargetPath { get; set; }
    public string? SourcePath { get; set; }
    public DateTimeOffset? LastWriteTimeUtc { get; set; }

    public double Percent => FileSize <= 0
        ? (TotalChunks > 0 && CompletedChunks >= TotalChunks ? 100d : 0d)
        : Math.Clamp(CompletedChunks * (double)ChunkSize * 100d / FileSize, 0d, 100d);
}
