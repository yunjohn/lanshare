using LanTransfer.App.Services;
using LanTransfer.Common.Models;
using Xunit;

namespace LanTransfer.App.Tests;

public sealed class TransferNotificationTrackerTests
{
    private readonly TransferNotificationTracker _tracker = new();

    [Fact]
    public void CompletedTransfer_ProducesOneSuccessNotification()
    {
        var record = Create(TransferState.Completed);

        var first = _tracker.Observe(record);
        var duplicate = _tracker.Observe(record);

        Assert.NotNull(first);
        Assert.Equal(TransferNotificationKind.Success, first.Kind);
        Assert.Contains("发送完成", first.Title);
        Assert.Contains("photo.zip", first.Message);
        Assert.Null(duplicate);
    }

    [Theory]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.VerificationFailed)]
    public void FailedTransfer_ProducesOneFailureNotification(TransferState state)
    {
        var record = Create(state);
        record.ErrorMessage = "网络连接已断开";

        var notification = _tracker.Observe(record);

        Assert.NotNull(notification);
        Assert.Equal(TransferNotificationKind.Failure, notification.Kind);
        Assert.Contains("网络连接已断开", notification.Message);
        Assert.Null(_tracker.Observe(record));
    }

    [Theory]
    [InlineData(TransferState.Cancelled)]
    [InlineData(TransferState.Rejected)]
    [InlineData(TransferState.Paused)]
    public void CancellationRejectionAndPause_DoNotProduceFailureNotifications(TransferState state)
    {
        Assert.Null(_tracker.Observe(Create(state)));
    }

    [Fact]
    public void ResumedTransfer_CanNotifyAgainAfterAnotherTerminalState()
    {
        var record = Create(TransferState.Failed);
        Assert.NotNull(_tracker.Observe(record));

        record.State = TransferState.Transferring;
        Assert.Null(_tracker.Observe(record));

        record.State = TransferState.Completed;
        var completed = _tracker.Observe(record);
        Assert.NotNull(completed);
        Assert.Equal(TransferNotificationKind.Success, completed.Kind);
    }

    private static TransferRecord Create(TransferState state) => new()
    {
        TransferId = "transfer-1",
        State = state,
        Direction = TransferDirection.Send,
        RootName = "photo.zip",
        RemoteDeviceName = "DESKTOP-A",
        TotalFiles = 1,
        TotalSize = 1024,
    };
}
