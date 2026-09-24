using LanTransfer.Common.Constants;

namespace LanTransfer.Core.Transfers;

/// <summary>
/// 滑动窗口速度计算。界面不得使用「总字节 / 总时间」作为实时速度，
/// 否则数字会剧烈跳动。
/// </summary>
public sealed class SpeedCalculator
{
    private readonly Queue<Sample> _samples = new();
    private readonly TimeSpan _window;
    private long _lastTotalBytes;
    private bool _hasLast;

    public SpeedCalculator(TimeSpan? window = null)
        => _window = window ?? AppConstants.SpeedWindow;

    public double BytesPerSecond { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        BytesPerSecond = 0;
        _hasLast = false;
        _lastTotalBytes = 0;
    }

    /// <summary>用累计已传输字节数打点。</summary>
    public void AddSample(long totalBytes, DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;

        if (_hasLast && totalBytes < _lastTotalBytes)
        {
            // 累计值回退（例如切换文件），重置窗口避免出现负速度
            Reset();
        }

        _hasLast = true;
        _lastTotalBytes = totalBytes;

        _samples.Enqueue(new Sample(now, totalBytes));

        var cutoff = now - _window;
        while (_samples.Count > 2 && _samples.Peek().Timestamp < cutoff)
            _samples.Dequeue();

        if (_samples.Count < 2)
        {
            BytesPerSecond = 0;
            return;
        }

        var oldest = _samples.Peek();
        var newest = _samples.Last();
        var seconds = (newest.Timestamp - oldest.Timestamp).TotalSeconds;

        BytesPerSecond = seconds <= 0.05
            ? 0
            : Math.Max(0, (newest.TotalBytes - oldest.TotalBytes) / seconds);
    }

    /// <summary>根据剩余字节数与当前速度估算剩余时间。</summary>
    public TimeSpan? EstimateEta(long remainingBytes)
    {
        if (remainingBytes <= 0) return TimeSpan.Zero;
        if (BytesPerSecond < 1024) return null;
        return TimeSpan.FromSeconds(remainingBytes / BytesPerSecond);
    }

    private readonly record struct Sample(DateTimeOffset Timestamp, long TotalBytes);
}
