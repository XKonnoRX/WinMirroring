using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>Real capture of this machine's primary monitor streamed between two nodes on loopback. Needs a display.</summary>
public sealed class ScreenStreamIntegrationTests : IAsyncLifetime
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

    [Fact(Explicit = true)]
    public Task ViewerReceivesTheMonitor() => StreamThePrimaryMonitorAsync(Core.Settings.VideoCodec.Jpeg, "jpeg");

    /// <summary>The same over hardware H.264: capture, encode, the session, decode, and the picture compared.</summary>
    [Fact(Explicit = true)]
    public Task ViewerReceivesTheMonitorAsVideo()
    {
        if (!VideoCodecSupport.CanEncodeH264 || !VideoCodecSupport.CanDecodeH264)
        {
            Assert.Skip("No hardware H.264 encoder (or no decoder) on this machine.");
        }

        return StreamThePrimaryMonitorAsync(Core.Settings.VideoCodec.Auto, "h264");
    }

    /// <summary>
    /// The picture is encoded at the size the viewer says it is showing, not the size of the screen. A view at
    /// half the size costs the far device a quarter of the decoding and a quarter of the colour conversion, which
    /// on a tablet are the two most expensive things it does per frame. Needs a hardware encoder and a graphics
    /// card that will do the conversion — the processor path cannot scale.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ThePictureIsSentAtTheSizeTheViewerSaysItShowsIt()
    {
        if (!VideoCodecSupport.CanEncodeH264 || !VideoCodecSupport.CanDecodeH264)
        {
            Assert.Skip("No hardware H.264 encoder (or no decoder) on this machine.");
        }

        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("SizeA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("SizeB", discoveryB, $"127.0.0.1:{discoveryA}");
        a.Settings.Screen.VideoCodec = Core.Settings.VideoCodec.Auto;
        await a.StartAsync();
        await b.StartAsync();

        var log = Path.Combine(Path.GetTempPath(), "winmirroring-stream-size.log");
        File.WriteAllText(log, string.Empty);
        await using var screenA = new ScreenStreamingService(a, new FileLogger<ScreenStreamingService>(log));
        await using var screenB = new ScreenStreamingService(b, new FileLogger<ScreenStreamingService>(log));
        screenA.Start();
        screenB.Start();

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        await WaitUntilAsync(() => screenA.PeerVirtualDisplay(b.Identity.Id) is not null, ct);

        var opened = new TaskCompletionSource<ScreenViewer>(TaskCreationOptions.RunContinuationsAsynchronously);
        screenB.ViewerOpened += viewer => opened.TrySetResult(viewer);
        Assert.True(await screenB.RequestScreenAsync(a.Identity.Id, 0));
        var viewer = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var sizes = new List<(int Width, int Height)>();
        viewer.Attach(update =>
        {
            lock (sizes)
            {
                sizes.Add((update.Width, update.Height));
            }

            viewer.FramePresented();
        });

        await WaitUntilAsync(() => Seen(sizes).Count > 0, ct);
        var full = Seen(sizes)[0];
        var primary = DisplayCatalog.Monitors()[0];
        Assert.True(full.Width >= primary.Bounds.Width, "the first frames should be the screen's own size");

        // Half the width, and a height that is not what limits it: the source fits it to the shape of the screen.
        viewer.SetShownSize(full.Width / 2, full.Height);

        await WaitUntilAsync(() => Seen(sizes).Any(s => s.Width < full.Width), ct);
        var scaled = Seen(sizes).Last(s => s.Width < full.Width);

        Assert.InRange(scaled.Width, (full.Width / 2) - 160, (full.Width / 2) + 160);
        Assert.Equal(full.Width / (double)full.Height, scaled.Width / (double)scaled.Height, 1);

        // And asking for the screen's own size again brings it back.
        viewer.SetShownSize(full.Width, full.Height);
        await WaitUntilAsync(() => Seen(sizes).Last().Width >= full.Width, ct);

        viewer.Close();
        await WaitUntilAsync(() => screenA.Sources.Count == 0, ct);
    }

    private static List<(int Width, int Height)> Seen(List<(int Width, int Height)> sizes)
    {
        lock (sizes)
        {
            return [.. sizes];
        }
    }

    private async Task StreamThePrimaryMonitorAsync(Core.Settings.VideoCodec codec, string expectedCodec)
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ScreenA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("ScreenB", discoveryB, $"127.0.0.1:{discoveryA}");
        a.Settings.Screen.VideoCodec = codec;
        await a.StartAsync();
        await b.StartAsync();

        var log = Path.Combine(Path.GetTempPath(), $"winmirroring-stream-{expectedCodec}.log");
        File.WriteAllText(log, string.Empty);
        await using var screenA = new ScreenStreamingService(a, new FileLogger<ScreenStreamingService>(log));
        await using var screenB = new ScreenStreamingService(b, new FileLogger<ScreenStreamingService>(log));
        screenA.Start();
        screenB.Start();

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);

        // The source picks the codec from what the viewer said it can decode: wait until it has heard.
        await WaitUntilAsync(() => screenA.PeerVirtualDisplay(b.Identity.Id) is not null, ct);

        var opened = new TaskCompletionSource<ScreenViewer>(TaskCreationOptions.RunContinuationsAsynchronously);
        screenB.ViewerOpened += viewer => opened.TrySetResult(viewer);
        Assert.True(await screenB.RequestScreenAsync(a.Identity.Id, 0));
        var viewer = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var updates = new List<FrameUpdate>();
        var first = new TaskCompletionSource<FrameUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Attach(update =>
        {
            // The image belongs to the viewer and only until FramePresented: copy it before giving it back.
            var kept = update with { Image = (byte[])update.Image.Clone() };
            lock (updates)
            {
                updates.Add(kept);
            }

            first.TrySetResult(kept);
            viewer.FramePresented();
        });

        var frame = await first.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var primary = DisplayCatalog.Monitors()[0];

        Assert.Equal("monitor", viewer.Info.Kind);
        Assert.Equal(expectedCodec, viewer.Info.Codec);

        // Without a DPI-aware manifest (as in this test host) monitor bounds are scaled, capture is physical:
        // same proportions, capture at least as large.
        Assert.True(frame.Width >= primary.Bounds.Width);
        Assert.Equal(primary.Bounds.Width / (double)primary.Bounds.Height, frame.Width / (double)frame.Height, 2);
        Assert.True(frame.Resized);
        Assert.Contains(frame.Image, b => b != 0);
        Assert.Single(screenA.Sources);

        // The streamed image must match a direct capture of the same monitor (JPEG is lossy, the screen may change
        // a little in between, so compare the mean error rather than exact pixels).
        var received = frame.Image;

        using (var direct = WgcCapture.ForMonitor(primary.Handle, NullLogger.Instance))
        {
            direct.Start();
            Assert.True(await direct.WaitForFrameAsync(0, TimeSpan.FromSeconds(5), ct));
            var reference = Array.Empty<byte>();
            Assert.True(direct.TryCopyLatest(ref reference, out var width, out var height, out var version));
            Assert.Equal((frame.Width, frame.Height), (width, height));

            // The screen goes on living while a frame travels, and a desktop with anything moving on it — a
            // clock, a window redrawing, this test's own console — makes a comparison against a later capture
            // meaningless. So take a second capture and compare only where the two agree: whatever stood still
            // is what the streamed picture can honestly be held to.
            await Task.Delay(300, ct);
            var later = Array.Empty<byte>();
            await direct.WaitForFrameAsync(version, TimeSpan.FromSeconds(2), ct);
            Assert.True(direct.TryCopyLatest(ref later, out _, out _, out _));

            // Not exact equality: a capture of the same still screen wobbles by a unit or two, and holding it to
            // the letter would call the whole screen moving.
            const int Still = 2;
            long error = 0;
            var compared = 0;
            for (var i = 0; i < reference.Length; i += 4)
            {
                if (Math.Abs(reference[i] - later[i]) > Still
                    || Math.Abs(reference[i + 1] - later[i + 1]) > Still
                    || Math.Abs(reference[i + 2] - later[i + 2]) > Still)
                {
                    continue; // that pixel was changing anyway
                }

                error += Math.Abs(reference[i] - received[i]) + Math.Abs(reference[i + 1] - received[i + 1]) + Math.Abs(reference[i + 2] - received[i + 2]);
                compared++;
            }

            var still = compared / (reference.Length / 4.0);
            Assert.SkipWhen(still < 0.5, $"only {still:P0} of the screen stood still; nothing to compare against");

            var meanError = error / (compared * 3.0);
            TestContext.Current.TestOutputHelper?.WriteLine($"mean error {meanError:F2} over {still:P0} of the screen");
            Assert.InRange(meanError, 0, 12);
        }

        viewer.Close();
        await WaitUntilAsync(() => screenA.Sources.Count == 0, ct);
    }

    [Fact]
    public async Task ExtendWithoutTheDriverIsRefusedWithAReason()
    {
        if (ScreenStreamingService.IsVirtualDisplayAvailable)
        {
            Assert.Skip("The virtual display driver is installed here; this test covers the machine without it.");
        }

        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var host = CreateNode("HostA", discoveryA, $"127.0.0.1:{discoveryB}");
        var tablet = CreateNode("TabletB", discoveryB, $"127.0.0.1:{discoveryA}");
        await host.StartAsync();
        await tablet.StartAsync();

        await using var hostScreen = new ScreenStreamingService(host, NullLogger<ScreenStreamingService>.Instance);
        await using var tabletScreen = new ScreenStreamingService(tablet, NullLogger<ScreenStreamingService>.Instance);
        hostScreen.Start();
        tabletScreen.Start();

        await WaitUntilAsync(() => tablet.Peers.TryGet(host.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await tablet.PairAsync(host.Identity.Id, ct));
        await WaitUntilAsync(() => host.Sessions.TryGetSession(tablet.Identity.Id, out var s) && s.State == SessionState.Ready, ct);

        var failed = new TaskCompletionSource<(string Peer, string Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        tabletScreen.ExtendFailed += (peer, reason) => failed.TrySetResult((peer, reason));

        // The host invites; the tablet answers with its screen size; the host has no virtual monitor and says so.
        Assert.True(await hostScreen.InviteAsDisplayAsync(tablet.Identity.Id));
        var (peerName, why) = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal("HostA", peerName);
        Assert.Contains("драйвер", why);
        Assert.Null(hostScreen.ExtendedTo);
        Assert.Empty(hostScreen.Sources);
    }

    private MirroringNode CreateNode(string name, int discoveryPort, string manualAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.ScreenTests", Guid.NewGuid().ToString("N"));
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
