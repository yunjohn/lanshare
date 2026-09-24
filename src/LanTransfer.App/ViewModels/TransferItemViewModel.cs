using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.App.ViewModels;

/// <summary>传输任务列表项 / 卡片。UI 不直接操作文件，只调用 TransferManager。</summary>
public sealed partial class TransferItemViewModel : ObservableObject
{
    private readonly ITransferManager _transfers;

    public TransferItemViewModel(TransferRecord record, ITransferManager transfers)
    {
        _transfers = transfers;
        Record = record;
        Apply();
    }

    public TransferRecord Record { get; }

    public string TransferId => Record.TransferId;

    [ObservableProperty] private string _displayName = string.Empty;

    [ObservableProperty] private string _subTitle = string.Empty;

    [ObservableProperty] private TransferState _state = TransferState.Pending;

    [ObservableProperty] private TransferDirection _direction = TransferDirection.Send;

    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private string _speedText = "--";

    [ObservableProperty] private string _etaText = "--";

    [ObservableProperty] private string _fileCountText = string.Empty;

    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string? _savedPath;

    [ObservableProperty] private string _stateText = string.Empty;

    [ObservableProperty] private bool _isActive;

    public bool CanPause => State is TransferState.Transferring or TransferState.Preparing;
    public bool CanResume => State is TransferState.Paused or TransferState.Failed or TransferState.Cancelled;
    public bool CanCancel => State is TransferState.Transferring or TransferState.Preparing or
        TransferState.Paused or TransferState.WaitingApproval or TransferState.Queued or TransferState.Pending;
    public bool CanDelete => !State.IsTerminal() || State == TransferState.Failed ||
                             State == TransferState.VerificationFailed;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsReceive => Direction == TransferDirection.Receive;

    /// <summary>用最新的记录刷新界面字段。</summary>
    public void Update(TransferRecord record)
    {
        Record.State = record.State;
        Record.TransferredSize = record.TransferredSize;
        Record.TotalSize = record.TotalSize;
        Record.CompletedFiles = record.CompletedFiles;
        Record.ErrorCode = record.ErrorCode;
        Record.ErrorMessage = record.ErrorMessage;
        Record.CompletedAt = record.CompletedAt;

        Apply();
    }

    /// <summary>用实时进度快照刷新。</summary>
    public void UpdateProgress(TransferProgressSnapshot snapshot)
    {
        UiDispatcher.Send(() =>
        {
            State = snapshot.State;
            Percent = snapshot.Percent;
            ProgressText = $"{snapshot.TransferredSize.ToSizeString()} / {snapshot.TotalSize.ToSizeString()}";
            SpeedText = snapshot.BytesPerSecond.ToSpeedString();
            EtaText = snapshot.Eta.ToEtaString();
            FileCountText = snapshot.TotalFiles > 1
                ? $"{snapshot.CompletedFiles} / {snapshot.TotalFiles} 个文件"
                : string.Empty;
            ErrorMessage = snapshot.ErrorMessage;
            StateText = Converters.TransferStateTextConverter.Describe(snapshot.State);
            IsActive = snapshot.State.IsActive();
            NotifyCommandState();
        });
    }

    private void Apply()
    {
        UiDispatcher.Send(() =>
        {
            DisplayName = string.IsNullOrWhiteSpace(Record.RootName) ? "传输任务" : Record.RootName;
            SubTitle = $"{Record.RemoteDeviceName}";
            State = Record.State;
            Direction = Record.Direction;
            Percent = Record.Percent;
            ProgressText = $"{Record.TransferredSize.ToSizeString()} / {Record.TotalSize.ToSizeString()}";
            FileCountText = Record.TotalFiles > 1 ? $"{Record.CompletedFiles} / {Record.TotalFiles} 个文件" : string.Empty;
            ErrorMessage = Record.ErrorMessage;
            StateText = Converters.TransferStateTextConverter.Describe(Record.State);
            IsActive = Record.State.IsActive();
            NotifyCommandState();
        });
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsReceive));
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        try
        {
            await _transfers.PauseAsync(TransferId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatcher.Send(() => ErrorMessage = ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        try
        {
            await _transfers.ResumeAsync(TransferId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatcher.Send(() => ErrorMessage = ex.Message);
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        try
        {
            await _transfers.CancelAsync(TransferId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatcher.Send(() => ErrorMessage = ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            await _transfers.DeleteIncompleteAsync(TransferId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UiDispatcher.Send(() => ErrorMessage = ex.Message);
        }
    }
}
