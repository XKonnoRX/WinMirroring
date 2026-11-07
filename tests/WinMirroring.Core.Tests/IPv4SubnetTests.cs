using System.Net;
using WinMirroring.Core.Network;

namespace WinMirroring.Core.Tests;

public class IPv4SubnetTests
{
    [Theory]
    [InlineData("192.168.1.156", 24, "192.168.1.0", "192.168.1.255")]
    [InlineData("10.20.30.40", 8, "10.0.0.0", "10.255.255.255")]
    [InlineData("172.19.0.1", 24, "172.19.0.0", "172.19.0.255")]
    [InlineData("100.102.192.35", 32, "100.102.192.35", "100.102.192.35")]
    public void ComputesNetworkAndBroadcast(string address, int prefix, string network, string broadcast)
    {
        var subnet = new IPv4Subnet(IPAddress.Parse(address), prefix);

        Assert.Equal(IPAddress.Parse(network), subnet.Network);
        Assert.Equal(IPAddress.Parse(broadcast), subnet.Broadcast);
    }

    [Fact]
    public void ContainsOnlyAddressesInsideTheNetwork()
    {
        var subnet = new IPv4Subnet(IPAddress.Parse("192.168.1.156"), 24);

        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.191")));
        Assert.False(subnet.Contains(IPAddress.Parse("192.168.2.1")));
        Assert.False(subnet.Contains(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void PointToPointHasNoBroadcast()
    {
        Assert.True(new IPv4Subnet(IPAddress.Parse("100.64.0.1"), 32).IsPointToPoint);
        Assert.False(new IPv4Subnet(IPAddress.Parse("10.0.0.1"), 30).IsPointToPoint);
    }

    [Fact]
    public void PrefixFromMask()
    {
        Assert.Equal(24, IPv4Subnet.PrefixFromMask(IPAddress.Parse("255.255.255.0")));
        Assert.Equal(0, IPv4Subnet.PrefixFromMask(IPAddress.Parse("0.0.0.0")));
        Assert.Equal(32, IPv4Subnet.PrefixFromMask(IPAddress.Parse("255.255.255.255")));
    }
}
