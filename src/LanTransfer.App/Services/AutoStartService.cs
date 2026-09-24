using Microsoft.Win32;

namespace LanTransfer.App.Services;

public interface IAutoStartService
{
    void SetEnabled(bool enabled);
}

/// <summary>当前用户开机登录后启动；使用 --background 参数静默驻留托盘。</summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanTransfer";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法打开当前用户的开机启动配置。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("无法确定程序可执行文件路径。");

        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
