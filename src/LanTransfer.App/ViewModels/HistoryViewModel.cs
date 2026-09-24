using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.App.ViewModels;

/// <summary>历史记录页。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ITransferManager _transfers;
    private readonly IDialogService _dialogs;
    private readonly ILogger<HistoryViewModel> _logger;

    public HistoryViewModel(ITransferManager transfers, IDialogService dialogs, ILogger<HistoryViewModel> logger)
    {
        _transfers = transfers;
        _dialogs = dialogs;
        _logger = logger;

        _transfers.TransferUpdated += (_, _) => Refresh();
        _transfers.TransferAdded += (_, _) => Refresh();

        Refresh();
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _summary = string.Empty;

    [ObservableProperty] private bool _showCompletedOnly;

    public bool HasItems => Items.Count > 0;

    public void Refresh()
    {
        UiDispatcher.Send(() =>
        {
            var records = _transfers.Transfers
                .Where(r => !ShowCompletedOnly || r.State == TransferState.Completed)
                .ToList();

            Items.Clear();
            foreach (var record in records) Items.Add(new HistoryItemViewModel(record));

            var completed = records.Count(r => r.State == TransferState.Completed);
            var failed = records.Count(r => r.State is TransferState.Failed or TransferState.VerificationFailed);
            var totalBytes = records.Where(r => r.State == TransferState.Completed).Sum(r => r.TotalSize);

            Summary = $"共 {records.Count} 条记录，成功 {completed}，失败 {failed}，累计传输 {totalBytes.ToSizeString()}";

            OnPropertyChanged(nameof(HasItems));
        });
    }

    partial void OnShowCompletedOnlyChanged(bool value) => Refresh();

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!_dialogs.Confirm("清空历史", "确定清空全部已结束的传输记录？（未完成的任务会保留，可继续断点续传）"))
            return;

        await _transfers.ClearHistoryAsync().ConfigureAwait(true);
        Refresh();
    }
}

/// <summary>历史记录列表项。</summary>
public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(TransferRecord record)
    {
        Record = record;

        DirectionText = record.Direction == TransferDirection.Send ? "↑ 发送" : "↓ 接收";
        DeviceText = record.RemoteDeviceName;
        NameText = string.IsNullOrWhiteSpace(record.RootName) ? "传输任务" : record.RootName;
        SizeText = record.TotalSize.ToSizeString();
        StateText = Converters.TransferStateTextConverter.Describe(record.State);
        TimeText = record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DurationText = record.CompletedAt.HasValue
            ? $"耗时 {(record.CompletedAt.Value - record.CreatedAt).TotalSeconds:0} 秒"
            : string.Empty;
        ErrorText = record.ErrorMessage ?? string.Empty;
        FolderPath = record.DownloadRoot;
    }

    public TransferRecord Record { get; }

    public string DirectionText { get; }
    public string DeviceText { get; }
    public string NameText { get; }
    public string SizeText { get; }
    public string StateText { get; }
    public string TimeText { get; }
    public string DurationText { get; }
    public string ErrorText { get; }
    public string? FolderPath { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath);

    public void OpenFolder()
    {
        if (!CanOpenFolder) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FolderPath}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }
}
