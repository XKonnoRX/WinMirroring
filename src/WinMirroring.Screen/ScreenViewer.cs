using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Media;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen;

/// <summary>Changed part of the shared image after one frame message; the UI copies these rectangles.</summary>
/// <remarks>
/// With <paramref name="Surface"/> set, the picture is not in <paramref name="Image"/> (which is then empty) but on
/// the graphics card, in that surface, and the window draws it from there. It is the window's until it calls
/// <see cref="ScreenViewer.FramePresented"/> — and stays untouched after that for as long as it is the last one
/// the window was given.
/// </remarks>
public sealed record FrameUpdate(uint FrameNo, int Width, int Height, byte[] Image, IReadOnlyList<Rectangle> Dirty, bool Resized, byte CropId = 0, GpuSurface? Surface = null);

/// <summary>
/// Viewer side of one stream, independent of the UI. Decodes frames into a persistent BGRA image and hands each
/// update to the UI.
///
/// A frame is acknowledged as soon as it has been decoded, before the window has painted anything: the
/// acknowledgement is the source's flow control, and tying it to the window manager meant that any hiccup here — a
/// layout pass, a garbage collection, the window hidden or merely busy — stopped the source from capturing at all.
/// The window now gets whatever it can keep up with: while it still holds the buffer it was given, frames are
/// decoded but not handed over, so the picture gets older rather than the stream stopping. Pointer, keyboard and
/// crop requests go back in order.
/// </summary>
public sealed class ScreenViewer : IAsyncDisposable
{
    /// <summary>
    /// After this long, a window that has not said it finished painting is assumed not to be coming back, and the
    /// next frame is handed over anyway. Only a safety net: the picture would otherwise stay frozen for good.
    /// </summary>
    private static readonly TimeSpan PresentGrace = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    /// <summary>One frame message and when it got here, so its acknowledgement can say how long it was held.</summary>
    private readonly record struct Incoming(byte[] Message, long Arrived);

    private readonly ILogger _logger;
    private readonly Channel<Incoming> _frames = Channel.CreateUnbounded<Incoming>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TileDecoder _decoder = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _timer;
    private Action<FrameUpdate>? _present;
    private H264VideoDecoder? _videoDecoder;
    private bool _videoDecodeUnavailable;

    /// <summary>The graphics card was tried for this stream and let it down; from then on the processor decodes.</summary>
    private bool _decodeInMemory;

    /// <summary>The picture the decoder writes into.</summary>
    private byte[] _image = [];

    /// <summary>The buffer to decode into next, once the window takes <see cref="_image"/> off our hands.</summary>
    private byte[] _spare = [];

    private int _width;
    private int _height;
    private int _closed;

    /// <summary>When the buffer the window holds was handed to it, or 0 while it holds none.</summary>
    private long _presentingSince;

    /// <summary>The same moment on a stopwatch, and how long the last painting took, for the acknowledgement.</summary>
    private long _presentStarted;
    private int _paintMs = -1;

    /// <summary>A frame was decoded but not shown, so the next one the window takes must redraw everything.</summary>
    private bool _missedFrames;

    // Counters, touched by the decode loop only (and read from anywhere).
    private long _reportedTicks;
    private long _decoded;
    private long _skipped;
    private long _reportedDecoded;
    private long _reportedSkipped;

    /// <summary>Sends one sealed datagram to the source; false when it could not go.</summary>
    internal delegate bool MediaSender(ReadOnlySpan<byte> datagram);

    internal ScreenViewer(PeerSession session, ScreenStreamMessage info, ILogger logger, MediaSender? sendMedia = null)
    {
        Session = session;
        Info = info;
        _logger = logger;
        _sendMedia = sendMedia;

        OpenMediaPath(info.MediaKey);

        // The window paints on the dispatcher, whose timing is as coarse as the system timer; while a view is open
        // that is worth making fine (see MediaTimerResolution).
        _timer = MediaTimerResolution.Hold();
        _decodeLoop = Task.Run(DecodeLoopAsync);
        _ = Task.Run(SendLoopAsync);
    }

    public PeerSession Session { get; }

    public uint StreamId => Info.StreamId;

    public ScreenStreamMessage Info { get; private set; }

    public string PeerName => Session.PeerName;

    /// <summary>Frames decoded here since the view opened.</summary>
    public long FramesDecoded => Volatile.Read(ref _decoded);

    /// <summary>...of those, the ones never handed to the window because it was still busy with an earlier one.</summary>
    public long FramesNotShown => Volatile.Read(ref _skipped);

    public event Action<ScreenViewer>? InfoChanged;

    /// <summary>The stream ended (closed here or by the source); the reason is for display.</summary>
    public event Action<ScreenViewer, string?>? Closed;

    /// <summary>
    /// Starts decoding; <paramref name="present"/> runs on a worker thread and must call
    /// <see cref="FramePresented"/> when it is done. The update's image belongs to the viewer and is only valid
    /// until that call: the decoder takes the buffer back then and the next frames go into it.
    /// </summary>
    public void Attach(Action<FrameUpdate> present)
    {
        _present = present;
        _attached.TrySetResult();
    }

    /// <summary>
    /// The window is done with the buffer it was given; the decoder may use it again. How long it took is kept and
    /// travels in the next acknowledgement: it is the last stretch of a picture's journey, and the only one neither
    /// device could see. Everything else — the encoder, the link, the decoding — is measured at one end or the
    /// other, so without this the hundred milliseconds between moving a finger and seeing it move had a piece
    /// nobody could account for.
    /// </summary>
    public void FramePresented()
    {
        var started = Volatile.Read(ref _presentStarted);
        if (started != 0)
        {
            Volatile.Write(ref _paintMs, (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        lock (_surfaceGate)
        {
            // The picture it was given is now the one it is drawing from; the one before it is free to be written.
            if (_handedFrom is { } pool)
            {
                if (pool.Handed is { } shown)
                {
                    pool.Shown = shown;
                    pool.Handed = null;
                }

                _handedFrom = null;
            }
        }

        Volatile.Write(ref _presentingSince, 0);
    }

    // ---- pictures that stay on the graphics card ----------------------------------------------------------
    //
    // See GpuSurface for why. What is decided here is which surface the decoder may write into, and the rule is
    // that it is never one the window might be drawing from. The window draws from the last surface it confirmed
    // (Shown), and may at any moment switch to the one it has been handed and not yet confirmed (Handed); so the
    // decoder writes into a third (Spare). Three is therefore the number: with two, the decoder's next picture
    // would have to go into the one the window had not yet let go of, and what was on the screen would be the top
    // of one picture and the bottom of the next.
    //
    // Pictures of the whole screen and pictures of a part of it (see SetCrop) each have their own three. The
    // window keeps the last picture of the whole under the part it is zoomed into, and that picture has to stay
    // what it was while parts are being decoded.

    private sealed class SurfacePool
    {
        public List<GpuSurface> All = [];

        /// <summary>The set before this one — a different size, or another decoder — kept until the one after it.</summary>
        public List<GpuSurface> Retired = [];
        public GpuSurface? Shown;
        public GpuSurface? Handed;
        public GpuSurface? Spare;
        public H264VideoDecoder? Owner;
        public int Width;
        public int Height;
    }

    private readonly object _surfaceGate = new();
    private readonly SurfacePool _wholeSurfaces = new();
    private readonly SurfacePool _partSurfaces = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<byte, bool> _isPart = new();
    private SurfacePool? _handedFrom;
    private volatile bool _surfacesWanted;
    private long _onCard;
    private long _reportedOnCard;

    /// <summary>
    /// Whether the window can draw pictures straight from the graphics card. While it says so, and while the card
    /// is the one decoding, pictures are left there (<see cref="FrameUpdate.Surface"/>); otherwise, and whenever
    /// the window takes this back, they arrive in memory as they always did. The window may change its mind at
    /// any time — it is the one that finds out whether drawing them works.
    /// </summary>
    public bool SurfacesWanted
    {
        get => _surfacesWanted;
        set => _surfacesWanted = value;
    }

    /// <summary>A surface the decoder may write the next picture into, or null when it must go into memory.</summary>
    private GpuSurface? SpareOf(SurfacePool pool, H264VideoDecoder decoder, int width, int height)
    {
        lock (_surfaceGate)
        {
            if (!ReferenceEquals(pool.Owner, decoder) || pool.Width != width || pool.Height != height)
            {
                // Not given up at once: one of them may be on its way to the window, or on the screen. They go
                // when the set after this one is itself replaced, by which time neither can be true.
                foreach (var old in pool.Retired)
                {
                    old.Dispose();
                }

                pool.Retired = pool.All;
                pool.All = [];
                pool.Shown = pool.Handed = pool.Spare = null;
                pool.Owner = decoder;
                pool.Width = width;
                pool.Height = height;
            }

            if (pool.Spare is null)
            {
                pool.Spare = pool.All.FirstOrDefault(s => !ReferenceEquals(s, pool.Shown) && !ReferenceEquals(s, pool.Handed));
                if (pool.Spare is null && pool.All.Count < 4 && decoder.CreateSurface(width, height) is { } made)
                {
                    pool.All.Add(made);
                    pool.Spare = made;
                }
            }

            return pool.Spare;
        }
    }

    private void ReleaseSurfaces()
    {
        lock (_surfaceGate)
        {
            foreach (var pool in new[] { _wholeSurfaces, _partSurfaces })
            {
                foreach (var surface in pool.All.Concat(pool.Retired))
                {
                    surface.Dispose();
                }

                pool.All = [];
                pool.Retired = [];
                pool.Shown = pool.Handed = pool.Spare = null;
                pool.Owner = null;
            }

            _handedFrom = null;
        }
    }

    /// <summary>
    /// Hands the window a picture that is on the card, or leaves it unshown while the window is still busy with
    /// the one before — in which case it stays in the spare surface and the next picture is written over it.
    /// </summary>
    private void PresentSurface(uint frameNo, SurfacePool pool, GpuSurface surface, bool resized, byte cropId)
    {
        _decoded++;
        _onCard++;
        Report();
        if (_present is not { } present)
        {
            return;
        }

        var now = Environment.TickCount64;
        var holding = Volatile.Read(ref _presentingSince);
        lock (_surfaceGate)
        {
            if (holding != 0 && now - holding < PresentGrace.TotalMilliseconds)
            {
                _skipped++;
                return;
            }

            // A window that never said it had finished (see PresentGrace) is taken to be drawing what it was given.
            if (_handedFrom is { Handed: { } unconfirmed } stale)
            {
                stale.Shown = unconfirmed;
                stale.Handed = null;
            }

            pool.Handed = surface;
            pool.Spare = null;
            _handedFrom = pool;
        }

        _missedFrames = false;
        Volatile.Write(ref _presentingSince, now);
        Volatile.Write(ref _presentStarted, Stopwatch.GetTimestamp());
        present(new FrameUpdate(frameNo, surface.Width, surface.Height, [], [new Rectangle(0, 0, surface.Width, surface.Height)], resized, cropId, surface));
    }

    /// <summary>
    /// The part of the whole picture the source is sending at the moment, as the window last asked for it (see
    /// <see cref="SetCrop"/>): the whole of it unless the window has zoomed in. The window goes on thinking in
    /// positions on the whole picture — that is what it lays out and what its pointer is measured on — while the
    /// source reads every position against the part it is sending, so they are converted here, on the way out and
    /// on the way in. Against what was last <em>asked for</em>, not what is on the screen: positions travel on the
    /// same connection, in order, behind the request, so by the time the source reads one that request is the part
    /// it is sending.
    /// </summary>
    private sealed record Part(float X, float Y, float Width, float Height);

    private static readonly Part Whole = new(0, 0, 1, 1);
    private volatile Part _part = Whole;

    public void SendPointer(PointerAction action, MouseButton button, float x, float y, int wheel = 0)
    {
        var part = _part;
        _outbox.Writer.TryWrite(ScreenCodec.EncodePointer(StreamId, action, button, (x - part.X) / part.Width, (y - part.Y) / part.Height, wheel));
    }

    /// <summary>Touch contact: action 1 down, 2 move, 3 up, 4 cancel; <paramref name="contact"/> 0–9.</summary>
    public void SendTouch(byte action, byte contact, float x, float y)
    {
        var part = _part;
        _outbox.Writer.TryWrite(ScreenCodec.EncodeTouch(StreamId, action, contact, (x - part.X) / part.Width, (y - part.Y) / part.Height));
    }

    public void SendKey(ushort virtualKey, ushort scanCode, bool up, bool extended) =>
        _outbox.Writer.TryWrite(ScreenCodec.EncodeKey(StreamId, virtualKey, scanCode, up, extended));

    /// <summary>Characters that came without a usable key (touch keyboard, IME, emoji).</summary>
    public void SendText(string text)
    {
        if (text.Length > 0)
        {
            _outbox.Writer.TryWrite(ScreenCodec.EncodeText(StreamId, text));
        }
    }

    /// <summary>Stream only this normalized part of the source image (the full image is 0,0,1,1).</summary>
    /// <remarks>
    /// <paramref name="id"/> names the request, and comes back on every picture made of it
    /// (<see cref="FrameUpdate.CropId"/>). <paramref name="part"/> says which part of the picture the window lays
    /// out this request is — null for all of it — and is what positions are converted against from here on.
    /// </remarks>
    public void SetCrop(RectangleF crop, byte id = 0, RectangleF? part = null)
    {
        _part = part is { Width: > 0, Height: > 0 } p ? new Part(p.X, p.Y, p.Width, p.Height) : Whole;
        if (id != 0)
        {
            _isPart[id] = !ReferenceEquals(_part, Whole); // which of the two sets of surfaces its pictures go into
        }

        _outbox.Writer.TryWrite(ScreenCodec.EncodeCrop(StreamId, ScreenCodec.Normalize(crop), id));
    }

    /// <summary>
    /// Tells the source how large the picture is actually being drawn here, in real pixels, so it can encode it
    /// at that size instead of its own. A window showing a full screen at half its size otherwise pays for every
    /// pixel it never shows, twice: once in the decoder, once turning the result into pixels.
    ///
    /// Rounded up to a step, and only sent when the rounded answer changes, so dragging a window's edge does not
    /// have the source rebuild its encoder on every pixel of the drag.
    /// </summary>
    public void SetShownSize(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return;
        }

        var rounded = new System.Drawing.Size(RoundUp(width), RoundUp(height));
        if (rounded == _shownSize)
        {
            return;
        }

        _shownSize = rounded;
        _outbox.Writer.TryWrite(ScreenCodec.EncodeSize(StreamId, rounded.Width, rounded.Height));
    }

    /// <summary>Up, not down: asking for a little more than is shown costs little and never softens the picture.</summary>
    private static int RoundUp(int value) => Math.Clamp((value + SizeStep - 1) / SizeStep * SizeStep, 320, 7680);

    private const int SizeStep = 160;
    private System.Drawing.Size _shownSize;

    public void SwitchMonitor(int index, string? key = null) => _ = SendControlAsync(new ScreenRequestMessage(StreamId, index, key));

    /// <summary>Asks the source to act on its real window (restore it from minimised).</summary>
    public void SendWindowCommand(WindowCommand command) => _outbox.Writer.TryWrite(ScreenCodec.EncodeWindowCommand(StreamId, command));

    public void Close() => _ = CloseAsync(notifySource: true, reason: null);

    // ---- the sharp picture of a screen that has stopped ------------------------------------------------

    /// <summary>One such picture, decoded: tightly packed BGRA, and which request for a part of the screen it is of.</summary>
    public sealed record StillPicture(int Width, int Height, byte[] Bgra, byte CropId, uint AfterFrameNo);

    private uint _lastFrameNo;
    private long _stills;

    /// <summary>The number of the newest video frame that has reached the decoder.</summary>
    public uint LastFrameNo => Volatile.Read(ref _lastFrameNo);

    /// <summary>How many sharp pictures have been shown here.</summary>
    public long StillsShown => Interlocked.Read(ref _stills);

    /// <summary>
    /// A sharp picture of the screen as it stands, to lay over the video until the next video frame arrives. Raised
    /// off the UI thread; whoever shows it should look at <see cref="LastFrameNo"/> once more as it does, since a
    /// frame may have arrived in between.
    /// </summary>
    public event Action<ScreenViewer, StillPicture>? StillArrived;

    internal void OnStill(byte[] message)
    {
        if (StillArrived is null
            || !ScreenCodec.TryDecodeStill(message, out var after, out var width, out var height, out var cropId, out var offset)
            || LastFrameNo > after)
        {
            return; // nobody to show it, or the screen has moved on since it was taken
        }

        // Off this thread: it is the one messages arrive on, and opening a picture this size takes a frame or two.
        _ = Task.Run(() =>
        {
            if (Volatile.Read(ref _closed) != 0
                || StillCodec.Decode(message.AsSpan(offset)) is not { } decoded
                || decoded.Width != width || decoded.Height != height
                || LastFrameNo > after)
            {
                return;
            }

            Interlocked.Increment(ref _stills);
            StillArrived?.Invoke(this, new StillPicture(width, height, decoded.Bgra, cropId, after));
        });
    }

    /// <summary>The program in front on the source, by its process name, or empty until it has said.</summary>
    public string ForegroundProcess { get; private set; } = string.Empty;

    /// <summary>The program in front on the source changed. Raised off the UI thread.</summary>
    public event Action<ScreenViewer>? ForegroundChanged;

    internal void OnForeground(string process)
    {
        ForegroundProcess = process;
        ForegroundChanged?.Invoke(this);
    }

    // ---- small pictures of the source's other monitors ------------------------------------------------

    /// <summary>One of them: a JPEG, and when it came.</summary>
    public sealed record MonitorPicture(string MonitorKey, int Width, int Height, byte[] Jpeg, long ReceivedTicks);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MonitorPicture> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private long _picturesAskedTicks;
    private bool _picturesOn;

    /// <summary>The latest picture of each of the source's other monitors, by the monitor's name; empty until asked for.</summary>
    public IReadOnlyDictionary<string, MonitorPicture> MonitorPictures => _pictures;

    /// <summary>A new picture of one of them arrived. Raised off the UI thread.</summary>
    public event Action<ScreenViewer>? MonitorPicturesChanged;

    /// <summary>
    /// Asks the source to keep sending them, or to stop. Meant to be called again every second or so for as long
    /// as they are wanted — the source stops by itself when it has not been asked for a while, so a viewer that
    /// simply goes away is not sent them for ever.
    /// </summary>
    public void WantMonitorPictures(bool on)
    {
        var now = Environment.TickCount64;
        if (on == _picturesOn && (!on || now - _picturesAskedTicks < 2000))
        {
            return;
        }

        _picturesOn = on;
        _picturesAskedTicks = now;
        if (!on)
        {
            _pictures.Clear();
        }

        _ = SendControlAsync(new ScreenThumbsMessage(StreamId, on));
    }

    internal void OnThumbnail(ScreenThumbMessage thumb)
    {
        if (thumb.Jpeg.Length is 0 or > 256 * 1024 || _pictures.Count > 16 && !_pictures.ContainsKey(thumb.MonitorKey))
        {
            return;
        }

        _pictures[thumb.MonitorKey] = new MonitorPicture(thumb.MonitorKey, thumb.Width, thumb.Height, thumb.Jpeg, Environment.TickCount64);
        MonitorPicturesChanged?.Invoke(this);
    }

    /// <summary>How the stream is doing as its source last said (about once a second), or null before it has.</summary>
    public ScreenStatsMessage? Stats { get; private set; }

    internal void OnStats(ScreenStatsMessage stats) => Stats = stats;

    /// <summary>What this end has counted since the view opened, for showing beside what the source says.</summary>
    public sealed record ViewCounts(long Decoded, long NotShown, long Hurried, long OnCard, long ByDatagram, long Rebuilt, long GivenUp, long AskedAgain);

    public ViewCounts Counts
    {
        get
        {
            var (delivered, repaired, lost) = Datagrams;
            return new ViewCounts(
                Volatile.Read(ref _decoded), Volatile.Read(ref _skipped), Volatile.Read(ref _hurried), Volatile.Read(ref _onCard),
                delivered, repaired, lost, PiecesAskedFor);
        }
    }

    /// <summary>
    /// The person watching says the stream hitched just now. Written to this device's log with what it is
    /// counting, and passed to the source, which writes what it is measuring: two lines at the same second, one
    /// from each end, which is where looking for the cause starts.
    /// </summary>
    public void MarkHitch(string note)
    {
        var counts = Counts;
        var stats = Stats;
        _logger.LogWarning(
            "HITCH marked here in the view of {Peer} (stream {Stream}): {Note}. So far: {Decoded} decoded, {NotShown} "
            + "not shown while the window was busy, {Hurried} hurried, {OnCard} left on the card; by UDP {ByDatagram} "
            + "pictures, {Rebuilt} rebuilt, {GivenUp} given up, {Asked} requests for pieces; {Waiting} waiting to be "
            + "decoded; the source last said: acknowledged in {Ack} ms, {Kbps} kbit/s, {Fps} frames/s, {Path}",
            PeerName, StreamId, note, counts.Decoded, counts.NotShown, counts.Hurried, counts.OnCard, counts.ByDatagram,
            counts.Rebuilt, counts.GivenUp, counts.AskedAgain, Volatile.Read(ref _waiting),
            stats?.AckMs, stats?.Kbps, stats?.Fps, stats is null ? "nothing yet" : stats.Udp ? "UDP" : "the connection");
        _ = SendControlAsync(new ScreenMarkMessage(StreamId, note));
    }

    /// <summary>The source says a tap from here put the focus in a text box there (true), or took it out (false).</summary>
    public event Action<ScreenViewer, bool>? KeyboardHint;

    internal void OnKeyboardHint(bool show) => KeyboardHint?.Invoke(this, show);

    internal void OnInfo(ScreenStreamMessage info)
    {
        Info = info;
        OpenMediaPath(info.MediaKey);
        InfoChanged?.Invoke(this);
    }

    // ---- pictures by UDP -------------------------------------------------------------------------------
    //
    // A picture is only worth anything while it is current. On the session's one reliable connection a lost
    // segment holds back everything behind it until it has been asked for again and sent again — a whole round
    // trip in which the next picture, the one that should be on the screen by then, waits its turn behind a
    // picture nobody will ever want again. So the source may send them as UDP datagrams instead, and this is where
    // they are put back together.
    //
    // The key comes in the stream's description, inside the session's TLS, which is what lets the datagrams need no
    // handshake of their own. Nothing here is assumed to work: pictures keep coming on the connection until the
    // probes have been answered, and go back there if they stop arriving.

    private readonly object _mediaGate = new();
    private MediaCipher? _mediaCipher;
    private VideoReassembly? _reassembly;
    private byte[] _mediaPlain = [];
    private string _mediaKey = string.Empty;
    private Task? _mediaTicks;
    private int _udpReported;
    private long _udpAnsweredTicks = long.MinValue / 2;

    /// <summary>
    /// How often a probe is answered at most. Each round of probes needs an answer of its own — a source whose
    /// path went quiet probes again later, and an answer given once per view left it asking for ever — but a round
    /// is a probe every hundred and twenty milliseconds, and one answer serves the round.
    /// </summary>
    private static readonly TimeSpan AnswerProbesEvery = TimeSpan.FromMilliseconds(400);

    /// <summary>Half a frame at sixty a second: how closely the clock is watched while a picture is in pieces.</summary>
    private static readonly TimeSpan TickWhileWaiting = TimeSpan.FromMilliseconds(4);

    /// <summary>And how often the same loop so much as wakes while no picture is waiting for anything.</summary>
    private static readonly TimeSpan TickWhileIdle = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Everything about putting pictures together happens because a datagram arrived, which is enough while they
    /// keep arriving. This is for when they stop: a picture lost at the very moment the screen stopped changing
    /// would otherwise hold back the one after it — the one showing what the screen settled on — until something
    /// moved again.
    ///
    /// So it watches closely only while there is something to watch. With nothing in pieces there is nothing this
    /// can decide, and a screen that is not changing sends nothing at all — which is most of the time a view is
    /// open, and was a hundred and twenty-five wake-ups a second spent on deciding nothing. The device watching
    /// is usually the one on a battery.
    /// </summary>
    private async Task TickMediaAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                bool waiting;
                lock (_mediaGate)
                {
                    _reassembly?.Tick();
                    waiting = _reassembly?.Waiting == true;
                }

                await Task.Delay(waiting ? TickWhileWaiting : TickWhileIdle, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void OpenMediaPath(string key)
    {
        if (key.Length == 0 || key == _mediaKey)
        {
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(key);
            if (bytes.Length != MediaCipher.KeySize)
            {
                return;
            }

            lock (_mediaGate)
            {
                _mediaCipher?.Dispose();
                _mediaCipher = new MediaCipher(bytes);

                // Where the source listens for them, missing pieces are asked for again before a picture is
                // given up (see ViewerReplies); where it does not, nothing is asked and nothing waits.
                var replies = Info.MediaReplies && _sendMedia is not null;
                _reassembly = new VideoReassembly(OnWholePicture, OnLostPicture, null, replies ? AskAgain : null);
                lock (_replyGate)
                {
                    _replyCipher?.Dispose();
                    _replyCipher = replies ? new MediaCipher(bytes) : null;
                }

                _mediaPlain = new byte[VideoDatagram.SafeDatagramBytes];
                _mediaKey = key;
                _mediaTicks ??= Task.Run(TickMediaAsync);
            }
        }
        catch (FormatException)
        {
            _logger.LogDebug("The key for this stream's datagrams is not readable; its pictures stay on the connection");
        }
    }

    /// <summary>
    /// One datagram, straight off the receive loop. Quick on purpose: authenticating it and copying its piece into
    /// place is all that happens here, and a whole picture is handed to the decoder the same way one off the
    /// connection is.
    /// </summary>
    internal void OnMediaPacket(byte kind, ReadOnlySpan<byte> packet)
    {
        lock (_mediaGate)
        {
            if (_mediaCipher is not { } cipher || _reassembly is not { } reassembly)
            {
                return;
            }

            if (!cipher.TryOpen(packet, _mediaPlain, out var length))
            {
                return; // forged, corrupted, or for a stream this is not
            }

            if (kind == VideoLink.ProbeKind)
            {
                var now = Environment.TickCount64;
                if (now - _udpAnsweredTicks >= AnswerProbesEvery.TotalMilliseconds)
                {
                    _udpAnsweredTicks = now;
                    Volatile.Write(ref _udpReported, 1);
                    _outbox.Writer.TryWrite(ScreenCodec.EncodeRequest(ScreenMessageType.UdpReady, StreamId));
                }

                return;
            }

            if (kind == VideoLink.FrameKind
                && VideoDatagram.TryReadHeader(_mediaPlain.AsSpan(0, length), out var header))
            {
                // Datagrams arriving are also the answer to the probes, for a path that came up mid-stream.
                ReportUdpWorks();
                reassembly.Add(header, _mediaPlain.AsSpan(VideoDatagram.HeaderSize, length - VideoDatagram.HeaderSize));
            }
        }
    }

    /// <summary>Tells the source, once, that its datagrams reach here, so the pictures can move off the connection.</summary>
    private void ReportUdpWorks()
    {
        if (Interlocked.Exchange(ref _udpReported, 1) == 0)
        {
            _outbox.Writer.TryWrite(ScreenCodec.EncodeRequest(ScreenMessageType.UdpReady, StreamId));
        }
    }

    // ---- saying things back by datagram ---------------------------------------------------------------
    //
    // See ViewerReplies for what and why. Sealed with the key the source handed over, under sequence numbers the
    // source never uses, and through a cipher of their own: the one that opens what arrives is busy on the thread
    // that receives.

    private readonly MediaSender? _sendMedia;
    private readonly object _replyGate = new();
    private readonly byte[] _replyPlain = new byte[ViewerReplies.MaxPayload];
    private readonly byte[] _replyPacket = new byte[ViewerReplies.MaxPayload + MediaCipher.Overhead];
    private readonly ViewerReplies.Ack[] _lastAcks = new ViewerReplies.Ack[ViewerReplies.AcksPerDatagram];
    private MediaCipher? _replyCipher;
    private ulong _replySequence;
    private int _lastAckCount;
    private long _asked;
    private long _reportedAsked;

    /// <summary>How many times this view has asked for pieces of a picture to be sent again.</summary>
    internal long PiecesAskedFor => Interlocked.Read(ref _asked);

    /// <summary>Whether the source's datagrams are arriving here, so that the way back is worth trying at all.</summary>
    private bool RepliesGo => Volatile.Read(ref _udpReported) != 0;

    /// <summary>Called with <see cref="_replyGate"/> held; the payload is in <see cref="_replyPlain"/>.</summary>
    private void SendReplyLocked(byte kind, int length)
    {
        if (_replyCipher is not { } cipher || _sendMedia is not { } send)
        {
            return;
        }

        try
        {
            var size = cipher.Seal(kind, StreamId, ViewerReplies.ViewerSequenceBase + _replySequence++, _replyPlain.AsSpan(0, length), _replyPacket);
            send(_replyPacket.AsSpan(0, size));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // The connection carries the same acknowledgement, and a request that does not go is a picture given
            // up a little later, as it always was.
        }
    }

    /// <summary>Pieces of a picture are missing (none named: all of it): ask the source to send them again.</summary>
    private void AskAgain(uint frameNo, IReadOnlyList<int> pieces)
    {
        Interlocked.Increment(ref _asked);
        lock (_replyGate)
        {
            SendReplyLocked(ViewerReplies.ResendKind, ViewerReplies.WriteResend(_replyPlain, frameNo, pieces));
        }
    }

    /// <summary>
    /// The acknowledgement that is also on its way on the connection, sent by datagram as well — with the few
    /// before it, so that one of these going missing costs nothing.
    /// </summary>
    private void AckByDatagram(uint frameNo, int held, int painted)
    {
        if (!RepliesGo)
        {
            return;
        }

        lock (_replyGate)
        {
            if (_replyCipher is null)
            {
                return;
            }

            Array.Copy(_lastAcks, 0, _lastAcks, 1, _lastAcks.Length - 1);
            _lastAcks[0] = new ViewerReplies.Ack(frameNo, held, painted);
            _lastAckCount = Math.Min(_lastAckCount + 1, _lastAcks.Length);
            SendReplyLocked(ViewerReplies.AckKind, ViewerReplies.WriteAcks(_replyPlain, _lastAcks.AsSpan(0, _lastAckCount)));
        }
    }

    /// <summary>A picture is whole: into the decoder it goes, exactly as one off the connection would.</summary>
    private void OnWholePicture(uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> picture, byte tag) =>
        Enqueue(ScreenCodec.EncodeVideoFrame(StreamId, frameNo, width, height, keyframe, picture, tag));

    /// <summary>
    /// A picture lost more than one of its datagrams and is not coming. It is acknowledged all the same, because a
    /// sender waiting on it would stall over a picture that no longer exists, and a whole one is asked for, because
    /// everything after this is read against what was lost.
    /// </summary>
    private void OnLostPicture(uint frameNo)
    {
        AckByDatagram(frameNo, 0, 0);
        _outbox.Writer.TryWrite(ScreenCodec.EncodeAck(StreamId, frameNo, 0));
        _outbox.Writer.TryWrite(ScreenCodec.EncodeRequest(ScreenMessageType.Refresh, StreamId));
    }

    /// <summary>How the pictures of this stream have arrived, for the view's own line in the log.</summary>
    internal (long Delivered, long Repaired, long Lost) Datagrams
    {
        get
        {
            lock (_mediaGate)
            {
                return _reassembly is { } r ? (r.Delivered, r.Repaired, r.Lost) : (0, 0, 0);
            }
        }
    }

    internal void Enqueue(byte[] message)
    {
        Interlocked.Increment(ref _waiting);
        if (!_frames.Writer.TryWrite(new Incoming(message, Stopwatch.GetTimestamp())))
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>
    /// A frame that came on the connection rather than as datagrams. The side that puts datagrams together is told
    /// the picture's number first, so it does not go on waiting for one that has already arrived this way.
    /// </summary>
    internal void OnFrameFromConnection(byte[] message)
    {
        if (ScreenCodec.TryReadVideoFrame(message, out var frameNo, out _, out _, out _, out _))
        {
            lock (_mediaGate)
            {
                _reassembly?.Passed(frameNo);
            }
        }

        Enqueue(message);
    }

    /// <summary>Frames that have arrived and are waiting for the decoder, the one it is working on not counted.</summary>
    private int _waiting;

    /// <summary>
    /// How many pictures in a row may be decoded without being drawn. When a newer picture is already waiting, the
    /// one in hand is out of date before it is finished, so only the part of the work the next picture depends on
    /// is done for it. Without this a device that takes a hair longer over a frame than frames take to arrive
    /// builds up a queue it never works off: every picture is then shown two or three frames late, for as long as
    /// the screen keeps moving, which is exactly when the delay is felt. Bounded, so that a device that can never
    /// keep up still shows every third picture rather than none.
    /// </summary>
    private const int MaxHurried = 2;
    private int _hurriedInARow;
    private long _hurried;
    private long _reportedHurried;
    private bool _videoShown;
    private int _videoWidth;
    private int _videoHeight;

    /// <summary>
    /// The source's pointer, taken as it arrives rather than queued behind the frames: the whole reason it travels
    /// on its own is that it must not wait for one. A shape comes once and is kept here under its id; a position is
    /// eighteen bytes and comes as often as the mouse moves.
    /// </summary>
    internal void OnPointerMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length > 0 && message[0] == (byte)ScreenMessageType.CursorShape)
        {
            if (ScreenCodec.TryDecodeCursorShape(message) is { } shape)
            {
                lock (_shapes)
                {
                    if (_shapes.Count >= MaxShapes)
                    {
                        _shapes.Clear();
                    }

                    _shapes[shape.ShapeId] = shape;
                }
            }

            return;
        }

        if (ScreenCodec.TryDecodeCursor(message) is { } cursor)
        {
            // The source says where its pointer is on the part it is sending; the window wants it on the whole.
            var part = _part;
            cursor = cursor with { X = part.X + (cursor.X * part.Width), Y = part.Y + (cursor.Y * part.Height) };
            Pointer = cursor;
            PointerMoved?.Invoke(this, cursor);
        }
    }

    /// <summary>Shapes kept for this stream; Windows has a few dozen pointers and a stream may rename them once.</summary>
    private const int MaxShapes = 64;
    private readonly Dictionary<uint, ScreenCursorShape> _shapes = [];

    /// <summary>Where the source's pointer was last said to be, or null while it has not said at all.</summary>
    public ScreenCursor? Pointer { get; private set; }

    /// <summary>The source's pointer moved, changed shape or went away. Raised off the UI thread.</summary>
    public event Action<ScreenViewer, ScreenCursor>? PointerMoved;

    /// <summary>The pixels of a shape the source has sent, or null when it has not sent that one (yet).</summary>
    public ScreenCursorShape? PointerShape(uint shapeId)
    {
        lock (_shapes)
        {
            return _shapes.TryGetValue(shapeId, out var shape) ? shape : null;
        }
    }

    /// <summary>
    /// Whether this view ended without anybody ending it — the connection went, or the source's own application
    /// did — so that opening it again is what the person watching would want (see <see cref="ViewEndings"/>).
    /// Set before <see cref="Closed"/> is raised.
    /// </summary>
    public bool CanResume { get; private set; }

    internal Task CloseAsync(bool notifySource, string? reason, bool lostConnection = false)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return Task.CompletedTask;
        }

        CanResume = !notifySource && ViewEndings.CanResume(lostConnection, reason);

        _frames.Writer.TryComplete();
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        Closed?.Invoke(this, reason);
        return notifySource ? SendControlAsync(new ScreenStopMessage(StreamId, "closed by viewer")) : Task.CompletedTask;
    }

    /// <summary>
    /// The loop that decodes, kept so that closing can wait for it. The decoders belong to it: it is the only
    /// thing that calls into them, and it gives them up itself when it ends. They used to be given up by whoever
    /// closed the view, at once — while this loop could be in the middle of a picture, inside the decoder, which
    /// was then freed under it. That is an access violation, and it takes the whole application with it: closing a
    /// view at the wrong moment closed everything.
    /// </summary>
    private readonly Task _decodeLoop;

    /// <summary>How long closing waits for a picture in the decoder to be finished before leaving it be.</summary>
    private static readonly TimeSpan DecoderFinishesWithin = TimeSpan.FromSeconds(3);

    private async Task DecodeLoopAsync()
    {
        try
        {
            await DecodeFramesAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseSurfaces();
            _videoDecoder?.Dispose();
            _videoDecoder = null;
            _decoder.Dispose();
        }
    }

    private async Task DecodeFramesAsync()
    {
        var ct = _cts.Token;
        try
        {
            await _attached.Task.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var incoming in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _waiting);
                if (ScreenCodec.TryReadHeader(incoming.Message, out var type, out _) && type == ScreenMessageType.VideoFrame)
                {
                    await DecodeVideoAsync(incoming, ct).ConfigureAwait(false);
                    continue;
                }

                await DecodeTilesAsync(incoming, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await CloseAsync(notifySource: false, reason: "соединение потеряно", lostConnection: true).ConfigureAwait(false);
        }
    }

    private async Task DecodeTilesAsync(Incoming incoming, CancellationToken ct)
    {
        var frame = ScreenCodec.TryDecodeFrame(incoming.Message);
        if (frame is null || frame.Width == 0 || frame.Height == 0)
        {
            return;
        }

        var resized = frame.Width != _width || frame.Height != _height;
        if (resized)
        {
            _width = frame.Width;
            _height = frame.Height;
            _image = new byte[_width * _height * 4];
        }

        Parallel.ForEach(frame.Tiles, tile =>
        {
            try
            {
                _decoder.DecodeInto(tile, _image, _width, _height);
            }
            catch (Exception ex) when (ex is InvalidDataException or SharpGen.Runtime.SharpGenException)
            {
                _logger.LogDebug("Bad tile at {X},{Y}: {Message}", tile.X, tile.Y, ex.Message);
            }
        });

        await AckAsync(frame.FrameNo, incoming.Arrived, ct).ConfigureAwait(false);
        var dirty = frame.Tiles.Select(t => new Rectangle(t.X, t.Y, t.Width, t.Height)).ToList();
        Present(frame.FrameNo, dirty, resized, accumulates: true);
    }

    private async Task DecodeVideoAsync(Incoming incoming, CancellationToken ct)
    {
        var frame = ScreenCodec.TryDecodeVideoFrame(incoming.Message);
        if (frame is null || frame.Width == 0 || frame.Height == 0)
        {
            return;
        }

        if (_videoDecoder is null && !_videoDecodeUnavailable)
        {
            _videoDecoder = H264VideoDecoder.TryCreate(frame.Width, frame.Height, _logger, useGraphicsCard: !_decodeInMemory);
            if (_videoDecoder is null)
            {
                _videoDecodeUnavailable = true;
                _logger.LogWarning("No H.264 decoder available for the stream from {Peer}", PeerName);
                RequestTiles("на этом устройстве нет декодера H.264");
            }
        }

        Volatile.Write(ref _lastFrameNo, frame.FrameNo);

        // Behind: a newer picture is already here. This one is decoded, because the next is read against it, but
        // not drawn (see MaxHurried). Never the first picture, and never across a change of size — those are the
        // ones the window is waiting on to know what it is showing.
        var hurry = _videoShown
            && _hurriedInARow < MaxHurried
            && frame.Width == _videoWidth
            && frame.Height == _videoHeight
            && Volatile.Read(ref _waiting) > 0;
        _videoWidth = frame.Width;
        _videoHeight = frame.Height;

        var produced = false;
        var resized = false;
        SurfacePool? pool = null;
        GpuSurface? onto = null;
        var onCard = false;
        if (_videoDecoder is not null)
        {
            // The source says how large each picture is, and it changes while the stream runs: it sends the
            // picture at the size this window is showing it at.
            _videoDecoder.Expect(frame.Width, frame.Height);
            try
            {
                // Left on the card when the window can draw it from there; into memory otherwise.
                if (!hurry && SurfacesWanted && _videoDecoder.UsesGraphicsCard)
                {
                    pool = _isPart.TryGetValue(frame.CropId, out var part) && part ? _partSurfaces : _wholeSurfaces;
                    onto = SpareOf(pool, _videoDecoder, frame.Width, frame.Height);
                }

                produced = _videoDecoder.DecodeInto(frame.Data, ref _image, out var width, out var height, read: !hurry, onto: onto);
                onCard = produced && onto is not null && _videoDecoder.LastWasLeftOnCard;
                if (onCard)
                {
                    width = onto!.Width;
                    height = onto.Height;
                }

                if (produced && !hurry)
                {
                    resized = width != _width || height != _height;
                    _width = width;
                    _height = height;
                }
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _logger.LogDebug("H.264 decode failed: {Message}", ex.Message);
            }

            // The card took the stream and then would not finish a picture: build the decoder again without it.
            // A stream on the processor is slower, which is not the same as no picture.
            if (_videoDecoder.GraphicsCardFailed)
            {
                _logger.LogWarning("The graphics card stopped producing pictures for the stream from {Peer}; the processor takes the stream over", PeerName);
                _decodeInMemory = true;
                _videoDecoder.Dispose();
                _videoDecoder = null;
                ReleaseSurfaces();
                produced = false;
            }
        }

        if (!produced)
        {
            // Video keeps arriving but nothing comes out: ask for tiles rather than show an empty view.
            _videoSilentSince ??= Environment.TickCount64;
            if (Environment.TickCount64 - _videoSilentSince > VideoSilenceBeforeFallbackMs)
            {
                RequestTiles("видео H.264 не декодируется");
            }

            // Still acknowledge so the source's in-flight window does not stall while the decoder primes.
            await AckAsync(frame.FrameNo, incoming.Arrived, ct).ConfigureAwait(false);
            return;
        }

        _videoSilentSince = null;
        await AckAsync(frame.FrameNo, incoming.Arrived, ct).ConfigureAwait(false);
        if (hurry)
        {
            _hurriedInARow++;
            _hurried++;
            _decoded++;
            Report();
            return;
        }

        _hurriedInARow = 0;
        _videoShown = true;
        if (onCard)
        {
            PresentSurface(frame.FrameNo, pool!, onto!, resized, frame.CropId);
            return;
        }

        // Video carries the whole picture every time, so nothing has to be carried over between buffers.
        Present(frame.FrameNo, [new Rectangle(0, 0, _width, _height)], resized, accumulates: false, frame.CropId);
    }

    /// <summary>
    /// Hands the finished picture to the window, or leaves this frame unshown while the window is still busy with
    /// the previous one. Nothing waits here: the frame has already been acknowledged, so a slow window costs frames
    /// rather than stopping the stream at its source.
    ///
    /// The buffer the window takes is not written to again until it comes back, so the decoder moves to the spare
    /// one. The tile path redraws only what changed, so for it the picture is carried across to the spare first;
    /// video needs no carrying over. A frame that was not shown leaves the window's own bitmap behind what the
    /// buffer holds, so the next frame it does take is marked as changed all over.
    /// </summary>
    private void Present(uint frameNo, IReadOnlyList<Rectangle> dirty, bool resized, bool accumulates, byte cropId = 0)
    {
        _decoded++;
        Report();
        if (_present is not { } present)
        {
            return;
        }

        var holding = Volatile.Read(ref _presentingSince);
        if (holding != 0 && Environment.TickCount64 - holding < PresentGrace.TotalMilliseconds)
        {
            _missedFrames = true;
            _skipped++;
            return;
        }

        var image = _image;
        if (_missedFrames)
        {
            // Not "resized": that would have the window build a new bitmap and resize itself around it. It only
            // needs to be told that everything in this frame is new to it.
            _missedFrames = false;
            dirty = [new Rectangle(0, 0, _width, _height)];
        }

        Volatile.Write(ref _presentingSince, Environment.TickCount64);
        Volatile.Write(ref _presentStarted, Stopwatch.GetTimestamp());

        // Swap buffers before the window is told, so the decoder can start the next frame while it paints.
        var pixels = _width * _height * 4;
        if (_spare.Length != image.Length)
        {
            _spare = new byte[image.Length];
        }

        if (accumulates && pixels > 0 && pixels <= image.Length)
        {
            image.AsSpan(0, pixels).CopyTo(_spare);
        }

        (_image, _spare) = (_spare, image);
        present(new FrameUpdate(frameNo, _width, _height, image, dirty, resized, cropId));
    }

    /// <summary>Every ten seconds, what this view actually managed: decoded, shown, and how far behind the window is.</summary>
    private void Report()
    {
        var now = Environment.TickCount64;
        if (_reportedTicks == 0)
        {
            _reportedTicks = now;
            return;
        }

        if (now - _reportedTicks < ReportEvery.TotalMilliseconds)
        {
            return;
        }

        var seconds = (now - _reportedTicks) / 1000.0;
        var decoded = _decoded - _reportedDecoded;
        var skipped = _skipped - _reportedSkipped;
        var (delivered, repaired, lost) = Datagrams;
        var hurried = _hurried - _reportedHurried;
        var onCard = _onCard - _reportedOnCard;
        var asked = Interlocked.Read(ref _asked);
        _logger.LogInformation(
            "View of {Peer} (stream {Stream}): {Rate:F1} frames/s decoded, {Shown:F1} shown, {Skipped} not shown "
            + "while the window was busy, {Hurried} decoded without being drawn to catch up, {OnCard} left on the "
            + "graphics card for the window; by UDP {Delivered} pictures of which {Repaired} had a lost datagram "
            + "rebuilt, {Lost} given up on, {Asked} requests for pieces to be sent again",
            PeerName, StreamId, decoded / seconds, (decoded - skipped - hurried) / seconds, skipped, hurried, onCard, delivered, repaired, lost, asked - _reportedAsked);
        _reportedAsked = asked;
        _reportedHurried = _hurried;
        _reportedOnCard = _onCard;
        _reportedTicks = now;
        _reportedDecoded = _decoded;
        _reportedSkipped = _skipped;
    }

    private const long VideoSilenceBeforeFallbackMs = 3000;

    /// <summary>When video frames started arriving without a picture coming out, or null.</summary>
    private long? _videoSilentSince;
    private int _tilesRequested;

    /// <summary>Asks the source, once, to send JPEG tiles instead of H.264.</summary>
    private void RequestTiles(string reason)
    {
        if (Interlocked.Exchange(ref _tilesRequested, 1) != 0)
        {
            return;
        }

        _logger.LogWarning("Asking {Peer} for JPEG tiles instead of H.264: {Reason}", PeerName, reason);
        _ = SendControlAsync(new ScreenCodecFallbackMessage(StreamId, reason));
    }

    /// <summary>
    /// Acknowledges a frame, saying how long it was held here: from arriving on the connection to this moment, so
    /// it covers waiting its turn, decoding and being turned into pixels. The source measures a round trip and
    /// cannot otherwise tell how much of it was the link and how much was this device.
    /// </summary>
    private Task AckAsync(uint frameNo, long arrived, CancellationToken ct)
    {
        var held = (int)Math.Round(Stopwatch.GetElapsedTime(arrived).TotalMilliseconds);
        var painted = Volatile.Read(ref _paintMs);

        // By datagram first: it is the one that does not wait behind anything.
        AckByDatagram(frameNo, held, Math.Max(painted, 0));
        return Session.SendAsync(ChannelId.Screen, ScreenCodec.EncodeAck(StreamId, frameNo, held, Math.Max(painted, 0)), ct).AsTask();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await Session.SendAsync(ChannelId.Screen, message, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private async Task SendControlAsync(ControlMessage message)
    {
        try
        {
            await Session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Screen control message not sent: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(notifySource: true, reason: null).ConfigureAwait(false);

        // The decoders are given up by the loop that uses them, when it ends (see _decodeLoop). If it is stuck
        // inside one for longer than this, they are left alone: something leaked is better than something freed
        // while it is being used.
        try
        {
            await _decodeLoop.WaitAsync(DecoderFinishesWithin).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("The decoder of the view of {Peer} was still busy when the view closed", PeerName);
        }

        lock (_mediaGate)
        {
            _mediaCipher?.Dispose();
            _mediaCipher = null;
            lock (_replyGate)
            {
                _replyCipher?.Dispose();
                _replyCipher = null;
            }

            _reassembly = null;
        }

        _timer.Dispose();
    }
}
