using System.Windows;
using System.Windows.Threading;
using LanTransfer.App.Services;
using LanTransfer.App.Startup;
using LanTransfer.App.ViewModels;
using LanTransfer.App.Views;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Logging;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LanTransfer.App;

/// <summary>
/// 应用入口。负责：配置 → 日志 → 依赖注入 → 启动网络服务 → 显示主窗口。
/// 任何单个子系统（UDP / Kestrel / 同步）启动失败都不得导致应用退出。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();

        // 1. 先用固定级别引导日志，读取配置后再按配置级别重建
        var bootstrapLogger = LogSetup.Create(level: "Information").CreateLogger();
        var bootstrapFactory = new SerilogLoggerFactory(bootstrapLogger);

        var settings = new SettingsService(bootstrapFactory.CreateLogger<SettingsService>());

        try
        {
            await settings.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载配置失败，将使用默认配置。\n\n{ex.Message}", "LAN Transfer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await bootstrapLogger.DisposeAsync().ConfigureAwait(true);

        var serilog = LogSetup.Create(level: settings.Current.LogLevel).CreateLogger();
        Log.Logger = serilog;

        var loggerFactory = new SerilogLoggerFactory(serilog);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging(builder => builder.AddSerilog(serilog, dispose: false));
        services.AddLanTransfer();

        // 用已加载配置的实例覆盖注册，避免出现两份配置对象
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton(settings);

        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false,
        });

        _logger = _services.GetRequiredService<ILogger<App>>();

        AttachGlobalExceptionHandlers();

        _logger.LogInformation("================ LAN Transfer 启动 ================");
        _logger.LogInformation("版本 {Version}，协议版本 {Protocol}，日志目录 {LogDir}",
            AppConstants.AppVersion, AppConstants.ProtocolVersion, AppPaths.LogDirectory);

        try
        {
            _services.GetRequiredService<IAutoStartService>().SetEnabled(settings.Current.AutoStart);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新开机启动项失败");
        }

        // 先把主窗口（及其 MainViewModel）建出来，再启动 Kestrel / UDP：
        // 接收确认、配对、同步请求的弹窗处理器是在 MainViewModel 构造时订阅的，
        // 若先启动服务，启动窗口期内到达的请求会「无人订阅」而被静默忽略
        // （对端只能看到等待超时，本机界面上连记录都没有）。
        MainWindow? mainWindow;
        try
        {
            mainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "创建主窗口失败，应用退出");
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n详细信息已写入日志：\n{AppPaths.LogDirectory}",
                "LAN Transfer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        await InitializeSubsystemsAsync().ConfigureAwait(true);

        try
        {
            var startHidden = e.Args.Any(arg =>
                string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (startHidden) mainWindow.StartHidden();
            else mainWindow.Show();
        }
        catch (Exception ex)
        {
            // 主窗口显示失败时必须退出：否则进程会无窗口驻留，并一直占用传输端口
            _logger?.LogCritical(ex, "显示主窗口失败，应用退出");

            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n详细信息已写入日志：\n{AppPaths.LogDirectory}",
                "LAN Transfer", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private async Task InitializeSubsystemsAsync()
    {
        if (_services is null) return;

        var identity = _services.GetRequiredService<IIdentityService>();
        var trustStore = _services.GetRequiredService<ITrustStore>();
        var devices = _services.GetRequiredService<IDeviceManager>();
        var transfers = _services.GetRequiredService<ITransferManager>();
        var server = _services.GetRequiredService<ITransferServer>();
        var discovery = _services.GetRequiredService<IDiscoveryService>();
        var syncEngine = _services.GetRequiredService<SyncEngine>();

        await SafeAsync(() => _services.GetRequiredService<IDeviceRepository>().InitializeAsync(),
            "初始化设备仓储");
        await SafeAsync(() => _services.GetRequiredService<ITransferRepository>().InitializeAsync(),
            "初始化传输仓储");
        await SafeAsync(() => _services.GetRequiredService<ISyncRepository>().InitializeAsync(),
            "初始化同步仓储");

        await SafeAsync(() => identity.InitializeAsync(), "初始化设备身份");
        await SafeAsync(() => trustStore.InitializeAsync(), "初始化信任存储");
        await SafeAsync(() => devices.InitializeAsync(), "初始化设备管理器");
        await SafeAsync(() => transfers.LoadHistoryAsync(), "加载传输历史");
        await SafeAsync(() => server.StartAsync(), "启动传输服务");
        await SafeAsync(() => discovery.StartAsync(), "启动设备发现");
        await SafeAsync(() => syncEngine.InitializeAsync(), "初始化同步引擎");
        await SafeAsync(() => transfers.AutoResumePendingAsync(), "自动恢复未完成传输");

        if (!server.IsRunning && !string.IsNullOrEmpty(server.LastError))
            _logger?.LogWarning("传输服务未运行: {Error}", server.LastError);

        if (!discovery.IsRunning && !string.IsNullOrEmpty(discovery.LastError))
            _logger?.LogWarning("设备发现未运行: {Error}", discovery.LastError);
    }

    private async Task SafeAsync(Func<Task> action, string description)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 单个子系统失败不影响其它子系统
            _logger?.LogError(ex, "{Description} 失败", description);
        }
    }

    private void AttachGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "发生未处理的致命异常");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "后台任务出现未观察异常");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "UI 线程未处理异常");

        // 单个传输/同步失败不应导致程序退出
        e.Handled = true;

        MessageBox.Show(
            $"操作失败：{e.Exception.Message}\n\n详细信息已写入日志：\n{AppPaths.LogDirectory}",
            "LAN Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("================ LAN Transfer 退出 ================");

        if (_services is not null)
        {
            await SafeAsync(() => _services.GetRequiredService<IDiscoveryService>().StopAsync(), "停止设备发现");
            await SafeAsync(() => _services.GetRequiredService<ITransferServer>().StopAsync(), "停止传输服务");
            await SafeAsync(async () =>
            {
                await _services.GetRequiredService<SyncEngine>().DisposeAsync().ConfigureAwait(false);
            }, "停止同步引擎");

            try
            {
                await _services.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "释放服务容器时出现异常");
            }
        }

        await Log.CloseAndFlushAsync().ConfigureAwait(false);

        base.OnExit(e);
    }
}
