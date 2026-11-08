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

    /// <summary>A viewing device pressed at (<paramref name="x"/>, <paramref name="y"/>) in physical screen pixels.</summary>
    public static void NoteDown(int x, int y)
    {
        lock (Gate)
        {
            _x = x;
            _y = y;
            _at = Environment.TickCount64;
        }
    }

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
