namespace WinMirroring.Screen;

/// <summary>
/// Keeps one stream inside what the link can actually carry, by moving one number: the share of the configured
/// bitrate it asks for. Wi-Fi that cannot carry twelve megabits a second makes TCP queue up, which delays the
/// acknowledgements, which stalls the send window — so without this the picture froze rather than getting coarser.
///
/// It no longer touches the frame rate. The send window is already the rate controller — a stream can only go as
/// fast as its frames are acknowledged — and a second, slower limiter on top of it could only ever take away rate
/// the link was willing to carry. Worse, the two argued: the window would find thirty frames a second while this
/// was asking for fourteen.
///
/// And it judges on a count, not on a smoothed delay. The smoothed round trip of a real session sits around
/// seventy milliseconds while individual frames come back anywhere between thirty and two hundred, so a smoothed
/// value compared against thresholds crosses them several times a second: the earlier version of this moved its
/// target up or down about once a second, every second, for hours, and each move changed the encoder's bitrate.
/// A bitrate change makes a hardware encoder restart its rate control and emit a big frame, and a big frame on a
/// loaded link is a visible hitch — which is exactly the once-or-twice-a-second stutter that was reported. Now it
/// asks a question a single late frame cannot answer: of the frames acknowledged in the last three seconds, how
/// many were late? Only a quarter of them being late moves anything.
/// </summary>
internal sealed class StreamPacing
{
    /// <summary>Steps are small and rare on purpose: it is the lurching between settings that is seen.</summary>
    private const double Down = 0.8;
    private const double Up = 1.1;

    /// <summary>
    /// Except when there is nothing to weigh up. A link where most frames come back late is not marginal, and
    /// stepping down a fifth at a time would spend fifteen seconds walking to a bitrate it could have had in six.
    /// </summary>
    private const double FarDown = 0.6;

    /// <summary>
    /// Never below a third of what was configured. Below that the picture is mush, and a stream nobody can read is
    /// not a better outcome than a stream that drops a frame now and then.
    /// </summary>
    private const double MinShare = 1.0 / 3;

    /// <summary>This many of the last window's frames coming back late means the link is loaded...</summary>
    private const double TooManyLate = 0.25;

    /// <summary>...and this many means it is not a close call (see <see cref="FarDown"/>).</summary>
    private const double FarTooLate = 0.5;

    /// <summary>...and this few means there is room to ask for more.</summary>
    private const double FewLate = 0.05;

    /// <summary>Fewer acknowledgements than this in a window says nothing; wait for more rather than guess.</summary>
    private const int EnoughAcks = 8;

    private static readonly TimeSpan AdjustEvery = TimeSpan.FromSeconds(3);

    private double _share = 1;
    private int _ceiling;
    private long _changedTicks;
    private long _changes;
    private LinkReading _seen;
    private bool _started;

    public StreamPacing(int maxFps) => _ceiling = Math.Clamp(maxFps, 1, 60);

    /// <summary>
    /// Frames per second the stream should aim for: what it was configured for. The window decides what actually
    /// goes out — see the remarks above on why nothing else should.
    /// </summary>
    public int Fps => _ceiling;

    /// <summary>Fraction of the configured bitrate (and of the tile quality range) to ask for right now.</summary>
    public double Share => _share;

    /// <summary>True while the stream is running below what it was configured for.</summary>
    public bool HeldBack => _share < 0.99;

    /// <summary>How many times the target has moved, so a log line can show whether it is settled or hunting.</summary>
    public long Changes => _changes;

    /// <summary>
    /// Takes the window's view of the link and moves the bitrate target. <paramref name="maxFps"/> comes in every
    /// time because the setting can change under a running stream.
    /// </summary>
    public void Observe(LinkReading reading, int maxFps, long nowTicks)
    {
        _ceiling = Math.Clamp(maxFps, 1, 60);
        if (!_started)
        {
            _started = true;
            _seen = reading;
            _changedTicks = nowTicks;
            return;
        }

        if (nowTicks - _changedTicks < AdjustEvery.TotalMilliseconds)
        {
            return;
        }

        var acks = reading.Acks - _seen.Acks;
        var late = reading.LateAcks - _seen.LateAcks;

        // A frame the window stopped waiting for is a frame that was later than late, and it never shows up as an
        // acknowledgement at all, so it is counted on both sides of the fraction.
        var stalls = reading.GivenUp - _seen.GivenUp;
        if (acks + stalls < EnoughAcks)
        {
            return; // not enough to judge yet; the next frame asks again
        }

        _seen = reading;
        _changedTicks = nowTicks;
        var lateShare = (late + stalls) / (double)(acks + stalls);
        if (lateShare > TooManyLate)
        {
            Move(lateShare > FarTooLate ? FarDown : Down);
            return;
        }

        if (lateShare < FewLate && _share < 1)
        {
            Move(Up);
        }
    }

    /// <summary>Moves the share, and only counts a change that actually changed something: a stream sitting on its
    /// floor is settled, and a log line saying it moved thirty times would say the opposite.</summary>
    private void Move(double by)
    {
        var next = Math.Clamp(_share * by, MinShare, 1);
        if (Math.Abs(next - _share) < 0.0001)
        {
            return;
        }

        _changes++;
        _share = next;
    }
}
