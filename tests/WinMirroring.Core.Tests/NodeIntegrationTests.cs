using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;

namespace WinMirroring.Core.Tests;

/// <summary>Two real nodes on loopback: discovery by probe, PIN pairing, pinned reconnect.</summary>
public sealed class NodeIntegrationTests : IAsyncLifetime
{
    private readonly Dictionary<MirroringNode, FixedAnswerPairingUi> _ui = [];

    private readonly List<MirroringNode> _nodes = [];
    private readonly List<string> _directories = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            var thumbprint = node.Settings.CertificateThumbprint;
            await node.DisposeAsync();
            DeviceIdentityStore.RemoveFromStore(thumbprint);
        }

        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task TwoNodesDiscoverPairAndReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("TestA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("TestB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"]);

        await a.StartAsync();
        await b.StartAsync();

        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        var paired = await a.PairAsync(b.Identity.Id, ct);
        Assert.True(paired, "pairing failed");

        // Nothing typed anywhere; both screens showed the same confirmation code.
        Assert.NotNull(_ui[a].ShownCode);
        Assert.Equal(_ui[a].ShownCode, _ui[b].ShownCode);

        await WaitUntilAsync(() => IsReady(a, b), TimeSpan.FromSeconds(10), "A has no ready session to B", ct);
        await WaitUntilAsync(() => IsReady(b, a), TimeSpan.FromSeconds(10), "B has no ready session to A", ct);

        var pairedRecordOnA = Assert.Single(a.Settings.PairedPeers);
        Assert.Equal(b.Identity.Id, pairedRecordOnA.Id);
        Assert.Equal(b.Identity.Fingerprint, pairedRecordOnA.Fingerprint);
        var pairedRecordOnB = Assert.Single(b.Settings.PairedPeers);
        Assert.Equal(a.Identity.Fingerprint, pairedRecordOnB.Fingerprint);

        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var live));
        var rtt = await live.PingAsync(TimeSpan.FromSeconds(2), ct);
        Assert.NotNull(rtt);
        Assert.InRange(rtt.Value.TotalMilliseconds, 0, 1000);
        Assert.True(Environment.TickCount64 - live.LastReceivedTicks < 2000);

        // Both sides are paired now, so a dropped link must come back on its own through discovery.
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var first));
        await first.CloseAsync("test");

        await WaitUntilAsync(
            () => a.Sessions.TryGetSession(b.Identity.Id, out var again) && again.State == SessionState.Ready && !ReferenceEquals(again, first),
            TimeSpan.FromSeconds(30), "A did not reconnect to B", ct);
        await WaitUntilAsync(() => IsReady(b, a), TimeSpan.FromSeconds(10), "B did not get the new session", ct);
    }

    [Fact]
    public async Task RejectedPairingIsNotStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("RejectA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("RejectB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}"], accept: false);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        var paired = await a.PairAsync(b.Identity.Id, ct);

        Assert.False(paired);
        Assert.Empty(a.Settings.PairedPeers);
        Assert.Empty(b.Settings.PairedPeers);
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var session));
        Assert.Equal(SessionState.Unpaired, session.State);
    }

    /// <summary>
    /// The app disposes its services in order and the service container disposes them again afterwards; a second
    /// dispose that throws used to abort the exit before the app shut down, leaving it running with no windows.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        var node = CreateNode("Twice", FreeUdpPort(), FreeTcpPort(), ["127.0.0.1:1"]);
        await node.StartAsync();

        await node.DisposeAsync();
        await node.DisposeAsync();
    }

    /// <summary>
    /// Three devices in a chain: A and C each know only B. Pairing is confirmed twice, A with B and C with B, and
    /// after that A and C recognise each other from one press — no third confirmation, and no code on anyone's
    /// screen — and reach each other directly, with B out of the way.
    /// </summary>
    [Fact]
    public async Task ADeviceTrustedByBothEndsIntroducesThemToEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var discoveryC = FreeUdpPort();

        // A and C are told about B and nothing else: anything they learn of each other comes through B.
        var a = CreateNode("ChainA", discoveryA, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);
        var b = CreateNode("ChainB", discoveryB, FreeTcpPort(), [$"127.0.0.1:{discoveryA}", $"127.0.0.1:{discoveryC}"]);
        var c = CreateNode("ChainC", discoveryC, FreeTcpPort(), [$"127.0.0.1:{discoveryB}"]);

        await a.StartAsync();
        await b.StartAsync();
        await c.StartAsync();

        await WaitUntilAsync(() => a.Peers.TryGet(b.Identity.Id, out var p) && p.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct), "A could not pair with B");

        await WaitUntilAsync(() => c.Peers.TryGet(b.Identity.Id, out var p) && p.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "C never discovered B", ct);
        Assert.True(await c.PairAsync(b.Identity.Id, ct), "C could not pair with B");

        await WaitUntilAsync(() => IsReady(a, b) && IsReady(b, a) && IsReady(c, b) && IsReady(b, c),
            TimeSpan.FromSeconds(20), "the chain never came up", ct);

        // B tells each end who else it is paired with.
        await WaitUntilAsync(() => a.Introductions.TryGet(c.Identity.Id, out _),
            TimeSpan.FromSeconds(20), "A was never told about C", ct);

        var known = Assert.Single(a.Introductions.Snapshot());
        Assert.Equal(
            (c.Identity.Id, c.Identity.Fingerprint, b.Settings.DeviceName),
            (known.Id, known.Fingerprint, known.ViaName));
        Assert.DoesNotContain(a.Settings.PairedPeers, p => p.Id == c.Identity.Id);

        // One press on A, and nothing is confirmed anywhere.
        Assert.True(await a.Introductions.TrustAsync(c.Identity.Id), "trusting the introduction failed");
        Assert.Empty(a.Introductions.Snapshot());
        Assert.Equal(c.Identity.Fingerprint, a.Settings.PairedPeers.Single(p => p.Id == c.Identity.Id).Fingerprint);

        // That press travels back through B, so C trusts A without being asked either.
        await WaitUntilAsync(() => c.Settings.PairedPeers.Any(p => p.Id == a.Identity.Id),
            TimeSpan.FromSeconds(20), "C was never told that A had accepted", ct);
        Assert.Equal(a.Identity.Fingerprint, c.Settings.PairedPeers.Single(p => p.Id == a.Identity.Id).Fingerprint);

        // And now they talk to each other, not through B.
        await WaitUntilAsync(() => IsReady(a, c) && IsReady(c, a),
            TimeSpan.FromSeconds(40), "A and C never connected to each other", ct);
    }

    /// <summary>
    /// The rule that keeps a chain from being a way in: the device in the middle only ever carries a device's own
    /// record, proved on that very connection, and only to a device it is itself paired with.
    /// </summary>
    [Fact]
    public void ADeviceMayOnlyEverPassOnItsOwnRecord()
    {
        var mine = new VouchedPeer("sender", "Sender", "AA:BB", 1, 2);
        var someoneElse = new VouchedPeer("stranger", "Stranger", "CC:DD", 1, 2);

        Assert.True(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", true, true));

        // Passing on a record that is not the sender's own: this is the one that must never go through.
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", someoneElse), "sender", "AA:BB", true, true));

        // Its own id with someone else's fingerprint is the same attempt wearing a hat.
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine with { Fingerprint = "CC:DD" }), "sender", "AA:BB", true, true));

        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", senderIsPaired: false, targetIsPaired: true));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("far", mine), "sender", "AA:BB", senderIsPaired: true, targetIsPaired: false));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("", mine), "sender", "AA:BB", true, true));
        Assert.False(PeerIntroductions.MayPassOn(new VouchRelayMessage("sender", mine), "sender", "AA:BB", true, true));
    }

    private static bool IsReady(MirroringNode from, MirroringNode to) =>
        from.Sessions.TryGetSession(to.Identity.Id, out var session) && session.State == SessionState.Ready;

    private MirroringNode CreateNode(string name, int discoveryPort, int transportPort, string[] manualAddresses, bool accept = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.Tests", Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        var ui = new FixedAnswerPairingUi(accept);

        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = transportPort,
                ManualAddresses = manualAddresses,
            },
            ui,
            NullLoggerFactory.Instance);
        _ui[node] = ui;

        var loopback = NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback);
        node.Settings.SelectedInterfaceIds.Add(loopback.Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(100, ct);
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>Plays the user on the device being asked; records the code each side was shown.</summary>
    private sealed class FixedAnswerPairingUi(bool accept) : IPairingUserInterface
    {
        public string? ShownCode { get; private set; }

        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
        {
            ShownCode = prompt.ConfirmationCode;
            return Task.FromResult(accept);
        }

        public void PairingStarted(PairingPrompt prompt) => ShownCode = prompt.ConfirmationCode;
    }
}
