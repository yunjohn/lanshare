using System.ComponentModel;
using System.Drawing;
using System.Windows;
using LanTransfer.App.ViewModels;
using LanTransfer.App.Views.Dialogs;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Views;

/// <summary>主窗口。只负责装配 ViewModel 与拖放转发，不含任何业务逻辑。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settings;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly Icon? _ownedIcon;
    private bool _allowExit;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel, ISettingsService settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        InitializeComponent();

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
    }

    public void StartHidden()
    {
        InitializeViewModel();
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
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
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowExit) return;

        var action = _settings.Current.CloseAction;

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

            if (dialog.RememberChoice)
            {
                // 同步等待落盘：随后可能立刻退出进程，异步保存会来不及写完
                SaveCloseAction(action);
            }
        }

        if (action == CloseWindowAction.Exit)
        {
            _allowExit = true;
            DisposeTrayIcon();
            return;
        }

        // 最小化到托盘：取消本次关闭，仅隐藏窗口
        e.Cancel = true;
        HideToTray(showTip: true);
    }

    private void SaveCloseAction(CloseWindowAction action)
    {
        try
        {
            var updated = _settings.Current.Clone();
            updated.CloseAction = action;
            _settings.SaveAsync(updated).GetAwaiter().GetResult();
        }
        catch
        {
            // 保存失败不影响本次关闭行为，下次仍会询问
        }
    }

    /// <summary>托盘菜单：改回「每次询问」。</summary>
    private void ResetClosePrompt()
    {
        SaveCloseAction(CloseWindowAction.Ask);

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

    private void ExitApplication()
    {
        Dispatcher.Invoke(() =>
        {
            _allowExit = true;
            DisposeTrayIcon();
            Application.Current.Shutdown();
        });
    }

    private void DisposeTrayIcon()
    {
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
