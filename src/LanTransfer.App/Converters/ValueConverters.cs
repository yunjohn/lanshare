using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LanTransfer.Common.Extensions;
using LanTransfer.Common.Models;

namespace LanTransfer.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>对象非 null → 可见（用于「已选中某项」的场景）。</summary>
public sealed class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>对象为 null → 可见（用于空状态提示）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            long l => l.ToSizeString(),
            int i => ((long)i).ToSizeString(),
            double d => ((long)d).ToSizeString(),
            _ => "--",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? d.ToSpeedString() : "--";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EtaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TimeSpan ts ? ((TimeSpan?)ts).ToEtaString() : "--";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? d.ToPercentString() : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class TransferStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TransferState state ? Describe(state) : string.Empty;

    public static string Describe(TransferState state) => state switch
    {
        TransferState.Pending => "等待中",
        TransferState.Queued => "排队中",
        TransferState.WaitingApproval => "等待对方确认",
        TransferState.Preparing => "准备中",
        TransferState.Transferring => "传输中",
        TransferState.Paused => "已暂停",
        TransferState.Verifying => "校验中",
        TransferState.Completed => "已完成",
        TransferState.Rejected => "已被拒绝",
        TransferState.Cancelled => "已取消",
        TransferState.Failed => "失败",
        TransferState.VerificationFailed => "校验失败",
        _ => state.ToString(),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class TransferStateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Completed = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush Active = new(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush Waiting = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Failure = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x5B, 0x64, 0x72));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TransferState state
            ? state switch
            {
                TransferState.Completed => Completed,
                TransferState.Transferring or TransferState.Preparing or TransferState.Verifying => Active,
                TransferState.WaitingApproval or TransferState.Paused or TransferState.Queued or
                    TransferState.Pending => Waiting,
                TransferState.Failed or TransferState.VerificationFailed or TransferState.Rejected => Failure,
                _ => Neutral,
            }
            : Neutral;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class DirectionArrowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TransferDirection direction
            ? direction == TransferDirection.Send ? "↑ 发送" : "↓ 接收"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class SyncStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SyncStatus status ? Describe(status) : string.Empty;

    public static string Describe(SyncStatus status) => status switch
    {
        SyncStatus.Idle => "已同步",
        SyncStatus.Scanning => "扫描中",
        SyncStatus.Syncing => "同步中",
        SyncStatus.Paused => "已暂停",
        SyncStatus.Conflict => "存在冲突",
        SyncStatus.Error => "错误",
        SyncStatus.WaitingApproval => "等待确认",
        _ => status.ToString(),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SyncMode mode)
            return mode switch
            {
                SyncMode.TwoWay => "双向同步",
                SyncMode.SendOnly => "仅发送（本机 → 对端）",
                SyncMode.ReceiveOnly => "仅接收（对端 → 本机）",
                _ => mode.ToString(),
            };

        if (value is TrustState trust)
            return trust switch
            {
                TrustState.Unknown => "陌生设备",
                TrustState.Pending => "待确认",
                TrustState.Trusted => "可信设备",
                TrustState.Revoked => "已解除信任",
                TrustState.IdentityChanged => "身份已变化",
                _ => trust.ToString(),
            };

        if (value is ConflictPolicy policy)
            return policy switch
            {
                ConflictPolicy.Overwrite => "覆盖",
                ConflictPolicy.Rename => "重命名（默认）",
                ConflictPolicy.Skip => "跳过",
                _ => policy.ToString(),
            };

        if (value is OnlineState online)
            return online switch
            {
                OnlineState.Online => "在线",
                OnlineState.Offline => "离线",
                _ => online.ToString(),
            };

        if (value is NetworkCategoryText text) return text.Value;

        if (value is CloseWindowAction close)
            return close switch
            {
                CloseWindowAction.Ask => "每次询问",
                CloseWindowAction.MinimizeToTray => "最小化到系统托盘",
                CloseWindowAction.Exit => "退出程序",
                _ => close.ToString(),
            };

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>包装网络类别枚举，便于在 ComboBox / TextBlock 中显示中文。</summary>
public sealed record NetworkCategoryText(string Value);

/// <summary>
/// 把「同步冲突记录」+ 裁决方式（ConverterParameter：UseLocal / UseRemote / KeepBoth）
/// 组装成 <see cref="ViewModels.ConflictResolutionRequest"/>，供裁决命令作为参数使用。
/// </summary>
public sealed class ConflictRequestConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Core.Interfaces.SyncConflictRecord conflict) return null;

        var resolution = parameter?.ToString() switch
        {
            "UseLocal" => Sync.Engine.ConflictResolution.UseLocal,
            "UseRemote" => Sync.Engine.ConflictResolution.UseRemote,
            _ => Sync.Engine.ConflictResolution.KeepBoth,
        };

        return new ViewModels.ConflictResolutionRequest(conflict.SyncPairId, conflict, resolution);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>导航索引 → 可见性（ConverterParameter 为目标索引）。</summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;

        return value.ToString() == parameter.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
