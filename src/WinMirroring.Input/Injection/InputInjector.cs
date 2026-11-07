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
        var screen = VirtualScreen.Current;
        var width = Math.Max(1, screen.Width - 1);
        var height = Math.Max(1, screen.Height - 1);
        var nx = (int)(((long)(x - screen.Left) * 65535 + width / 2) / width);
        var ny = (int)(((long)(y - screen.Top) * 65535 + height / 2) / height);

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
