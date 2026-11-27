using System.Net;
using System.Net.Sockets;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Tests;

/// <summary>
/// A connection is held to the interface its peer is on, so that a VPN coming up does not take it elsewhere. What
/// can be checked without a VPN is the choice — which interface, and when none — and that a socket held this way
/// still connects.
/// </summary>
public class InterfacePinTests
{
    private static readonly InterfacePin.Candidate Lan = new("Ethernet", 7, IPAddress.Parse("192.168.1.208"), 24);
    private static readonly InterfacePin.Candidate Tunnel = new("vpn-tun", 31, IPAddress.Parse("172.19.0.1"), 24);
    private static readonly InterfacePin.Candidate Wide = new("Corporate", 12, IPAddress.Parse("192.168.5.1"), 16);
    private static readonly InterfacePin.Candidate PointToPoint = new("Tailscale", 40, IPAddress.Parse("100.64.140.15"), 32);

    [Fact]
    public void APeerOnTheSameSubnetIsHeldToThatInterface()
    {
        var pick = InterfacePin.Choose([Tunnel, Lan, PointToPoint], IPAddress.Parse("192.168.1.191"));

        Assert.Equal(Lan, pick);
    }

    [Fact]
    public void TheNarrowerSubnetWinsWhenTwoContainThePeer()
    {
        Assert.Equal(Lan, InterfacePin.Choose([Wide, Lan], IPAddress.Parse("192.168.1.191")));
        Assert.Equal(Wide, InterfacePin.Choose([Wide, Lan], IPAddress.Parse("192.168.9.4")));
    }

    [Theory]
    [InlineData("8.8.8.8")] // through a router: which interface is the routing table's business
    [InlineData("100.64.140.20")] // the far end of a point-to-point tunnel
    [InlineData("127.0.0.1")]
    public void APeerOnNoneOfThisMachinesSubnetsIsNotHeldToAnything(string remote) =>
        Assert.Null(InterfacePin.Choose([Tunnel, Lan, PointToPoint], IPAddress.Parse(remote)));

    [Fact]
    public void AnIpv6PeerIsLeftAlone() =>
        Assert.Null(InterfacePin.Choose([Lan], IPAddress.Parse("fe80::1")));

    /// <summary>
    /// The option itself, on this machine's own network: a socket bound and held to the interface an address of
    /// this machine is on connects to a listener there. It does not show that a VPN is survived — only a VPN can —
    /// but it does show that holding a socket does not stop it working.
    /// </summary>
    [Fact]
    public async Task ASocketHeldToAnInterfaceStillConnects()
    {
        var own = InterfacePin.Local().FirstOrDefault(c => c.PrefixLength is >= 8 and <= 30 && !c.Address.ToString().StartsWith("169.254."));
        Assert.SkipWhen(own is null, "This machine has no network interface with a subnet.");

        using var listener = new TcpListener(own!.Address, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        var held = InterfacePin.TryPin(client.Client, own.Address, bind: true);
        Assert.NotNull(held);
        Assert.Contains(own.Name, held);

        var ct = TestContext.Current.CancellationToken;
        var accepting = listener.AcceptTcpClientAsync(ct).AsTask();
        await client.ConnectAsync(new IPEndPoint(own.Address, port), ct);
        using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(own.Address, ((IPEndPoint)client.Client.LocalEndPoint!).Address);
        await client.GetStream().WriteAsync(new byte[] { 42 }, ct);
        var read = new byte[1];
        Assert.Equal(1, await accepted.GetStream().ReadAsync(read, ct));
        Assert.Equal(42, read[0]);
    }

    [Fact]
    public void SwitchedOffNothingIsHeld()
    {
        var before = InterfacePin.Enabled;
        try
        {
            InterfacePin.Enabled = false;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Assert.Null(InterfacePin.TryPin(socket, IPAddress.Parse("192.168.1.191"), bind: true));
            Assert.Null(socket.LocalEndPoint);
        }
        finally
        {
            InterfacePin.Enabled = before;
        }
    }
}
