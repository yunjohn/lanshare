using System.Windows;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Views.Dialogs;

/// <summary>接收确认弹窗。用户决定是否接收来自某设备的文件。</summary>
public partial class IncomingTransferDialog : Window
{
    public IncomingTransferDialog(IncomingTransferEventArgs request)
    {
        InitializeComponent();

        Request = request;
        DataContext = this;
    }

    public IncomingTransferEventArgs Request { get; }

    public string RemoteDeviceName => Request.RemoteDeviceName;

    public string RootName => string.IsNullOrWhiteSpace(Request.RootName) ? "未命名传输" : Request.RootName;

    public string TypeText => Request.TransferType == TransferType.Folder ? "文件夹（含子目录）" : "单个文件";

    public string FileCountText => Request.TotalFiles > 1 ? $"{Request.TotalFiles} 个文件" : "1 个文件";

    public string TotalSizeText => Request.TotalSize.ToSizeString();

    public string FirstFileText => Request.FirstFileName;

    public bool HasFirstFile => !string.IsNullOrWhiteSpace(Request.FirstFileName);

    public bool IsTrusted => Request.IsTrusted;

    public string TrustText => Request.IsTrusted ? "可信设备（已配对）" : "陌生设备（未配对）";

    public string TrustHint => Request.IsTrusted
        ? "该设备已通过配对验证，证书指纹与记录一致。"
        : "该设备尚未与本机配对。请确认这是你认识的电脑；不确定时建议拒绝。";

    private void OnAcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnRejectClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
