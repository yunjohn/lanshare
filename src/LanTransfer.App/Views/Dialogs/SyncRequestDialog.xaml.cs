using System.IO;
using System.Windows;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Views.Dialogs;

/// <summary>同步请求授权弹窗：对端请求建立同步关系，由本机用户决定是否同意及本机目录。</summary>
public partial class SyncRequestDialog : Window
{
    public SyncRequestDialog(SyncRequestEventArgs request)
    {
        InitializeComponent();

        Request = request;
        DataContext = this;
    }

    public SyncRequestEventArgs Request { get; }

    /// <summary>用户选择的本机同步目录（ShowDialog 返回 true 后有效）。</summary>
    public string? SelectedLocalPath { get; private set; }

    public string RemoteDeviceName => Request.RemoteDeviceName;

    public string PairName => string.IsNullOrWhiteSpace(Request.Name) ? "同步关系" : Request.Name;

    public string RequestedRemotePath => Request.RequestedRemotePath;

    public string SuggestedLocalPath => Request.SuggestedLocalPath;

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择本机同步目录",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(LocalPathBox.Text) && Directory.Exists(LocalPathBox.Text))
            dialog.InitialDirectory = LocalPathBox.Text;

        if (dialog.ShowDialog() == true)
            LocalPathBox.Text = dialog.FolderName;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        var path = LocalPathBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请选择本机同步目录。", "同步请求", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedLocalPath = path;
        DialogResult = true;
    }

    private void OnRejectClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
