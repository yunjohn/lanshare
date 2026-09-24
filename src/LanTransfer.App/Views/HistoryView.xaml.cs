using System.Windows;
using System.Windows.Controls;
using LanTransfer.App.ViewModels;

namespace LanTransfer.App.Views;

/// <summary>历史记录页。</summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryItemViewModel item })
            item.OpenFolder();
    }
}
