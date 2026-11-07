using System.Drawing;
using WinMirroring.Screen.Native;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen.Capture;

public sealed record MonitorDescription(int Index, nint Handle, string DeviceName, Rectangle Bounds, bool IsPrimary);

public sealed record WindowDescription(nint Handle, string Title, Rectangle Bounds, uint ProcessId);

/// <summary>Monitors and shareable top-level windows of this device.</summary>
public static class DisplayCatalog
{
    private const uint MonitorInfoPrimary = 1;

    public static IReadOnlyList<MonitorDescription> Monitors()
    {
        var result = new List<MonitorDescription>();
        EnumDisplayMonitors(0, 0, (nint handle, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEXW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(handle, ref info))
            {
                result.Add(new MonitorDescription(0, handle, info.szDevice, info.rcMonitor.ToRectangle(), (info.dwFlags & MonitorInfoPrimary) != 0));
            }

            return true;
        }, 0);

        // Primary first, then left to right: stable indices that match what a person expects.
        return result
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .Select((m, i) => m with { Index = i })
            .ToList();
    }

    /// <summary>Visible, titled, non-tool top-level windows of other processes, in Z order.</summary>
    public static IReadOnlyList<WindowDescription> Windows()
    {
        var own = (uint)Environment.ProcessId;
        var result = new List<WindowDescription>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetAncestor(hwnd, GA_ROOT) != hwnd)
            {
                return true;
            }

            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            var title = GetTitle(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (title.Length == 0 || pid == own || GetVisibleBounds(hwnd) is not { Width: > 40, Height: > 40 } bounds)
            {
                return true;
            }

            result.Add(new WindowDescription(hwnd, title, bounds, pid));
            return true;
        }, 0);

        return result;
    }
}
