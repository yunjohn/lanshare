using System.Windows;
using System.Windows.Threading;

namespace LanTransfer.App.Services;

/// <summary>
/// UI 线程调度辅助。所有网络 / IO / Hash / SQLite 回调都必须经此切回 UI 线程，
/// 否则会出现跨线程访问控件异常或界面卡死。
/// </summary>
public static class UiDispatcher
{
    public static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    public static void Send(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
