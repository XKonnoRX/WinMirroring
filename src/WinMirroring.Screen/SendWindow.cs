namespace WinMirroring.Screen;

/// <summary>
/// How many frame messages one stream may have on the wire before the viewer acknowledges them.
///
/// A slot comes back with an acknowledgement, or on its own once the oldest unacknowledged frame is older than the
/// grace period. That second way is the whole point: with a window of two frames and no timeout, anything that
/// delayed one acknowledgement — a busy window manager on the viewer, a lost packet, a hidden window — stopped the
/// source from even capturing, so a momentary hiccup became a frozen picture. Now a slow viewer gets a stale
/// picture instead, which is what a viewer that cannot keep up should get.
///
/// It is also where the link is measured: the time from sending a frame to its acknowledgement, smoothed, is what
/// <see cref="StreamPacing"/> steers the frame rate and the bitrate by.
/// </summary>
internal sealed class SendWindow : IDisposable
{
    private readonly int _size;
    private readonly long _graceMs;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _acked = new(0);

    /// <summary>When each frame still waiting for an acknowledgement went out, oldest first.</summary>
    private readonly List<long> _inFlight = [];

    private double _delayMs;
    private long _givenUp;

    public SendWindow(int size, TimeSpan grace)
    {
        _size = Math.Max(1, size);
        _graceMs = (long)grace.TotalMilliseconds;
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

    /// <summary>Takes a slot for one frame message, waiting at most the grace period for an acknowledgement.</summary>
    public async Task TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            long wait;
            lock (_gate)
            {
                var now = Environment.TickCount64;
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

            await _acked.WaitAsync((int)wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Gives back a slot taken for a frame that in the end was not sent.</summary>
    public void Return()
    {
        lock (_gate)
        {
            if (_inFlight.Count > 0)
            {
                _inFlight.RemoveAt(_inFlight.Count - 1);
            }
        }
    }

    public void OnAck()
    {
        lock (_gate)
        {
            if (_inFlight.Count == 0)
            {
                return; // an acknowledgement for a frame this window already gave up on
            }

            var elapsed = Environment.TickCount64 - _inFlight[0];
            _inFlight.RemoveAt(0);
            _delayMs = _delayMs <= 0 ? elapsed : (_delayMs * 0.75) + (elapsed * 0.25);
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
