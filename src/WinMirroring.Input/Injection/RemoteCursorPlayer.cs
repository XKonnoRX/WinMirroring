using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Native;
using WinMirroring.Input.Screen;
using WinMirroring.Input.Timing;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Drives the local cursor on behalf of a remote controller.
/// <list type="bullet">
/// <item>The cursor position is tracked here instead of re-reading GetCursorPos after every injection: the system
/// applies SendInput asynchronously, so a read right after an injection returns the old position and motion
/// is lost or jumps back. The tracked position is re-synchronised only after the cursor has been idle.</item>
/// <item>Motion goes through a <see cref="PlayoutScheduler"/> and a dedicated playback thread with sub-millisecond
/// timing, so bursts from the network are replayed with their original spacing.</item>
/// <item>Discrete events (buttons, wheel, keys) first flush pending motion, so a click lands where the cursor
/// physically was when it happened.</item>
/// </list>
/// All injection happens under one lock, which keeps motion and clicks in order.
/// </summary>
internal sealed class RemoteCursorPlayer : IDisposable
{
    private const long ResyncAfterIdleMs = 150;
    private const long BlockedReportIntervalMs = 10_000;

    private readonly record struct Sample(long PlayAt, int Dx, int Dy);

    private readonly InputInjector _injector;
    private readonly Func<int> _edgeResistance;
    private readonly ILogger _logger;
    private readonly PlayoutScheduler _scheduler = new();
    private readonly object _gate = new();
    private readonly Queue<Sample> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    private volatile bool _stopping;
    private object? _owner;
    private Point _tracked;
    private long _lastInjectTicks;
    private long _lastBlockedReportTicks = -BlockedReportIntervalMs;
    private int _pushLeft;
    private int _pushRight;
    private int _pushTop;
    private int _pushBottom;

    public RemoteCursorPlayer(InputInjector injector, Func<int> edgeResistance, ILogger logger)
    {
        _injector = injector;
        _edgeResistance = edgeResistance;
        _logger = logger;
        _thread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "WinMirroring.CursorPlayback",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Raised (under the player lock, keep it short) when the cursor is pushed out across an edge.</summary>
    public event Action<object, Edge, float>? EdgeReached;

    /// <summary>Raised (off the player lock, at most every 10 s) when an elevated window swallows our input.</summary>
    public event Action<object, string>? InjectionBlocked;

    /// <summary>True while some controller drives the cursor.</summary>
    public bool HasOwner => Volatile.Read(ref _owner) is not null;

    public object? Owner => Volatile.Read(ref _owner);

    public double CurrentDelayMilliseconds
    {
        get
        {
            lock (_gate)
            {
                return _owner is null ? 0 : _scheduler.CurrentDelayMicroseconds / 1000.0;
            }
        }
    }

    public void Configure(bool smoothing, int maxDelayMs)
    {
        lock (_gate)
        {
            _scheduler.Enabled = smoothing;
            _scheduler.MaxDelayMicroseconds = Math.Clamp(maxDelayMs, 0, 200) * 1000L;
        }
    }

    public void Begin(object owner, Point entry)
    {
        lock (_gate)
        {
            _queue.Clear();
            _scheduler.Reset();
            Volatile.Write(ref _owner, owner);
            ResetPush();
            _tracked = entry;
            if (InjectionGuard.IsCursorSuppressed())
            {
                // The last input here was touch: Windows hides the cursor until a mouse moves.
                _injector.Wiggle();
            }

            _injector.MoveAbsolute(entry.X, entry.Y);
            _lastInjectTicks = Environment.TickCount64;
        }
    }

    /// <summary>Plays out what is still queued and gives up the cursor. Returns false if <paramref name="owner"/> did not own it.</summary>
    public bool End(object owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return false;
            }

            FlushLocked();
            Volatile.Write(ref _owner, null);
            ResetPush();
            return true;
        }
    }

    public void Motion(object owner, uint timestamp, int dx, int dy)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }

            var now = MonotonicClock.NowMicroseconds();
            var playAt = _scheduler.Schedule(timestamp, now);
            if (playAt <= now && _queue.Count == 0)
            {
                ApplyLocked(dx, dy);
                return;
            }

            _queue.Enqueue(new Sample(playAt, dx, dy));
        }

        _wake.Set();
    }

    /// <summary>Runs a discrete injection after all pending motion, in order with it.</summary>
    public void Execute(object owner, Action inject)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }

            FlushLocked();
            inject();
        }
    }

    private void PlaybackLoop()
    {
        using var sleeper = new PrecisionSleeper();
        while (!_stopping)
        {
            long next;
            try
            {
                lock (_gate)
                {
                    next = DrainDueLocked(MonotonicClock.NowMicroseconds());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cursor playback failed");
                next = -1;
            }

            if (next < 0)
            {
                _wake.WaitOne(100);
            }
            else
            {
                sleeper.Sleep(Math.Clamp(next - MonotonicClock.NowMicroseconds(), 0, 2000));
            }
        }
    }

    /// <summary>Applies every sample that is due as one movement; returns the next due time or -1.</summary>
    private long DrainDueLocked(long now)
    {
        var dx = 0;
        var dy = 0;
        var any = false;
        while (_queue.Count > 0 && _queue.Peek().PlayAt <= now)
        {
            var sample = _queue.Dequeue();
            dx += sample.Dx;
            dy += sample.Dy;
            any = true;
        }

        if (any)
        {
            ApplyLocked(dx, dy);
        }

        return _queue.Count > 0 ? _queue.Peek().PlayAt : -1;
    }

    private void FlushLocked()
    {
        var dx = 0;
        var dy = 0;
        var any = false;
        while (_queue.Count > 0)
        {
            var sample = _queue.Dequeue();
            dx += sample.Dx;
            dy += sample.Dy;
            any = true;
        }

        if (any)
        {
            ApplyLocked(dx, dy);
        }
    }

    private void ApplyLocked(int dx, int dy)
    {
        if (_owner is null)
        {
            return;
        }

        var nowTicks = Environment.TickCount64;
        if (nowTicks - _lastInjectTicks > ResyncAfterIdleMs && VirtualScreen.TryGetCursor(out var actual) && actual != _tracked)
        {
            if (InjectionGuard.IsForegroundAboveUs(out var title))
            {
                // Windows drops our moves while an elevated window is in front. Keep the position we track, so
                // the cursor can still be taken back across an edge instead of being stuck here.
                ReportBlockedLocked(title, nowTicks);
            }
            else
            {
                // Idle long enough that the difference comes from local input (touch, pen, a local mouse).
                _tracked = actual;
                if (InjectionGuard.IsCursorSuppressed())
                {
                    _injector.Wiggle();
                }
            }
        }

        var screen = VirtualScreen.Current;
        var wantedX = (long)_tracked.X + dx;
        var wantedY = (long)_tracked.Y + dy;
        var x = (int)Math.Clamp(wantedX, screen.Left, screen.Right - 1);
        var y = (int)Math.Clamp(wantedY, screen.Top, screen.Bottom - 1);

        if (x != _tracked.X || y != _tracked.Y)
        {
            _injector.MoveAbsolute(x, y);
            _tracked = new Point(x, y);
        }

        _lastInjectTicks = nowTicks;
        DetectEdge(screen, wantedX, wantedY, x, y);
    }

    private void DetectEdge(Rectangle screen, long wantedX, long wantedY, int x, int y)
    {
        var resistance = Math.Max(1, _edgeResistance());
        Edge? reached = null;

        _pushLeft = wantedX < screen.Left ? _pushLeft + (int)Math.Min(int.MaxValue / 4, screen.Left - wantedX) : 0;
        _pushRight = wantedX > screen.Right - 1 ? _pushRight + (int)Math.Min(int.MaxValue / 4, wantedX - (screen.Right - 1)) : 0;
        _pushTop = wantedY < screen.Top ? _pushTop + (int)Math.Min(int.MaxValue / 4, screen.Top - wantedY) : 0;
        _pushBottom = wantedY > screen.Bottom - 1 ? _pushBottom + (int)Math.Min(int.MaxValue / 4, wantedY - (screen.Bottom - 1)) : 0;

        if (_pushLeft >= resistance)
        {
            reached = Edge.Left;
        }
        else if (_pushRight >= resistance)
        {
            reached = Edge.Right;
        }
        else if (_pushTop >= resistance)
        {
            reached = Edge.Top;
        }
        else if (_pushBottom >= resistance)
        {
            reached = Edge.Bottom;
        }

        if (reached is { } edge && _owner is { } owner)
        {
            ResetPush();
            EdgeReached?.Invoke(owner, edge, EdgeMath.RelativeAlong(screen, edge, new Point(x, y)));
        }
    }

    private void ResetPush() => _pushLeft = _pushRight = _pushTop = _pushBottom = 0;

    private void ReportBlockedLocked(string title, long nowTicks)
    {
        if (nowTicks - _lastBlockedReportTicks < BlockedReportIntervalMs || _owner is not { } owner)
        {
            return;
        }

        _lastBlockedReportTicks = nowTicks;
        _logger.LogInformation("Injected input is blocked by an elevated window in front: {Title}", title);
        _ = Task.Run(() => InjectionBlocked?.Invoke(owner, title));
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
