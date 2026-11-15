using System.Diagnostics;

namespace WinMirroring.Screen;

/// <summary>
/// What the link looks like from the window: how long acknowledgements take, how much of that the viewer spends on
/// the frame itself, and how many frames came back late or not at all. The counts only ever grow, so a reader
/// takes two readings and subtracts.
/// </summary>
internal readonly record struct LinkReading(double DelayMs, double ViewerMs, long Acks, long LateAcks, long GivenUp, double PaintMs = 0);

/// <summary>
/// How many frame messages one stream may have on the wire before the viewer acknowledges them.
///
/// A slot comes back with an acknowledgement, or on its own once the oldest unacknowledged frame is older than the
/// grace period. That second way is the whole point: with a window of two frames and no timeout, anything that
/// delayed one acknowledgement — a busy window manager on the viewer, a lost packet, a hidden window — stopped the
/// source from even capturing, so a momentary hiccup became a frozen picture. Now a slow viewer gets a stale
/// picture instead, which is what a viewer that cannot keep up should get.
///
/// A slot stands for a frame that is <em>on the wire</em>, nothing else. It is taken immediately before the frame
/// is written to the connection and not a moment earlier: holding one across the encoder — which is what the source
/// used to do — spent a round trip's worth of the window on work that was not on the link at all, and with a window
/// of two that cost a third of the frame rate. The whole stream then ran at one frame per round trip instead of two.
///
/// It is also where the link is measured, and the measurement is taken on a stopwatch rather than the system tick
/// count: a round trip of seventy milliseconds read through a counter that moves in fifteen-millisecond steps is
/// mostly quantisation noise, and <see cref="StreamPacing"/> steers the bitrate by it.
/// </summary>
internal sealed class SendWindow : IDisposable
{
    private readonly int _size;
    private readonly double _graceMs;
    private readonly double _lateMs;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _acked = new(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>When each frame still waiting for an acknowledgement went out, oldest first.</summary>
    private readonly List<double> _inFlight = [];

    private double _delayMs;
    private double _viewerMs;
    private double _paintMs;
    private long _givenUp;
    private long _acks;
    private long _lateAcks;

    public SendWindow(int size, TimeSpan grace, TimeSpan late)
    {
        _size = Math.Max(1, size);
        _graceMs = grace.TotalMilliseconds;
        _lateMs = late.TotalMilliseconds;
    }

    /// <summary>Smoothed milliseconds from sending a frame to its acknowledgement; 0 before the first one.</summary>
    public double DelayMs
    {
        get
        {
            lock (_gate)
            {
                return _delayMs;
            }
        }
    }

    /// <summary>
    /// Smoothed milliseconds the viewer itself spent on a frame — waiting its turn, decoding it and turning it into
    /// pixels — as reported in its acknowledgements; 0 while no viewer has said. What is left of
    /// <see cref="DelayMs"/> after this is the link.
    /// </summary>
    public double ViewerMs
    {
        get
        {
            lock (_gate)
            {
                return _viewerMs;
            }
        }
    }

    /// <summary>How many frames went out without waiting any longer for an acknowledgement.</summary>
    public long GivenUp
    {
        get
        {
            lock (_gate)
            {
                return _givenUp;
            }
        }
    }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>One consistent look at all of it, for the pacer and the log line.</summary>
    public LinkReading Reading
    {
        get
        {
            lock (_gate)
            {
                return new LinkReading(_delayMs, _viewerMs, _acks, _lateAcks, _givenUp, _paintMs);
            }
        }
    }

    /// <summary>Takes a slot for one frame message, waiting at most the grace period for an acknowledgement.</summary>
    public async Task TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            double wait;
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                if (_inFlight.Count < _size)
                {
                    _inFlight.Add(now);
                    return;
                }

                wait = _graceMs - (now - _inFlight[0]);
                if (wait <= 0)
                {
                    // The viewer is slow, hidden or gone: let the frame go rather than stop the stream for it.
                    _inFlight.RemoveAt(0);
                    _inFlight.Add(now);
                    _givenUp++;
                    return;
                }
            }

            await _acked.WaitAsync((int)Math.Ceiling(wait), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A frame came back. <paramref name="viewerMs"/> is how long the viewer says it held the frame and
    /// <paramref name="paintMs"/> how long its window then took to put a picture on the screen; either is a
    /// negative number from a viewer that does not report it.
    /// </summary>
    public void OnAck(int viewerMs = -1, int paintMs = -1)
    {
        lock (_gate)
        {
            if (_inFlight.Count == 0)
            {
                return; // an acknowledgement for a frame this window already gave up on
            }

            var elapsed = _clock.Elapsed.TotalMilliseconds - _inFlight[0];
            _inFlight.RemoveAt(0);
            _delayMs = _delayMs <= 0 ? elapsed : (_delayMs * 0.75) + (elapsed * 0.25);
            _acks++;
            if (elapsed > _lateMs)
            {
                _lateAcks++;
            }

            if (viewerMs >= 0)
            {
                _viewerMs = _viewerMs <= 0 ? viewerMs : (_viewerMs * 0.75) + (viewerMs * 0.25);
            }

            if (paintMs >= 0)
            {
                _paintMs = _paintMs <= 0 ? paintMs : (_paintMs * 0.75) + (paintMs * 0.25);
            }
        }

        // One waiter only (the stream's own loop), so a spurious extra wake-up costs nothing.
        if (_acked.CurrentCount == 0)
        {
            try
            {
                _acked.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose() => _acked.Dispose();
}
