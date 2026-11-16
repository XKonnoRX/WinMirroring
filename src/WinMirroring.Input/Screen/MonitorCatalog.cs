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
            .OrderBy(m => DisplayNumber(m.Key) ?? int.MaxValue)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();

        // Everything that is not the virtual one — and all of them if somehow they all are, since a device with
        // no screen at all is not a case to invent behaviour for.
        var visible = All.Where(m => !m.IsVirtual).ToList();
        _visible = visible.Count > 0 ? visible : All;
    }

    public IReadOnlyList<MonitorInfo> All { get; }

    /// <summary>The real monitors in the order Windows numbers them (Settings → Display → Identify).</summary>
    public IReadOnlyList<MonitorInfo> ForLayout { get; }

    /// <summary>
    /// The monitors with a screen in front of them, which is where the pointer may go.
    ///
    /// The virtual monitor is the question here, and it is two different things on two different days. A window
    /// parked on it is parked there to be hidden, and then the monitor is nowhere: the pointer going there simply
    /// disappears. Shown to another device as its second monitor, the very same monitor is somewhere — somebody
    /// is looking at it — and the pointer belongs there as much as on any screen. So it is asked, every time,
    /// rather than decided when this list was built.
    /// </summary>
    public IReadOnlyList<MonitorInfo> Screens =>
        MonitorCatalog.VirtualMonitorIsHidden() ? _visible : All;

    private readonly IReadOnlyList<MonitorInfo> _visible;

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

    /// <summary>
    /// True when the cursor at <paramref name="at"/> on <paramref name="monitor"/> can go on across that edge onto
    /// another monitor — another monitor with a screen. A virtual monitor beyond an edge does not continue
    /// anything: the pointer going there would simply vanish, and the edge that leads to another device would stop
    /// leading anywhere, which is exactly what happened when a window was parked on one.
    /// </summary>
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
        return Screens.Any(m => m.Bounds.Contains(beyond));
    }

    /// <summary>True when this point is on a monitor nobody can see — a window parked out of sight lives there.</summary>
    public bool IsOutOfSight(Point point) => At(point) is { IsVirtual: true } && MonitorCatalog.VirtualMonitorIsHidden();

    /// <summary>The same point, or the nearest one on a monitor that actually shows something.</summary>
    public Point SnapToScreen(Point point)
    {
        if (!IsOutOfSight(point) && At(point) is not null)
        {
            return point;
        }

        var bounds = Screens.MinBy(m => DistanceSquared(m.Bounds, point))!.Bounds;
        return new Point(Math.Clamp(point.X, bounds.Left, bounds.Right - 1), Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1));
    }

    /// <summary>
    /// "Монитор 2 · 1920×1080 · справа", numbered the way Windows numbers it: by its display device (\\.\DISPLAY2
    /// is the "2" in Windows' display settings), not primary-first — a monitor called 1 here and 2 there is the
    /// first thing that makes a layout look wrong.
    /// </summary>
    public string Describe(MonitorInfo monitor)
    {
        var number = DisplayNumber(monitor.Key) ?? (ForLayout.ToList().IndexOf(monitor) + 1);
        var size = string.Create(CultureInfo.InvariantCulture, $"{monitor.Bounds.Width}×{monitor.Bounds.Height}");
        var where = monitor.Primary ? "основной" : PlaceOf(monitor);
        var name = number > 0 ? $"Монитор {number}" : "Монитор";
        return where is null ? $"{name} · {size}" : $"{name} · {size} · {where}";
    }

    /// <summary>The n of \\.\DISPLAYn, or null for a name of another shape.</summary>
    public static int? DisplayNumber(string key)
    {
        var digits = key.Length;
        while (digits > 0 && char.IsAsciiDigit(key[digits - 1]))
        {
            digits--;
        }

        return digits < key.Length && int.TryParse(key.AsSpan(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
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

    /// <summary>
    /// Whether the virtual monitor is, at this moment, a place nobody can see — true while windows are parked on
    /// it to hide them, false while another device is showing it as its second monitor. The app answers this; on
    /// its own the input side cannot know, and the answer decides whether the pointer may go there at all.
    ///
    /// Hidden by default, which is the safe way round: a pointer kept off a monitor that turns out to be visible
    /// is a nuisance, a pointer lost on one that is not is a mouse that has disappeared.
    /// </summary>
    public static Func<bool> VirtualMonitorIsHidden { get; set; } = static () => true;

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
