using System.Text.Json.Serialization;

namespace LanTransfer.Common.Models;

/// <summary>
/// 局域网中的一台设备。DeviceId 为 UUID v4，首次启动生成后永久保存；
/// 不得使用 IP / MAC / 计算机名作为唯一标识。
/// </summary>
public sealed class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>以 UDP 报文实际 Source IP 为准，不信任报文内声明的 IP。</summary>
    public string IpAddress { get; set; } = string.Empty;

    public int Port { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public int ProtocolVersion { get; set; }

    public TrustState TrustState { get; set; } = TrustState.Unknown;

    public OnlineState OnlineState { get; set; } = OnlineState.Offline;

    public DateTimeOffset LastSeen { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>对端自签名证书指纹（SHA-256，十六进制大写）。</summary>
    public string? CertificateFingerprint { get; set; }

    /// <summary>是否为手工添加的设备（不参与 UDP 上下线判定）。</summary>
    public bool IsManual { get; set; }

    [JsonIgnore]
    public bool IsTrusted => TrustState == TrustState.Trusted;

    [JsonIgnore]
    public bool IsOnline => OnlineState == OnlineState.Online;

    [JsonIgnore]
    public string EndPoint => $"{IpAddress}:{Port}";

    public DeviceInfo Clone() => (DeviceInfo)MemberwiseClone();
}

/// <summary>网络接口信息（网络诊断页展示用）。</summary>
public sealed class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public bool IsLoopback { get; set; }
    public bool IsVirtual { get; set; }
    public string? IPv4Address { get; set; }
    public string? SubnetMask { get; set; }
    public string? BroadcastAddress { get; set; }
    public int PrefixLength { get; set; }
    public bool SupportsBroadcast { get; set; }
    public long SpeedBitsPerSecond { get; set; }
}
