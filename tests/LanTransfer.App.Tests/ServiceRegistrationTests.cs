using LanTransfer.App.Services;
using LanTransfer.App.Startup;
using LanTransfer.App.ViewModels;
using LanTransfer.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanTransfer.App.Tests;

/// <summary>
/// 依赖注入注册的完整性守卫。
///
/// App 在启动时通过 GetRequiredService&lt;MainWindow&gt;() 取主窗口。只要
/// 「新增了 ViewModel/服务却忘记注册」，程序就会在启动瞬间抛出
/// “No service for type ... has been registered” 并弹窗失败 —— 这类问题
/// 编译期完全看不出来，只能靠测试兜住。
/// </summary>
public sealed class ServiceRegistrationTests
{
    /// <summary>UI 入口及其全部依赖必须都在容器里，缺一个都会导致启动崩溃。</summary>
    [Fact]
    public void AddLanTransfer_RegistersMainWindowAndEveryViewModel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLanTransfer();

        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var required = new[]
        {
            typeof(MainWindow),
            typeof(MainViewModel),
            typeof(TransferViewModel),
            typeof(SyncViewModel),
            typeof(DevicesViewModel),
            typeof(HistoryViewModel),
            typeof(SettingsViewModel),
            typeof(DiagnosticsViewModel),
            typeof(IDialogService),
        };

        var missing = required.Where(type => !registered.Contains(type)).ToArray();

        Assert.True(missing.Length == 0,
            "以下服务未在 AddLanTransfer 中注册，启动时会崩溃：" +
            string.Join("、", missing.Select(type => type.FullName)));
    }

    /// <summary>
    /// 每个已注册项的构造参数都必须可解析。
    /// ValidateOnBuild 会为所有注册项构造调用点，但不会真正实例化，
    /// 因此不需要 STA 线程或 WPF Application 也能跑。
    /// </summary>
    [Fact]
    public void AddLanTransfer_HasNoUnresolvableConstructorDependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLanTransfer();

        var exception = Record.Exception(() => services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));

        Assert.Null(exception);
    }
}
