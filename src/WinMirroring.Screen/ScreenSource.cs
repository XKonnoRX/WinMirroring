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
/// One outgoing stream of a monitor or a window to one viewer. At most two frame messages are unacknowledged, so
/// the frame rate follows what the link and the viewer can take and the send queue never grows. Pointer and
/// keyboard events from the viewer are injected here at the matching screen position.
/// </summary>
internal sealed class ScreenSource : IAsyncDisposable
{
    private const int MaxInFlight = 2;
    private const int PartBytes = 512 * 1024;
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(100);

    private readonly ScreenSettings _settings;
    private readonly ILogger _logger;
    private readonly InputInjector _injector = new();
    private readonly Injection.TouchInjector _touch;
    private readonly TileEncoder _encoder = new();
    private readonly SemaphoreSlim _window = new(MaxInFlight, MaxInFlight);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private long _lastBlockedReport = -10_000;
    private long _monitorCheckedTicks;

    private WgcCapture _capture;
    private MonitorDescription? _monitor;
    private RectangleF _crop = new(0, 0, 1, 1);
    private Size _frameSize;
    private int _forceAll = 1;
    private int _outstanding;
    private Task? _loop;
    private int _ended;

    private ScreenSource(PeerSession session, uint streamId, StreamKind kind, WgcCapture capture, MonitorDescription? monitor, nint window, string title, ScreenSettings settings, ILogger logger)
    {
        Session = session;
        StreamId = streamId;
        Kind = kind;
        _capture = capture;
        _monitor = monitor;
        Window = window;
        Title = title;
        _settings = settings;
        _logger = logger;
        _touch = new Injection.TouchInjector(logger);
        _capture.Closed += OnCaptureClosed;
    }

    public PeerSession Session { get; }

    public uint StreamId { get; }

    public StreamKind Kind { get; }

    public nint Window { get; }

    public string Title { get; private set; }

    public int MonitorIndex => _monitor?.Index ?? 0;

    public event Action<ScreenSource, string>? Ended;

    public static ScreenSource ForMonitor(PeerSession session, uint streamId, int monitorIndex, ScreenSettings settings, ILogger logger)
    {
        var monitor = PickMonitor(monitorIndex);
        return new ScreenSource(session, streamId, StreamKind.Monitor, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, MonitorTitle(monitor), settings, logger);
    }

    public static ScreenSource ForWindow(PeerSession session, uint streamId, nint window, ScreenSettings settings, ILogger logger)
    {
        var title = GetTitle(window);
        return new ScreenSource(session, streamId, StreamKind.Window, WgcCapture.ForWindow(window, logger), null, window, string.IsNullOrEmpty(title) ? "Окно" : title, settings, logger);
    }

    /// <summary>Streams the virtual monitor that extends this desktop onto the viewer.</summary>
    public static ScreenSource ForDisplay(PeerSession session, uint streamId, MonitorDescription monitor, ScreenSettings settings, ILogger logger) =>
        new(session, streamId, StreamKind.Display, WgcCapture.ForMonitor(monitor.Handle, logger), monitor, 0, $"Второй монитор {Environment.MachineName}", settings, logger);

    public ScreenStreamMessage Describe() => new(
        StreamId,
        Kind switch { StreamKind.Monitor => "monitor", StreamKind.Display => "display", _ => "window" },
        Title,
        Kind == StreamKind.Monitor ? MonitorIndex : 0,
        Kind switch { StreamKind.Monitor => DisplayCatalog.Monitors().Count, StreamKind.Display => 1, _ => 0 },
        _settings.AllowControl);

    public void Start()
    {
        _capture.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("Streaming {Title} to {Peer} (stream {Stream})", Title, Session.PeerName, StreamId);
    }

    /// <summary>Viewer asked for another monitor of a monitor stream.</summary>
    public void SwitchMonitor(int index)
    {
        if (Kind != StreamKind.Monitor)
        {
            return;
        }

        var monitor = PickMonitor(index);
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

    public void OnAck()
    {
        if (Interlocked.Decrement(ref _outstanding) >= 0)
        {
            _window.Release();
        }
        else
        {
            Interlocked.Increment(ref _outstanding);
        }
    }

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
        if (touchAction == Injection.TouchInjector.Action.Down && Kind == StreamKind.Window)
        {
            BringWindowForward();
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
            WinMirroring.Input.Injection.InputInjector.TypeText(text);
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
        uint frameNo = 0;
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

                var fresh = await capture.WaitForFrameAsync(seen, IdleWait, ct).ConfigureAwait(false);
                if (!fresh && (Volatile.Read(ref _forceAll) == 0 || seen == 0))
                {
                    continue;
                }

                await _window.WaitAsync(ct).ConfigureAwait(false);
                var slotHeld = true;
                try
                {
                    if (!capture.TryCopyLatest(ref frame, out var width, out var height, out var version))
                    {
                        continue;
                    }

                    seen = version;
                    var force = Interlocked.Exchange(ref _forceAll, 0) == 1;
                    Rectangle crop;
                    lock (_gate)
                    {
                        _frameSize = new Size(width, height);
                        crop = ScreenCodec.ToPixels(_crop, width, height);
                    }

                    _encoder.Quality = Math.Clamp(_settings.Quality, 30, 95) / 100f;
                    var tiles = _encoder.Encode(frame, width, height, crop, force);
                    if (tiles.Count == 0)
                    {
                        continue;
                    }

                    foreach (var part in Split(tiles))
                    {
                        if (!slotHeld)
                        {
                            await _window.WaitAsync(ct).ConfigureAwait(false);
                        }

                        slotHeld = false;
                        Interlocked.Increment(ref _outstanding);
                        await Session.SendAsync(ChannelId.Screen, ScreenCodec.EncodeFrame(StreamId, ++frameNo, _encoder.Width, _encoder.Height, part), ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (slotHeld)
                    {
                        _window.Release();
                    }
                }

                var minimumMs = 1000.0 / Math.Clamp(_settings.MaxFps, 1, 60);
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

    private static MonitorDescription PickMonitor(int index)
    {
        var monitors = DisplayCatalog.Monitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitors found.");
        }

        return monitors[Math.Clamp(index, 0, monitors.Count - 1)];
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
        _injector.ReleaseAll();
        _touch.Dispose();
        _cts.Dispose();
        _logger.LogInformation("Stopped streaming {Title} to {Peer}", Title, Session.PeerName);
    }
}
