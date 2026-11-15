using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Injection;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;
using WinMirroring.Screen.Transport;
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
/// One outgoing stream of a monitor or a window to one viewer. A few frame messages may be on the wire
/// unacknowledged at a time (see <see cref="SendWindow"/>) and that is what sets the rate the stream actually runs
/// at; the bitrate follows how promptly the viewer acknowledges them (see <see cref="StreamPacing"/>), so a link
/// that cannot carry the configured bitrate makes the picture coarser instead of freezing it. Pointer and keyboard
/// events from the viewer are injected here at the matching screen position.
/// </summary>
internal sealed class ScreenSource : IAsyncDisposable
{
    /// <summary>
    /// Frame messages allowed on the wire at once, which is also the most picture that can be in the air unseen —
    /// so it is kept as small as the arithmetic allows. The arithmetic: a stream can only run at the window divided
    /// by the round trip, so the window has to be the frame rate times the round trip. Once the viewer decodes on
    /// its graphics card a round trip is ten to twenty milliseconds, which three slots clear at sixty frames a
    /// second several times over; three still reaches sixty at fifty milliseconds, and past that the rate comes
    /// down rather than the picture piling up unseen, which is the right way round. (Two used to cap a thirty-frame
    /// stream at twenty-eight on a seventy-millisecond round trip, and at nineteen while a slot was also held
    /// across the encoder.) When the link cannot carry the bitrate it comes down instead (see
    /// <see cref="StreamPacing"/>), and the grace below bounds the worst case.
    /// </summary>
    private const int MaxInFlight = 3;

    private const int PartBytes = 512 * 1024;
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the capture loop waits for a new frame while the encoder still owes it one. Short, because what it
    /// is really waiting for is the encoder, and a frame that has been fed is finished a few milliseconds later.
    /// </summary>
    private static readonly TimeSpan EncoderWait = TimeSpan.FromMilliseconds(4);

    /// <summary>How long a frame handed to the encoder is still worth waiting those short waits for.</summary>
    private static readonly TimeSpan EncoderOwesFor = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Frames the encoder may be behind before one is dropped rather than handed over. Two, because the converter
    /// hands out four textures in turn and they have to cover what is waiting, what the encoder is reading and the
    /// one being written.
    /// </summary>
    private const int MaxQueuedFrames = 2;

    /// <summary>
    /// How often this device's pointer is looked at. About the rate a mouse reports at: the whole point of sending
    /// it on its own is that it does not wait for a frame, and a position is eighteen bytes.
    /// </summary>
    private static readonly TimeSpan PointerEvery = TimeSpan.FromMilliseconds(8);

    /// <summary>Shapes remembered per stream before the names start again; Windows has a few dozen pointers.</summary>
    private const int MaxShapes = 48;

    /// <summary>
    /// How often a viewer's request for a whole picture is acted on. A key frame is the most expensive thing a
    /// stream sends, and losses come in runs: answering each one separately would make a bad moment worse.
    /// </summary>
    private static readonly TimeSpan RefreshAtMost = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a frame may stay unacknowledged before the stream stops waiting for it. Whatever this is, it is
    /// also how long the picture stands still when it happens, so half a second — which is what it was — was half
    /// a second of frozen picture every time a frame went missing, about once a minute on this link. It only has
    /// to be comfortably longer than a real round trip, and a real round trip here is seventy milliseconds with
    /// spikes to a hundred and fifty.
    /// </summary>
    private static readonly TimeSpan AckGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// An acknowledgement slower than this counts as late. Deliberately close to one frame's worth of time: the
    /// delay a frame waits before being shown is what it measures, and a stream whose frames are routinely later
    /// than their own interval is a stream asking for more than the link will give.
    /// </summary>
    private static readonly TimeSpan LateAbove = TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private readonly ScreenSettings _settings;
    private readonly ILogger _logger;
    private readonly InputInjector _injector = new();
    private readonly Injection.TouchInjector _touch;
    private readonly TileEncoder _encoder = new();
    private readonly SendWindow _window = new(MaxInFlight, AckGrace, LateAbove);
    private readonly StreamPacing _pacing;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private long _lastBlockedReport = -10_000;
    private long _monitorCheckedTicks;

    // Diagnostics, written and read by the streaming loop only.
    private long _reportedTicks;
    private long _reportedChanges;
    private long _reportedAcks;
    private long _reportedLate;
    private long _framesSent;
    private long _bytesSent;

    private WgcCapture _capture;
    private MonitorDescription? _monitor;
    private RectangleF _crop = new(0, 0, 1, 1);
    private Size _frameSize;

    /// <summary>How large the viewer says it is drawing this stream; empty until it has said (see OnViewerSize).</summary>
    private Size _viewerSize;
    private int _forceAll = 1;

    /// <summary>
    /// Finished messages on their way to the link, one frame deep. Capturing, encoding and sending are three stages
    /// that used to happen one after another in a single loop, so a stream's period was the sum of all three and
    /// every slow frame of the encoder's was also a gap in the capture. Now each waits only on this.
    /// </summary>
    private readonly Channel<List<byte[]>> _outbound = Channel.CreateBounded<List<byte[]>>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    /// <summary>The encoder has been fed a frame it has not finished yet, so the next wait for one is a short one.</summary>
    private bool _awaitingEncoder;
    private long _awaitingSince;
    private Task? _loop;
    private Task? _sendLoop;

    /// <summary>The UDP path for this stream's pictures, or null where this node has no media socket.</summary>
    private VideoLink? _link;
    private long _refreshedTicks;
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

    // The pointer, sent on its own when the viewer draws it itself. Touched only by the pointer loop.
    private readonly bool _sendsPointer;
    private readonly Dictionary<nint, uint> _shapeIds = [];
    private Task? _pointerLoop;
    private uint _nextShapeId;
    private uint _sentShapeId;
    private float _sentX = -1;
    private float _sentY = -1;
    private bool _sentVisible;

    private ScreenSource(MirroringNode node, PeerSession session, uint streamId, StreamKind kind, WgcCapture capture, MonitorDescription? monitor, nint window, string title, ScreenSettings settings, bool useVideo, bool sendsPointer, ILogger logger)
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
        _sendsPointer = sendsPointer;
        _logger = logger;
        _pacing = new StreamPacing(settings.MaxFps);
        _touch = new Injection.TouchInjector(logger);
        _capture.Closed += OnCaptureClosed;
        _capture.CaptureCursor = !sendsPointer;

        // Made here rather than at the start because the stream is described to the viewer before it starts, and
        // the key for these datagrams travels in that description.
        _link = useVideo ? VideoLink.TryCreate(node, session, streamId, logger) : null;
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

    public static ScreenSource ForMonitor(MirroringNode node, PeerSession session, uint streamId, int monitorIndex, string? monitorKey, ScreenSettings settings, bool useVideo, bool sendsPointer, ILogger logger)
    {
        var monitor = PickMonitor(monitorIndex, monitorKey);
        return new ScreenSource(node, session, streamId, StreamKind.Monitor, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, MonitorTitle(monitor), settings, useVideo, sendsPointer, logger);
    }

    public static ScreenSource ForWindow(MirroringNode node, PeerSession session, uint streamId, nint window, ScreenSettings settings, bool useVideo, bool sendsPointer, ILogger logger)
    {
        var title = GetTitle(window);
        return new ScreenSource(node, session, streamId, StreamKind.Window, WgcCapture.ForWindow(window, logger), null, window, string.IsNullOrEmpty(title) ? "Окно" : title, settings, useVideo, sendsPointer, logger);
    }

    /// <summary>Streams the virtual monitor that extends this desktop onto the viewer.</summary>
    public static ScreenSource ForDisplay(MirroringNode node, PeerSession session, uint streamId, MonitorDescription monitor, ScreenSettings settings, bool useVideo, bool sendsPointer, ILogger logger) =>
        new(node, session, streamId, StreamKind.Display, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, $"Второй монитор {Environment.MachineName}", settings, useVideo, sendsPointer, logger);

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
        _monitor?.DeviceName ?? string.Empty,
        _link?.Key ?? string.Empty);

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
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
        if (_sendsPointer)
        {
            _pointerLoop = Task.Run(() => PointerLoopAsync(_cts.Token));
        }

        _logger.LogInformation(
            "Streaming {Title} to {Peer} (stream {Stream}){Pointer}",
            Title,
            Session.PeerName,
            StreamId,
            _sendsPointer ? "; the pointer travels on its own and is drawn there" : string.Empty);
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
        capture.CaptureCursor = !_sendsPointer;
        capture.Start();
        old.Dispose();
        Volatile.Write(ref _forceAll, 1);
    }

    /// <summary>A frame came back; <paramref name="viewerMs"/> is how long the viewer held it, or -1 if it did not say.</summary>
    public void OnAck(int viewerMs = -1)
    {
        _window.OnAck(viewerMs);
        _link?.Acknowledged();
    }

    /// <summary>The viewer is receiving this stream's UDP probes: its pictures move off the connection.</summary>
    public void OnUdpReady()
    {
        _link?.Confirm();

        // The first picture on the new path is a whole one. Not for the decoder's sake alone: a picture already on
        // the connection and one on UDP can pass each other, and a whole picture is read against nothing.
        Volatile.Write(ref _forceAll, 1);
    }

    /// <summary>
    /// The viewer lost a picture for good and cannot read the next ones against it, so it asks for a whole one.
    /// Throttled: several losses in a row are one answer, and a key frame is the most expensive thing a stream
    /// sends — answering every one of them would turn a bad moment into a worse one.
    /// </summary>
    public void OnRefreshRequested()
    {
        var now = Environment.TickCount64;
        if (now - _refreshedTicks < RefreshAtMost.TotalMilliseconds)
        {
            return;
        }

        _refreshedTicks = now;
        Volatile.Write(ref _forceAll, 1);
    }

    public void OnCrop(RectangleF crop)
    {
        lock (_gate)
        {
            _crop = ScreenCodec.Normalize(crop);
        }

        Volatile.Write(ref _forceAll, 1);
    }

    /// <summary>
    /// The viewer says how large it is actually drawing the picture. It is worth listening to: a device showing a
    /// full screen in a smaller window pays for every pixel it never shows — once in the decoder and once turning
    /// the result into pixels — and those two are the most expensive things it does per frame.
    /// </summary>
    public void OnViewerSize(Size size)
    {
        // This comes off the wire, so it is clamped here as well as rounded by the viewer.
        var wanted = new Size(Math.Clamp(size.Width, 0, 7680), Math.Clamp(size.Height, 0, 4320));
        lock (_gate)
        {
            if (_viewerSize == wanted)
            {
                return;
            }

            _viewerSize = wanted;
        }

        // Nothing is encoded until the screen changes, so without this a window resized over a still picture
        // would keep the old size until something moved.
        Volatile.Write(ref _forceAll, 1);
    }

    /// <summary>
    /// What size to encode this crop at: what the viewer says it is showing, fitted to the shape of the crop so
    /// the picture is not distorted, and never larger than the crop itself — making a picture bigger than it was
    /// captured would cost everything and show nothing. A viewer that has not said, or is showing it near enough
    /// its own size, gets the crop as it is.
    /// </summary>
    private Size OutputSize(Rectangle crop, bool canScale)
    {
        Size wanted;
        lock (_gate)
        {
            wanted = _viewerSize;
        }

        return FitOutput(crop, wanted, canScale);
    }

    /// <summary>The size rule itself, apart from the stream, so it can be read and tested on its own.</summary>
    internal static Size FitOutput(Rectangle crop, Size wanted, bool canScale)
    {
        var native = new Size(crop.Width, crop.Height);
        if (!canScale || wanted.Width < 16 || wanted.Height < 16 || crop.Width < 16 || crop.Height < 16)
        {
            return native;
        }

        var scale = Math.Min(wanted.Width / (double)crop.Width, wanted.Height / (double)crop.Height);
        if (scale >= 0.95)
        {
            return native; // near enough that scaling would cost more in sharpness than it saves
        }

        return new Size(
            Math.Max(16, Nv12Converter.Even((int)Math.Round(crop.Width * scale))),
            Math.Max(16, Nv12Converter.Even((int)Math.Round(crop.Height * scale))));
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

    /// <summary>
    /// A point on this device's screen → where it is in the streamed (cropped) picture, 0 to 1. The way back from
    /// <see cref="MapToScreen"/>, and what puts the pointer where it belongs on the viewer's picture. Outside 0 to 1
    /// means the point is not in what is being streamed at all.
    /// </summary>
    internal static (float X, float Y) MapFromScreen(Point screen, RectangleF crop, Size frame, Rectangle bounds)
    {
        var pixels = ScreenCodec.ToPixels(crop, frame.Width, frame.Height);
        if (pixels.Width <= 0 || pixels.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (-1, -1);
        }

        var frameX = (screen.X - bounds.Left) * (double)frame.Width / bounds.Width;
        var frameY = (screen.Y - bounds.Top) * (double)frame.Height / bounds.Height;
        return ((float)((frameX - pixels.X) / pixels.Width), (float)((frameY - pixels.Y) / pixels.Height));
    }

    /// <summary>
    /// Follows this device's pointer and sends it on its own, so the viewer can draw it at the rate a mouse moves
    /// rather than the rate frames arrive. Shapes are sent once each and kept there; a position is a dozen bytes.
    /// </summary>
    private async Task PointerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PointerEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SendPointerIfChangedAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("The pointer is no longer being sent: {Message}", ex.Message);
        }
    }

    private async Task SendPointerIfChangedAsync(CancellationToken ct)
    {
        if (!CursorReader.TryRead(out var shape, out var screen, out var showing))
        {
            return;
        }

        Size frame;
        RectangleF crop;
        Rectangle bounds;
        lock (_gate)
        {
            frame = _frameSize;
            crop = _crop;
            bounds = Kind != StreamKind.Window ? CurrentMonitorBoundsLocked() : GetVisibleBounds(Window) ?? Rectangle.Empty;
        }

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return; // nothing has been captured yet, so there is no picture to place a pointer on
        }

        var (x, y) = MapFromScreen(screen, crop, frame, bounds);
        var inside = x is >= 0 and <= 1 && y is >= 0 and <= 1;
        var visible = showing && inside;
        var shapeId = visible ? await ShapeIdAsync(shape, ct).ConfigureAwait(false) : _sentShapeId;

        // A tenth of a pixel of the picture is not a move anyone can see, and the pointer is read a hundred times
        // a second: without this a mouse resting on a desk would still be sending.
        var moved = Math.Abs(x - _sentX) * frame.Width >= 0.5 || Math.Abs(y - _sentY) * frame.Height >= 0.5;
        if (visible == _sentVisible && shapeId == _sentShapeId && (!visible || !moved))
        {
            return;
        }

        _sentVisible = visible;
        _sentShapeId = shapeId;
        _sentX = x;
        _sentY = y;
        await Session.SendAsync(ChannelId.Screen, ScreenCodec.EncodeCursor(StreamId, shapeId, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), visible), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The id this shape travels under, sending its pixels the first time it is seen. Zero means it could not be
    /// drawn at all, and the viewer then shows a pointer of its own rather than none.
    /// </summary>
    private async Task<uint> ShapeIdAsync(nint shape, CancellationToken ct)
    {
        if (_shapeIds.TryGetValue(shape, out var known))
        {
            return known;
        }

        var picture = CursorReader.TryDraw(shape);
        if (picture is null)
        {
            _shapeIds[shape] = 0;
            return 0;
        }

        // A handle is only as good as the shape it points at: a pointer that is destroyed and another made can be
        // handed the same handle, so the names are not kept for ever.
        if (_shapeIds.Count >= MaxShapes)
        {
            _shapeIds.Clear();
        }

        var id = ++_nextShapeId;
        _shapeIds[shape] = id;
        await Session.SendAsync(
            ChannelId.Screen,
            ScreenCodec.EncodeCursorShape(StreamId, id, picture.Width, picture.Height, picture.HotspotX, picture.HotspotY, picture.Pixels),
            ct).ConfigureAwait(false);
        return id;
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

                // Shorter while the encoder still owes a frame: it finishes one a few milliseconds after it is fed,
                // and that frame must not wait for the screen to change again — on a screen that has just stopped
                // changing, the next change may be a long time coming, and that frame is the one that shows what
                // the screen settled on.
                // ...and only for as long as an encoder could honestly still be working on it: an encoder that has
                // stopped answering must not leave this loop polling at a quarter of a millisecond for ever.
                var owed = _awaitingEncoder && Environment.TickCount64 - _awaitingSince < EncoderOwesFor.TotalMilliseconds;
                var fresh = await capture.WaitForFrameAsync(seen, owed ? EncoderWait : IdleWait, ct).ConfigureAwait(false);
                if (!fresh && _videoEncoder is { HasPendingOutput: true } pending)
                {
                    // The screen is still, but the encoder finished a frame after it was last asked: send it, or the
                    // last change would only appear with the next one.
                    _awaitingEncoder = false;
                    await QueueAsync(BuildVideoMessages(pending.TakePending()), ct).ConfigureAwait(false);
                    continue;
                }

                if (!fresh && (Volatile.Read(ref _forceAll) == 0 || seen == 0))
                {
                    continue;
                }

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

                // Handed to the send loop rather than sent here. The queue holds one, so this loop can be capturing
                // and the encoder working while the last frame is on the wire, and none of the three waits for
                // another except through that one place — which is also what stops this loop running ahead.
                await QueueAsync(messages, ct).ConfigureAwait(false);

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
        var videoCrop = new Rectangle(crop.X, crop.Y, Nv12Converter.Even(crop.Width), Nv12Converter.Even(crop.Height));
        if (videoCrop.Width < 16 || videoCrop.Height < 16)
        {
            return null; // too small for H.264 this frame; the tile path handles it
        }

        // Only the graphics card can scale on the way; when the processor is doing the conversion the crop is
        // encoded at its own size. Guessing here is safe: if the card turns out not to do it after all, the next
        // frame works it out again and the encoder is rebuilt once.
        var output = OutputSize(videoCrop, !_gpuFailed && capture.UsesSharedDevice);
        if (_videoEncoder is null || _videoEncoder.Width != output.Width || _videoEncoder.Height != output.Height)
        {
            _videoEncoder?.Dispose();
            _videoEncoder = H264VideoEncoder.TryCreate(output.Width, output.Height, _pacing.Fps, ScaleBitrate(output.Width, output.Height), _logger);
            if (_videoEncoder is null)
            {
                _logger.LogInformation("H.264 encoder unavailable for {Title}; using JPEG tiles", Title);
                _videoFailed = true;
                return null;
            }

            if (output.Width != videoCrop.Width || output.Height != videoCrop.Height)
            {
                _logger.LogInformation(
                    "{Title} is sent to {Peer} at {Width}×{Height}: that is the size it is shown at there",
                    Title, Session.PeerName, output.Width, output.Height);
            }

            force = true; // a fresh encoder emits a key frame anyway
        }

        // An encoder that has fallen behind is not given more to be behind on: the frame is dropped here instead,
        // and the next one carries a newer picture anyway. Without this the only thing that bounded how far it
        // could fall behind was its own queue, and each frame in that queue holds a picture in memory or a texture
        // on the card that cannot be written again while it sits there.
        if (_videoEncoder.Waiting >= MaxQueuedFrames)
        {
            Owed();
            if (force)
            {
                Volatile.Write(ref _forceAll, 1); // a full refresh that is dropped has to happen on the next frame
            }

            return BuildVideoMessages(_videoEncoder.TakePending());
        }

        IReadOnlyList<VideoAccessUnit> units;
        try
        {
            // Fed here, collected below: the two are deliberately not the same step.
            if (!TryFeedOnGpu(capture, videoCrop, output, force))
            {
                if (!capture.TryCopyLatest(ref frame, out var width, out _, out _))
                {
                    return [];
                }

                // The processor path cannot scale, so this only produces anything when the sizes already agree;
                // when they do not, the frame is skipped and the next one is encoded at the crop's own size.
                _videoEncoder.Feed(frame, width, videoCrop, force);
            }

            // Whatever the encoder has finished by now, which is normally the frame before this one: it works on
            // its own thread and takes a few milliseconds, and waiting here for the frame just fed is what made
            // this loop's period the sum of capturing, encoding and sending instead of the longest of them.
            units = _videoEncoder.TakePending();
            if (units.Count == 0)
            {
                Owed();
            }
            else
            {
                _awaitingEncoder = false;
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
    /// Hands the frame to the encoder without it ever leaving the graphics card: it is cropped and turned into NV12
    /// there and given to the encoder as a texture. False means this is not possible — then the frame is read back
    /// into memory and converted by the processor, exactly as before, and that stays the way for this stream.
    /// </summary>
    private bool TryFeedOnGpu(WgcCapture capture, Rectangle crop, Size output, bool force)
    {
        if (_gpuFailed || !capture.UsesSharedDevice || _videoEncoder is not { UsesSharedDevice: true } encoder)
        {
            return false;
        }

        var converter = _gpuConverter ??= GpuNv12Converter.TryCreate(_logger);
        if (converter is null || converter.NextTarget(output.Width, output.Height) is not { } target)
        {
            return GiveUpOnGpu();
        }

        if (!capture.WithLatestFrame(source => converter.TryConvert(source, crop, target)))
        {
            return GiveUpOnGpu();
        }

        encoder.FeedTexture(target, force);
        if (!_gpuReported)
        {
            _gpuReported = true;
            _logger.LogInformation("{Title} is encoded on the graphics card; frames are not copied through memory", Title);
        }

        return true;
    }

    /// <summary>The encoder owes a frame from now, which is what makes the next wait for one a short one.</summary>
    private void Owed()
    {
        if (!_awaitingEncoder)
        {
            _awaitingSince = Environment.TickCount64;
        }

        _awaitingEncoder = true;
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

    /// <summary>
    /// Hands finished messages to the send loop. Nothing is dropped on the way: a video frame is read against the
    /// one before it, so a frame missing from the middle of a stream is not a frame the viewer can do without. The
    /// queue is one deep instead, and a full queue holds the capture loop up — which is the right thing for it to
    /// wait on, since there is no point capturing faster than the link takes.
    /// </summary>
    private async Task QueueAsync(List<byte[]> messages, CancellationToken ct)
    {
        if (messages.Count > 0)
        {
            await _outbound.Writer.WriteAsync(messages, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends what the capture loop has finished with, one flow-control slot per message (a slot comes back with the
    /// viewer's Ack). Its own loop so that the time a frame spends on the wire is not also time the next frame is
    /// not being captured in.
    /// </summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var messages in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Here rather than anywhere else because everything this link does, probing included, seals
                // datagrams with one cipher, and a cipher serves one thread at a time.
                _link?.ProbeIfDue();

                foreach (var message in messages)
                {
                    // A slot stands for a picture on its way, whichever way it goes.
                    await _window.TakeAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _framesSent);
                    Interlocked.Add(ref _bytesSent, message.Length);
                    if (!TrySendByUdp(message))
                    {
                        await Session.SendAsync(ChannelId.Screen, message, ct).ConfigureAwait(false);
                    }
                }
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
            _logger.LogError(ex, "Screen stream {Stream} stopped sending", StreamId);
            End("error");
        }
    }

    /// <summary>
    /// Sends one message as UDP datagrams if the picture it holds can go that way and the path is working. The
    /// picture is read straight out of the message rather than built again: the two carry the same bytes, and the
    /// connection is still where this one goes if UDP will not take it.
    /// </summary>
    private bool TrySendByUdp(byte[] message)
    {
        if (_link is not { Carries: true } link
            || !ScreenCodec.TryReadVideoFrame(message, out var frameNo, out var width, out var height, out var keyframe, out var offset))
        {
            return false;
        }

        return link.TrySend(frameNo, width, height, keyframe, message.AsSpan(offset));
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
        var reading = _window.Reading;
        _pacing.Observe(reading, _settings.MaxFps, now);
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

            // Taken away rather than read and cleared: the send loop is adding to them from its own thread.
            var frames = Interlocked.Exchange(ref _framesSent, 0);
            var bytes = Interlocked.Exchange(ref _bytesSent, 0);
            _logger.LogInformation(
                "Stream {Stream} to {Peer}: {Codec} over {Path}, {Rate:F1} messages/s, {Kbps:F0} kbit/s at {Share:P0} "
                + "of the configured bitrate{Held}, {Fps} fps asked for, bitrate moved {Changes} times, acknowledged "
                + "in {Delay:F0} ms of which the viewer spent {Viewer:F0} ms, {Late} of {Acks} late, {GivenUp} not "
                + "waited for, {InFlight} in flight",
                StreamId,
                Session.PeerName,
                Codec,
                _link is { Carries: true } carrying ? $"UDP ({carrying.Datagrams} datagrams)" : "the connection",
                frames / seconds,
                bytes * 8 / 1000.0 / seconds,
                _pacing.Share,
                _pacing.HeldBack ? " (held back)" : string.Empty,
                fps,
                _pacing.Changes - _reportedChanges,
                reading.DelayMs,
                reading.ViewerMs,
                reading.LateAcks - _reportedLate,
                reading.Acks - _reportedAcks,
                reading.GivenUp,
                _window.InFlight);
            _reportedTicks = now;
            _reportedChanges = _pacing.Changes;
            _reportedAcks = reading.Acks;
            _reportedLate = reading.LateAcks;
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
        _outbound.Writer.TryComplete();
        foreach (var running in new[] { _loop, _sendLoop, _pointerLoop })
        {
            if (running is null)
            {
                continue;
            }

            try
            {
                await running.ConfigureAwait(false);
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

        _link?.Dispose();
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
