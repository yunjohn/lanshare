using System.Globalization;

namespace LanTransfer.Common.Extensions;

/// <summary>字节数 / 速度 / 时间的人类可读格式化。</summary>
public static class FormattingExtensions
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>格式化字节数为可读字符串，例如 4.00 GB。</summary>
    public static string ToSizeString(this long bytes)
    {
        if (bytes < 0) return "-" + (-bytes).ToSizeString();
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.00} {SizeUnits[unit]}");
    }

    /// <summary>格式化速度为 MB/s。</summary>
    public static string ToSpeedString(this double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 MB/s";
        double mb = bytesPerSecond / 1024d / 1024d;
        if (mb >= 100) return string.Create(CultureInfo.InvariantCulture, $"{mb:0} MB/s");
        if (mb >= 10) return string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} MB/s");
        if (mb >= 1) return string.Create(CultureInfo.InvariantCulture, $"{mb:0.00} MB/s");
        double kb = bytesPerSecond / 1024d;
        if (kb >= 1) return string.Create(CultureInfo.InvariantCulture, $"{kb:0.0} KB/s");
        return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond:0} B/s");
    }

    /// <summary>格式化剩余时间为「12 秒 / 3 分 20 秒 / 1 小时 05 分」。</summary>
    public static string ToEtaString(this TimeSpan? eta)
    {
        if (eta is null) return "--";
        var value = eta.Value;
        if (value.TotalSeconds <= 0) return "--";
        if (value.TotalSeconds < 1) return "< 1 秒";
        if (value.TotalSeconds < 60) return $"{(int)value.TotalSeconds} 秒";
        if (value.TotalMinutes < 60)
            return $"{(int)value.TotalMinutes} 分 {value.Seconds:00} 秒";
        if (value.TotalHours < 24)
            return $"{(int)value.TotalHours} 小时 {value.Minutes:00} 分";
        return $"{(int)value.TotalDays} 天 {value.Hours:00} 小时";
    }

    /// <summary>格式化百分比。</summary>
    public static string ToPercentString(this double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0.#}%");
}
