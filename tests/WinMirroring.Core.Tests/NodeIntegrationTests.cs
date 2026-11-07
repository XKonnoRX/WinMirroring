using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;

namespace WinMirroring.Core.Tests;

/// <summary>Two real nodes on loopback: discovery by probe, PIN pairing, pinned reconnect.</summary>
public sealed class NodeIntegrationTests : IAsyncLifetime
{
    private const string Pin = "246810";

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
        var a = CreateNode("TestA", discoveryA, FreeTcpPort(), $"127.0.0.1:{discoveryB}");
        var b = CreateNode("TestB", discoveryB, FreeTcpPort(), $"127.0.0.1:{discoveryA}");

        await a.StartAsync();
        await b.StartAsync();

        await WaitUntilAsync(
            () => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null,
            TimeSpan.FromSeconds(15), "A never discovered B", ct);

        var paired = await a.PairAsync(b.Identity.Id, ct);
        Assert.True(paired, "pairing failed");

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
    public async Task WrongPinIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("WrongA", discoveryA, FreeTcpPort(), $"127.0.0.1:{discoveryB}", pin: "111111");
        var b = CreateNode("WrongB", discoveryB, FreeTcpPort(), $"127.0.0.1:{discoveryA}", pin: "222222");

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

    private static bool IsReady(MirroringNode from, MirroringNode to) =>
        from.Sessions.TryGetSession(to.Identity.Id, out var session) && session.State == SessionState.Ready;

    private MirroringNode CreateNode(string name, int discoveryPort, int transportPort, string manualAddress, string pin = Pin)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.Tests", Guid.NewGuid().ToString("N"));
        _directories.Add(directory);

        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = transportPort,
                ManualAddresses = [manualAddress],
                PinOverride = pin,
            },
            new AutoAcceptPairingUi(),
            NullLoggerFactory.Instance);

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

    private sealed class AutoAcceptPairingUi : IPairingUserInterface
    {
        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult(true);

        public Task<string?> RequestPinAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
