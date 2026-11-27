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
    /// How often the collect loop looks for a finished picture. Two milliseconds is a tenth of a frame at sixty a
    /// second: cheap enough not to matter and short enough that a picture is on the wire as soon as it exists.
    /// </summary>
    private static readonly TimeSpan CollectEvery = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// The same look on a battery. Five hundred wake-ups a second is what two milliseconds costs while a stream
    /// runs; eight is a hundred and twenty-five, and what it buys back is up to six milliseconds of a picture's
    /// age — which is the trade this mode exists to make.
    /// </summary>
    private static readonly TimeSpan CollectOnBattery = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// How still the screen has to be before one whole, sharp picture is sent. Long enough that it does not fire
    /// between the frames of something moving, short enough that it is already there by the time anyone has
    /// finished moving their eyes to what they were dragging.
    /// </summary>
    private static readonly TimeSpan SettleAfter = TimeSpan.FromMilliseconds(180);

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

    /// <summary>The pointer on a battery: sixty positions a second instead of a hundred and twenty-five.</summary>
    private static readonly TimeSpan PointerOnBattery = TimeSpan.FromMilliseconds(16);

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

    /// <summary>
    /// How long the last picture of a burst may go unanswered, on UDP, before a whole one is sent after it. A
    /// picture lost in the middle of a stream is noticed by the viewer, because the next one arrives and says so.
    /// The last one before the screen stops changing has no next one: if it is lost, nothing tells anyone, and the
    /// viewer is left looking at the screen as it was one change ago — a typed letter that does not appear, a menu
    /// that does not open — until something else moves. Comfortably past a real round trip, and no longer.
    /// </summary>
    private const double TailLossAfterMs = 100;

    /// <summary>...and how long before the same picture is simply sent a second time, which is tried first.</summary>
    private const double TailResendAfterMs = 30;
    private uint _tailResent;

    private readonly ScreenSettings _settings;
    private readonly ILogger _logger;
    private readonly InputInjector _injector = new();
    private readonly Injection.TouchInjector _touch;
    private readonly TileEncoder _encoder = new();
    private readonly SendWindow _window = new(MaxInFlight, AckGrace, LateAbove);
    private readonly StreamPacing _pacing;
    private readonly WinMirroring.Core.Power.PowerMode _power;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private long _lastBlockedReport = -10_000;
    private long _monitorCheckedTicks;

    /// <summary>How often the viewer is told how the stream is doing (see ScreenStatsMessage).</summary>
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(1);

    private long _statsTicks;
    private long _statsFrames;
    private long _statsBytes;
    private double _statsEncoderMs;
    private int _reportNow;

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

    /// <summary>
    /// Held around every touch of <see cref="_videoEncoder"/>. The capture loop owns it — it makes one, feeds it and
    /// gives it up when the picture changes size — while the collect loop reads what it has finished, so the two
    /// have to agree on when it exists.
    /// </summary>
    private readonly object _encoderGate = new();

    /// <summary>When the frame the encoder is working on was handed to it, for the capture-to-wire measurement.</summary>
    private long _fedAt;
    private long _heldTicks;
    private long _heldFrames;

    // When the screen last changed, and whether the one sharp picture for this quiet spell has been sent.
    private long _lastFreshTicks;
    private bool _settled;
    private Task? _loop;
    private Task? _collectLoop;
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

    /// <summary>The name the viewer gave the part of the screen it last asked for (see ScreenCodec.EncodeCrop).</summary>
    private volatile byte _cropId;

    /// <summary>
    /// The name of the part each frame handed to the encoder shows, in the order they were handed over, so that
    /// what comes out can be given the name of what went in: a frame is collected a few milliseconds after it was
    /// fed, and the part asked for may have changed in between. Touched only under <see cref="_encoderGate"/>.
    /// </summary>
    private readonly Queue<byte> _fedTags = new();

    /// <summary>The frame at the size it is sent at, when the processor is the one making it smaller.</summary>
    private byte[] _scaled = [];
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
        _power = node.Power;
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
        _link?.Key ?? string.Empty,
        CropTags: true,
        MediaReplies: _link is not null);

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
        _collectLoop = Task.Run(() => CollectLoopAsync(_cts.Token));
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
        if (_sendsPointer)
        {
            _pointerLoop = Task.Run(() => PointerLoopAsync(_cts.Token));
        }

        _foregroundLoop = Task.Run(() => ForegroundLoopAsync(_cts.Token));

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
            _cropId = 0;
            Title = MonitorTitle(monitor);
        }

        old.Closed -= OnCaptureClosed;
        capture.Closed += OnCaptureClosed;
        capture.CaptureCursor = !_sendsPointer;
        capture.Start();
        old.Dispose();
        Volatile.Write(ref _forceAll, 1);
    }

    /// <summary>
    /// Frame <paramref name="frameNo"/> came back; <paramref name="viewerMs"/> is how long the viewer held it, or
    /// -1 if it did not say.
    /// </summary>
    public void OnAck(uint frameNo, int viewerMs = -1, int paintMs = -1)
    {
        _window.OnAck(frameNo, viewerMs, paintMs);
        _link?.Acknowledged();
    }

    /// <summary>
    /// A datagram from the viewer: acknowledgements, which are acted on exactly as the ones that come on the
    /// connection are (the same one arriving both ways is counted once), or a request for pieces of a picture,
    /// which the link answers itself.
    /// </summary>
    public void OnMediaPacket(ReadOnlySpan<byte> packet)
    {
        if (_link is not { } link)
        {
            return;
        }

        var acks = new List<ViewerReplies.Ack>(ViewerReplies.AcksPerDatagram);
        if (link.Receive(packet, acks))
        {
            foreach (var ack in acks)
            {
                OnAck(ack.FrameNo, ack.ViewerMs, ack.PaintMs);
            }
        }
    }

    /// <summary>The viewer is receiving this stream's UDP probes: its pictures move off the connection.</summary>
    public void OnUdpReady()
    {
        if (_link?.Confirm() != true)
        {
            return; // an answer to probes nobody is waiting on any more; nothing changed
        }

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

    public void OnCrop(RectangleF crop, byte id = 0)
    {
        lock (_gate)
        {
            _crop = ScreenCodec.Normalize(crop);
            _cropId = id;
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
        else if (touchAction == Injection.TouchInjector.Action.Up)
        {
            // Whoever watches for text boxes being tapped looks at where the focus went once the tap is over.
            RemoteInputTracker.NoteTouchEnded(Session.PeerId, StreamId);
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
        if (!up)
        {
            RemoteInputTracker.NoteKey();
        }

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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_power.Every(PointerEvery, PointerOnBattery), ct).ConfigureAwait(false);
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

                // Blocked here, not on a clock. This loop used to finish its work and then sleep out the rest of a
                // frame's period, which meant a frame arriving during that sleep sat untouched for however much of
                // it was left — up to a whole period of the picture's age for nothing, since the frame was going to
                // be encoded anyway. What paces the capture is the capture: the screen produces a frame when it is
                // composed, and that is the soonest anyone could have it.
                var fresh = await capture.WaitForFrameAsync(seen, IdleWait, ct).ConfigureAwait(false);

                if (fresh)
                {
                    _lastFreshTicks = Environment.TickCount64;
                    _settled = false;
                }
                else if (!_settled && seen != 0 && Environment.TickCount64 - _lastFreshTicks >= SettleAfter.TotalMilliseconds)
                {
                    // The screen has stopped moving. What is on it now was encoded while it was moving, which is
                    // where a codec spends the least on detail — so the picture sitting in front of someone is the
                    // blurriest one of the whole burst, and it is the one they are going to read. One whole picture
                    // puts that right, and now is when it is free: nothing is competing for the link, and with no
                    // key frames on a clock any more (see H264VideoEncoder's group of pictures) this is the only
                    // one a quiet stream sends.
                    _settled = true;
                    Volatile.Write(ref _forceAll, 1);
                }

                // Nothing new on the screen and a picture still unanswered: on UDP that is what a lost last picture
                // looks like from here (see TailLossAfterMs). A whole one goes after it — and again, no more often
                // than whole pictures are ever sent, until one is answered.
                if (!fresh && seen != 0 && _link is { Carries: true } carrying)
                {
                    var waited = _window.OldestAgeMs;
                    if (waited > Math.Max(TailLossAfterMs, _window.DelayMs * 3))
                    {
                        OnRefreshRequested();
                    }
                    else if (waited > Math.Max(TailResendAfterMs, _window.DelayMs * 2)
                        && _window.OldestFrame is { } unanswered
                        && unanswered != _tailResent)
                    {
                        // Sooner than that, and cheaper: the same picture again. If it was the picture that was
                        // lost this puts it right without a whole one; if it was only its acknowledgement, the
                        // viewer has it already and the copy changes nothing.
                        _tailResent = unanswered;
                        carrying.Resend(unanswered, []);
                    }
                }

                // Here rather than further down, so that the measurements and the stream's own line in the log keep
                // coming on a screen that is standing still.
                var minimumMs = 1000.0 / Observe();

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

                // The cap, applied by leaving a frame out rather than by sleeping. Sleeping would make the next
                // frame older without saving any work; skipping one costs the work and keeps every frame that is
                // taken as young as the capture can make it. A tolerance, because at sixty a second against a
                // sixty-hertz screen the two periods are the same number and a hair of jitter either way must not
                // start dropping every other frame.
                if (fresh && seen != 0 && pacing.Elapsed.TotalMilliseconds < minimumMs * 0.75)
                {
                    seen = version;
                    continue;
                }

                pacing.Restart();
                seen = version;
                var force = Interlocked.Exchange(ref _forceAll, 0) == 1;
                Rectangle crop;
                RectangleF cropFraction;
                byte cropId;
                lock (_gate)
                {
                    _frameSize = new Size(width, height);
                    cropFraction = _crop;
                    cropId = _cropId;
                    crop = ScreenCodec.ToPixels(cropFraction, width, height);
                }

                if (_tilesRequested is { } reason && _useVideo && !_videoFailed)
                {
                    _logger.LogInformation("{Peer} gets no picture from H.264 ({Reason}); sending JPEG tiles", Session.PeerName, reason);
                    _videoFailed = true;
                    lock (_encoderGate)
                    {
                        _videoEncoder?.Dispose();
                        _videoEncoder = null;
                    }

                    _gpuConverter?.Dispose();
                    _gpuConverter = null;
                    force = true;
                    _ = Session.SendControlAsync(Describe());
                }

                var fedVideo = false;
                if (_useVideo && !_videoFailed)
                {
                    // The encoder belongs to this loop but is read by the one that collects from it, so every touch
                    // of it — making one, feeding it, giving it up — happens here.
                    lock (_encoderGate)
                    {
                        fedVideo = TryFeedVideo(capture, ref frame, width, crop, force, cropId);
                    }

                    if (_videoFailed)
                    {
                        force = true; // just fell back to tiles: make the first JPEG frame complete
                    }
                }

                if (!fedVideo)
                {
                    if (!capture.TryCopyLatest(ref frame, out width, out height, out _))
                    {
                        continue;
                    }

                    // Tiles are made here and handed straight over; what the encoder makes of a video frame is
                    // collected by a loop of its own, as soon as it exists (see CollectLoopAsync).
                    await QueueAsync(BuildJpegMessages(frame, width, height, ScreenCodec.ToPixels(cropFraction, width, height), force), ct).ConfigureAwait(false);
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
            _logger.LogError(ex, "Screen stream {Stream} failed", StreamId);
            End("error");
        }
    }

    /// <summary>
    /// Hands one frame to the H.264 encoder. False means "use JPEG for this frame" — because the crop is too small
    /// for video, or because the encoder could not be created (which also sets <see cref="_videoFailed"/> so the
    /// whole stream stays on tiles from now on). What the encoder makes of the frame is collected by
    /// <see cref="CollectLoopAsync"/> as soon as it exists, which is not this loop's business.
    /// </summary>
    private bool TryFeedVideo(WgcCapture capture, ref byte[] frame, int frameWidth, Rectangle crop, bool force, byte cropId)
    {
        var videoCrop = new Rectangle(crop.X, crop.Y, Nv12Converter.Even(crop.Width), Nv12Converter.Even(crop.Height));
        if (videoCrop.Width < 16 || videoCrop.Height < 16)
        {
            return false; // too small for H.264 this frame; the tile path handles it
        }

        // Whichever of the two is converting can scale on the way: the graphics card as part of the conversion,
        // the processor just before it (see BgraScaler). It used to be the card or nothing, and a card that is not
        // used for conversion — this one's colours are not the ones the two ends agreed on — meant every frame
        // went out at the screen's own size for the viewer to shrink.
        var output = OutputSize(videoCrop, canScale: true);
        if (_videoEncoder is null || _videoEncoder.Width != output.Width || _videoEncoder.Height != output.Height)
        {
            _videoEncoder?.Dispose();
            _fedTags.Clear();
            _videoEncoder = H264VideoEncoder.TryCreate(output.Width, output.Height, _pacing.Fps, ScaleBitrate(output.Width, output.Height), _logger);
            if (_videoEncoder is null)
            {
                _logger.LogInformation("H.264 encoder unavailable for {Title}; using JPEG tiles", Title);
                _videoFailed = true;
                return false;
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
            if (force)
            {
                Volatile.Write(ref _forceAll, 1); // a full refresh that is dropped has to happen on the next frame
            }

            return true;
        }

        try
        {
            if (!TryFeedOnGpu(capture, videoCrop, output, force, cropId))
            {
                if (!capture.TryCopyLatest(ref frame, out var width, out _, out _))
                {
                    return true; // nothing to hand over this turn; the next frame carries a newer picture
                }

                if (output.Width == videoCrop.Width && output.Height == videoCrop.Height)
                {
                    Fed(_videoEncoder.Feed(frame, width, videoCrop, force), cropId);
                }
                else
                {
                    // Made smaller first, in a buffer kept for the purpose, and handed over as a whole picture.
                    var needed = output.Width * output.Height * 4;
                    if (_scaled.Length != needed)
                    {
                        _scaled = new byte[needed];
                    }

                    BgraScaler.Scale(frame, width, videoCrop, _scaled, output.Width, output.Height);
                    Fed(_videoEncoder.Feed(_scaled, output.Width, new Rectangle(0, 0, output.Width, output.Height), force), cropId);
                }
            }

            _fedAt = Stopwatch.GetTimestamp();
            return true;
        }
        catch (Exception ex)
        {
            if (capture.IsDisposed)
            {
                // Not the encoder's failure: the capture was closed under this frame by a switch of monitors.
                _logger.LogDebug("A frame of {Title} was dropped as its capture closed: {Message}", Title, ex.Message);
                return true;
            }

            // With the exception, not just its message: this has happened once in the wild (while two monitors were
            // being streamed at once) and dropped a stream to tiles, and a message alone says nothing about where.
            _logger.LogWarning(ex, "H.264 encode failed for {Title}; using JPEG tiles", Title);
            _videoEncoder.Dispose();
            _videoEncoder = null;
            _gpuConverter?.Dispose();
            _gpuConverter = null;
            _videoFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Hands the frame to the encoder without it ever leaving the graphics card: it is cropped and turned into NV12
    /// there and given to the encoder as a texture. False means this is not possible — then the frame is read back
    /// into memory and converted by the processor, exactly as before, and that stays the way for this stream.
    /// </summary>
    private bool TryFeedOnGpu(WgcCapture capture, Rectangle crop, Size output, bool force, byte cropId)
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
            if (capture.IsDisposed)
            {
                // The view switched monitors and this capture was closed under us. That is not the card refusing:
                // there is nothing to convert this turn, and the next turn has the new capture.
                return true;
            }

            return GiveUpOnGpu();
        }

        Fed(encoder.FeedTexture(target, force), cropId);
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

    /// <summary>Remembers which part of the screen the frame just handed to the encoder shows, if it took it.</summary>
    private void Fed(bool taken, byte cropId)
    {
        if (taken)
        {
            _fedTags.Enqueue(cropId);
        }
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
            // One picture out for each frame in, in order, so the name at the head of the line is this one's.
            var cropId = _fedTags.TryDequeue(out var fed) ? fed : _cropId;
            messages.Add(ScreenCodec.EncodeVideoFrame(StreamId, ++_frameNo, encoder.Width, encoder.Height, unit.Keyframe, unit.Data, cropId));
        }

        // If the encoder ever swallowed a frame without making a picture of it, the line would be one name too
        // long from then on, and every later picture would carry the name of the one before it. More names than
        // frames it can still be holding means that happened: the oldest are dropped.
        while (_fedTags.Count > encoder.Waiting + 2)
        {
            _fedTags.Dequeue();
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
    /// Takes each encoded picture from the encoder the moment it exists, rather than on the capture loop's next
    /// turn. Measured on this encoder: it holds a 1080p frame for about twenty-one milliseconds and a smaller one
    /// for ten, while the capture loop comes round every sixteen or seventeen — so a frame collected by that loop
    /// had been sitting finished for anything up to a whole period, which is pure age added to a picture for
    /// nothing. This is the shortest of the three loops and the only one whose job is to not wait.
    /// </summary>
    private async Task CollectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<byte[]>? messages = null;
                long fedAt;
                lock (_encoderGate)
                {
                    if (_videoEncoder is { HasPendingOutput: true } encoder)
                    {
                        messages = BuildVideoMessages(encoder.TakePending());
                    }

                    fedAt = _fedAt;
                }

                if (messages is { Count: > 0 })
                {
                    Interlocked.Add(ref _heldTicks, Stopwatch.GetTimestamp() - fedAt);
                    Interlocked.Increment(ref _heldFrames);
                    await QueueAsync(messages, ct).ConfigureAwait(false);
                    continue;
                }

                // Nothing ready. Looked at rather than waited on: the encoder's own signal would have to outlive
                // the encoder, which is replaced whenever the picture changes size, and two milliseconds of age is
                // a cheaper thing to pay than that is to get right.
                await Task.Delay(_power.Every(CollectEvery, CollectOnBattery), ct).ConfigureAwait(false);
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
            _logger.LogError(ex, "Screen stream {Stream} stopped collecting", StreamId);
            End("error");
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
                    // A slot stands for a picture on its way, whichever way it goes. It is taken under the
                    // picture's own number (both kinds of frame carry it in the same place), so that the
                    // acknowledgement that comes back is matched to this picture and not to whichever is oldest.
                    var frameNo = message.Length >= 9 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(5)) : 0;
                    await _window.TakeAsync(frameNo, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _framesSent);
                    Interlocked.Add(ref _bytesSent, message.Length);
                    Interlocked.Increment(ref _statsFrames);
                    Interlocked.Add(ref _statsBytes, message.Length);
                    var carried = _link is { Carries: true };
                    if (!TrySendByUdp(message))
                    {
                        if (carried && _link is { Carries: false })
                        {
                            // The path has just been given up on, which means the pictures sent on it lately did
                            // not arrive: what goes on the connection from here would be read against pictures the
                            // viewer never saw. The next one is a whole one.
                            Volatile.Write(ref _forceAll, 1);
                        }

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

        return link.TrySend(frameNo, width, height, keyframe, message, offset, ScreenCodec.VideoCropId(message));
    }

    /// <summary>
    /// Takes the link's measurements, moves the frame rate and bitrate targets and returns the frame rate to pace
    /// this stream at. Also writes the stream's own line to the log every ten seconds: when a view stutters, the
    /// numbers that say why — how long acknowledgements take, how many frames the window stopped waiting for, what
    /// is actually going out — should already be in the log rather than having to be guessed at afterwards.
    /// </summary>
    /// <summary>
    /// The most frames a second this stream will send: what this device is set to, lowered while it is saving
    /// power, and lowered again to what the device watching asked for — which it does when <em>it</em> is the one
    /// on a battery. Half the frames is half the capturing, encoding and sending here and half the decoding and
    /// painting there, so either end being on a battery is reason enough.
    /// </summary>
    private int EffectiveMaxFps
    {
        get
        {
            var here = _power.Fps(_settings.MaxFps);
            return ViewerFps is { } asked && asked > 0 ? Math.Min(here, asked) : here;
        }
    }

    /// <summary>Frames a second the viewer asked for, or null when it asked for nothing in particular.</summary>
    public int? ViewerFps { get; set; }

    private int Observe()
    {
        var now = Environment.TickCount64;
        var reading = _window.Reading;
        _pacing.Observe(reading, EffectiveMaxFps, now);
        var fps = _pacing.Fps;
        lock (_encoderGate)
        {
            if (_videoEncoder is { } encoder)
            {
                encoder.Retune(fps, ScaleBitrate(encoder.Width, encoder.Height));
            }
        }

        TellTheViewer(now, reading, fps);
        if (_reportedTicks == 0)
        {
            _reportedTicks = now;
        }
        else if (now - _reportedTicks >= ReportEvery.TotalMilliseconds || Interlocked.Exchange(ref _reportNow, 0) == 1)
        {
            var seconds = (now - _reportedTicks) / 1000.0;

            // Taken away rather than read and cleared: the send loop is adding to them from its own thread.
            var frames = Interlocked.Exchange(ref _framesSent, 0);
            var bytes = Interlocked.Exchange(ref _bytesSent, 0);
            // The whole journey of a picture, in the order it happens: how long the encoder held it, how long from
            // the wire to the acknowledgement, how much of that was the viewer decoding and how much its window
            // painting. Added up, that is the delay between something moving on one screen and moving on the other
            // — which is the thing to argue with, and it used to be guessed at.
            var encoderMs = Interlocked.Exchange(ref _heldFrames, 0) is var held and > 0
                ? Interlocked.Exchange(ref _heldTicks, 0) * 1000.0 / Stopwatch.Frequency / held
                : 0;
            if (encoderMs > 0)
            {
                _statsEncoderMs = encoderMs;
            }

            _logger.LogInformation(
                "Stream {Stream} to {Peer}: {Codec} over {Path}, {Rate:F1} messages/s, {Kbps:F0} kbit/s at {Share:P0} "
                + "of the configured bitrate{Held}, {Fps} fps asked for, bitrate moved {Changes} times, "
                + "{Encoder:F0} ms in the encoder, acknowledged in {Delay:F0} ms of which the viewer spent "
                + "{Viewer:F0} ms decoding and {Paint:F0} ms painting, {Late} of {Acks} late, {GivenUp} not waited "
                + "for, {InFlight} in flight",
                StreamId,
                Session.PeerName,
                Codec,
                _link is { Carries: true } carrying ? $"UDP ({carrying.Datagrams} datagrams, {carrying.Resent} sent again)" : "the connection",
                frames / seconds,
                bytes * 8 / 1000.0 / seconds,
                _pacing.Share,
                _pacing.HeldBack ? " (held back)" : string.Empty,
                fps,
                _pacing.Changes - _reportedChanges,
                encoderMs,
                reading.DelayMs,
                reading.ViewerMs,
                reading.PaintMs,
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

    /// <summary>Once a second, the numbers the viewer cannot measure for itself (see ScreenStatsMessage).</summary>
    private void TellTheViewer(long now, LinkReading reading, int fps)
    {
        if (_statsTicks == 0)
        {
            _statsTicks = now;
            return;
        }

        var elapsed = now - _statsTicks;
        if (elapsed < StatsEvery.TotalMilliseconds)
        {
            return;
        }

        _statsTicks = now;
        var seconds = elapsed / 1000.0;
        var frames = Interlocked.Exchange(ref _statsFrames, 0);
        var bytes = Interlocked.Exchange(ref _statsBytes, 0);

        // The encoder's time is averaged over the ten-second report; between reports the last one stands, and
        // before the first there is the current average.
        var heldFrames = Interlocked.Read(ref _heldFrames);
        var encoderMs = heldFrames > 0 ? Interlocked.Read(ref _heldTicks) * 1000.0 / Stopwatch.Frequency / heldFrames : _statsEncoderMs;
        var stats = new ScreenStatsMessage(
            StreamId,
            (int)Math.Round(reading.DelayMs),
            (int)Math.Round(reading.ViewerMs),
            (int)Math.Round(encoderMs),
            (int)Math.Round(bytes * 8 / 1000.0 / seconds),
            Math.Round(frames / seconds, 1),
            _link is { Carries: true },
            (int)Math.Round(_pacing.Share * 100),
            reading.GivenUp,
            _link?.Resent ?? 0);
        _ = SendQuietlyAsync(stats);
    }

    private async Task SendQuietlyAsync(ControlMessage message)
    {
        try
        {
            await Session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The stream's own loops notice a connection that has gone; this is only telling.
        }
    }

    // ---- which program is in front ---------------------------------------------------------------------

    /// <summary>How often the program in front is looked at. A change of window is a human-speed event.</summary>
    private static readonly TimeSpan ForegroundEvery = TimeSpan.FromMilliseconds(400);

    private Task? _foregroundLoop;

    /// <summary>
    /// Tells the viewer which program is in front here, when the stream starts and whenever it changes — so that
    /// the buttons it shows can be the ones for that program (see ScreenForegroundMessage).
    /// </summary>
    private async Task ForegroundLoopAsync(CancellationToken ct)
    {
        uint knownProcess = 0;
        var said = string.Empty;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var window = Kind == StreamKind.Window ? Window : GetForegroundWindow();
                if (window != 0 && GetWindowThreadProcessId(window, out var processId) != 0 && processId != 0 && processId != knownProcess)
                {
                    knownProcess = processId;
                    var name = ProcessName(processId);
                    if (name.Length > 0 && !string.Equals(name, said, StringComparison.OrdinalIgnoreCase))
                    {
                        said = name;
                        await Session.SendControlAsync(new ScreenForegroundMessage(StreamId, name), ct).ConfigureAwait(false);
                    }
                }

                await Task.Delay(ForegroundEvery, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty; // it has gone already, or is one this process may not look at
        }
    }

    // ---- small pictures of the other monitors ------------------------------------------------------------

    /// <summary>How often each of them is taken while the viewer wants them.</summary>
    private static readonly TimeSpan ThumbsEvery = TimeSpan.FromMilliseconds(1500);

    /// <summary>Asked for again within this long, or they stop: a viewer that has gone must not be sent them for ever.</summary>
    private static readonly TimeSpan ThumbsWantedFor = TimeSpan.FromSeconds(6);

    private long _thumbsWantedUntil;
    private int _thumbsRunning;

    /// <summary>The viewer wants (or no longer wants) pictures of this device's other monitors.</summary>
    public void OnThumbsWanted(bool on)
    {
        if (!on || Kind != StreamKind.Monitor)
        {
            Volatile.Write(ref _thumbsWantedUntil, 0);
            return;
        }

        Volatile.Write(ref _thumbsWantedUntil, Environment.TickCount64 + (long)ThumbsWantedFor.TotalMilliseconds);
        if (Interlocked.Exchange(ref _thumbsRunning, 1) == 0)
        {
            _ = Task.Run(() => ThumbsLoopAsync(_cts.Token));
        }
    }

    private async Task ThumbsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Environment.TickCount64 < Volatile.Read(ref _thumbsWantedUntil))
            {
                var shown = _monitor?.DeviceName;
                foreach (var monitor in DisplayCatalog.Monitors())
                {
                    if (string.Equals(monitor.DeviceName, shown, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // the one being watched is on the viewer's screen already, at full size
                    }

                    if (MonitorThumbnail.Capture(monitor.Bounds, out var width, out var height) is { } jpeg)
                    {
                        await Session.SendControlAsync(new ScreenThumbMessage(StreamId, monitor.DeviceName, width, height, jpeg), ct).ConfigureAwait(false);
                    }
                }

                await Task.Delay(ThumbsEvery, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Small pictures of the other monitors stopped: {Message}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _thumbsRunning, 0);
        }
    }

    /// <summary>
    /// The person watching says the stream hitched just now. Said in the log at once, and the stream's full line
    /// is written straight after it rather than whenever its ten seconds are next up.
    /// </summary>
    public void OnMark(string note)
    {
        var reading = _window.Reading;
        _logger.LogWarning(
            "HITCH marked by the viewer of {Title} on {Peer} (stream {Stream}): {Note}. Just now: acknowledged in "
            + "{Delay:F0} ms of which the viewer {Viewer:F0} ms, {InFlight} in flight, oldest unanswered for "
            + "{Oldest:F0} ms, {GivenUp} not waited for so far, path {Path}",
            Title, Session.PeerName, StreamId, note, reading.DelayMs, reading.ViewerMs, _window.InFlight,
            _window.OldestAgeMs, reading.GivenUp, _link is { Carries: true } ? "UDP" : "the connection");
        Interlocked.Exchange(ref _reportNow, 1);
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
    /// Full-screen bitrate scaled down for smaller pictures and again by what the link is currently willing to
    /// carry (see <see cref="StreamPacing"/>).
    ///
    /// Down by the square root of the area, not by the area. A small picture is small because somebody is looking
    /// at one part of a screen — a window, or a zoomed-in corner — and is looking at it closely: by the area, a
    /// quarter of the screen got a quarter of the bits and every pixel of it exactly what it had before, so
    /// zooming in on a blurred picture showed the same blur larger. By the root it gets half, which is twice as
    /// much for each pixel that is actually on the viewer's screen, and still half of what the whole would cost.
    /// </summary>
    private int ScaleBitrate(int width, int height)
    {
        var baseKbps = Math.Clamp(_settings.VideoBitrateKbps, 500, 100_000);
        var fraction = Math.Clamp(Math.Sqrt(width * (double)height / (1920 * 1080)), 0.25, 1.0);
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
        foreach (var running in new[] { _loop, _collectLoop, _sendLoop, _pointerLoop, _foregroundLoop })
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
