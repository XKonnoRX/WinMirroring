using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;

namespace WinMirroring.Input.Tests;

/// <summary>Three real nodes on loopback in a chain A — B — C (A and C never connect): the layout still travels.</summary>
public sealed class LayoutRelayIntegrationTests : IAsyncLifetime
{
    private readonly List<MirroringNode> _nodes = [];
    private readonly List<InputSharingService> _inputs = [];
    private readonly List<string> _directories = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var input in _inputs)
        {
            await input.DisposeAsync();
        }

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
    public async Task ALayoutReachesADeviceItsAuthorIsNotConnectedTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (portA, portB, portC) = (FreeUdpPort(), FreeUdpPort(), FreeUdpPort());
        var a = CreateNode("RelayA", portA, $"127.0.0.1:{portB}");
        var b = CreateNode("RelayB", portB, $"127.0.0.1:{portA}", $"127.0.0.1:{portC}");
        var c = CreateNode("RelayC", portC, $"127.0.0.1:{portB}");
        foreach (var node in _nodes)
        {
            await node.StartAsync();
        }

        var inputA = CreateInput(a);
        CreateInput(b);
        var inputC = CreateInput(c);

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var p) && p.TransportEndPoint is not null && b.Peers.TryGet(c.Identity.Id, out var q) && q.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        Assert.True(await b.PairAsync(c.Identity.Id, ct));
        await WaitUntilAsync(() => IsReady(a, b) && IsReady(c, b), ct);
        Assert.False(a.Sessions.TryGetSession(c.Identity.Id, out var direct) && direct.State == SessionState.Ready, "A and C must not be connected");

        inputA.ApplyLayout(
        [
            new LayoutCell(a.Identity.Id, 0, 0),
            new LayoutCell(b.Identity.Id, 1, 0),
            new LayoutCell(c.Identity.Id, 2, 0),
        ]);

        await WaitUntilAsync(() => c.Settings.Input.LayoutVersion == a.Settings.Input.LayoutVersion, ct);
        Assert.Equal(a.Identity.Id, c.Settings.Input.LayoutOrigin);
        Assert.Equal(b.Identity.Id, inputC.Layout.Neighbor(c.Identity.Id, Layout.Edge.Left));
    }

    private static bool IsReady(MirroringNode from, MirroringNode to) =>
        from.Sessions.TryGetSession(to.Identity.Id, out var session) && session.State == SessionState.Ready;

    private InputSharingService CreateInput(MirroringNode node)
    {
        // No hooks in a test: the service only takes part in the layout sync.
        node.Settings.Input.Enabled = false;
        var input = new InputSharingService(node, NullLogger<InputSharingService>.Instance);
        input.Start();
        _inputs.Add(input);
        return input;
    }

    private MirroringNode CreateNode(string name, int discoveryPort, params string[] manualAddresses)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.RelayTests", Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = FreeTcpPort(),
                ManualAddresses = manualAddresses,
            },
            new AcceptAll(),
            NullLoggerFactory.Instance);
        node.Settings.SelectedInterfaceIds.Add(NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback).Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "condition not reached in time");
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

    private sealed class AcceptAll : IPairingUserInterface
    {
        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult(true);
    }
}
