using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;

namespace WinMirroring.Clipboard.Tests;

/// <summary>Real sessions and the real clipboard (text is restored afterwards); runs only on request.</summary>
public sealed class ClipboardSyncLiveTests : IAsyncLifetime
{
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "winmirroring-sync-diag.txt");
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

    [Fact(Explicit = true)]
    public async Task TextSentByAPeerLandsOnTheClipboard()
    {
        File.WriteAllText(TracePath, string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ClipA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("ClipB", discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();

        await using var syncB = new ClipboardSyncService(b, NullLogger<ClipboardSyncService>.Instance);
        syncB.Start();
        Trace($"B sync started, drag engine {(syncB.Drag is null ? "missing" : "ready")}");

        await WaitUntilAsync(() => a.Peers.TryGet(b.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await a.PairAsync(b.Identity.Id, ct));
        await WaitUntilAsync(() => b.Sessions.TryGetSession(a.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var sessionAtoB));
        Trace("paired");

        using var observer = new ClipboardThread(NullLogger.Instance, () => 1 << 20);
        var marker = $"WinMirroring sync test {Guid.NewGuid():N}";
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.LocalChanged += s =>
        {
            var text = s.Items.FirstOrDefault(i => i.Kind == ClipboardItemKind.UnicodeText);
            if (text is not null)
            {
                var value = Encoding.Unicode.GetString(text.Data).TrimEnd('\0');
                if (value == marker)
                {
                    seen.TrySetResult(value);
                }
            }
        };
        observer.Start();
        var original = await observer.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);

        try
        {
            var message = ClipboardCodec.EncodeData([new ClipboardItem(ClipboardItemKind.UnicodeText, Encoding.Unicode.GetBytes(marker + "\0"))]);
            await sessionAtoB.SendAsync(ChannelId.Clipboard, message, ct);
            Trace("sent from A");

            var landed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Trace($"clipboard now: {landed}; B activity: {syncB.LastActivity}");
            Assert.Equal(marker, landed);
        }
        finally
        {
            Trace($"B activity at end: '{syncB.LastActivity}'");
            if (original is { Items.Count: > 0 })
            {
                await observer.WriteItemsAsync(original.Items).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static void Trace(string line) => File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");

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
    }
}
