namespace WinMirroring.Clipboard;

/// <summary>
/// Keeps the clipboard at the newest thing that reached this device. What other devices send is applied off the
/// receive loop, each message on its own, and a large one is first pulled over the network — so content that arrived
/// earlier can be ready later than what came after it, and used to land on top of it. Here everything takes a number
/// in the order it arrived (what the user copies on this device takes one too), and shows it again at the moment it
/// is to be put on the clipboard: whatever is by then behind something already applied came too late and is left out.
/// </summary>
internal sealed class ArrivalOrder
{
    private readonly object _gate = new();
    private long _issued;
    private long _applied;

    /// <summary>The place in line of something that has just arrived, or has just been copied here.</summary>
    public long Next() => Interlocked.Increment(ref _issued);

    /// <summary>Something that arrived after <paramref name="arrival"/> is on the clipboard already.</summary>
    public bool IsSuperseded(long arrival)
    {
        lock (_gate)
        {
            return arrival < _applied;
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> unless something newer has been applied, and remembers it as applied once it
    /// has run; false when it came too late. The check and the write are one step, so nothing gets in between.
    /// Only what was really written counts: a newer message that failed, or turned out to hold nothing, does not
    /// keep an older good one off the clipboard.
    /// </summary>
    public bool Apply(long arrival, Action write)
    {
        lock (_gate)
        {
            if (arrival < _applied)
            {
                return false;
            }

            write();
            _applied = arrival;
            return true;
        }
    }

    /// <summary>The user copied something on this device just now: newer than anything still on its way in.</summary>
    public void CopiedHere() => Apply(Next(), static () => { });
}
