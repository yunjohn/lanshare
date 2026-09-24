using System.IO;
using System.Windows;
using LanTransfer.App.Views.Dialogs;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Services;

/// <summary>对话框服务：让 ViewModel 在不直接引用 Window 的前提下弹出交互窗口。</summary>
public interface IDialogService
{
    /// <summary>陌生设备发送文件 → 接收确认。</summary>
    bool ConfirmIncomingTransfer(IncomingTransferEventArgs request);

    /// <summary>设备配对：两端显示同一验证码，用户确认。</summary>
    bool ConfirmPairing(PairingRequestedEventArgs request);

    /// <summary>对端请求创建同步关系。</summary>
    (bool Accepted, string? LocalPath) ConfirmSyncRequest(SyncRequestEventArgs request);

    /// <summary>创建同步关系的目录选择。</summary>
    (bool Confirmed, string LocalPath, string RemotePath, string Name, SyncMode Mode) CreateSyncPair(
        string defaultLocalPath, string remoteDeviceName, string remotePath);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>选择文件夹。</summary>
    string? PickFolder(string? initialDirectory = null);

    /// <summary>选择一个或多个文件；取消返回 null。</summary>
    IReadOnlyList<string>? PickFiles(string title = "选择要发送的文件");
}

public sealed class DialogService : IDialogService
{
    /// <summary>
    /// 取可用的 Owner。WPF 不允许把 Owner 设为「从未显示过」的窗口（会抛 InvalidOperationException），
    /// 而 --background / 开机自启时主窗口恰恰只设了状态、从未 Show 过——
    /// 那样所有接收确认都会抛异常并被上层按「拒绝」处理，用户完全收不到文件。
    /// </summary>
    private static Window? SafeOwner()
    {
        var main = Application.Current?.MainWindow;
        return main is { IsLoaded: true } ? main : null;
    }

    public bool ConfirmIncomingTransfer(IncomingTransferEventArgs request)
    {
        var window = new IncomingTransferDialog(request)
        {
            Owner = SafeOwner(),
        };

        return window.ShowDialog() == true;
    }

    public bool ConfirmPairing(PairingRequestedEventArgs request)
    {
        var window = new PairingDialog(request)
        {
            Owner = SafeOwner(),
        };

        return window.ShowDialog() == true;
    }

    public (bool Accepted, string? LocalPath) ConfirmSyncRequest(SyncRequestEventArgs request)
    {
        var window = new SyncRequestDialog(request)
        {
            Owner = SafeOwner(),
        };

        var result = window.ShowDialog() == true;
        return (result, result ? window.SelectedLocalPath : null);
    }

    public (bool Confirmed, string LocalPath, string RemotePath, string Name, SyncMode Mode) CreateSyncPair(
        string defaultLocalPath, string remoteDeviceName, string remotePath)
    {
        var window = new CreateSyncPairDialog(defaultLocalPath, remoteDeviceName, remotePath)
        {
            Owner = SafeOwner(),
        };

        var result = window.ShowDialog() == true;

        return result
            ? (true, window.SelectedLocalPath, window.RemotePath, window.PairName, window.Mode)
            : (false, string.Empty, string.Empty, string.Empty, SyncMode.TwoWay);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) ==
        MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickFolder(string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string>? PickFiles(string title = "选择要发送的文件")
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
}
