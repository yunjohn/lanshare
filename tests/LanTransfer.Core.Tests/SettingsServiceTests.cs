using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>
/// 配置服务测试：DeviceId 首次生成后必须稳定、非法端口/分块必须回退、配置损坏不得导致启动失败。
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lantransfer-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    private SettingsService Create() => new(NullLogger<SettingsService>.Instance, _file);

    [Fact]
    public async Task LoadAsync_CreatesFileWithDefaults()
    {
        var service = Create();

        await service.LoadAsync();

        Assert.True(File.Exists(_file));
        Assert.False(string.IsNullOrWhiteSpace(service.Current.DeviceId));
        Assert.Equal(AppConstants.DefaultDiscoveryPort, service.Current.DiscoveryPort);
        Assert.Equal(AppConstants.DefaultTransferPort, service.Current.TransferPort);
        Assert.Equal(AppConstants.DefaultChunkSize, service.Current.ChunkSize);
        Assert.Equal(Environment.MachineName, service.Current.DeviceName);
    }

    [Fact]
    public async Task DeviceId_IsStableAcrossReloads()
    {
        var first = Create();
        await first.LoadAsync();
        var id = first.Current.DeviceId;

        var second = Create();
        await second.LoadAsync();

        Assert.Equal(id, second.Current.DeviceId);
    }

    [Fact]
    public async Task LoadAsync_GeneratesDistinctDeviceIdsPerInstallation()
    {
        var other = Path.Combine(_dir, "other", "settings.json");

        var a = new SettingsService(NullLogger<SettingsService>.Instance, _file);
        var b = new SettingsService(NullLogger<SettingsService>.Instance, other);

        await a.LoadAsync();
        await b.LoadAsync();

        Assert.NotEqual(a.Current.DeviceId, b.Current.DeviceId);
    }

    [Fact]
    public async Task SaveAsync_NormalizesInvalidPorts()
    {
        var service = Create();
        await service.LoadAsync();

        var settings = service.Current.Clone();
        settings.DiscoveryPort = 0;
        settings.TransferPort = 70000;

        await service.SaveAsync(settings);

        Assert.Equal(AppConstants.DefaultDiscoveryPort, service.Current.DiscoveryPort);
        Assert.Equal(AppConstants.DefaultTransferPort, service.Current.TransferPort);
    }

    [Fact]
    public async Task SaveAsync_RejectsChunkSizeOutsideAllowedSet()
    {
        var service = Create();
        await service.LoadAsync();

        var settings = service.Current.Clone();
        settings.ChunkSize = 12345;

        await service.SaveAsync(settings);

        Assert.Equal(AppConstants.DefaultChunkSize, service.Current.ChunkSize);
        Assert.Contains(AppConstants.DefaultChunkSize, AppConstants.AllowedChunkSizes);
    }

    [Fact]
    public async Task SaveAsync_NormalizesSyncAndTombstoneIntervals()
    {
        var service = Create();
        await service.LoadAsync();

        var settings = service.Current.Clone();
        settings.SyncScanIntervalMinutes = 7;
        settings.TombstoneRetentionDays = 3;

        await service.SaveAsync(settings);

        Assert.Contains(service.Current.SyncScanIntervalMinutes, AppConstants.AllowedSyncScanIntervals);
        Assert.Contains(service.Current.TombstoneRetentionDays, AppConstants.AllowedTombstoneRetentionDays);
    }

    [Fact]
    public async Task SaveAsync_RaisesSettingsChanged()
    {
        var service = Create();
        await service.LoadAsync();

        var raised = 0;
        service.SettingsChanged += (_, _) => Interlocked.Increment(ref raised);

        var settings = service.Current.Clone();
        settings.DeviceName = "PC-TEST";
        await service.SaveAsync(settings);

        Assert.Equal(1, raised);
        Assert.Equal("PC-TEST", service.Current.DeviceName);
    }

    [Fact]
    public async Task LoadAsync_CorruptedFile_FallsBackToDefaultsAndBacksUp()
    {
        await File.WriteAllTextAsync(_file, "{ this is not json ");

        var service = Create();
        await service.LoadAsync();

        Assert.Equal(AppConstants.DefaultTransferPort, service.Current.TransferPort);
        Assert.True(File.Exists(_file + ".corrupted"));

        // 备份后重新写入的必须是合法 JSON
        var reloaded = Create();
        await reloaded.LoadAsync();
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Current.DeviceId));
    }

    [Fact]
    public async Task AutoAcceptTrustedDevice_DefaultsToOff()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.False(service.Current.AutoAcceptTrustedDevice);
    }

    [Fact]
    public async Task SaveAsync_DoesNotLeakTemporaryFile()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public async Task Clone_ProducesIndependentCopy()
    {
        var service = Create();
        await service.LoadAsync();

        var clone = service.Current.Clone();
        clone.DeviceName = "MUTATED";

        Assert.NotEqual("MUTATED", service.Current.DeviceName);
        Assert.NotEqual(clone.DeviceId, string.Empty);
        Assert.Equal(clone.DeviceId, service.Current.DeviceId);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ApplyAsync_IsAliasForSave()
    {
        var service = Create();
        await service.LoadAsync();

        var settings = service.Current.Clone();
        settings.TransferPort = 45678;

        await service.ApplyAsync(settings);

        Assert.Equal(45678, service.Current.TransferPort);
    }

    // ---------------------------------------------------------------- 关闭行为（只询问一次）

    [Fact]
    public async Task CloseAction_DefaultsToAsk()
    {
        var service = Create();
        await service.LoadAsync();

        Assert.Equal(CloseWindowAction.Ask, service.Current.CloseAction);
    }

    /// <summary>用户选定的关闭方式必须持久化，否则每次关闭都会重新询问。</summary>
    [Fact]
    public async Task CloseAction_IsPersistedAndReloaded()
    {
        var first = Create();
        await first.LoadAsync();

        var settings = first.Current.Clone();
        settings.CloseAction = CloseWindowAction.MinimizeToTray;
        await first.ApplyAsync(settings);

        // 以字符串形式存储，配置文件可读
        Assert.Contains("MinimizeToTray", await File.ReadAllTextAsync(_file));

        var second = Create();
        await second.LoadAsync();

        Assert.Equal(CloseWindowAction.MinimizeToTray, second.Current.CloseAction);
    }

    /// <summary>
    /// 配置里出现无法识别的关闭方式时，只把该项回退为「每次询问」，
    /// 绝不能判定整份配置损坏并重置（那样会丢掉 DeviceId 等全部设置）。
    /// </summary>
    [Fact]
    public async Task CloseAction_UnknownValue_FallsBackWithoutResettingOtherSettings()
    {
        var first = Create();
        await first.LoadAsync();

        var settings = first.Current.Clone();
        settings.DeviceName = "KEEP-ME";
        await first.ApplyAsync(settings);

        var json = await File.ReadAllTextAsync(_file);
        await File.WriteAllTextAsync(_file, json.Replace("\"Ask\"", "\"这不是合法的关闭方式\""));

        var second = Create();
        await second.LoadAsync();

        Assert.Equal(CloseWindowAction.Ask, second.Current.CloseAction);
        Assert.Equal("KEEP-ME", second.Current.DeviceName);
        Assert.False(File.Exists(_file + ".corrupted"));
    }
}
