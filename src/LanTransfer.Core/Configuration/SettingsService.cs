using System.Text.Json;
using System.Text.Json.Serialization;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Configuration;

/// <summary>
/// 配置服务。配置保存在 %LOCALAPPDATA%\LanTransfer\settings.json。
/// DeviceId 首次生成后永久保存，之后启动不得重新生成。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();

    /// <param name="logger">日志。</param>
    /// <param name="settingsFilePath">
    /// 配置文件路径。默认 %LOCALAPPDATA%\LanTransfer\settings.json；
    /// 显式传入便于测试与便携部署，避免硬编码路径。
    /// </param>
    public SettingsService(ILogger<SettingsService> logger, string? settingsFilePath = null)
    {
        _logger = logger;
        _settingsFile = settingsFilePath ?? AppPaths.SettingsFile;
    }

    /// <summary>当前使用的配置文件绝对路径。</summary>
    public string SettingsFilePath => _settingsFile;

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectory();

        AppSettings settings;

        if (File.Exists(_settingsFile))
        {
            try
            {
                await using var stream = File.OpenRead(_settingsFile);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions,
                               cancellationToken).ConfigureAwait(false)
                           ?? new AppSettings();
            }
            catch (Exception ex)
            {
                // 配置损坏不得导致程序无法启动
                _logger.LogError(ex, "配置文件损坏，已回退到默认配置: {Path}", _settingsFile);
                settings = new AppSettings();
                TryBackupCorrupted();
            }
        }
        else
        {
            settings = new AppSettings();
        }

        Normalize(settings);
        _current = settings;

        await SaveInternalAsync(settings, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("配置已加载: DeviceName={Name} UDP={Udp} TCP={Tcp} ChunkSize={Chunk} Download={Download}",
            settings.DeviceName, settings.DiscoveryPort, settings.TransferPort, settings.ChunkSize,
            settings.DownloadPath);

        SettingsChanged?.Invoke(this, settings);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var clone = settings.Clone();
        Normalize(clone);
        _current = clone;
        await SaveInternalAsync(clone, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, clone);
    }

    public Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken = default)
        => SaveAsync(settings, cancellationToken);

    private async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var temp = _settingsFile + ".tmp";

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _settingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败: {Path}", _settingsFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_settingsFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    private void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = Guid.NewGuid().ToString();
            _logger.LogInformation("首次启动，生成 DeviceId: {DeviceId}", settings.DeviceId);
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceName))
            settings.DeviceName = Environment.MachineName;

        if (settings.DiscoveryPort is < 1 or > 65535)
            settings.DiscoveryPort = AppConstants.DefaultDiscoveryPort;

        if (settings.TransferPort is < 1 or > 65535)
            settings.TransferPort = AppConstants.DefaultTransferPort;

        if (!AppConstants.AllowedChunkSizes.Contains(settings.ChunkSize))
            settings.ChunkSize = AppConstants.DefaultChunkSize;

        if (!AppConstants.AllowedSyncScanIntervals.Contains(settings.SyncScanIntervalMinutes))
            settings.SyncScanIntervalMinutes = AppConstants.DefaultSyncScanIntervalMinutes;

        if (!AppConstants.AllowedTombstoneRetentionDays.Contains(settings.TombstoneRetentionDays))
            settings.TombstoneRetentionDays = AppConstants.DefaultTombstoneRetentionDays;

        if (string.IsNullOrWhiteSpace(settings.DownloadPath))
            settings.DownloadPath = AppSettings.DefaultDownloadPath();

        // 关闭行为必须是已定义的值；读到垃圾值时回退为「每次询问」
        if (!Enum.IsDefined(settings.CloseAction))
            settings.CloseAction = CloseWindowAction.Ask;

        // 自动接收可信设备默认关闭，不得被配置文件的缺失字段意外打开
        if (!File.Exists(_settingsFile))
            settings.AutoAcceptTrustedDevice = false;
    }

    private void TryBackupCorrupted()
    {
        try
        {
            var backup = _settingsFile + ".corrupted";
            File.Copy(_settingsFile, backup, overwrite: true);
            _logger.LogWarning("已将损坏的配置文件备份到 {Path}", backup);
        }
        catch
        {
            // 忽略
        }
    }
}
