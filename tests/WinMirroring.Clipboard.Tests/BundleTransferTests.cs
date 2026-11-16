using System.Net;
using System.Net.Sockets;
using System.Text;
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

/// <summary>
/// Many files in one stream (bundles) between two nodes over a paired TLS session on loopback: what arrives is
/// exactly what was there, text is compressed on the way, Explorer's one-file-at-a-time reading is served ahead,
/// and a device that does not send bundles is still read the old way.
/// </summary>
public sealed class BundleTransferTests : IAsyncLifetime
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
    public async Task AFolderOfManySmallFilesAndALargeOneArrivesExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (fetcher, store) = await ConnectAsync(ct);
        var source = MakeProject(_directories[0], smallFiles: 600, largeBytes: 5_000_000);
        var manifest = store.Create([source], "BundleA")!;
        var target = Path.Combine(_directories[1], "received");

        var reports = new List<(long Done, long Total)>();
        await OfferDownloader.DownloadAsync(manifest, fetcher, (done, total) => reports.Add((done, total)), ct, target);

        AssertSameTree(source, Path.Combine(target, Path.GetFileName(source)));
        Assert.Equal((manifest.TotalSize, manifest.TotalSize), reports[^1]);
    }

    [Fact]
    public async Task TextIsCompressedOnTheWayAndAlreadyCompressedDataIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (fetcher, store) = await ConnectAsync(ct);
        var folder = Path.Combine(_directories[0], "text");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 200; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, $"f{i}.cs"), SourceText(i, 20_000), ct);
        }

        var random = new byte[3_000_000];
        Random.Shared.NextBytes(random);
        var media = Path.Combine(_directories[0], "clip.mp4");
        await File.WriteAllBytesAsync(media, random, ct);

        var text = store.Create([folder], "BundleA")!;
        await using (var bundle = (await fetcher.OpenBundleAsync(text.OfferId, 0, text.Entries.Count, ct))!)
        {
            while (await bundle.ReadAsync(ct) is not null)
            {
            }

            Assert.True(bundle.WireBytes * 4 < bundle.RawBytes, $"text sent as {bundle.WireBytes} of {bundle.RawBytes}");
        }

        var video = store.Create([media], "BundleA")!;
        await using (var bundle = (await fetcher.OpenBundleAsync(video.OfferId, 0, 1, ct))!)
        {
            while (await bundle.ReadAsync(ct) is not null)
            {
            }

            // Sent as it is: no bigger than the data plus the block and record headers.
            Assert.InRange(bundle.WireBytes, bundle.RawBytes, bundle.RawBytes + 64);
        }
    }

    [Fact]
    public async Task ExplorerReadingFileByFileIsServedFromOneBundleEvenWhenItSkipsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (fetcher, store) = await ConnectAsync(ct);
        var source = MakeProject(_directories[0], smallFiles: 120, largeBytes: 3_000_000);
        var manifest = store.Create([source], "BundleA")!;

        using var prefetcher = new OfferPrefetcher(manifest, fetcher, NullLogger.Instance);
        var files = manifest.Entries.Select((e, i) => (Entry: e, Index: i)).Where(x => !x.Entry.IsDirectory).ToList();
        var skipped = files[5].Index;
        foreach (var (entry, index) in files)
        {
            if (index == skipped)
            {
                continue; // Explorer told to skip this one
            }

            var stream = prefetcher.Open(index);
            Assert.NotNull(stream);
            using var copy = new MemoryStream();
            await Task.Run(() => stream.CopyTo(copy), ct);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(source)!, entry.Path), ct), copy.ToArray());
        }

        // Pasted a second time once the first paste is done: a new bundle serves it all again.
        var again = prefetcher.Open(files[0].Index);
        Assert.NotNull(again);
        using var second = new MemoryStream();
        await Task.Run(() => again.CopyTo(second), ct);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(source)!, files[0].Entry.Path), ct), second.ToArray());
    }

    [Fact]
    public async Task ADeviceThatDoesNotSendBundlesIsReadChunkByChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var (fetcher, store) = await ConnectAsync(ct);
        var source = MakeProject(_directories[0], smallFiles: 20, largeBytes: 700_000);
        var manifest = store.Create([source], "BundleA")!;
        var target = Path.Combine(_directories[1], "old");

        await OfferDownloader.DownloadAsync(manifest, new ChunksOnly(fetcher), null, ct, target);

        AssertSameTree(source, Path.Combine(target, Path.GetFileName(source)));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>A source that only answers chunk requests, as a device from before bundles does.</summary>
    private sealed class ChunksOnly(IRemoteFileSource inner) : IRemoteFileSource
    {
        public Task<FileChunk> ReadAsync(long offerId, int index, long offset, int length, CancellationToken ct) =>
            inner.ReadAsync(offerId, index, offset, length, ct);
    }

    private static string MakeProject(string root, int smallFiles, int largeBytes)
    {
        var project = Path.Combine(root, "project");
        for (var i = 0; i < smallFiles; i++)
        {
            var folder = Path.Combine(project, $"dir{i % 7}", $"sub{i % 3}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"file{i}.txt"), SourceText(i, 100 + (i * 37 % 4000)));
        }

        Directory.CreateDirectory(Path.Combine(project, "empty-folder"));
        File.WriteAllBytes(Path.Combine(project, "empty.bin"), []);
        var large = new byte[largeBytes];
        Random.Shared.NextBytes(large);
        File.WriteAllBytes(Path.Combine(project, "large.bin"), large);
        return project;
    }

    private static string SourceText(int seed, int length)
    {
        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append("public sealed class Item").Append(seed).Append(" { public int Value { get; set; } = ").Append(text.Length).AppendLine("; }");
        }

        return text.ToString(0, length);
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var want = Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(expected, p)).Order().ToList();
        var got = Directory.EnumerateFileSystemEntries(actual, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(actual, p)).Order().ToList();
        Assert.Equal(want, got);
        foreach (var relative in want.Where(r => File.Exists(Path.Combine(expected, r))))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, relative)), File.ReadAllBytes(Path.Combine(actual, relative)));
        }
    }

    private async Task<(FileFetcher Fetcher, LocalOfferStore Store)> ConnectAsync(CancellationToken ct)
    {
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("BundleA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("BundleB", discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var sessionOnA));
        Assert.True(b.Sessions.TryGetSession(a.Identity.Id, out var sessionOnB));

        // A has the files; B takes them. The same routing the clipboard service does.
        var store = new LocalOfferStore();
        var server = new FileServer(store, NullLogger.Instance);
        sessionOnA.RegisterHandler(ChannelId.File, (session, payload, token) =>
        {
            switch (payload.Span[0])
            {
                case FileCodec.RequestMessage:
                    var request = payload.ToArray();
                    _ = Task.Run(() => server.HandleRequestAsync(session, request));
                    break;
                case BundleCodec.RequestMessage or BundleCodec.AckMessage or BundleCodec.CancelMessage:
                    server.HandleBundleMessage(session, payload.Span);
                    break;
            }

            return ValueTask.CompletedTask;
        });

        var fetcher = new FileFetcher(sessionOnB);
        sessionOnB.RegisterHandler(ChannelId.File, (_, payload, _) =>
        {
            if (payload.Span[0] == FileCodec.ResponseMessage)
            {
                fetcher.OnResponse(payload.Span);
            }
            else
            {
                fetcher.OnBundleMessage(payload);
            }

            return ValueTask.CompletedTask;
        });

        return (fetcher, store);
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
