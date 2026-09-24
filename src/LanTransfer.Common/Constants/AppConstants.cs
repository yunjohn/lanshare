namespace LanTransfer.Common.Constants;

/// <summary>
/// 全局常量。协议相关的魔法数字必须集中在此处，禁止散落到各模块。
/// </summary>
public static class AppConstants
{
    /// <summary>产品名，用于日志、注册表、目录命名。</summary>
    public const string ProductName = "LanTransfer";

    /// <summary>发现协议标识，出现在每个 UDP 报文中。</summary>
    public const string ProtocolName = "lan-transfer";

    /// <summary>当前协议版本。协议不兼容时不得静默降级。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>应用语义版本。</summary>
    public const string AppVersion = "1.0.0";

    /// <summary>API 版本前缀。</summary>
    public const string ApiPrefix = "/api/v1";

    /// <summary>默认 UDP 发现端口。</summary>
    public const int DefaultDiscoveryPort = 39520;

    /// <summary>默认 TCP/HTTPS 传输端口。</summary>
    public const int DefaultTransferPort = 39521;

    /// <summary>默认分块大小：4 MiB。</summary>
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>允许配置的分块大小。</summary>
    public static readonly IReadOnlyList<int> AllowedChunkSizes = new[]
    {
        1 * 1024 * 1024,
        2 * 1024 * 1024,
        4 * 1024 * 1024,
        8 * 1024 * 1024,
        16 * 1024 * 1024,
    };

    /// <summary>UDP 广播间隔。</summary>
    public static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(3);

    /// <summary>超过该时间未收到设备报文即判定离线。</summary>
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromSeconds(10);

    /// <summary>设备在线判定扫描间隔。</summary>
    public static readonly TimeSpan DeviceSweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>离线设备主动探测的最小间隔。</summary>
    public static readonly TimeSpan DeviceProbeInterval = TimeSpan.FromSeconds(10);

    /// <summary>HTTPS 在线探测超时。</summary>
    public static readonly TimeSpan DeviceProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>单次 Chunk 上传的超时时间。</summary>
    public static readonly TimeSpan ChunkUploadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>创建传输 / 握手类请求的超时时间。</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    /// <summary>配对需要等待两端人工确认，使用独立于普通 API 的长超时。</summary>
    public static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(190);

    /// <summary>流式 IO 缓冲区大小（严禁整文件读入内存）。</summary>
    public const int StreamBufferSize = 128 * 1024;

    /// <summary>
    /// 读取「可能正被其它程序打开」的文件时使用的共享模式。
    ///
    /// Windows 的共享检查是**双向**的：既要求已有句柄允许我们读，也要求我们声明的
    /// 共享模式允许已有句柄的访问方式。所以这里若只写 <see cref="FileShare.Read"/>，
    /// 当 Word / Excel / 播放器等仍持有该文件的**写**句柄时（哪怕文件已经保存），
    /// 我们的打开会直接失败（共享冲突）——表现为「文件明明已保存，只要没关闭就同步/发送不了」。
    /// 允许 ReadWrite + Delete 才能读取这类文件；代价是可能读到正在写入的中间状态，
    /// 因此调用方仍需靠 size/mtime 与 SHA-256 校验保证最终一致性。
    /// </summary>
    public const FileShare FileReadSharing = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>速度滑动窗口长度（秒）。</summary>
    public static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(4);

    /// <summary>默认同步全量扫描间隔（分钟）。</summary>
    public const int DefaultSyncScanIntervalMinutes = 10;

    /// <summary>同步扫描间隔可选项（分钟）。</summary>
    public static readonly IReadOnlyList<int> AllowedSyncScanIntervals = new[] { 5, 10, 30, 60 };

    /// <summary>Tombstone 默认保留天数。</summary>
    public const int DefaultTombstoneRetentionDays = 30;

    /// <summary>Tombstone 保留天数可选项。</summary>
    public static readonly IReadOnlyList<int> AllowedTombstoneRetentionDays = new[] { 7, 30, 90 };

    /// <summary>断点续传元数据文件后缀。</summary>
    public const string PartExtension = ".part";

    /// <summary>断点续传元数据后缀（与 .part 同目录）。</summary>
    public const string PartMetadataExtension = ".part.json";
}
