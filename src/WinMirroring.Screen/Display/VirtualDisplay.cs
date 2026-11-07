using System.Drawing;
using System.Runtime.InteropServices;
using WinMirroring.Screen.Capture;

namespace WinMirroring.Screen.Display;

public readonly record struct DisplayMode(int Width, int Height, int Frequency);

/// <summary>
/// The monitor of the open-source Virtual Display Driver (IddCx, github.com/VirtualDrivers/Virtual-Display-Driver).
/// The driver creates its monitors when the device starts and cannot add or remove them on the fly, so it keeps one
/// monitor permanently and we attach it to the desktop only while another device shows it, through the ordinary
/// display settings API (no elevation needed). Its resolutions come from the driver's settings file
/// (<see cref="VirtualDisplaySetup"/>).
/// </summary>
public static class VirtualDisplay
{
    private const int EnumCurrentSettings = -1;
    private const uint DisplayDeviceAttachedToDesktop = 0x1;
    private const uint DisplayDevicePrimary = 0x4;
    private const uint DmBitsPerPel = 0x40000;
    private const uint DmPelsWidth = 0x80000;
    private const uint DmPelsHeight = 0x100000;
    private const uint DmDisplayFrequency = 0x400000;
    private const uint DmPosition = 0x20;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;

    private static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>GDI name (\\.\DISPLAYn) of the driver's output, or null when the driver is not installed.</summary>
    public static string? FindDeviceName()
    {
        var device = NewDisplayDevice();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if (IsVirtualDisplay(device))
            {
                return device.DeviceName;
            }

            device = NewDisplayDevice();
        }

        return null;
    }

    public static bool IsAttached(string deviceName) =>
        TryGetDevice(deviceName, out var device) && (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0;

    /// <summary>Modes the driver offers (one per size, the highest refresh rate up to 60 Hz preferred).</summary>
    public static IReadOnlyList<DisplayMode> Modes(string deviceName)
    {
        var modes = new List<DisplayMode>();
        var mode = NewDevMode();
        for (var i = 0; EnumDisplaySettingsExW(deviceName, i, ref mode, 0); i++)
        {
            modes.Add(new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));
            mode = NewDevMode();
        }

        return modes
            .GroupBy(m => (m.Width, m.Height))
            .Select(g => g.Where(m => m.Frequency <= 60).OrderByDescending(m => m.Frequency).FirstOrDefault() is { Width: > 0 } preferred
                ? preferred
                : g.OrderBy(m => m.Frequency).First())
            .ToList();
    }

    /// <summary>
    /// The mode closest to a <paramref name="width"/>×<paramref name="height"/> screen: the exact size, else the same
    /// shape no larger than it, else the closest shape. <paramref name="exact"/> says whether the size matched.
    /// </summary>
    public static DisplayMode? PickMode(IReadOnlyList<DisplayMode> modes, int width, int height, out bool exact)
    {
        exact = false;
        if (modes.Count == 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        if (modes.FirstOrDefault(m => m.Width == width && m.Height == height) is { Width: > 0 } match)
        {
            exact = true;
            return match;
        }

        var aspect = (double)width / height;
        var sameShape = modes
            .Where(m => Math.Abs((double)m.Width / m.Height - aspect) < 0.02 && m.Width <= width && m.Height <= height)
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ToList();
        if (sameShape.Count > 0)
        {
            return sameShape[0];
        }

        return modes
            .OrderBy(m => Math.Abs(Math.Log((double)m.Width / m.Height / aspect)))
            .ThenBy(m => Math.Abs((long)m.Width * m.Height - (long)width * height))
            .First();
    }

    /// <summary>Puts the monitor on the desktop at <paramref name="position"/> in <paramref name="mode"/>.</summary>
    public static bool Attach(string deviceName, DisplayMode mode, Point position, out int result)
    {
        var devMode = NewDevMode();
        devMode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmBitsPerPel | (mode.Frequency > 0 ? DmDisplayFrequency : 0);
        devMode.dmPositionX = position.X;
        devMode.dmPositionY = position.Y;
        devMode.dmPelsWidth = (uint)mode.Width;
        devMode.dmPelsHeight = (uint)mode.Height;
        devMode.dmBitsPerPel = 32;
        devMode.dmDisplayFrequency = (uint)Math.Max(0, mode.Frequency);
        result = ChangeDisplaySettingsExW(deviceName, ref devMode, 0, CdsUpdateRegistry | CdsNoReset, 0);
        if (result != DispChangeSuccessful)
        {
            return false;
        }

        result = ChangeDisplaySettingsExW(null, 0, 0, 0, 0);
        return result == DispChangeSuccessful;
    }

    /// <summary>
    /// Fallback when the legacy call refuses to attach a detached output: "extend" topology, which puts every
    /// connected monitor (the virtual one included) on the desktop. The caller then sets mode and position.
    /// </summary>
    public static bool ExtendTopology()
    {
        const uint SdcTopologyExtend = 0x4;
        const uint SdcApply = 0x80;
        return SetDisplayConfig(0, 0, 0, 0, SdcApply | SdcTopologyExtend) == 0;
    }

    /// <summary>Takes the monitor off the desktop (windows on it move back to the other monitors).</summary>
    public static bool Detach(string deviceName)
    {
        if (!TryGetDevice(deviceName, out var device) || (device.StateFlags & DisplayDeviceAttachedToDesktop) == 0)
        {
            return true;
        }

        if ((device.StateFlags & DisplayDevicePrimary) != 0)
        {
            return false; // never take away the main screen
        }

        var devMode = NewDevMode();
        devMode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
        return ChangeDisplaySettingsExW(deviceName, ref devMode, 0, CdsUpdateRegistry | CdsNoReset, 0) == DispChangeSuccessful
            && ChangeDisplaySettingsExW(null, 0, 0, 0, 0) == DispChangeSuccessful;
    }

    public static MonitorDescription? FindMonitor(string deviceName) =>
        DisplayCatalog.Monitors().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where a new monitor of <paramref name="size"/> goes next to the existing ones, on <paramref name="side"/>
    /// ("right", "left", "top" or "bottom"), aligned with the primary monitor.
    /// </summary>
    public static Point PlaceBeside(IEnumerable<MonitorDescription> others, Size size, string side)
    {
        var list = others.ToList();
        if (list.Count == 0)
        {
            return Point.Empty;
        }

        var union = list.Select(m => m.Bounds).Aggregate(Rectangle.Union);
        var primary = list.FirstOrDefault(m => m.IsPrimary)?.Bounds ?? list[0].Bounds;
        return side switch
        {
            "left" => new Point(union.Left - size.Width, primary.Top),
            "top" => new Point(primary.Left, union.Top - size.Height),
            "bottom" => new Point(primary.Left, union.Bottom),
            _ => new Point(union.Right, primary.Top),
        };
    }

    /// <summary>Effective scale of a monitor of this process (per-monitor DPI aware), in percent.</summary>
    public static int ScalePercentOf(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? (int)Math.Round(dpiX * 100.0 / 96) : 100;

    /// <summary>Physical pixel size of the primary monitor and its scale, as this device would like to be extended to.</summary>
    public static (int Width, int Height, int ScalePercent) PrimaryScreen()
    {
        var primary = DisplayCatalog.Monitors().FirstOrDefault(m => m.IsPrimary) ?? DisplayCatalog.Monitors().FirstOrDefault();
        if (primary is null)
        {
            return (1920, 1080, 100);
        }

        var mode = NewDevMode();
        var (width, height) = EnumDisplaySettingsExW(primary.DeviceName, EnumCurrentSettings, ref mode, 0)
            ? ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight)
            : (primary.Bounds.Width, primary.Bounds.Height);
        return (width, height, ScalePercentOf(primary.Handle));
    }

    /// <summary>
    /// Sets Windows' scaling for the monitor, like Settings → Display → Scale. Windows has no public API for
    /// this; the display config call used here is the one the Settings app uses, so failure is expected on some
    /// builds and only means the scale stays as Windows chose it.
    /// </summary>
    public static bool TrySetScale(string deviceName, int percent)
    {
        if (!TryFindSource(deviceName, out var adapter, out var sourceId))
        {
            return false;
        }

        var get = new DpiScaleGet { header = new DeviceInfoHeader { type = -3, size = Marshal.SizeOf<DpiScaleGet>(), adapterId = adapter, id = sourceId } };
        if (DisplayConfigGetDeviceInfo(ref get) != 0)
        {
            return false;
        }

        var recommended = Math.Abs(get.minScaleRel);
        var wanted = Array.IndexOf(ScaleSteps, ScaleSteps.OrderBy(s => Math.Abs(s - percent)).First());
        var relative = Math.Clamp(wanted - recommended, get.minScaleRel, get.maxScaleRel);
        if (relative == get.curScaleRel)
        {
            return true;
        }

        var set = new DpiScaleSet { header = new DeviceInfoHeader { type = -4, size = Marshal.SizeOf<DpiScaleSet>(), adapterId = adapter, id = sourceId }, scaleRel = relative };
        return DisplayConfigSetDeviceInfo(ref set) == 0;
    }

    private static bool TryFindSource(string deviceName, out Luid adapter, out uint sourceId)
    {
        adapter = default;
        sourceId = 0;
        const uint QdcOnlyActivePaths = 2;
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
        {
            return false;
        }

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0)
        {
            return false;
        }

        foreach (var path in paths.Take((int)pathCount))
        {
            var name = new SourceDeviceName
            {
                header = new DeviceInfoHeader { type = 1, size = Marshal.SizeOf<SourceDeviceName>(), adapterId = path.sourceAdapterId, id = path.sourceId },
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 && string.Equals(name.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                adapter = path.sourceAdapterId;
                sourceId = path.sourceId;
                return true;
            }
        }

        return false;
    }

    private static bool IsVirtualDisplay(DISPLAY_DEVICEW device) =>
        device.DeviceString.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
        || device.DeviceID.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDevice(string deviceName, out DISPLAY_DEVICEW found)
    {
        var device = NewDisplayDevice();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if (string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = device;
                return true;
            }

            device = NewDisplayDevice();
        }

        found = default;
        return false;
    }

    private static DISPLAY_DEVICEW NewDisplayDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };

    private static DEVMODEW NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };

    // ---- interop -----------------------------------------------------------------------------------------

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

    /// <summary>DEVMODEW with the display branch of its union.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid sourceAdapterId;
        public uint sourceId;
        public uint sourceModeInfoIdx;
        public uint sourceStatusFlags;
        public Luid targetAdapterId;
        public uint targetId;
        public uint targetModeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public uint refreshNumerator;
        public uint refreshDenominator;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint targetStatusFlags;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public int infoType;
        public uint id;
        public Luid adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int type;
        public int size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DeviceInfoHeader header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet
    {
        public DeviceInfoHeader header;
        public int scaleRel;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string deviceName, int modeNum, ref DEVMODEW devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODEW devMode, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? deviceName, nint devMode, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, nint paths, uint modeCount, nint modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
