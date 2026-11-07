using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Injects straight into this process's session with SendInput and InjectTouchInput. Everything carries the
/// injection marker in dwExtraInfo so our own hooks can tell it from physical input. This is both the default sink
/// in the normal app and the one the elevated helper uses to carry out what it receives.
/// </summary>
public sealed class LocalInjectionSink : IInjectionSink
{
    private const uint KeyeventfUnicode = 0x0004;

    // Touch injection (moved here so it can run in the elevated helper too).
    private const int MaxTouchContacts = 10;
    private const uint PtTouch = 2;
    private const uint TouchMaskContactArea = 0x00000001;
    private const uint TouchMaskOrientation = 0x00000002;
    private const uint TouchMaskPressure = 0x00000004;
    private const uint TouchFeedbackDefault = 0x1;
    private const int ContactRadius = 2;

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private readonly Lazy<bool> _touchReady = new(() => InitializeTouchInjection(MaxTouchContacts, TouchFeedbackDefault));
    private readonly ILogger? _logger;
    private bool _touchWarned;

    public LocalInjectionSink(ILogger? logger = null) => _logger = logger;

    public bool IsConnected => true;

    public void MoveAbsolute(int x, int y)
    {
        // ceil(p * 65536 / size): Windows maps back with floor(n * size / 65536), which then lands exactly on p.
        var screen = VirtualScreen.Current;
        var width = Math.Max(1, screen.Width);
        var height = Math.Max(1, screen.Height);
        var nx = (int)Math.Clamp(((long)(x - screen.Left) * 65536 + width - 1) / width, 0, 65535);
        var ny = (int)Math.Clamp(((long)(y - screen.Top) * 65536 + height - 1) / height, 0, 65535);

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, dwExtraInfo = InjectionMarker } },
        });
    }

    public void MoveRelative(int dx, int dy) => Send(new INPUT
    {
        type = INPUT_MOUSE,
        u = { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE, dwExtraInfo = InjectionMarker } },
    });

    public void Button(MouseButton button, bool down)
    {
        uint flags;
        uint data = 0;
        switch (button)
        {
            case MouseButton.Left: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case MouseButton.Right: flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case MouseButton.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MouseButton.X1: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case MouseButton.X2: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: return;
        }

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { mouseData = data, dwFlags = flags, dwExtraInfo = InjectionMarker } },
        });
    }

    public void Wheel(int delta, bool horizontal) => Send(new INPUT
    {
        type = INPUT_MOUSE,
        u = { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, dwExtraInfo = InjectionMarker } },
    });

    public void Key(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        var input = new INPUT { type = INPUT_KEYBOARD };
        if (scanCode != 0)
        {
            flags |= KEYEVENTF_SCANCODE;
            if (extended)
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }

            input.u.ki = new KEYBDINPUT { wScan = scanCode, dwFlags = flags, dwExtraInfo = InjectionMarker };
        }
        else
        {
            input.u.ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = flags, dwExtraInfo = InjectionMarker };
        }

        Send(input);
    }

    public void Unicode(char ch, bool up) => Send(new INPUT
    {
        type = INPUT_KEYBOARD,
        u = { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode | (up ? KEYEVENTF_KEYUP : 0u), dwExtraInfo = InjectionMarker } },
    });

    public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts)
    {
        if (contacts.Count == 0)
        {
            return;
        }

        if (!_touchReady.Value)
        {
            if (!_touchWarned)
            {
                _touchWarned = true;
                _logger?.LogWarning("Touch injection is unavailable on this device (error {Error})", Marshal.GetLastWin32Error());
            }

            return;
        }

        var frame = new POINTER_TOUCH_INFO[contacts.Count];
        for (var i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            frame[i] = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PtTouch,
                    pointerId = c.Id,
                    pointerFlags = c.Flags,
                    ptPixelLocation = new POINT { X = c.X, Y = c.Y },
                },
                touchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure,
                rcContact = new RECT { Left = c.X - ContactRadius, Top = c.Y - ContactRadius, Right = c.X + ContactRadius, Bottom = c.Y + ContactRadius },
                orientation = 90,
                pressure = 32000,
            };
        }

        if (!InjectTouchInput((uint)frame.Length, frame))
        {
            _logger?.LogDebug("InjectTouchInput failed: {Error}", Marshal.GetLastWin32Error());
        }
    }

    private static void Send(INPUT input) => SendInput(1, [input], InputSize);

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
