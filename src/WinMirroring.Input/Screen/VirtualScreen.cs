using System.Drawing;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Screen;

/// <summary>Bounding rectangle of all monitors, in physical pixels (the process is per-monitor DPI aware).</summary>
public static class VirtualScreen
{
    public static Rectangle Current => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
        Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

    public static bool TryGetCursor(out Point point)
    {
        if (GetCursorPos(out var native))
        {
            point = new Point(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    public static void SetCursor(Point point) => SetCursorPos(point.X, point.Y);

    /// <summary>Centre of the primary monitor (which always starts at 0,0), a safe parking spot for the cursor.</summary>
    public static Point PrimaryCenter => new(
        Math.Max(1, GetSystemMetrics(SM_CXSCREEN)) / 2,
        Math.Max(1, GetSystemMetrics(SM_CYSCREEN)) / 2);
}
