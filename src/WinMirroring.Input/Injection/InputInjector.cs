using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Replays events from a controlling device. It keeps the policy — which keys and buttons are held, so they can be
/// released when control leaves, and the cursor geometry for nudging — and hands the actual OS calls to an
/// <see cref="IInjectionSink"/>. By default that is <see cref="InjectionRouter.Instance"/>, which injects locally or,
/// when control of administrator windows is on, through the elevated helper.
/// </summary>
public sealed class InputInjector
{
    private readonly IInjectionSink _sink;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _downKeys = [];
    private readonly HashSet<MouseButton> _downButtons = [];

    public InputInjector(IInjectionSink? sink = null) => _sink = sink ?? InjectionRouter.Instance;

    public void MoveAbsolute(int x, int y) => _sink.MoveAbsolute(x, y);

    public void Button(MouseButton button, bool down)
    {
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

        _sink.Button(button, down);
    }

    public bool IsButtonDown(MouseButton button)
    {
        lock (_gate)
        {
            return _downButtons.Contains(button);
        }
    }

    /// <summary>A button event that is not remembered for <see cref="ReleaseAll"/> (the caller owns its release).</summary>
    public void ButtonUntracked(MouseButton button, bool down) => _sink.Button(button, down);

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
        _sink.MoveAbsolute(x, cursor.Y);
        _sink.MoveAbsolute(cursor.X, cursor.Y);
    }

    /// <summary>
    /// A relative one-pixel move and back. Unlike absolute moves, it counts as a mouse being used, so a cursor
    /// that Windows hid after touch input becomes visible again.
    /// </summary>
    public void Wiggle()
    {
        _sink.MoveRelative(1, 0);
        _sink.MoveRelative(-1, 0);
    }

    public void Wheel(int delta, bool horizontal) => _sink.Wheel(delta, horizontal);

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

        _sink.Key(virtualKey, scanCode, up, extended);
    }

    /// <summary>Types characters as Unicode packets (no layout involved): what a touch keyboard or IME produced.</summary>
    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r' || ch == '\n')
            {
                _sink.Key(0x0D, 0, up: false, extended: false);
                _sink.Key(0x0D, 0, up: true, extended: false);
                continue;
            }

            _sink.Unicode(ch, up: false);
            _sink.Unicode(ch, up: true);
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
            _sink.Key(vk, scan, up: true, extended);
        }

        foreach (var button in buttons)
        {
            _sink.Button(button, down: false);
        }
    }
}
