namespace WinMirroring.Screen;

/// <summary>
/// Keeps one stream inside what the link can actually carry.
///
/// It watches a single number — how long the viewer takes to acknowledge a frame — and steps the frame rate and the
/// bitrate down as soon as that starts to lag, then back up once acknowledgements are prompt again. Without this a
/// stream simply asked for its configured bitrate (12 Mbit/s for a full screen) whatever the link was willing to
/// give: Wi-Fi that could not carry it made TCP queue up, which delayed the acknowledgements, which stalled the
/// send window — so the picture froze rather than getting coarser.
///
/// Backing off is quick and recovering is slow, which is the right way round: a freeze is far worse than a few
/// seconds of a lower frame rate.
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

    private const int MinFps = 8;
    private const double MinShare = 0.15;
    private static readonly TimeSpan BackOffEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StepUpEvery = TimeSpan.FromSeconds(3);

    private double _fps;
    private double _share = 1;
    private int _ceiling;
    private long _changedTicks;
    private long _seenGivenUp;
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

    /// <summary>
    /// Takes the window's view of the link and moves the targets. <paramref name="maxFps"/> comes in every time
    /// because the setting can change under a running stream.
    /// </summary>
    public void Observe(double ackDelayMs, long givenUp, int maxFps, long nowTicks)
    {
        _ceiling = Math.Clamp(maxFps, 1, 60);
        _fps = Math.Min(_fps, _ceiling);
        var stalled = givenUp > _seenGivenUp;
        _seenGivenUp = givenUp;

        if (!_started)
        {
            _started = true;
            _changedTicks = nowTicks;
            return;
        }

        if (stalled || ackDelayMs > HighDelayMs)
        {
            if (nowTicks - _changedTicks < BackOffEvery.TotalMilliseconds)
            {
                return;
            }

            _changedTicks = nowTicks;
            _fps = Math.Max(MinFps, _fps * 0.7);
            _share = Math.Max(MinShare, _share * 0.7);
            return;
        }

        if (ackDelayMs is <= 0 or >= LowDelayMs || nowTicks - _changedTicks < StepUpEvery.TotalMilliseconds)
        {
            return;
        }

        _changedTicks = nowTicks;
        _fps = Math.Min(_ceiling, _fps + 3);
        _share = Math.Min(1, _share * 1.25);
    }
}
