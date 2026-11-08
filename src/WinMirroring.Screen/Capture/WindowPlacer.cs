using System.Drawing;

namespace WinMirroring.Screen.Capture;

/// <summary>Where a window lands when it is moved onto another monitor: same size, centred, never larger than the monitor.</summary>
public static class WindowPlacer
{
    /// <summary>
    /// A rectangle for <paramref name="window"/> centred inside <paramref name="monitor"/>, keeping the window's
    /// size unless it does not fit (then it is shrunk to the monitor). A zero or negative dimension fills that axis.
    /// </summary>
    public static Rectangle PlaceInside(Rectangle window, Rectangle monitor)
    {
        var width = window.Width > 0 ? Math.Min(window.Width, monitor.Width) : monitor.Width;
        var height = window.Height > 0 ? Math.Min(window.Height, monitor.Height) : monitor.Height;
        var x = monitor.Left + (monitor.Width - width) / 2;
        var y = monitor.Top + (monitor.Height - height) / 2;
        return new Rectangle(x, y, width, height);
    }
}
