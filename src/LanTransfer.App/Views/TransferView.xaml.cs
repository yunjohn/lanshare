using System.Windows;
using System.Windows.Controls;
using LanTransfer.App.ViewModels;

namespace LanTransfer.App.Views;

/// <summary>文件传输页。只负责把拖放事件转发给 ViewModel，不含任何业务逻辑。</summary>
public partial class TransferView : UserControl
{
    public TransferView()
    {
        InitializeComponent();
    }

    private TransferViewModel? ViewModel => DataContext as TransferViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 只有拖入真实文件/文件夹时才显示「复制」光标
        e.Effects = TryGetDroppedPaths(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedPaths(e, out var paths)) return;

        e.Handled = true;

        if (ViewModel is { } viewModel)
            await viewModel.DropPathsAsync(paths).ConfigureAwait(true);
    }

    private static bool TryGetDroppedPaths(DragEventArgs e, out string[] paths)
    {
        paths = Array.Empty<string>();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return false;

        paths = dropped;
        return paths.Length > 0;
    }
}
