using Microsoft.Extensions.Logging;

namespace LanTransfer.Sync.Watcher;

/// <summary>
/// 文件变化实时监听。
/// 关键点：不能只依赖 FileSystemWatcher——事件可能丢失、重复、缓冲区溢出，
/// 且程序关闭期间的变化无法捕获。因此本类只负责「尽快发现变化并合并去抖」，
/// 真正的正确性由 SyncEngine 的定期全量扫描 + 基线比对保证。
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    private readonly string _rootPath;
    private readonly TimeSpan _debounce;
    private readonly ILogger _logger;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _gate = new();
    private bool _pending;

    public FileChangeWatcher(string rootPath, TimeSpan debounce, ILogger logger)
    {
        _rootPath = rootPath;
        _debounce = debounce;
        _logger = logger;
    }

    /// <summary>去抖窗口结束后触发一次（可能合并多次文件系统事件）。</summary>
    public event EventHandler? Changed;

    public bool IsRunning => _watcher is not null;

    public void Start()
    {
        if (_watcher is not null) return;

        if (!Directory.Exists(_rootPath))
        {
            _logger.LogWarning("同步目录不存在，无法启动变化监听: {Root}", _rootPath);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _logger.LogInformation("已启动同步目录变化监听: {Root}", _rootPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动文件变化监听失败: {Root}", _rootPath);
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;

        if (watcher is null) return;

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "停止文件变化监听时出现异常");
        }

        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pending = false;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule();

    private void OnRenamed(object sender, RenamedEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出会导致事件丢失，必须强制一次全量扫描
        _logger.LogWarning(e.GetException(), "文件监听缓冲区溢出，将触发一次全量扫描");
        Schedule(immediate: true);
    }

    private void Schedule(bool immediate = false)
    {
        lock (_gate)
        {
            if (_debounceTimer is null)
                _debounceTimer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

            _pending = true;
            _debounceTimer.Change(immediate ? TimeSpan.Zero : _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理文件变化事件时发生异常");
        }
    }

    public void Dispose() => Stop();
}
