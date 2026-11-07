using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinMirroring.Screen.Injection;

/// <summary>
/// Real touch contacts on this device (InjectTouchInput), so swipes scroll, pinches zoom and press-and-hold acts
/// as it does on a touch screen, instead of everything becoming mouse clicks. Works without touch hardware.
/// Every injection carries all active contacts, and a keepalive re-sends them while fingers rest.
/// </summary>
internal sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;
    private const uint PtTouch = 2;
    private const uint PointerFlagInRange = 0x00000002;
    private const uint PointerFlagInContact = 0x00000004;
    private const uint PointerFlagDown = 0x00010000;
    private const uint PointerFlagUpdate = 0x00020000;
    private const uint PointerFlagUp = 0x00040000;
    private const uint PointerFlagCanceled = 0x00008000;
    private const uint TouchMaskContactArea = 0x00000001;
    private const uint TouchMaskOrientation = 0x00000002;
    private const uint TouchMaskPressure = 0x00000004;
    private const uint TouchFeedbackDefault = 0x1;
    private const int ContactRadius = 2;

    private static readonly Lazy<bool> Initialized = new(() => InitializeTouchInjection(MaxContacts, TouchFeedbackDefault));

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Point> _contacts = [];
    private readonly Timer _keepAlive;
    private long _lastInjectTicks;
    private bool _warned;

    public TouchInjector(ILogger logger)
    {
        _logger = logger;
        _keepAlive = new Timer(_ => KeepAlive(), null, 50, 50);
    }

    public enum Action : byte
    {
        Down = 1,
        Move = 2,
        Up = 3,
        Cancel = 4,
    }

    public void Apply(Action action, uint id, Point point)
    {
        if (!Initialized.Value)
        {
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning("Touch injection is unavailable on this device (error {Error})", Marshal.GetLastWin32Error());
            }

            return;
        }

        id %= MaxContacts;
        lock (_gate)
        {
            switch (action)
            {
                case Action.Down:
                    _contacts[id] = point;
                    Inject(id, PointerFlagDown | PointerFlagInRange | PointerFlagInContact);
                    break;

                case Action.Move when _contacts.ContainsKey(id):
                    _contacts[id] = point;
                    Inject(id, PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact);
                    break;

                case Action.Up when _contacts.ContainsKey(id):
                    _contacts[id] = point;
                    Inject(id, PointerFlagUp);
                    _contacts.Remove(id);
                    break;

                case Action.Cancel when _contacts.ContainsKey(id):
                    Inject(id, PointerFlagUp | PointerFlagCanceled);
                    _contacts.Remove(id);
                    break;
            }
        }
    }

    /// <summary>Lifts every finger (stream stopped, connection lost).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var id in _contacts.Keys.ToList())
            {
                Inject(id, PointerFlagUp | PointerFlagCanceled);
                _contacts.Remove(id);
            }
        }
    }

    private void KeepAlive()
    {
        lock (_gate)
        {
            if (_contacts.Count == 0 || Environment.TickCount64 - _lastInjectTicks < 50)
            {
                return;
            }

            Inject(null, 0);
        }
    }

    /// <summary>Injects all contacts; <paramref name="changed"/> gets <paramref name="flags"/>, the others an update.</summary>
    private void Inject(uint? changed, uint flags)
    {
        var frame = new POINTER_TOUCH_INFO[_contacts.Count];
        var i = 0;
        foreach (var (id, point) in _contacts)
        {
            var contactFlags = id == changed ? flags : PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact;
            frame[i++] = Contact(id, point, contactFlags);
        }

        if (frame.Length > 0 && !InjectTouchInput((uint)frame.Length, frame))
        {
            _logger.LogDebug("InjectTouchInput failed: {Error}", Marshal.GetLastWin32Error());
        }

        _lastInjectTicks = Environment.TickCount64;
    }

    private static POINTER_TOUCH_INFO Contact(uint id, Point point, uint flags) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PtTouch,
            pointerId = id,
            pointerFlags = flags,
            ptPixelLocation = new POINT { X = point.X, Y = point.Y },
        },
        touchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure,
        rcContact = new RECT { Left = point.X - ContactRadius, Top = point.Y - ContactRadius, Right = point.X + ContactRadius, Bottom = point.Y + ContactRadius },
        orientation = 90,
        pressure = 32000,
    };

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public nint sourceDevice;
        public nint hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public int buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
}
