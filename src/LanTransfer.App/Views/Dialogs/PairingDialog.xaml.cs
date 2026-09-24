using System.Windows;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.Views.Dialogs;

/// <summary>配对确认弹窗：两端显示同一验证码，由用户比对确认。</summary>
public partial class PairingDialog : Window
{
    public PairingDialog(PairingRequestedEventArgs request)
    {
        InitializeComponent();

        Request = request;
        DataContext = this;
    }

    public PairingRequestedEventArgs Request { get; }

    public string RemoteDeviceName => Request.RemoteDeviceName;

    public string RemoteDeviceId => Request.RemoteDeviceId;

    public string RemoteFingerprint => Request.RemoteFingerprint;

    public string VerificationCode => Request.VerificationCode;

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
