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

    /// <summary>
    /// Zoomed in, a viewer asks for the part of the screen it has in view. The pictures that come back have to be
    /// of that part — smaller than the screen — and each has to say which request it was made for, because the
    /// viewer draws a part in the place it is a part of and pictures of the last request are still arriving when
    /// the next is made. Asking for the whole again brings the whole back, under its own name.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task APartOfTheScreenComesBackUnderTheNameItWasAskedForBy()
    {
        if (!VideoCodecSupport.CanEncodeH264 || !VideoCodecSupport.CanDecodeH264)
        {
            Assert.Skip("No hardware H.264 encoder (or no decoder) on this machine.");
        }

        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("PartA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("PartB", discoveryB, $"127.0.0.1:{discoveryA}");
        a.Settings.Screen.VideoCodec = Core.Settings.VideoCodec.Auto;
        await a.StartAsync();
        await b.StartAsync();

        var log = Path.Combine(Path.GetTempPath(), "winmirroring-stream-part.log");
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
        Assert.True(viewer.Info.CropTags, "the source should say it names the part each picture is of");

        var frames = new List<(int Width, int Height, byte CropId)>();
        viewer.Attach(update =>
        {
            lock (frames)
            {
                frames.Add((update.Width, update.Height, update.CropId));
            }

            viewer.FramePresented();
        });

        List<(int Width, int Height, byte CropId)> Got()
        {
            lock (frames)
            {
                return [.. frames];
            }
        }

        await WaitUntilAsync(() => Got().Count > 0, ct);
        var full = Got()[0];
        Assert.Equal(0, full.CropId);

        // The middle quarter of the screen, as request 5.
        var part = new System.Drawing.RectangleF(0.25f, 0.25f, 0.5f, 0.5f);
        viewer.SetCrop(part, 5, part);
        await WaitUntilAsync(() => Got().Any(f => f.CropId == 5), ct);
        var detail = Got().Last(f => f.CropId == 5);
        Assert.InRange(detail.Width, (full.Width / 2) - 4, (full.Width / 2) + 4);
        Assert.InRange(detail.Height, (full.Height / 2) - 4, (full.Height / 2) + 4);

        // Nothing that came before the request was answered may carry its name, and nothing after it the old one
        // at the new size: a picture drawn in the wrong place is what the names exist to prevent.
        Assert.All(Got().Where(f => f.CropId == 5), f => Assert.Equal((detail.Width, detail.Height), (f.Width, f.Height)));
        Assert.All(Got().Where(f => f.CropId == 0), f => Assert.Equal((full.Width, full.Height), (f.Width, f.Height)));

        // Another part of the same size, somewhere else: the size alone would not tell them apart.
        var moved = new System.Drawing.RectangleF(0.5f, 0.5f, 0.5f, 0.5f);
        viewer.SetCrop(moved, 6, moved);
        await WaitUntilAsync(() => Got().Any(f => f.CropId == 6), ct);
        var afterSix = Got().SkipWhile(f => f.CropId != 6).ToList();
        Assert.All(afterSix, f => Assert.Equal(6, f.CropId));

        // And the whole again.
        viewer.SetCrop(ViewParts.Whole, 7);
        await WaitUntilAsync(() => Got().Any(f => f.CropId == 7), ct);
        var whole = Got().Last(f => f.CropId == 7);
        Assert.Equal((full.Width, full.Height), (whole.Width, whole.Height));

        viewer.Close();
        await WaitUntilAsync(() => screenA.Sources.Count == 0, ct);
    }

    /// <summary>
    /// A view that ends because the other device's application went away says so, and one that is closed here does
    /// not: that is the whole of what decides whether it is opened again by itself. Then the same monitor can be
    /// asked for again on the same session and a picture comes — which is what reopening does.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task AViewThatLostItsSourceSaysItCanBeOpenedAgainAndCanBe()
    {
        var ct = TestContext.Current.CancellationToken;
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ResumeA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("ResumeB", discoveryB, $"127.0.0.1:{discoveryA}");
        await a.StartAsync();
        await b.StartAsync();

        var log = Path.Combine(Path.GetTempPath(), "winmirroring-stream-resume.log");
        File.WriteAllText(log, string.Empty);
        var screenA = new ScreenStreamingService(a, new FileLogger<ScreenStreamingService>(log));
        await using var screenB = new ScreenStreamingService(b, new FileLogger<ScreenStreamingService>(log));
        screenA.Start();
        screenB.Start();

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct);
        Assert.True(await b.PairAsync(a.Identity.Id, ct));
        await WaitUntilAsync(() => a.Sessions.TryGetSession(b.Identity.Id, out var s) && s.State == SessionState.Ready, ct);
        await WaitUntilAsync(() => screenA.PeerVirtualDisplay(b.Identity.Id) is not null, ct);

        var views = new List<ScreenViewer>();
        screenB.ViewerOpened += viewer =>
        {
            lock (views)
            {
                views.Add(viewer);
            }
        };

        ScreenViewer? Latest(int count)
        {
            lock (views)
            {
                return views.Count >= count ? views[count - 1] : null;
            }
        }

        // Closed here: over, by somebody's choice.
        Assert.True(await screenB.RequestScreenAsync(a.Identity.Id, 0));
        await WaitUntilAsync(() => Latest(1) is not null, ct);
        var first = Latest(1)!;
        var firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Closed += (_, _) => firstEnded.TrySetResult();
        first.Close();
        await firstEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(first.CanResume);
        await WaitUntilAsync(() => screenA.Sources.Count == 0, ct);

        // The source's application goes away under an open view: nobody chose that.
        Assert.True(await screenB.RequestScreenAsync(a.Identity.Id, 0));
        await WaitUntilAsync(() => Latest(2) is not null, ct);
        var second = Latest(2)!;
        var frames = 0;
        second.Attach(_ =>
        {
            Interlocked.Increment(ref frames);
            second.FramePresented();
        });
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0, ct);
        var secondEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Closed += (_, reason) => secondEnded.TrySetResult(reason);
        await screenA.DisposeAsync();
        Assert.Equal("shutdown", await secondEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(second.CanResume);

        // It comes back (here, a new service on the same node, as an application started again would be), and the
        // same monitor asked for again gives a picture.
        await using var screenAgain = new ScreenStreamingService(a, new FileLogger<ScreenStreamingService>(log));
        screenAgain.Start();
        await WaitUntilAsync(() => screenAgain.PeerVirtualDisplay(b.Identity.Id) is not null, ct);
        Assert.True(await screenB.RequestScreenAsync(a.Identity.Id, second.Info.MonitorIndex, second.Info.MonitorKey));
        await WaitUntilAsync(() => Latest(3) is not null, ct);
        var third = Latest(3)!;
        var again = 0;
        third.Attach(_ =>
        {
            Interlocked.Increment(ref again);
            third.FramePresented();
        });
        await WaitUntilAsync(() => Volatile.Read(ref again) > 0, ct);

        third.Close();
        await WaitUntilAsync(() => screenAgain.Sources.Count == 0, ct);
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

        // Before the comparison below, which stands aside on a desktop that will not hold still: whether the
        // pictures reach the viewer by UDP has nothing to do with whether anything on the screen is moving.
        if (expectedCodec == "h264")
        {
            await ThePicturesMoveToUdpAsync(viewer, ct);
        }

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

        await ThePointerArrivesOnItsOwnAsync(screenA, b.Identity.Id, viewer, primary, ct);

        viewer.Close();
        await WaitUntilAsync(() => screenA.Sources.Count == 0, ct);
    }

    /// <summary>
    /// The pointer is not in the picture any more; it travels on its own and is drawn by the viewer. So it has to
    /// arrive: a position, and the pixels of the shape that position names. The pointer is put in the middle of the
    /// streamed monitor for this and then put back where it was, because where it happens to be is not this test's
    /// to decide and a pointer on another monitor is not in this stream at all.
    ///
    /// What the position is held to is the thing that matters about it: the source is asked where that point on the
    /// picture is on its own screen — the same question a tap on the picture asks — and it has to come back where
    /// the pointer actually is. A pointer drawn anywhere other than where a click would land is worse than none.
    /// </summary>
    private static async Task ThePointerArrivesOnItsOwnAsync(ScreenStreamingService source, string viewerId, ScreenViewer viewer, MonitorDescription monitor, CancellationToken ct)
    {
        Assert.True(GetCursorPos(out var was));
        try
        {
            var middle = new System.Drawing.Point(monitor.Bounds.Left + (monitor.Bounds.Width / 2), monitor.Bounds.Top + (monitor.Bounds.Height / 2));
            Assert.True(SetCursorPos(middle.X, middle.Y));

            // Windows hides the mouse pointer once a screen has been touched and does not bring it back for a
            // SetCursorPos — only real movement does. There is then nothing for the source to send and nothing for
            // this to hold it to, which is the behaviour wanted rather than a failure.
            Assert.SkipUnless(
                Capture.CursorReader.TryRead(out _, out _, out var showing) && showing,
                "the system is not showing a mouse pointer at all (a screen here has been touched)");

            // Waiting for a pointer at the place it was put, not merely for one: wherever the mouse already was is
            // also a visible pointer, and it will have arrived before this moved it.
            bool AtTheMiddle() =>
                viewer.Pointer is { Visible: true } at
                && source.TryMapToScreen(viewerId, viewer.StreamId, at.X, at.Y, out var screen)
                && Math.Abs(screen.X - middle.X) <= 4
                && Math.Abs(screen.Y - middle.Y) <= 4;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!AtTheMiddle())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    GetCursorPos(out var где);
                    var seen = viewer.Pointer is { } p
                        ? $"{p.X:F4},{p.Y:F4} visible {p.Visible} shape {p.ShapeId}" + (source.TryMapToScreen(viewerId, viewer.StreamId, p.X, p.Y, out var s) ? $" → {s}" : " (not mappable)")
                        : "nothing at all";
                    Assert.Fail($"the pointer was put at {middle} and is at {где.X},{где.Y}, but the viewer has {seen}");
                }

                await Task.Delay(50, ct);
            }

            // The shape its id names must have arrived too, or there would be nothing to draw.
            var pointer = viewer.Pointer!;
            await WaitUntilAsync(() => viewer.PointerShape(pointer.ShapeId) is not null, ct);
            var shape = viewer.PointerShape(pointer.ShapeId)!;
            Assert.InRange(shape.Width, 8, Protocol.ScreenCodec.MaxCursorSize);
            Assert.Equal(shape.Width * shape.Height * 4, shape.Pixels.Length);
            Assert.Contains(shape.Pixels, b => b != 0);
        }
        finally
        {
            SetCursorPos(was.X, was.Y);
        }
    }

    /// <summary>
    /// The pictures leave the connection for UDP, where a lost one does not hold back the next. Every part of that
    /// has to work for one picture to arrive this way: the key handed over inside the session, the probes the
    /// source sends on the new path, the answer the viewer gives on the old one, the cutting into datagrams and the
    /// putting back together. Nothing is lost over a loopback, so this also says the pieces line up exactly.
    /// </summary>
    private static async Task ThePicturesMoveToUdpAsync(ScreenViewer viewer, CancellationToken ct)
    {
        await WaitUntilAsync(() => viewer.Datagrams.Delivered > 0, ct);

        var (delivered, repaired, lost) = viewer.Datagrams;
        var line = $"by UDP: {delivered} pictures, {repaired} repaired, {lost} given up on";
        TestContext.Current.TestOutputHelper?.WriteLine(line);

        // Also to a file, because this test stands aside further down on a desktop that will not hold still, and a
        // test that was skipped prints nothing — while whether the pictures took the new path is the whole point.
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "winmirroring-udp.txt"), line + Environment.NewLine);
        Assert.True(delivered > 0, "no picture arrived by UDP");
        Assert.Equal(0, lost);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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
