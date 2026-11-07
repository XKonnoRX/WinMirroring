using System.Runtime.InteropServices;
using WinMirroring.Input.Native;
using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Replays events from a controlling device through SendInput. Everything injected carries the marker in
/// dwExtraInfo, and pressed keys/buttons are tracked so they can be released when control leaves.
/// </summary>
public sealed class InputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private readonly object _gate = new();
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _downKeys = [];
    private readonly HashSet<MouseButton> _downButtons = [];

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

        lock (_gate)
        {
            if (down)
            {
                _downButtons.Add(button);
            }
            else
            {
                _downButtons.Remove(button);
            }
        }

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { mouseData = data, dwFlags = flags, dwExtraInfo = InjectionMarker } },
        });
    }

    public bool IsButtonDown(MouseButton button)
    {
        lock (_gate)
        {
            return _downButtons.Contains(button);
        }
    }

    /// <summary>A button event that is not remembered for <see cref="ReleaseAll"/> (the caller owns its release).</summary>
    public void ButtonUntracked(MouseButton button, bool down)
    {
        var flags = (button, down) switch
        {
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            _ => 0u,
        };

        if (flags != 0)
        {
            Send(new INPUT
            {
                type = INPUT_MOUSE,
                u = { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = InjectionMarker } },
            });
        }
    }

    /// <summary>
    /// Moves the cursor one pixel and back. A drag-and-drop loop re-checks the window under the cursor only on
    /// mouse movement, so this makes it notice a window that just appeared there.
    /// </summary>
    public void Nudge()
    {
        if (!VirtualScreen.TryGetCursor(out var cursor))
        {
            return;
        }

        var screen = VirtualScreen.Current;
        var x = cursor.X + 1 < screen.Right ? cursor.X + 1 : cursor.X - 1;
        MoveAbsolute(x, cursor.Y);
        MoveAbsolute(cursor.X, cursor.Y);
    }

    public void Wheel(int delta, bool horizontal)
    {
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, dwExtraInfo = InjectionMarker } },
        });
    }

    /// <summary>Keys go by scan code so the target's keyboard layout decides what they mean.</summary>
    public void Key(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        lock (_gate)
        {
            if (up)
            {
                _downKeys.Remove(virtualKey);
            }
            else
            {
                _downKeys[virtualKey] = (scanCode, extended);
            }
        }

        SendKey(virtualKey, scanCode, up, extended);
    }

    /// <summary>Types characters as Unicode packets (no layout involved): what a touch keyboard or IME produced.</summary>
    public static void TypeText(string text)
    {
        const uint KeyeventfUnicode = 0x0004;
        foreach (var ch in text)
        {
            if (ch == '\r' || ch == '\n')
            {
                SendKey(0x0D, 0, up: false, extended: false);
                SendKey(0x0D, 0, up: true, extended: false);
                continue;
            }

            foreach (var up in (ReadOnlySpan<bool>)[false, true])
            {
                Send(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode | (up ? KEYEVENTF_KEYUP : 0u), dwExtraInfo = InjectionMarker } },
                });
            }
        }
    }

    /// <summary>Releases every key and button we still hold down; called when the controller leaves or disconnects.</summary>
    public void ReleaseAll()
    {
        List<(ushort Vk, ushort Scan, bool Extended)> keys;
        List<MouseButton> buttons;
        lock (_gate)
        {
            keys = _downKeys.Select(k => (k.Key, k.Value.Scan, k.Value.Extended)).ToList();
            buttons = _downButtons.ToList();
            _downKeys.Clear();
            _downButtons.Clear();
        }

        foreach (var (vk, scan, extended) in keys)
        {
            SendKey(vk, scan, up: true, extended);
        }

        foreach (var button in buttons)
        {
            Button(button, down: false);
        }
    }

    private static void SendKey(ushort virtualKey, ushort scanCode, bool up, bool extended)
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

    private static void Send(INPUT input) => SendInput(1, [input], InputSize);
}
