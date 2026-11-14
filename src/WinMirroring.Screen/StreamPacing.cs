namespace WinMirroring.Screen;

/// <summary>
/// Keeps one stream inside what the link can actually carry.
///
/// It watches a single number — how long the viewer takes to acknowledge a frame — and moves the frame rate and the
/// bitrate to hold that number inside a band. Without this a stream simply asked for its configured bitrate
/// (12 Mbit/s for a full screen) whatever the link was willing to give: Wi-Fi that could not carry it made TCP
/// queue up, which delayed the acknowledgements, which stalled the send window — so the picture froze rather than
/// getting coarser.
///
/// The first version of this moved far too eagerly. One frame the window stopped waiting for — a single hiccup
/// anywhere, which happens a few times a minute on Wi-Fi — cut the frame rate by a third at once, and climbing back
/// took three seconds a step. The log of a real session shows what that looks like: a target walking 30, 22, 19,
/// 13, 22, 21, 23, 30 while acknowledgements were coming back in forty to seventy milliseconds the whole time.
/// A picture whose rate lurches like that stutters about once a second, which is exactly what it was reported to
/// do. So: a single hiccup is now worth one small step, trouble has to persist to be taken seriously, and getting
/// back up is nearly as quick as coming down.
/// </summary>
internal sealed class StreamPacing
{
    /// <summary>
    /// Acknowledgements slower than this mean the link (or the viewer) is behind. It is deliberately close to one
    /// frame's worth of time: the delay a frame waits before being shown is what this number measures, so letting
    /// it reach a third of a second — as it first did — meant tolerating exactly the lag that was complained about.
    /// </summary>
    private const double HighDelayMs = 120;

    /// <summary>...and faster than this means there is room to ask for more.</summary>
    private const double LowDelayMs = 55;

    private const int MinFps = 12;
    private const double MinShare = 0.2;

    /// <summary>Steps are small on purpose: it is the lurching between rates that is seen, not the rate itself.</summary>
    private const double Down = 0.85;
    private const double Up = 1.15;

    private static readonly TimeSpan AdjustEvery = TimeSpan.FromSeconds(1);

    private double _fps;
    private double _share = 1;
    private int _ceiling;
    private long _changedTicks;
    private long _seenGivenUp;
    private long _changes;
    private bool _started;

    public StreamPacing(int maxFps)
    {
        _ceiling = Math.Clamp(maxFps, 1, 60);
        _fps = _ceiling;
    }

    /// <summary>Frames per second the stream should aim for right now.</summary>
    public int Fps => Math.Max(1, (int)Math.Round(_fps));

    /// <summary>Fraction of the configured bitrate (and of the tile quality range) to ask for right now.</summary>
    public double Share => _share;

    /// <summary>True while the stream is running below what it was configured for.</summary>
    public bool HeldBack => Fps < _ceiling || _share < 0.99;

    /// <summary>How many times the target has moved, so a log line can show whether it is settled or hunting.</summary>
    public long Changes => _changes;

    /// <summary>
    /// Takes the window's view of the link and moves the targets. <paramref name="maxFps"/> comes in every time
    /// because the setting can change under a running stream.
    /// </summary>
    public void Observe(double ackDelayMs, long givenUp, int maxFps, long nowTicks)
    {
        _ceiling = Math.Clamp(maxFps, 1, 60);
        _fps = Math.Min(_fps, _ceiling);
        var stalls = givenUp - _seenGivenUp;
        _seenGivenUp = givenUp;

        if (!_started)
        {
            _started = true;
            _changedTicks = nowTicks;
            return;
        }

        if (nowTicks - _changedTicks < AdjustEvery.TotalMilliseconds)
        {
            return;
        }

        // Frames the window gave up on are the strongest signal there is, but one of them is a hiccup, not a trend:
        // it is worth one step down, the same as a delay over the band, and no more.
        var behind = stalls > 0 || ackDelayMs > HighDelayMs;
        var room = stalls == 0 && ackDelayMs > 0 && ackDelayMs < LowDelayMs;
        if (!behind && !room)
        {
            return;
        }

        _changedTicks = nowTicks;
        _changes++;
        if (behind)
        {
            _fps = Math.Max(MinFps, _fps * Down);
            _share = Math.Max(MinShare, _share * Down);
            return;
        }

        _fps = Math.Min(_ceiling, _fps * Up);
        _share = Math.Min(1, _share * Up);
    }
}
