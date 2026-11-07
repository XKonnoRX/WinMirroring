namespace WinMirroring.Input.Timing;

/// <summary>
/// Adaptive jitter buffer for mouse motion. Each sample carries the controller's capture time; the scheduler
/// estimates the one-way offset (minimum of arrival − capture over time) and the recent jitter above it, and
/// returns a local play-out time so samples are replayed with their original spacing even when the network
/// delivers them in bursts. Latency itself cannot be removed: the buffer only spends a few milliseconds to hide
/// variation, never more than <see cref="MaxDelayMicroseconds"/>.
/// </summary>
public sealed class PlayoutScheduler
{
    /// <summary>Headroom added on top of the observed jitter.</summary>
    public const long MarginMicroseconds = 1_000;

    /// <summary>After this much silence the clocks are re-synchronised from scratch.</summary>
    private const long ResetAfterIdleMicroseconds = 10_000_000;

    private bool _initialized;
    private uint _lastRemoteWire;
    private long _remote;
    private long _minOffset;
    private long _lastArrival;
    private double _jitterPeak;
    private long _lastPlayAt;

    public PlayoutScheduler(bool enabled = true, long maxDelayMicroseconds = 30_000)
    {
        Enabled = enabled;
        MaxDelayMicroseconds = maxDelayMicroseconds;
    }

    public bool Enabled { get; set; }

    public long MaxDelayMicroseconds { get; set; }

    /// <summary>Buffer delay chosen for the most recent sample.</summary>
    public long CurrentDelayMicroseconds { get; private set; }

    public void Reset()
    {
        _initialized = false;
        CurrentDelayMicroseconds = 0;
    }

    /// <param name="remoteWire">Capture time on the controller (wrapping microseconds).</param>
    /// <param name="arrival">Local arrival time in microseconds (<see cref="MonotonicClock"/>).</param>
    /// <returns>Local time at which the sample should be applied; never earlier than the previous one.</returns>
    public long Schedule(uint remoteWire, long arrival)
    {
        if (!Enabled)
        {
            _initialized = false;
            CurrentDelayMicroseconds = 0;
            return arrival;
        }

        if (!_initialized || arrival - _lastArrival > ResetAfterIdleMicroseconds)
        {
            _initialized = true;
            _remote = remoteWire;
            _lastRemoteWire = remoteWire;
            _minOffset = arrival - _remote;
            _lastArrival = arrival;
            _jitterPeak = 0;
            _lastPlayAt = long.MinValue;
        }
        else
        {
            _remote += unchecked((int)(remoteWire - _lastRemoteWire));
            _lastRemoteWire = remoteWire;

            var elapsed = Math.Max(0, arrival - _lastArrival);
            _lastArrival = arrival;

            // Let the minimum creep up (0.5 ms per second) so clock drift and route changes are followed,
            // and let the jitter peak decay (10 ms per second) so one spike does not keep the buffer large.
            _minOffset += elapsed / 2000;
            _jitterPeak = Math.Max(0, _jitterPeak - elapsed / 100.0);
        }

        var offset = arrival - _remote;
        if (offset < _minOffset)
        {
            _minOffset = offset;
        }

        var jitter = offset - _minOffset;
        if (jitter > _jitterPeak)
        {
            _jitterPeak = jitter;
        }

        var delay = Math.Clamp((long)_jitterPeak + MarginMicroseconds, 0, Math.Max(0, MaxDelayMicroseconds));
        CurrentDelayMicroseconds = delay;

        var playAt = _remote + _minOffset + delay;
        if (playAt < _lastPlayAt)
        {
            playAt = _lastPlayAt;
        }

        _lastPlayAt = playAt;
        return playAt;
    }
}
