using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Clipboard.Files;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard.Tests;

/// <summary>A real file pulled from one node to another over a paired TLS session on loopback.</summary>
public sealed class FileTransferIntegrationTests : IAsyncLifetime
{
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
    public async Task FileIsPulledOverTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("FileA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("FileB", discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var sessionOnA));
        Assert.True(b.Sessions.TryGetSession(a.Identity.Id, out var sessionOnB));

        // A offers a file; B pulls it.
        var content = new byte[1_500_000];
        Random.Shared.NextBytes(content);
        var file = Path.Combine(_directories[0], "payload.bin");
        await File.WriteAllBytesAsync(file, content, ct);
        var store = new LocalOfferStore();
        var manifest = store.Create([file], "FileA")!;
        using var server = new FileServer(store, NullLogger.Instance);
        sessionOnA.RegisterHandler(ChannelId.File, (session, payload, token) =>
        {
            var request = payload.ToArray();
            _ = Task.Run(() => server.HandleRequestAsync(session, request));
            return ValueTask.CompletedTask;
        });

        var fetcher = new FileFetcher(sessionOnB);
        sessionOnB.RegisterHandler(ChannelId.File, (_, payload, _) =>
        {
            fetcher.OnResponse(payload.Span);
            return ValueTask.CompletedTask;
        });

        await using var remote = new RemoteFileStream(fetcher, manifest.OfferId, 0, content.Length);
        using var received = new MemoryStream();
        await remote.CopyToAsync(received, ct);

        Assert.Equal(content, received.ToArray());

        var missing = await fetcher.ReadAsync(manifest.OfferId + 1, 0, 0, 10, ct);
        Assert.Equal(FileChunkStatus.NotFound, missing.Status);
    }

    private MirroringNode CreateNode(string name, int discoveryPort, string manualAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.ClipTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = FreeTcpPort(),
                ManualAddresses = [manualAddress],
                PinOverride = "112233",
            },
            new AcceptAll(),
            NullLoggerFactory.Instance);
        node.Settings.SelectedInterfaceIds.Add(NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback).Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
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

        public Task<string?> RequestPinAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
