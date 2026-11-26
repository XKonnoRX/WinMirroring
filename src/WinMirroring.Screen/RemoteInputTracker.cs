namespace WinMirroring.Screen;

/// <summary>
/// Remembers where a viewing device last pressed on this screen. This app's own overlays (the floating button and the
/// control panel) are kept out of capture, so that device cannot see them; with this they can tell its press from a
/// local one and ignore it instead of opening a menu nobody over there can see.
/// </summary>
public static class RemoteInputTracker
{
    private const int TolerancePixels = 12;
    private const long FreshMs = 800;

    private static readonly object Gate = new();
    private static int _x;
    private static int _y;
    private static long _at = long.MinValue / 2;

    /// <summary>When a viewing device last pressed anything here — a button, a finger or a key.</summary>
    private static long _active = long.MinValue / 2;

    /// <summary>A viewing device pressed at (<paramref name="x"/>, <paramref name="y"/>) in physical screen pixels.</summary>
    public static void NoteDown(int x, int y)
    {
        lock (Gate)
        {
            _x = x;
            _y = y;
            _at = Environment.TickCount64;
            _active = _at;
        }
    }

    /// <summary>
    /// A finger of a viewing device left the screen here (peer id, stream id). Raised on whatever thread the touch
    /// arrived on: a handler that looks at what the touch did does it elsewhere.
    /// </summary>
    public static event Action<string, uint>? TouchEnded;

    public static void NoteTouchEnded(string peerId, uint streamId) => TouchEnded?.Invoke(peerId, streamId);

    /// <summary>A viewing device pressed a key here.</summary>
    public static void NoteKey() => Volatile.Write(ref _active, Environment.TickCount64);

    /// <summary>
    /// True when a viewing device pressed something here within the last <paramref name="milliseconds"/>: what is
    /// being done on this screen right now is, as likely as not, being done from there.
    /// </summary>
    public static bool ActiveWithin(long milliseconds) => Environment.TickCount64 - Volatile.Read(ref _active) <= milliseconds;

    /// <summary>True when a press at this physical screen point is the one a viewing device just made.</summary>
    public static bool IsRemote(double x, double y)
    {
        lock (Gate)
        {
            return Environment.TickCount64 - _at <= FreshMs
                && Math.Abs(x - _x) <= TolerancePixels
                && Math.Abs(y - _y) <= TolerancePixels;
        }
    }
}
