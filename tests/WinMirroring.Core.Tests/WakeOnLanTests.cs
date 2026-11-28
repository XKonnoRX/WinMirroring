using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinMirroring.Core.Network;

namespace WinMirroring.Core.Tests;

/// <summary>
/// Waking a device over the network is a packet nobody answers, sent to an address learnt earlier: if either is
/// wrong there is no error to see, only a device that stays asleep. So the parts that can be wrong quietly are
/// held here — what the packet is, whose neighbour a device counts as, and where the packet goes.
/// </summary>
public class WakeOnLanTests
{
    private static readonly byte[] Adapter = [0xD8, 0xBB, 0xC1, 0xCC, 0xD2, 0xBE];

    private static NetworkInterfaceInfo Nic(string address, int prefix, NetworkInterfaceType type = NetworkInterfaceType.Ethernet, bool vpn = false, bool loopback = false) =>
        new("{id-" + address + "}", "nic " + address, "test adapter", IPAddress.Parse(address), new IPv4Subnet(IPAddress.Parse(address), prefix), type, loopback, vpn);

    [Fact]
    public void ThePacketIsSixOnesAndThenTheAddressSixteenTimes()
    {
        var packet = WakeOnLan.MagicPacket(Adapter);

        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(Adapter, packet[(6 + (i * 6))..(12 + (i * 6))]);
        }
    }

    [Fact]
    public void AnAddressThatIsNotSixBytesMakesNoPacket() =>
        Assert.Throws<ArgumentException>(() => WakeOnLan.MagicPacket(new byte[] { 1, 2, 3 }));

    [Theory]
    [InlineData("D8-BB-C1-CC-D2-BE")]
    [InlineData("d8:bb:c1:cc:d2:be")]
    [InlineData("D8BBC1CCD2BE")]
    [InlineData("  D8-BB-C1-CC-D2-BE ")]
    public void AnAddressIsReadHoweverItIsWritten(string text)
    {
        Assert.True(WakeOnLan.TryParse(text, out var address));
        Assert.Equal(Adapter, address);
        Assert.Equal("D8-BB-C1-CC-D2-BE", WakeOnLan.Format(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D8-BB-C1-CC-D2")]
    [InlineData("D8-BB-C1-CC-D2-BE-00")]
    [InlineData("D8-BB-C1-CC-D2-ZZ")]
    [InlineData("00-00-00-00-00-00")] // what a neighbour table holds for a device that never answered
    [InlineData("FF-FF-FF-FF-FF-FF")] // everybody: not a device
    public void WhatIsNotOneDevicesAddressIsNotRead(string? text) => Assert.False(WakeOnLan.TryParse(text, out _));

    [Fact]
    public void ADeviceIsANeighbourOnlyOnALocalNetworkItsAddressIsIn()
    {
        var lan = Nic("192.168.1.208", 24);
        var wifi = Nic("10.0.0.7", 24, NetworkInterfaceType.Wireless80211);
        var vpn = Nic("172.19.0.1", 24, vpn: true);
        var tailscale = Nic("100.64.140.15", 32, NetworkInterfaceType.Unknown);
        var loopback = Nic("127.0.0.1", 8, NetworkInterfaceType.Loopback, loopback: true);
        var all = new[] { vpn, tailscale, loopback, lan, wifi };

        Assert.Same(lan, WakeOnLan.NeighbourhoodOf(IPAddress.Parse("192.168.1.191"), all));
        Assert.Same(wifi, WakeOnLan.NeighbourhoodOf(IPAddress.Parse("10.0.0.20"), all));

        // Reached through a VPN, through Tailscale, on this machine itself, or not in any of its networks: there is
        // no hardware address to learn of such a device, and a broadcast would not reach it.
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("172.19.0.9"), all));
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("100.119.58.45"), all));
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("127.0.0.1"), all));
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("8.8.8.8"), all));
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("192.168.1.208"), all)); // this machine's own address
        Assert.Null(WakeOnLan.NeighbourhoodOf(IPAddress.Parse("fe80::1"), all));
    }

    [Fact]
    public void ThePacketGoesToEverybodyOnEveryLocalNetworkAndToWhereTheDeviceLastWas()
    {
        var lan = Nic("192.168.1.208", 24);
        var virtualSwitch = Nic("172.18.16.1", 20);
        var vpn = Nic("172.19.0.1", 24, vpn: true);

        var shots = WakeOnLan.Shots(
            [lan, virtualSwitch, vpn],
            rememberedBroadcast: "192.168.1.255",
            lastKnownAddresses: ["100.119.58.45", "192.168.1.191", "not an address"],
            ports: [9]);

        WakeOnLan.Shot To(string from, string to) => new(IPAddress.Parse(from), new IPEndPoint(IPAddress.Parse(to), 9));
        Assert.Equal(
            [
                To("192.168.1.208", "192.168.1.255"),
                To("192.168.1.208", "255.255.255.255"),
                To("172.18.16.1", "172.18.31.255"),
                To("172.18.16.1", "255.255.255.255"),
                To("192.168.1.208", "192.168.1.191"), // its last address here: an adapter that answers for it asleep gets it directly
            ],
            shots);
    }

    [Fact]
    public void ANetworkTheDeviceWasOnButThisMachineIsNotStillGetsThePacket()
    {
        var shots = WakeOnLan.Shots([Nic("10.0.0.7", 24)], rememberedBroadcast: "192.168.1.255", lastKnownAddresses: [], ports: [9]);

        Assert.Contains(new WakeOnLan.Shot(null, new IPEndPoint(IPAddress.Parse("192.168.1.255"), 9)), shots);
    }

    [Fact]
    public void WithNoLocalNetworkAndNothingRememberedThereIsNowhereToSend() =>
        Assert.Empty(WakeOnLan.Shots([Nic("172.19.0.1", 24, vpn: true)], rememberedBroadcast: string.Empty, lastKnownAddresses: ["172.19.0.9"]));

    [Fact]
    public async Task WhatIsSentIsThePacketEachTime()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = (IPEndPoint)listener.Client.LocalEndPoint!;
        var packet = WakeOnLan.MagicPacket(Adapter);

        var went = await WakeOnLan.SendAsync(packet, [new WakeOnLan.Shot(IPAddress.Loopback, to)], times: 2, TestContext.Current.CancellationToken);

        Assert.Equal(1, went);
        for (var i = 0; i < 2; i++)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            var received = await listener.ReceiveAsync(wait.Token);
            Assert.Equal(packet, received.Buffer);
        }
    }

    [Fact]
    public async Task AnAdapterThatHasGoneDoesNotStopTheOthers()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = (IPEndPoint)listener.Client.LocalEndPoint!;

        // 192.0.2.1 is an address kept for documentation: no adapter here has it, so binding to it fails.
        var went = await WakeOnLan.SendAsync(
            WakeOnLan.MagicPacket(Adapter),
            [new WakeOnLan.Shot(IPAddress.Parse("192.0.2.1"), to), new WakeOnLan.Shot(IPAddress.Loopback, to)],
            times: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, went);
    }

    /// <summary>
    /// By hand, on a real network: set WM_WAKE_PROBE to the address of a device that is up on this machine's local
    /// network, and this says what hardware address would be remembered for it.
    /// </summary>
    [Fact(Explicit = true)]
    public void WhatADeviceOnThisNetworkAnswersWith()
    {
        var text = Environment.GetEnvironmentVariable("WM_WAKE_PROBE");
        Assert.SkipWhen(string.IsNullOrEmpty(text), "Set WM_WAKE_PROBE to an address on the local network.");
        var remote = IPAddress.Parse(text!);

        var nic = WakeOnLan.NeighbourhoodOf(remote, NetworkInterfaceCatalog.Enumerate());
        Assert.NotNull(nic);
        var address = WakeOnLan.HardwareAddressOf(remote, nic.Address);
        Assert.NotNull(address);
        TestContext.Current.TestOutputHelper?.WriteLine($"{remote} is a neighbour on {nic} and answers as {WakeOnLan.Format(address)}; everybody there is {nic.Subnet.Broadcast}");
    }
}
