using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using WinMirroring.Input.Layout;

namespace WinMirroring.Input.Screen;

/// <param name="Key">The display device name (\\.\DISPLAYn): stable while the monitor stays connected.</param>
/// <param name="IsVirtual">The Virtual Display Driver's monitor (another device's screen), not placed in the layout.</param>
public sealed record MonitorInfo(string Key, Rectangle Bounds, bool Primary, bool IsVirtual = false);

/// <summary>Geometry over one device's monitors, in physical pixels: which monitor a point is on, and its open edges.</summary>
public sealed class MonitorSet
{
    public MonitorSet(IReadOnlyList<MonitorInfo> monitors)
    {
        All = monitors.Count > 0 ? monitors : [new MonitorInfo(string.Empty, VirtualScreen.Current, true)];
        ForLayout = All
            .Where(m => !m.IsVirtual && m.Key.Length > 0)
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    public IReadOnlyList<MonitorInfo> All { get; }

    /// <summary>The real monitors in the order they are numbered for the user: primary first, then left to right.</summary>
    public IReadOnlyList<MonitorInfo> ForLayout { get; }

    public MonitorInfo? At(Point point)
    {
        foreach (var monitor in All)
        {
            if (monitor.Bounds.Contains(point))
            {
                return monitor;
            }
        }

        return null;
    }

    public MonitorInfo? Find(string? key) => string.IsNullOrEmpty(key) ? null : All.FirstOrDefault(m => m.Key == key);

    /// <summary>The monitor a point is on, or the closest one for a point off every monitor.</summary>
    public MonitorInfo Nearest(Point point) =>
        At(point) ?? All.MinBy(m => DistanceSquared(m.Bounds, point))!;

    /// <summary>The point itself when it is on a monitor, otherwise the closest point of the closest monitor.</summary>
    public Point Snap(Point point)
    {
        if (At(point) is not null)
        {
            return point;
        }

        var bounds = Nearest(point).Bounds;
        return new Point(Math.Clamp(point.X, bounds.Left, bounds.Right - 1), Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1));
    }

    /// <summary>True when the cursor at <paramref name="at"/> on <paramref name="monitor"/> can go on across that edge onto another monitor.</summary>
    public bool ContinuesBeyond(MonitorInfo monitor, Edge edge, Point at)
    {
        var bounds = monitor.Bounds;
        var beyond = edge switch
        {
            Edge.Left => new Point(bounds.Left - 1, at.Y),
            Edge.Right => new Point(bounds.Right, at.Y),
            Edge.Top => new Point(at.X, bounds.Top - 1),
            _ => new Point(at.X, bounds.Bottom),
        };
        return At(beyond) is not null;
    }

    /// <summary>"Монитор 2 · 1920×1080 · справа" (numbered as in <see cref="ForLayout"/>).</summary>
    public string Describe(MonitorInfo monitor)
    {
        var number = ForLayout.ToList().IndexOf(monitor) + 1;
        var size = string.Create(CultureInfo.InvariantCulture, $"{monitor.Bounds.Width}×{monitor.Bounds.Height}");
        var where = monitor.Primary ? "основной" : PlaceOf(monitor);
        var name = number > 0 ? $"Монитор {number}" : "Монитор";
        return where is null ? $"{name} · {size}" : $"{name} · {size} · {where}";
    }

    public string Signature => string.Join(';', All.Select(m => $"{m.Key}={m.Bounds}{(m.Primary ? "*" : string.Empty)}{(m.IsVirtual ? "v" : string.Empty)}"));

    private string? PlaceOf(MonitorInfo monitor)
    {
        if (All.FirstOrDefault(m => m.Primary) is not { } primary)
        {
            return null;
        }

        var a = monitor.Bounds;
        var p = primary.Bounds;
        if (a.Right <= p.Left)
        {
            return "слева";
        }

        if (a.Left >= p.Right)
        {
            return "справа";
        }

        if (a.Bottom <= p.Top)
        {
            return "сверху";
        }

        return a.Top >= p.Bottom ? "снизу" : null;
    }

    private static long DistanceSquared(Rectangle bounds, Point point)
    {
        long dx = point.X < bounds.Left ? bounds.Left - point.X : point.X >= bounds.Right ? point.X - (bounds.Right - 1) : 0;
        long dy = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y >= bounds.Bottom ? point.Y - (bounds.Bottom - 1) : 0;
        return (dx * dx) + (dy * dy);
    }
}

/// <summary>
/// This device's monitors, re-read at most once a second (the edge logic asks for every mouse movement) or at once
/// after <see cref="Refresh"/>; <see cref="Changed"/> fires when they really change.
/// </summary>
public static class MonitorCatalog
{
    private const long MaxAgeMs = 1000;
    private static readonly object Gate = new();
    private static volatile MonitorSet? _current;
    private static long _readTicks;

    public static event Action? Changed;

    public static MonitorSet Current
    {
        get
        {
            var current = _current;
            if (current is null || Environment.TickCount64 - Volatile.Read(ref _readTicks) > MaxAgeMs)
            {
                current = Refresh();
            }

            return current;
        }
    }

    public static MonitorSet Refresh()
    {
        MonitorSet fresh;
        bool changed;
        lock (Gate)
        {
            fresh = new MonitorSet(Enumerate());
            var previous = _current;
            changed = previous is not null && previous.Signature != fresh.Signature;
            _current = fresh;
            Volatile.Write(ref _readTicks, Environment.TickCount64);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return fresh;
    }

    private static List<MonitorInfo> Enumerate()
    {
        var virtualAdapters = VirtualAdapters();
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(handle, ref info))
            {
                var r = info.rcMonitor;
                monitors.Add(new MonitorInfo(
                    info.szDevice,
                    Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom),
                    (info.dwFlags & MonitorInfoPrimary) != 0,
                    virtualAdapters.Contains(info.szDevice)));
            }

            return true;
        }, 0);
        return monitors;
    }

    /// <summary>Display names driven by the Virtual Display Driver.</summary>
    private static HashSet<string> VirtualAdapters()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if (device.DeviceString.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
                || device.DeviceID.Contains("MttVDD", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(device.DeviceName);
            }

            device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        }

        return names;
    }

    private const uint MonitorInfoPrimary = 1;

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW displayDevice, uint flags);
}
