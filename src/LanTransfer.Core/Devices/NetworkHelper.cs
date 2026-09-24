using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanTransfer.Common.Models;

namespace LanTransfer.Core.Devices;

/// <summary>
/// 网络接口枚举工具。严禁使用 Dns.GetHostEntry().AddressList[0] 这种取第一张网卡的做法。
/// </summary>
public static class NetworkHelper
{
    private static readonly string[] VirtualKeywords =
    {
        "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "docker",
        "loopback", "tap-", "tun", "teredo", "isatap", "npcap", "bluetooth",
        "openvpn", "wireguard", "zerotier", "tailscale", "radmin", "hamachi",
    };

    /// <summary>枚举所有 IPv4、Up、非 Loopback 的接口。</summary>
    public static IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;
                if (unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    continue; // APIPA 自动专用地址，无法通信

                var prefixLength = unicast.PrefixLength;
                if (prefixLength is <= 0 or > 32) prefixLength = 24;

                result.Add(new NetworkInterfaceInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    InterfaceType = nic.NetworkInterfaceType.ToString(),
                    IsUp = true,
                    IsLoopback = false,
                    IsVirtual = IsVirtual(nic),
                    IPv4Address = unicast.Address.ToString(),
                    SubnetMask = PrefixToMask(prefixLength),
                    BroadcastAddress = ComputeBroadcast(unicast.Address, prefixLength),
                    PrefixLength = prefixLength,
                    SupportsBroadcast = nic.Supports(NetworkInterfaceComponent.IPv4),
                    SpeedBitsPerSecond = SafeSpeed(nic),
                });
            }
        }

        return result;
    }

    /// <summary>本机所有可用于 UDP 广播的 IPv4 地址（去重）。</summary>
    public static IReadOnlyList<string> GetLocalIPv4Addresses() =>
        GetInterfaces()
            .Where(i => !i.IsVirtual)
            .Select(i => i.IPv4Address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>首选 IPv4 地址（优先物理网卡）。</summary>
    public static string? GetPrimaryIPv4Address()
    {
        var interfaces = GetInterfaces();
        if (interfaces.Count == 0) return null;

        var physical = interfaces.FirstOrDefault(i => !i.IsVirtual);
        return (physical ?? interfaces[0]).IPv4Address;
    }

    /// <summary>返回所有应发送广播的 (本地地址, 广播地址) 组合。</summary>
    public static IReadOnlyList<(string LocalAddress, string BroadcastAddress)> GetBroadcastTargets()
    {
        var targets = new List<(string, string)>();

        foreach (var nic in GetInterfaces())
        {
            if (nic.IPv4Address is null || nic.BroadcastAddress is null) continue;
            targets.Add((nic.IPv4Address, nic.BroadcastAddress));
        }

        // 兜底：始终包含受限广播地址，兼容部分网卡无法计算定向广播的情况
        targets.Add(("0.0.0.0", "255.255.255.255"));

        return targets
            .GroupBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static bool IsVirtual(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        return VirtualKeywords.Any(text.Contains);
    }

    /// <summary>把前缀长度换算为点分十进制子网掩码。非法前缀回退到 /24。</summary>
    public static string PrefixToMask(int prefixLength)
    {
        if (prefixLength is <= 0 or > 32) prefixLength = 24;
        var mask = 0xFFFFFFFFu << (32 - prefixLength);
        var bytes = new[]
        {
            (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask,
        };
        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// 计算 IPv4 定向广播地址。只接受 IPv4；传入 IPv6 时直接失败，
    /// 而不是取前 4 字节算出无意义的地址。
    /// </summary>
    public static string ComputeBroadcast(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("只支持 IPv4 地址。", nameof(address));

        var addressBytes = address.GetAddressBytes();
        var mask = prefixLength is <= 0 or > 32 ? 24 : prefixLength;
        var maskValue = 0xFFFFFFFFu << (32 - mask);

        var value = ((uint)addressBytes[0] << 24) | ((uint)addressBytes[1] << 16) |
                    ((uint)addressBytes[2] << 8) | addressBytes[3];

        var broadcast = value | ~maskValue;

        return new IPAddress(new[]
        {
            (byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast,
        }).ToString();
    }

    private static long SafeSpeed(NetworkInterface nic)
    {
        try
        {
            return nic.Speed;
        }
        catch
        {
            return 0;
        }
    }
}
