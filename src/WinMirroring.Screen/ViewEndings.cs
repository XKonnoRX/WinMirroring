namespace WinMirroring.Screen;

/// <summary>
/// Why a view ended, read for one thing: whether it is worth opening again without being asked.
///
/// A view that ends because somebody ended it — closed here, stopped there, the window it showed closed — is over.
/// One that ends because the connection under it went away is not over in anybody's mind: the person watching did
/// nothing, and what they want is what they had. That used to mean opening it again by hand every time, and on a
/// network that changes under the session (a VPN switched on at the other end re-routes everything and resets the
/// connection) that was several times an evening.
/// </summary>
public static class ViewEndings
{
    /// <summary>
    /// <paramref name="lostConnection"/>: this device saw the session close, or the stream break, under the view.
    /// <paramref name="sourceReason"/>: what the source said when it stopped the stream, if it said anything —
    /// "shutdown" is its application closing (an update restarts it a moment later), "connection…" and "error" are
    /// its own end of the stream failing. Anything else it says is a decision, and is respected.
    /// </summary>
    public static bool CanResume(bool lostConnection, string? sourceReason) =>
        lostConnection
        || sourceReason is "shutdown" or "error"
        || (sourceReason is not null && sourceReason.StartsWith("connection", StringComparison.Ordinal));

    /// <summary>
    /// How many times a view to one device may be reopened in a row before it is left closed: a stream that fails
    /// the same way every time it starts would otherwise be started for ever.
    /// </summary>
    public const int MostInARow = 3;

    /// <summary>...and what "in a row" means: this many within this long.</summary>
    public static readonly TimeSpan InARowWithin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether one more reopening is allowed, given when the earlier ones were (in milliseconds on any steady
    /// clock). Removes the ones that are too old to count and, when the answer is yes, adds this one.
    /// </summary>
    public static bool AllowAnother(List<long> earlier, long now)
    {
        earlier.RemoveAll(at => now - at > InARowWithin.TotalMilliseconds);
        if (earlier.Count >= MostInARow)
        {
            return false;
        }

        earlier.Add(now);
        return true;
    }
}
