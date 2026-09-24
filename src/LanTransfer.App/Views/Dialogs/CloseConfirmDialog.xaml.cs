using System.Windows;
using LanTransfer.Common.Models;

namespace LanTransfer.App.Views.Dialogs;

/// <summary>
/// 关闭方式确认弹窗。只需询问一次：把用户的选择持久化到配置，
/// 之后 MainWindow 直接按记住的行为关闭，不再弹窗。
/// </summary>
public partial class CloseConfirmDialog : Window
{
    /// <param name="remembered">上次记住的行为，作为本次的默认选项。</param>
    public CloseConfirmDialog(CloseWindowAction remembered)
    {
        InitializeComponent();

        // 「下次默认当前选择」：把记住的行为预选上，直接回车即沿用
        var exit = remembered == CloseWindowAction.Exit;
        ExitRadio.IsChecked = exit;
        TrayRadio.IsChecked = !exit;
    }

    /// <summary>用户本次选择的行为。</summary>
    public CloseWindowAction SelectedAction =>
        ExitRadio.IsChecked == true ? CloseWindowAction.Exit : CloseWindowAction.MinimizeToTray;

    /// <summary>是否勾选了「以后不再询问」。</summary>
    public bool RememberChoice => RememberBox.IsChecked == true;

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
