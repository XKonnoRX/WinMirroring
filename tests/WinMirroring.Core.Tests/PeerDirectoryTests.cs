using System.Net;
using WinMirroring.Core.Peers;

namespace WinMirroring.Core.Tests;

/// <summary>
/// A device is shown as on the network because its beacons arrive. Nothing used to take that back when they
/// stopped: how long ago the last one came was only looked at when a session closed or a pairing was removed, so a
/// device with no session that was simply switched off went on being listed as on the network until the program
/// was restarted.
/// </summary>
public class PeerDirectoryTests
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(8);
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADeviceThatHasStoppedAnnouncingItselfIsNoLongerOnTheNetwork()
    {
        var directory = new PeerDirectory();
        var address = new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47621);
        directory.Update("silent", info =>
        {
            info.Name = "Silent";
            info.State = PeerState.Discovered;
            info.TransportEndPoint = address;
            info.LastSeen = Now - TimeSpan.FromSeconds(9);
        });
        directory.Update("talking", info =>
        {
            info.State = PeerState.Discovered;
            info.LastSeen = Now - TimeSpan.FromSeconds(3);
        });

        var said = new List<PeerInfo>();
        directory.Changed += said.Add;

        Assert.Equal(1, directory.ExpireUnseen(Now, Expiry));

        Assert.True(directory.TryGet("silent", out var silent));
        Assert.Equal(PeerState.Offline, silent.State);
        Assert.True(directory.TryGet("talking", out var talking));
        Assert.Equal(PeerState.Discovered, talking.State);

        // Whoever shows the list is told, and told about that device only.
        var told = Assert.Single(said);
        Assert.Equal(("silent", PeerState.Offline), (told.Id, told.State));

        // Off the network is all that is said of it. It keeps its place in the list and everything known about
        // it, so it can still be paired with by the address it was last at, and is simply back when it is heard.
        Assert.Equal("Silent", silent.Name);
        Assert.Equal(address, silent.TransportEndPoint);

        // And it is said once, not every time the list is looked over.
        Assert.Equal(0, directory.ExpireUnseen(Now + TimeSpan.FromSeconds(2), Expiry));
        Assert.Single(said);
    }

    /// <summary>A paired device belongs in the list whether it is switched on or not.</summary>
    [Fact]
    public void APairedDeviceThatGoesQuietStaysListedAndStaysPaired()
    {
        var directory = new PeerDirectory();
        directory.Update("paired", info =>
        {
            info.IsPaired = true;
            info.State = PeerState.Discovered;
            info.LastSeen = Now - TimeSpan.FromMinutes(5);
        });

        Assert.Equal(1, directory.ExpireUnseen(Now, Expiry));

        var listed = Assert.Single(directory.Snapshot());
        Assert.Equal(("paired", true, PeerState.Offline), (listed.Id, listed.IsPaired, listed.State));
    }

    /// <summary>
    /// Beacons say nothing about a device there is a session with, or one that is being connected to: those states
    /// are somebody else's to end. A device behind a VPN can be connected for hours without one beacon arriving.
    /// </summary>
    [Theory]
    [InlineData(PeerState.Connecting)]
    [InlineData(PeerState.Connected)]
    [InlineData(PeerState.Pairing)]
    [InlineData(PeerState.Ready)]
    [InlineData(PeerState.Offline)]
    public void OnlyADeviceListedAsAnnouncingItselfIsJudgedByItsBeacons(PeerState state)
    {
        var directory = new PeerDirectory();
        directory.Update("peer", info =>
        {
            info.State = state;
            info.LastSeen = Now - TimeSpan.FromHours(1);
        });

        var said = new List<PeerInfo>();
        directory.Changed += said.Add;

        Assert.Equal(0, directory.ExpireUnseen(Now, Expiry));
        Assert.True(directory.TryGet("peer", out var peer));
        Assert.Equal(state, peer.State);
        Assert.Empty(said);
    }

    /// <summary>
    /// "Discovered" is also where a failed attempt to connect leaves a device, whether or not a beacon was ever
    /// heard from it. With none heard, it is not on the network as far as anyone can tell.
    /// </summary>
    [Fact]
    public void ADeviceNeverHeardFromIsNotOnTheNetworkEither()
    {
        var directory = new PeerDirectory();
        directory.Update("unheard", info => info.State = PeerState.Discovered);

        Assert.Equal(1, directory.ExpireUnseen(Now, Expiry));
        Assert.True(directory.TryGet("unheard", out var unheard));
        Assert.Equal(PeerState.Offline, unheard.State);
    }
}
