using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Injection;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen;

public enum StreamKind
{
    Monitor,
    Window,

    /// <summary>The virtual monitor this device extends onto the viewer (it is the viewer's screen).</summary>
    Display,
}

/// <summary>
/// One outgoing stream of a monitor or a window to one viewer. A few frame messages may be unacknowledged at a time
/// (see <see cref="SendWindow"/>), and the frame rate and bitrate follow how promptly the viewer acknowledges them
/// (see <see cref="StreamPacing"/>), so a link that cannot carry the configured bitrate makes the picture coarser
/// instead of freezing it. Pointer and keyboard events from the viewer are injected here at the matching screen
/// position.
/// </summary>
internal sealed class ScreenSource : IAsyncDisposable
{
    /// <summary>
    /// Frame messages allowed on the wire at once. This is the picture's delay, not a throughput setting: a frame
    /// waiting its turn here is a frame the far screen is not showing yet, so four of them at twenty-five frames a
    /// second was a sixth of a second of delay all by itself. Two is enough to keep the link busy — the window
    /// only limits the rate once a round trip takes longer than half a frame — and the pacing below brings the rate
    /// down if it ever does.
    /// </summary>
    private const int MaxInFlight = 2;

    private const int PartBytes = 512 * 1024;
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a frame may stay unacknowledged before the stream stops waiting for it.</summary>
    private static readonly TimeSpan AckGrace = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private readonly ScreenSettings _settings;
    private readonly ILogger _logger;
    private readonly InputInjector _injector = new();
    private readonly Injection.TouchInjector _touch;
    private readonly TileEncoder _encoder = new();
    private readonly SendWindow _window = new(MaxInFlight, AckGrace);
    private readonly StreamPacing _pacing;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private long _lastBlockedReport = -10_000;
    private long _monitorCheckedTicks;

    // Diagnostics, written and read by the streaming loop only.
    private long _reportedTicks;
    private long _reportedChanges;
    private long _framesSent;
    private long _bytesSent;

    private WgcCapture _capture;
    private MonitorDescription? _monitor;
    private RectangleF _crop = new(0, 0, 1, 1);
    private Size _frameSize;
    private int _forceAll = 1;
    private Task? _loop;
    private IDisposable? _timer;
    private int _ended;
    private bool _reportedIconic;

    // Video (H.264) state, touched only by the streaming loop thread.
    private readonly bool _useVideo;
    private H264VideoEncoder? _videoEncoder;
    private bool _videoFailed;
    private GpuNv12Converter? _gpuConverter;
    private bool _gpuFailed;
    private bool _gpuReported;

    /// <summary>The viewer asked for tiles (its H.264 gives no picture); taken up by the send loop.</summary>
    private volatile string? _tilesRequested;
    private uint _frameNo;

    private ScreenSource(PeerSession session, uint streamId, StreamKind kind, WgcCapture capture, MonitorDescription? monitor, nint window, string title, ScreenSettings settings, bool useVideo, ILogger logger)
    {
        Session = session;
        StreamId = streamId;
        Kind = kind;
        _capture = capture;
        _monitor = monitor;
        Window = window;
        Title = title;
        _settings = settings;
        _useVideo = useVideo;
        _logger = logger;
        _pacing = new StreamPacing(settings.MaxFps);
        _touch = new Injection.TouchInjector(logger);
        _capture.Closed += OnCaptureClosed;
    }

    /// <summary>The codec this stream announces to the viewer: H.264 when negotiated, otherwise JPEG tiles.</summary>
    private string Codec => _useVideo && !_videoFailed ? "h264" : "jpeg";

    public PeerSession Session { get; }

    public uint StreamId { get; }

    public StreamKind Kind { get; }

    public nint Window { get; }

    public string Title { get; private set; }

    public int MonitorIndex => _monitor?.Index ?? 0;

    public event Action<ScreenSource, string>? Ended;

    public static ScreenSource ForMonitor(PeerSession session, uint streamId, int monitorIndex, string? monitorKey, ScreenSettings settings, bool useVideo, ILogger logger)
    {
        var monitor = PickMonitor(monitorIndex, monitorKey);
        return new ScreenSource(session, streamId, StreamKind.Monitor, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, MonitorTitle(monitor), settings, useVideo, logger);
    }

    public static ScreenSource ForWindow(PeerSession session, uint streamId, nint window, ScreenSettings settings, bool useVideo, ILogger logger)
    {
        var title = GetTitle(window);
        return new ScreenSource(session, streamId, StreamKind.Window, WgcCapture.ForWindow(window, logger), null, window, string.IsNullOrEmpty(title) ? "Окно" : title, settings, useVideo, logger);
    }

    /// <summary>Streams the virtual monitor that extends this desktop onto the viewer.</summary>
    public static ScreenSource ForDisplay(PeerSession session, uint streamId, MonitorDescription monitor, ScreenSettings settings, bool useVideo, ILogger logger) =>
        new(session, streamId, StreamKind.Display, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, $"Второй монитор {Environment.MachineName}", settings, useVideo, logger);

    public ScreenStreamMessage Describe() => new(
        StreamId,
        Kind switch { StreamKind.Monitor => "monitor", StreamKind.Display => "display", _ => "window" },
        Title,
        Kind == StreamKind.Monitor ? MonitorIndex : 0,
        Kind switch { StreamKind.Monitor => DisplayCatalog.Monitors().Count, StreamKind.Display => 1, _ => 0 },
        _settings.AllowControl,
        Kind == StreamKind.Window && IsIconic(Window),
        Kind == StreamKind.Window ? WindowScalePercent(Window) : 100,
        Codec,
        _monitor?.DeviceName ?? string.Empty);

    /// <summary>Viewer side runs a copy of the window: carry out a command on this device's real window.</summary>
    public void OnWindowCommand(WindowCommand command)
    {
        if (Kind != StreamKind.Window)
        {
            return;
        }

        if (command == WindowCommand.Restore && IsIconic(Window))
        {
            ShowWindow(Window, SW_RESTORE);
            SetForegroundWindow(Window);
        }
    }

    /// <summary>Tells the viewer when the captured window is minimised or restored, so its window can follow.</summary>
    private void ReportWindowStateIfChanged()
    {
        if (Kind != StreamKind.Window)
        {
            return;
        }

        // The window closed: WGC does not always raise its Closed event, so end the stream here too.
        if (!IsWindow(Window))
        {
            End("window closed");
            return;
        }

        var iconic = IsIconic(Window);
        if (iconic != _reportedIconic)
        {
            _reportedIconic = iconic;
            _ = Session.SendControlAsync(Describe());
        }
    }

    public void Start()
    {
        // Waits shorter than a frame are rounded up to about fifteen milliseconds otherwise, which both slows the
        // stream below its target and makes the spacing between frames uneven.
        _timer = MediaTimerResolution.Hold();
        _capture.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("Streaming {Title} to {Peer} (stream {Stream})", Title, Session.PeerName, StreamId);
    }

    /// <summary>The viewer cannot show H.264: the stream goes on as JPEG tiles from the next frame on.</summary>
    public void FallBackToTiles(string reason)
    {
        _tilesRequested = reason;
        Interlocked.Exchange(ref _forceAll, 1);
    }

    /// <summary>Viewer asked for another monitor of a monitor stream.</summary>
    public void SwitchMonitor(int index, string? key = null)
    {
        if (Kind != StreamKind.Monitor)
        {
            return;
        }

        var monitor = PickMonitor(index, key);
        var capture = WgcCapture.ForMonitor(monitor.Handle, _logger);
        WgcCapture old;
        lock (_gate)
        {
            old = _capture;
            _capture = capture;
            _monitor = monitor;
            _crop = new RectangleF(0, 0, 1, 1);
            Title = MonitorTitle(monitor);
        }

        old.Closed -= OnCaptureClosed;
        capture.Closed += OnCaptureClosed;
        capture.Start();
        old.Dispose();
        Volatile.Write(ref _forceAll, 1);
    }

    public void OnAck() => _window.OnAck();

    public void OnCrop(RectangleF crop)
    {
        lock (_gate)
        {
            _crop = ScreenCodec.Normalize(crop);
        }

        Volatile.Write(ref _forceAll, 1);
    }

    public void OnPointer(PointerAction action, MouseButton button, float x, float y, int wheel)
    {
        if (!_settings.AllowControl || !TryMapToScreen(x, y, out var point))
        {
            return;
        }

        switch (action)
        {
            case PointerAction.Move:
                _injector.MoveAbsolute(point.X, point.Y);
                break;

            case PointerAction.Down:
                if (Kind == StreamKind.Window)
                {
                    BringWindowForward();
                }

                RemoteInputTracker.NoteDown(point.X, point.Y);
                _injector.MoveAbsolute(point.X, point.Y);
                _injector.Button(button, down: true);
                ReportIfBlocked();
                break;

            case PointerAction.Up:
                _injector.MoveAbsolute(point.X, point.Y);
                _injector.Button(button, down: false);
                break;

            case PointerAction.Wheel:
            case PointerAction.HorizontalWheel:
                _injector.MoveAbsolute(point.X, point.Y);
                _injector.Wheel(wheel, action == PointerAction.HorizontalWheel);
                break;
        }
    }

    public void OnTouch(byte action, byte contact, float x, float y)
    {
        if (!_settings.AllowControl || !TryMapToScreen(x, y, out var point))
        {
            return;
        }

        var touchAction = (Injection.TouchInjector.Action)action;
        if (touchAction == Injection.TouchInjector.Action.Down)
        {
            if (Kind == StreamKind.Window)
            {
                BringWindowForward();
            }

            RemoteInputTracker.NoteDown(point.X, point.Y);
        }

        _touch.Apply(touchAction, contact, point);
        if (touchAction == Injection.TouchInjector.Action.Down)
        {
            ReportIfBlocked();
        }
    }

    /// <summary>Tells the viewer (at most every 10 s) that an elevated window in front swallows its input.</summary>
    private void ReportIfBlocked()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastBlockedReport) < 10_000 || !WinMirroring.Input.Native.InjectionGuard.IsForegroundAboveUs(out var title))
        {
            return;
        }

        Interlocked.Exchange(ref _lastBlockedReport, now);
        _logger.LogInformation("Input from the viewer of {Peer} is blocked by an elevated window: {Title}", Session.PeerName, title);
        _ = Task.Run(async () =>
        {
            try
            {
                await Session.SendControlAsync(new Core.Messages.InputBlockedMessage(title)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug("Blocked-input notice not sent: {Message}", ex.Message);
            }
        });
    }

    /// <summary>Puts a window stream's window in front (before dropping files on it, for example).</summary>
    public void BringForward()
    {
        if (Kind == StreamKind.Window)
        {
            BringWindowForward();
        }
    }

    public void OnKey(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        if (_settings.AllowControl)
        {
            _injector.Key(virtualKey, scanCode, up, extended);
        }
    }

    public void OnText(string text)
    {
        if (_settings.AllowControl && text.Length > 0)
        {
            _injector.TypeText(text);
        }
    }

    /// <summary>Normalized position in the streamed (cropped) image → physical screen pixel on this device.</summary>
    internal bool TryMapToScreen(float x, float y, out Point point)
    {
        point = default;
        Rectangle bounds;
        Size frame;
        RectangleF crop;
        lock (_gate)
        {
            frame = _frameSize;
            crop = _crop;
            bounds = Kind != StreamKind.Window ? CurrentMonitorBoundsLocked() : GetVisibleBounds(Window) ?? Rectangle.Empty;
        }

        if (frame.Width <= 0 || frame.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        point = MapToScreen(x, y, crop, frame, bounds);
        return true;
    }

    /// <summary>Monitor bounds, re-read at most once a second: monitors move when the display layout changes.</summary>
    private Rectangle CurrentMonitorBoundsLocked()
    {
        var now = Environment.TickCount64;
        if (now - _monitorCheckedTicks > 1000)
        {
            _monitorCheckedTicks = now;
            if (DisplayCatalog.Monitors().FirstOrDefault(m => string.Equals(m.DeviceName, _monitor!.DeviceName, StringComparison.OrdinalIgnoreCase)) is { } fresh)
            {
                _monitor = _monitor! with { Bounds = fresh.Bounds, Handle = fresh.Handle };
            }
        }

        return _monitor!.Bounds;
    }

    internal static Point MapToScreen(float x, float y, RectangleF crop, Size frame, Rectangle bounds)
    {
        var pixels = ScreenCodec.ToPixels(crop, frame.Width, frame.Height);
        var frameX = pixels.X + x * pixels.Width;
        var frameY = pixels.Y + y * pixels.Height;
        var screenX = bounds.Left + (int)Math.Round(frameX * bounds.Width / frame.Width);
        var screenY = bounds.Top + (int)Math.Round(frameY * bounds.Height / frame.Height);
        return new Point(Math.Clamp(screenX, bounds.Left, bounds.Right - 1), Math.Clamp(screenY, bounds.Top, bounds.Bottom - 1));
    }

    private void BringWindowForward()
    {
        if (IsIconic(Window))
        {
            ShowWindow(Window, SW_RESTORE);
        }

        SetForegroundWindow(Window);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var frame = Array.Empty<byte>();
        long seen = 0;
        WgcCapture? lastCapture = null;
        var pacing = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                WgcCapture capture;
                lock (_gate)
                {
                    capture = _capture;
                }

                if (!ReferenceEquals(capture, lastCapture))
                {
                    lastCapture = capture;
                    seen = 0;
                }

                ReportWindowStateIfChanged();

                var fresh = await capture.WaitForFrameAsync(seen, IdleWait, ct).ConfigureAwait(false);
                if (!fresh && _videoEncoder is { HasPendingOutput: true } pending)
                {
                    // The screen is still, but the encoder finished a frame after it was last asked: send it, or the
                    // last change would only appear with the next one.
                    await SendAsync(BuildVideoMessages(pending.TakePending()), ct).ConfigureAwait(false);
                    continue;
                }

                if (!fresh && (Volatile.Read(ref _forceAll) == 0 || seen == 0))
                {
                    continue;
                }

                await _window.TakeAsync(ct).ConfigureAwait(false);
                var slotHeld = true;
                try
                {
                    // Only the size and the age of the frame, not its pixels: whether they are needed in memory at
                    // all depends on which encoder ends up taking this frame.
                    if (!capture.TryLatestInfo(out var width, out var height, out var version))
                    {
                        continue;
                    }

                    seen = version;
                    var force = Interlocked.Exchange(ref _forceAll, 0) == 1;
                    Rectangle crop;
                    RectangleF cropFraction;
                    lock (_gate)
                    {
                        _frameSize = new Size(width, height);
                        cropFraction = _crop;
                        crop = ScreenCodec.ToPixels(cropFraction, width, height);
                    }

                    if (_tilesRequested is { } reason && _useVideo && !_videoFailed)
                    {
                        _logger.LogInformation("{Peer} gets no picture from H.264 ({Reason}); sending JPEG tiles", Session.PeerName, reason);
                        _videoFailed = true;
                        _videoEncoder?.Dispose();
                        _videoEncoder = null;
                        _gpuConverter?.Dispose();
                        _gpuConverter = null;
                        force = true;
                        _ = Session.SendControlAsync(Describe());
                    }

                    List<byte[]>? messages = null;
                    if (_useVideo && !_videoFailed)
                    {
                        messages = BuildVideoMessages(capture, ref frame, width, crop, force);
                        if (_videoFailed)
                        {
                            force = true; // just fell back to tiles: make the first JPEG frame complete
                        }
                    }

                    if (messages is null)
                    {
                        if (!capture.TryCopyLatest(ref frame, out width, out height, out _))
                        {
                            continue;
                        }

                        messages = BuildJpegMessages(frame, width, height, ScreenCodec.ToPixels(cropFraction, width, height), force);
                    }
                    if (messages.Count == 0)
                    {
                        continue;
                    }

                    foreach (var message in messages)
                    {
                        if (!slotHeld)
                        {
                            await _window.TakeAsync(ct).ConfigureAwait(false);
                        }

                        slotHeld = false;
                        _framesSent++;
                        _bytesSent += message.Length;
                        await Session.SendAsync(ChannelId.Screen, message, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (slotHeld)
                    {
                        _window.Return();
                    }
                }

                var minimumMs = 1000.0 / Observe();
                var wait = minimumMs - pacing.Elapsed.TotalMilliseconds;
                if (wait > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
                }

                pacing.Restart();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            End($"connection: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screen stream {Stream} failed", StreamId);
            End("error");
        }
    }

    /// <summary>
    /// Encodes one frame as H.264 and returns the ready access units as VideoFrame messages. Returns null to mean
    /// "use JPEG for this frame" — because the crop is too small for video, or because the encoder could not be
    /// created (which also sets <see cref="_videoFailed"/> so the whole stream stays on tiles from now on). An
    /// empty list means the encoder pipeline has no output yet; nothing is sent this frame.
    /// </summary>
    private List<byte[]>? BuildVideoMessages(WgcCapture capture, ref byte[] frame, int frameWidth, Rectangle crop, bool force)
    {
        var videoWidth = Nv12Converter.Even(crop.Width);
        var videoHeight = Nv12Converter.Even(crop.Height);
        if (videoWidth < 16 || videoHeight < 16)
        {
            return null; // too small for H.264 this frame; the tile path handles it
        }

        if (_videoEncoder is null || _videoEncoder.Width != videoWidth || _videoEncoder.Height != videoHeight)
        {
            _videoEncoder?.Dispose();
            _videoEncoder = H264VideoEncoder.TryCreate(videoWidth, videoHeight, _pacing.Fps, ScaleBitrate(videoWidth, videoHeight), _logger);
            if (_videoEncoder is null)
            {
                _logger.LogInformation("H.264 encoder unavailable for {Title}; using JPEG tiles", Title);
                _videoFailed = true;
                return null;
            }

            force = true; // a fresh encoder emits a key frame anyway
        }

        var videoCrop = new Rectangle(crop.X, crop.Y, videoWidth, videoHeight);
        IReadOnlyList<VideoAccessUnit> units;
        try
        {
            if (!TryEncodeOnGpu(capture, videoCrop, force, out units))
            {
                if (!capture.TryCopyLatest(ref frame, out var width, out _, out _))
                {
                    return [];
                }

                units = _videoEncoder.Encode(frame, width, videoCrop, force);
            }
        }
        catch (Exception ex)
        {
            // With the exception, not just its message: this has happened once in the wild (while two monitors were
            // being streamed at once) and dropped a stream to tiles, and a message alone says nothing about where.
            _logger.LogWarning(ex, "H.264 encode failed for {Title}; using JPEG tiles", Title);
            _videoEncoder.Dispose();
            _videoEncoder = null;
            _gpuConverter?.Dispose();
            _gpuConverter = null;
            _videoFailed = true;
            return null;
        }

        return BuildVideoMessages(units);
    }

    /// <summary>
    /// Encodes the frame without it ever leaving the graphics card: it is cropped and turned into NV12 there and
    /// handed to the encoder as a texture. False means this is not possible — then the frame is read back into
    /// memory and converted by the processor, exactly as before, and that stays the way for this stream.
    /// </summary>
    private bool TryEncodeOnGpu(WgcCapture capture, Rectangle crop, bool force, out IReadOnlyList<VideoAccessUnit> units)
    {
        units = [];
        if (_gpuFailed || !capture.UsesSharedDevice || _videoEncoder is not { UsesSharedDevice: true } encoder)
        {
            return false;
        }

        var converter = _gpuConverter ??= GpuNv12Converter.TryCreate(_logger);
        if (converter is null || converter.NextTarget(crop.Width, crop.Height) is not { } target)
        {
            return GiveUpOnGpu();
        }

        if (!capture.WithLatestFrame(source => converter.TryConvert(source, crop, target)))
        {
            return GiveUpOnGpu();
        }

        units = encoder.EncodeTexture(target, force);
        if (!_gpuReported)
        {
            _gpuReported = true;
            _logger.LogInformation("{Title} is encoded on the graphics card; frames are not copied through memory", Title);
        }

        return true;
    }

    private bool GiveUpOnGpu()
    {
        if (!_gpuFailed)
        {
            _gpuFailed = true;
            _logger.LogInformation("{Title} is converted by the processor; the graphics card would not do it", Title);
        }

        _gpuConverter?.Dispose();
        _gpuConverter = null;
        return false;
    }

    private List<byte[]> BuildVideoMessages(IReadOnlyList<VideoAccessUnit> units)
    {
        var encoder = _videoEncoder;
        var messages = new List<byte[]>(units.Count);
        if (encoder is null)
        {
            return messages;
        }

        foreach (var unit in units)
        {
            messages.Add(ScreenCodec.EncodeVideoFrame(StreamId, ++_frameNo, encoder.Width, encoder.Height, unit.Keyframe, unit.Data));
        }

        return messages;
    }

    /// <summary>Sends frame messages one flow-control slot each (a slot comes back with the viewer's Ack).</summary>
    private async Task SendAsync(List<byte[]> messages, CancellationToken ct)
    {
        foreach (var message in messages)
        {
            await _window.TakeAsync(ct).ConfigureAwait(false);
            _framesSent++;
            _bytesSent += message.Length;
            await Session.SendAsync(ChannelId.Screen, message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the link's measurements, moves the frame rate and bitrate targets and returns the frame rate to pace
    /// this stream at. Also writes the stream's own line to the log every ten seconds: when a view stutters, the
    /// numbers that say why — how long acknowledgements take, how many frames the window stopped waiting for, what
    /// is actually going out — should already be in the log rather than having to be guessed at afterwards.
    /// </summary>
    private int Observe()
    {
        var now = Environment.TickCount64;
        _pacing.Observe(_window.DelayMs, _window.GivenUp, _settings.MaxFps, now);
        var fps = _pacing.Fps;
        if (_videoEncoder is { } encoder)
        {
            encoder.Retune(fps, ScaleBitrate(encoder.Width, encoder.Height));
        }

        if (_reportedTicks == 0)
        {
            _reportedTicks = now;
        }
        else if (now - _reportedTicks >= ReportEvery.TotalMilliseconds)
        {
            var seconds = (now - _reportedTicks) / 1000.0;
            _logger.LogInformation(
                "Stream {Stream} to {Peer}: {Codec}, {Rate:F1} messages/s, {Kbps:F0} kbit/s, aiming at {Fps} fps{Held}, "
                + "target moved {Changes} times, acknowledged in {Delay:F0} ms, {GivenUp} not waited for, {InFlight} in flight",
                StreamId,
                Session.PeerName,
                Codec,
                _framesSent / seconds,
                _bytesSent * 8 / 1000.0 / seconds,
                fps,
                _pacing.HeldBack ? " (held back)" : string.Empty,
                _pacing.Changes - _reportedChanges,
                _window.DelayMs,
                _window.GivenUp,
                _window.InFlight);
            _reportedTicks = now;
            _reportedChanges = _pacing.Changes;
            _framesSent = 0;
            _bytesSent = 0;
        }

        return fps;
    }

    private List<byte[]> BuildJpegMessages(byte[] frame, int frameWidth, int frameHeight, Rectangle crop, bool force)
    {
        // A link that cannot keep up gets coarser tiles, the same way the video path gets a lower bitrate.
        var quality = Math.Clamp(_settings.Quality, 30, 95);
        _encoder.Quality = (float)(30 + ((quality - 30) * _pacing.Share)) / 100f;
        var tiles = _encoder.Encode(frame, frameWidth, frameHeight, crop, force);
        if (tiles.Count == 0)
        {
            return [];
        }

        var messages = new List<byte[]>();
        foreach (var part in Split(tiles))
        {
            messages.Add(ScreenCodec.EncodeFrame(StreamId, ++_frameNo, _encoder.Width, _encoder.Height, part));
        }

        return messages;
    }

    /// <summary>
    /// Full-screen bitrate scaled down for smaller crops (at least 15% of the configured value) and again by what
    /// the link is currently willing to carry (see <see cref="StreamPacing"/>).
    /// </summary>
    private int ScaleBitrate(int width, int height)
    {
        var baseKbps = Math.Clamp(_settings.VideoBitrateKbps, 500, 100_000);
        var fraction = Math.Clamp(width * (double)height / (1920 * 1080), 0.15, 1.0);
        return (int)Math.Round(baseKbps * fraction * _pacing.Share);
    }

    private static IEnumerable<List<EncodedTile>> Split(List<EncodedTile> tiles)
    {
        var part = new List<EncodedTile>();
        var size = 0;
        foreach (var tile in tiles)
        {
            if (part.Count > 0 && size + tile.Jpeg.Length > PartBytes)
            {
                yield return part;
                part = [];
                size = 0;
            }

            part.Add(tile);
            size += tile.Jpeg.Length + 12;
        }

        if (part.Count > 0)
        {
            yield return part;
        }
    }

    private void OnCaptureClosed() => End("window closed");

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            Ended?.Invoke(this, reason);
        }
    }

    /// <summary>
    /// The monitor the viewer asked for: by its display device name where it gave one (exact, whatever the two
    /// sides' lists look like), then by the index it carries, and only then by position in the list.
    /// </summary>
    internal static MonitorDescription PickMonitor(int index, string? key = null)
    {
        var monitors = DisplayCatalog.Monitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitors found.");
        }

        if (!string.IsNullOrEmpty(key) && monitors.FirstOrDefault(m => m.DeviceName == key) is { } named)
        {
            return named;
        }

        return monitors.FirstOrDefault(m => m.Index == index) ?? monitors[Math.Clamp(index, 0, monitors.Count - 1)];
    }

    private static string MonitorTitle(MonitorDescription monitor) =>
        $"Монитор {monitor.Index + 1} ({monitor.Bounds.Width}×{monitor.Bounds.Height})";

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1);
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        WgcCapture capture;
        lock (_gate)
        {
            capture = _capture;
        }

        capture.Closed -= OnCaptureClosed;
        capture.Dispose();
        _encoder.Dispose();
        _videoEncoder?.Dispose();
        _gpuConverter?.Dispose();
        _injector.ReleaseAll();
        _touch.Dispose();
        _window.Dispose();
        _timer?.Dispose();
        _cts.Dispose();
        _logger.LogInformation("Stopped streaming {Title} to {Peer}", Title, Session.PeerName);
    }
}
