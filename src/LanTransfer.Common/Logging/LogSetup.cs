using LanTransfer.Common.Constants;
using Serilog;
using Serilog.Events;

namespace LanTransfer.Common.Logging;

/// <summary>
/// Serilog 配置。日志位置 %LOCALAPPDATA%\LanTransfer\Logs，按天滚动并限制历史文件数量。
/// 严禁记录私钥、完整证书私钥、敏感 Token 与文件内容。
/// </summary>
public static class LogSetup
{
    /// <summary>创建基础 LoggerConfiguration。</summary>
    public static LoggerConfiguration Create(string? logDirectory = null, string level = "Information",
        bool writeToConsole = false)
    {
        var directory = logDirectory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(directory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(level))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("app", AppConstants.ProductName)
            .WriteTo.File(
                Path.Combine(directory, "lan-transfer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 64L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (writeToConsole)
            configuration.WriteTo.Console();

        return configuration;
    }

    public static LogEventLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" or "info" => LogEventLevel.Information,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    /// <summary>把日志级别字符串归一化为界面可选值。</summary>
    public static IReadOnlyList<string> AvailableLevels { get; } =
        new[] { "Verbose", "Debug", "Information", "Warning", "Error" };
}
