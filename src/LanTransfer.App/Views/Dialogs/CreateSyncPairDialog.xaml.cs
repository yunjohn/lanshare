using System.IO;
using System.Windows;
using LanTransfer.Common.Models;

namespace LanTransfer.App.Views.Dialogs;

/// <summary>新建同步关系弹窗：由发起方指定本机目录、对端目录与同步模式。</summary>
public partial class CreateSyncPairDialog : Window
{
    public CreateSyncPairDialog(string defaultLocalPath, string remoteDeviceName, string remotePath)
    {
        InitializeComponent();

        DefaultLocalPath = defaultLocalPath;
        RemoteDeviceName = remoteDeviceName;
        DefaultRemotePath = remotePath;
        DefaultPairName = string.IsNullOrWhiteSpace(remoteDeviceName)
            ? "同步关系"
            : $"{remoteDeviceName} 同步";

        SelectedMode = SyncMode.TwoWay;
        DataContext = this;
    }

    public string DefaultLocalPath { get; }

    public string RemoteDeviceName { get; }

    public string DefaultRemotePath { get; }

    public string DefaultPairName { get; }

    public IReadOnlyList<SyncMode> Modes { get; } = new[]
    {
        SyncMode.TwoWay, SyncMode.SendOnly, SyncMode.ReceiveOnly,
    };

    public SyncMode SelectedMode { get; set; } = SyncMode.TwoWay;

    /// <summary>用户确认的本机同步目录。</summary>
    public string SelectedLocalPath { get; private set; } = string.Empty;

    /// <summary>用户指定的对端同步目录。</summary>
    public string RemotePath { get; private set; } = string.Empty;

    /// <summary>同步关系名称。</summary>
    public string PairName { get; private set; } = string.Empty;

    /// <summary>同步模式。</summary>
    public SyncMode Mode => SelectedMode;

    private void OnBrowseLocalClick(object sender, RoutedEventArgs e)
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

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var local = LocalPathBox.Text?.Trim();
        var remote = RemotePathBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(local))
        {
            MessageBox.Show("请选择本机同步目录。", "新建同步关系", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            MessageBox.Show("请填写对端同步目录（对方电脑上的绝对路径）。", "新建同步关系",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedLocalPath = local;
        RemotePath = remote;
        PairName = string.IsNullOrWhiteSpace(PairNameBox.Text) ? DefaultPairName : PairNameBox.Text.Trim();
        SelectedMode = ModeBox.SelectedItem is SyncMode mode ? mode : SyncMode.TwoWay;

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
