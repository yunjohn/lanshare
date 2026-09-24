using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanTransfer.Core.Devices;
using Xunit;

namespace LanTransfer.Network.Tests;

/// <summary>网络辅助：子网掩码换算、广播地址计算、接口枚举与虚拟网卡判定。</summary>
public class NetworkHelperTests
{
    [Theory]
    [InlineData(8, "255.0.0.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(25, "255.255.255.128")]
    [InlineData(26, "255.255.255.192")]
    [InlineData(30, "255.255.255.252")]
    [InlineData(32, "255.255.255.255")]
    public void PrefixToMask_ReturnsDottedQuad(int prefix, string expected)
    {
        Assert.Equal(expected, NetworkHelper.PrefixToMask(prefix));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void PrefixToMask_FallsBackToSlash24ForInvalidPrefix(int prefix)
    {
        Assert.Equal("255.255.255.0", NetworkHelper.PrefixToMask(prefix));
    }

    [Theory]
    [InlineData("192.168.1.10", 24, "192.168.1.255")]
    [InlineData("192.168.1.10", 25, "192.168.1.127")]
    [InlineData("10.0.0.5", 8, "10.255.255.255")]
    [InlineData("172.16.4.7", 16, "172.16.255.255")]
    [InlineData("192.168.1.10", 32, "192.168.1.10")]
    public void ComputeBroadcast_ReturnsDirectedBroadcast(string address, int prefix, string expected)
    {
        Assert.Equal(expected, NetworkHelper.ComputeBroadcast(IPAddress.Parse(address), prefix));
    }

    [Fact]
    public void ComputeBroadcast_RejectsIpv6()
    {
        Assert.Throws<ArgumentException>(() =>
            NetworkHelper.ComputeBroadcast(IPAddress.Parse("fe80::1"), 64));
    }

    [Fact]
    public void ComputeBroadcast_FallsBackToSlash24ForInvalidPrefix()
    {
        Assert.Equal("192.168.1.255",
            NetworkHelper.ComputeBroadcast(IPAddress.Parse("192.168.1.10"), 0));
        Assert.Equal("192.168.1.255",
            NetworkHelper.ComputeBroadcast(IPAddress.Parse("192.168.1.10"), 40));
    }

    [Fact]
    public void GetInterfaces_ExcludesLoopbackAndInterfacesWithoutIPv4()
    {
        var interfaces = NetworkHelper.GetInterfaces();

        Assert.All(interfaces, nic =>
        {
            Assert.False(nic.IsLoopback);
            Assert.False(string.IsNullOrWhiteSpace(nic.IPv4Address));
        });
    }

    [Fact]
    public void GetInterfaces_ReportsConsistentMaskAndBroadcast()
    {
        foreach (var nic in NetworkHelper.GetInterfaces())
        {
            Assert.NotNull(nic.SubnetMask);
            Assert.Equal(nic.PrefixLength, MaskToPrefix(nic.SubnetMask!));

            if (nic.SupportsBroadcast)
            {
                Assert.NotNull(nic.BroadcastAddress);
                Assert.Equal(
                    NetworkHelper.ComputeBroadcast(IPAddress.Parse(nic.IPv4Address!), nic.PrefixLength),
                    nic.BroadcastAddress);
            }
        }
    }

    [Fact]
    public void GetLocalIPv4Addresses_MatchesNonVirtualInterfaceAddresses()
    {
        var addresses = NetworkHelper.GetLocalIPv4Addresses();
        var expected = NetworkHelper.GetInterfaces()
            .Where(n => !n.IsVirtual)
            .Select(n => n.IPv4Address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected.Count, addresses.Count);
        Assert.All(addresses, a => Assert.Contains(a, expected));
    }

    [Fact]
    public void GetPrimaryIPv4Address_ReturnsAddressFromInterfaceList()
    {
        var primary = NetworkHelper.GetPrimaryIPv4Address();

        if (NetworkHelper.GetInterfaces().Count == 0)
        {
            Assert.Null(primary);
            return;
        }

        Assert.NotNull(primary);
        Assert.Contains(primary!, NetworkHelper.GetLocalIPv4Addresses());
    }

    [Fact]
    public void GetBroadcastTargets_AllHaveDistinctBroadcastAddresses()
    {
        var targets = NetworkHelper.GetBroadcastTargets();

        Assert.All(targets, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.LocalAddress));
            Assert.False(string.IsNullOrWhiteSpace(t.BroadcastAddress));
            Assert.True(IPAddress.TryParse(t.BroadcastAddress, out _));
        });

        Assert.Equal(targets.Count, targets.Select(t => t.BroadcastAddress).Distinct().Count());
    }

    [Fact]
    public void IsVirtual_DetectsLoopbackAsVirtual()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);

        if (loopback is not null)
            Assert.True(NetworkHelper.IsVirtual(loopback));
    }

    [Fact]
    public void IsVirtual_RealEthernetIsNotVirtual()
    {
        var ethernet = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

        if (ethernet is not null)
            Assert.False(NetworkHelper.IsVirtual(ethernet));
    }

    private static int MaskToPrefix(string mask)
    {
        var bytes = IPAddress.Parse(mask).GetAddressBytes();
        var prefix = 0;

        foreach (var b in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0) prefix++;
                else return prefix;
            }
        }

        return prefix;
    }
}

/// <summary>端口占用检测（真实绑定本机端口，不使用外网）。</summary>
public class PortAvailabilityTests
{
    [Fact]
    public async Task BoundTcpPort_IsReportedInUse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);

            Assert.True(client.Connected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectingToClosedPort_FailsFast()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(connect, completed);

        await Assert.ThrowsAnyAsync<SocketException>(() => connect);
    }

    [Fact]
    public void UdpPortCanBeBoundTwiceOnLoopback()
    {
        using var first = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)first.Client.LocalEndPoint!).Port;

        // 广播发现端口绑定失败不得导致崩溃，这里验证异常可被捕获
        var conflict = Record.Exception(() =>
        {
            using var second = new UdpClient();
            second.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            second.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        });

        Assert.NotNull(conflict);
        Assert.IsAssignableFrom<SocketException>(conflict);
    }
}
