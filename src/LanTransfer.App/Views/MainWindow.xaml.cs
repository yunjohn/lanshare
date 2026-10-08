using System.ComponentModel;
using System.Drawing;
using System.Windows;
using LanTransfer.App.Services;
using LanTransfer.App.ViewModels;
using LanTransfer.App.Views.Dialogs;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Views;

/// <summary>主窗口。只负责装配 ViewModel 与拖放转发，不含任何业务逻辑。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settings;
    private readonly ITransferManager _transfers;
    private readonly TransferNotificationTracker _notificationTracker;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly Icon? _ownedIcon;
    private bool _allowExit;
    private bool _initialized;
    private bool _trayIconDisposed;
    private bool _exitRequested;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, ITransferManager transfers,
        TransferNotificationTracker notificationTracker)
    {
        _viewModel = viewModel;
        _settings = settings;
        _transfers = transfers;
        _notificationTracker = notificationTracker;
        InitializeComponent();
        Title = $"LAN Transfer v{AppConstants.AppVersion} — 局域网文件传输与同步";

        DataContext = viewModel;
        Loaded += (_, _) => InitializeViewModel();
        Closing += OnClosing;

        using var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app-icon.ico"))?.Stream;
        if (iconStream is not null)
        {
            using var loadedIcon = new Icon(iconStream);
            _ownedIcon = (Icon)loadedIcon.Clone();
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 LAN Transfer", null, (_, _) => ShowFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("恢复「每次询问关闭方式」", null, (_, _) => ResetClosePrompt());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "LAN Transfer · 局域网传输与同步",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.BalloonTipClicked += (_, _) => ShowTransfersFromTray();
        _transfers.TransferUpdated += OnTransferUpdated;
    }

    public void StartHidden()
    {
        InitializeViewModel();
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
    }

    /// <summary>用户再次启动程序时，恢复已有实例的主窗口。</summary>
    public void ActivateFromSecondaryInstance() => ShowFromTray();

    public void AllowApplicationExit()
    {
        _allowExit = true;
        DisposeTrayIcon();
    }

    private void InitializeViewModel()
    {
        if (_initialized) return;
        _initialized = true;
        _viewModel.Initialize();
    }

    /// <summary>
    /// 关闭窗口。只询问一次：默认按配置里记住的行为处理；
    /// 只有行为为「每次询问」时才弹窗，并把用户本次的选择存下来。
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowExit) return;
        if (_exitRequested)
        {
            e.Cancel = true;
            return;
        }

        var action = _settings.Current.CloseAction;
        var rememberChoice = false;

        if (action == CloseWindowAction.Ask)
        {
            var dialog = new CloseConfirmDialog(CloseWindowAction.Ask) { Owner = this };

            // 取消 = 什么都不做，返回程序
            if (dialog.ShowDialog() != true)
            {
                e.Cancel = true;
                return;
            }

            action = dialog.SelectedAction;
            rememberChoice = dialog.RememberChoice;
        }

        if (action == CloseWindowAction.MinimizeToTray)
        {
            // 先取消关闭并隐藏窗口，再异步落盘。不能在 UI 线程上
            // 用 GetResult 同步等待，否则文件流的异步释放会与 UI 上下文互相等待。
            e.Cancel = true;
            HideToTray(showTip: true);

            if (rememberChoice)
                await SaveCloseActionAsync(action);

            return;
        }

        if (rememberChoice)
        {
            e.Cancel = true;
            _exitRequested = true;
            await SaveCloseActionAsync(action);
            await ((App)Application.Current).RequestExitAsync();
            return;
        }

        e.Cancel = true;
        _exitRequested = true;
        await ((App)Application.Current).RequestExitAsync();
    }

    private async Task SaveCloseActionAsync(CloseWindowAction action)
    {
        try
        {
            var updated = _settings.Current.Clone();
            updated.CloseAction = action;
            await _settings.SaveAsync(updated);
        }
        catch
        {
            // 保存失败不影响本次关闭行为，下次仍会询问
        }
    }

    /// <summary>托盘菜单：改回「每次询问」。</summary>
    private async void ResetClosePrompt()
    {
        await SaveCloseActionAsync(CloseWindowAction.Ask);

        _trayIcon.BalloonTipTitle = "已恢复关闭询问";
        _trayIcon.BalloonTipText = "下次关闭窗口时会再次询问关闭方式。";
        _trayIcon.ShowBalloonTip(3000);
    }

    private void HideToTray(bool showTip)
    {
        Hide();
        ShowInTaskbar = false;

        if (!showTip) return;

        _trayIcon.BalloonTipTitle = "LAN Transfer 正在后台运行";
        _trayIcon.BalloonTipText = "文件接收和实时同步将继续进行。双击托盘图标可恢复窗口。";
        _trayIcon.ShowBalloonTip(3000);
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            InitializeViewModel();
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ShowTransfersFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _viewModel.SelectedNavigationIndex = 0;
            ShowFromTray();
        });
    }

    private void OnTransferUpdated(object? sender, TransferRecord record)
    {
        // 删除未完成任务时管理器也会抛一次 TransferUpdated；这不是新的失败。
        if (_transfers.Transfers.All(item =>
                !string.Equals(item.TransferId, record.TransferId, StringComparison.OrdinalIgnoreCase)))
        {
            _notificationTracker.Forget(record.TransferId);
            return;
        }

        var notification = _notificationTracker.Observe(record);
        if (notification is null || !_settings.Current.TransferNotifications) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_trayIconDisposed) return;

            _trayIcon.BalloonTipIcon = notification.Kind == TransferNotificationKind.Success
                ? System.Windows.Forms.ToolTipIcon.Info
                : System.Windows.Forms.ToolTipIcon.Error;
            _trayIcon.BalloonTipTitle = notification.Title;
            _trayIcon.BalloonTipText = notification.Message;
            _trayIcon.ShowBalloonTip(6000);
        });
    }

    private void ExitApplication()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitRequested) return;
            _exitRequested = true;
            _ = ((App)Application.Current).RequestExitAsync();
        });
    }

    private void DisposeTrayIcon()
    {
        if (_trayIconDisposed) return;
        _trayIconDisposed = true;

        _transfers.TransferUpdated -= OnTransferUpdated;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
