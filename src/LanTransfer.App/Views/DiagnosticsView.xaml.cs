using System.Windows;
using System.Windows.Controls;
using LanTransfer.App.ViewModels;

namespace LanTransfer.App.Views;

/// <summary>网络诊断页。</summary>
public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel viewModel)
            viewModel.Refresh();
    }
}
