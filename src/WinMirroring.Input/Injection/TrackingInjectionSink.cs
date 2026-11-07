using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Injection;

/// <summary>
/// Wraps a sink and remembers which keys and buttons it put down, so <see cref="ReleaseAll"/> can lift them if the
/// connection drops mid-press. The elevated helper uses this: if the app dies while a button is held, nothing stays
/// stuck down on the controlled device.
/// </summary>
public sealed class TrackingInjectionSink : IInjectionSink
{
    private readonly IInjectionSink _inner;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _downKeys = [];
    private readonly HashSet<MouseButton> _downButtons = [];

    public TrackingInjectionSink(IInjectionSink inner) => _inner = inner;

    public bool IsConnected => _inner.IsConnected;

    public void MoveAbsolute(int x, int y) => _inner.MoveAbsolute(x, y);

    public void MoveRelative(int dx, int dy) => _inner.MoveRelative(dx, dy);

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

        _inner.Button(button, down);
    }

    public void Wheel(int delta, bool horizontal) => _inner.Wheel(delta, horizontal);

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

        _inner.Key(virtualKey, scanCode, up, extended);
    }

    public void Unicode(char ch, bool up) => _inner.Unicode(ch, up);

    public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts) => _inner.TouchFrame(contacts);

    /// <summary>Lifts every key and button still held down.</summary>
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
            _inner.Key(vk, scan, up: true, extended);
        }

        foreach (var button in buttons)
        {
            _inner.Button(button, down: false);
        }
    }
}
