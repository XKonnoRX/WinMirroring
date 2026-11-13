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

namespace WinMirroring.Clipboard.Tests;

/// <summary>Files sent to one chosen device (not through the clipboard) over a paired session on loopback.</summary>
public sealed class SendFilesIntegrationTests : IAsyncLifetime
{
    private readonly List<MirroringNode> _nodes = [];
    private readonly List<ClipboardSyncService> _services = [];
    private readonly List<string> _directories = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
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
    public async Task FilesTheReceiverTakesArriveWholeAndTheSenderIsTold()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, syncA, syncB) = await ConnectAsync(ct);

        var content = new byte[2_000_000];
        Random.Shared.NextBytes(content);
        var file = Path.Combine(_directories[0], "build.zip");
        await File.WriteAllBytesAsync(file, content, ct);
        var target = Path.Combine(_directories[1], "taken");

        var progress = new List<FileTransferProgress>();
        syncB.TransferProgress += p => progress.Add(p);
        SendFilesRequest? seen = null;
        syncB.FilesOffered += (session, request) =>
        {
            seen = request;
            _ = syncB.AcceptFilesAsync(session, request, target, ct);
        };

        var result = await syncA.SendFilesAsync(b.Identity.Id, [file], SendFilesPurpose.Update, "1.0.13", ct);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(seen);
        Assert.Equal(SendFilesPurpose.Update, seen.Purpose);
        Assert.Equal("1.0.13", seen.Note);
        Assert.Equal("FilesA", seen.Manifest.SourceName);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(target, "build.zip"), ct));

        // Progress is reported and ends as finished and successful.
        Assert.Contains(progress, p => p is { Finished: true, Ok: true });
        Assert.All(progress, p => Assert.Equal(content.Length, p.Total));
    }

    [Fact]
    public async Task ARefusedOfferTransfersNothingAndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, b, syncA, syncB) = await ConnectAsync(ct);

        var file = Path.Combine(_directories[0], "note.txt");
        await File.WriteAllTextAsync(file, "hello", ct);
        syncB.FilesOffered += (session, request) => syncB.DeclineFiles(session, request, "пользователь отказался");

        var result = await syncA.SendFilesAsync(b.Identity.Id, [file], SendFilesPurpose.Open, "note.txt", ct);

        Assert.False(result.Ok);
        Assert.Equal("пользователь отказался", result.Error);
    }

    [Fact]
    public async Task ADeviceThatCannotTakeFilesAnswersInsteadOfLeavingTheSenderWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, b, syncA, _) = await ConnectAsync(ct);

        // syncB has no FilesOffered handler: an older or headless build.
        var file = Path.Combine(_directories[0], "note.txt");
        await File.WriteAllTextAsync(file, "hello", ct);

        var result = await syncA.SendFilesAsync(b.Identity.Id, [file], SendFilesPurpose.Open, null, ct);

        Assert.False(result.Ok);
        Assert.Equal("это устройство не принимает файлы напрямую", result.Error);
    }

    [Fact]
    public async Task SendingToADeviceThatIsNotConnectedFailsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, syncA, _) = await ConnectAsync(ct);
        var file = Path.Combine(_directories[0], "note.txt");
        await File.WriteAllTextAsync(file, "hello", ct);

        var result = await syncA.SendFilesAsync("nobody", [file], SendFilesPurpose.Open, null, ct);

        Assert.False(result.Ok);
        Assert.Equal("устройство не на связи", result.Error);
    }

    [Fact]
    public void APinnedOfferSurvivesLaterCopiesUntilItIsReleased()
    {
        var store = new LocalOfferStore();
        var file = Path.Combine(_directories.Count > 0 ? _directories[0] : Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "x");
        try
        {
            var pinned = store.Create([file], "A", pinned: true)!;
            for (var i = 0; i < 12; i++)
            {
                store.Create([file], "A");
            }

            Assert.True(store.TryGetFile(pinned.OfferId, 0, out _));

            store.Release(pinned.OfferId);
            for (var i = 0; i < 12; i++)
            {
                store.Create([file], "A");
            }

            Assert.False(store.TryGetFile(pinned.OfferId, 0, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheMessagesRoundTripAndNonsenseIsRefused()
    {
        var manifest = new FileOfferManifest(7, "PC", [new FileOfferEntry("build.zip", 10, false, 0)]);
        var request = new SendFilesRequest(3, SendFilesPurpose.Update, "1.0.13", manifest);

        // Compared field by field: a record holding a List is equal only by reference.
        var decoded = ClipboardCodec.TryDecodeSendFiles(ClipboardCodec.EncodeSendFiles(request));
        Assert.NotNull(decoded);
        Assert.Equal((request.RequestId, request.Purpose, request.Note), (decoded.RequestId, decoded.Purpose, decoded.Note));
        Assert.Equal((manifest.OfferId, manifest.SourceName), (decoded.Manifest.OfferId, decoded.Manifest.SourceName));
        Assert.Equal(manifest.Entries, decoded.Manifest.Entries);

        var result = new SendFilesResult(3, false, "нет места");
        Assert.Equal(result, ClipboardCodec.TryDecodeSendFilesResult(ClipboardCodec.EncodeSendFilesResult(result)));

        // A path that would escape the target folder, and a message of the wrong type.
        var escaping = new SendFilesRequest(1, SendFilesPurpose.Open, null, new FileOfferManifest(1, "PC", [new FileOfferEntry(@"..\..\evil.exe", 1, false, 0)]));
        Assert.Null(ClipboardCodec.TryDecodeSendFiles(ClipboardCodec.EncodeSendFiles(escaping)));
        Assert.Null(ClipboardCodec.TryDecodeSendFiles(ClipboardCodec.EncodeOffer(manifest)));
        Assert.Null(ClipboardCodec.TryDecodeSendFilesResult([ClipboardCodec.SendFilesResultMessage]));
    }

    private async Task<(MirroringNode A, MirroringNode B, ClipboardSyncService SyncA, ClipboardSyncService SyncB)> ConnectAsync(CancellationToken ct)
    {
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("FilesA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("FilesB", discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();

        var syncA = CreateSync(a);
        var syncB = CreateSync(b);

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(
            () => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready
                && b.Sessions.TryGetSession(a.Identity.Id, out var t) && t.State == SessionState.Ready,
            ct);
        return (a, b, syncA, syncB);
    }

    private ClipboardSyncService CreateSync(MirroringNode node)
    {
        // Only the sessions and the file channels are needed here, not the clipboard thread or the drag surface.
        var service = new ClipboardSyncService(node, NullLogger<ClipboardSyncService>.Instance);
        service.Start();
        _services.Add(service);
        return service;
    }

    private MirroringNode CreateNode(string name, int discoveryPort, string manualAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.SendFilesTests", Guid.NewGuid().ToString("N"));
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
            new AcceptAllPairing(),
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

    private sealed class AcceptAllPairing : IPairingUserInterface
    {
        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult(true);
    }
}
