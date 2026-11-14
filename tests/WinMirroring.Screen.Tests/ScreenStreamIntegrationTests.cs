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
            Assert.True(direct.TryCopyLatest(ref reference, out var width, out var height, out _));
            Assert.Equal((frame.Width, frame.Height), (width, height));

            long error = 0;
            for (var i = 0; i < reference.Length; i += 4)
            {
                error += Math.Abs(reference[i] - received[i]) + Math.Abs(reference[i + 1] - received[i + 1]) + Math.Abs(reference[i + 2] - received[i + 2]);
            }

            var meanError = error / (reference.Length / 4.0 * 3);
            TestContext.Current.TestOutputHelper?.WriteLine($"mean error {meanError:F2}");
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
