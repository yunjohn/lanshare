using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;

namespace LanTransfer.App.Services;

/// <summary>
/// 把传输状态变化转换为一次性系统通知。同一任务重复上报终态时不会重复弹出；
/// 任务恢复到非终态后，下一次成功或失败仍会通知。
/// </summary>
public sealed class TransferNotificationTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TransferNotificationKind> _notified =
        new(StringComparer.OrdinalIgnoreCase);

    public TransferSystemNotification? Observe(TransferRecord record)
    {
        var kind = record.State switch
        {
            TransferState.Completed => TransferNotificationKind.Success,
            TransferState.Failed or TransferState.VerificationFailed => TransferNotificationKind.Failure,
            _ => (TransferNotificationKind?)null,
        };

        lock (_gate)
        {
            if (kind is null)
            {
                if (!record.State.IsTerminal()) _notified.Remove(record.TransferId);
                return null;
            }

            if (_notified.TryGetValue(record.TransferId, out var previous) && previous == kind.Value)
                return null;

            _notified[record.TransferId] = kind.Value;
        }

        var direction = record.Direction == TransferDirection.Receive ? "接收" : "发送";
        var name = string.IsNullOrWhiteSpace(record.RootName) ? "文件传输" : record.RootName;
        var device = string.IsNullOrWhiteSpace(record.RemoteDeviceName) ? "对方设备" : record.RemoteDeviceName;

        if (kind == TransferNotificationKind.Success)
        {
            var count = record.TotalFiles > 0 ? $"，{record.TotalFiles} 个文件" : string.Empty;
            var size = record.TotalSize > 0 ? $"，{record.TotalSize.ToSizeString()}" : string.Empty;
            return new TransferSystemNotification(kind.Value, $"文件{direction}完成",
                $"{name}\n{device}{count}{size}");
        }

        var error = string.IsNullOrWhiteSpace(record.ErrorMessage)
            ? (record.State == TransferState.VerificationFailed ? "文件完整性校验失败" : "请打开程序查看详情并重试")
            : record.ErrorMessage;

        return new TransferSystemNotification(kind.Value, $"文件{direction}失败",
            $"{name} · {device}\n{error}");
    }

    public void Forget(string transferId)
    {
        lock (_gate) _notified.Remove(transferId);
    }
}

public enum TransferNotificationKind
{
    Success,
    Failure,
}

public sealed record TransferSystemNotification(
    TransferNotificationKind Kind,
    string Title,
    string Message);
