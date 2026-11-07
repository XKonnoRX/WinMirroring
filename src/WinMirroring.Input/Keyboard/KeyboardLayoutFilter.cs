namespace WinMirroring.Input.Keyboard;

/// <summary>
/// Watches the layout of the window in front and picks out the user's own switches (hotkey, language bar) from
/// everything else that changes it: a switch made here on another device's request, and another window coming to
/// front with its own layout (Windows' "use a different input method for each app window" option).
/// Not thread-safe: the caller serializes access.
/// </summary>
public sealed class KeyboardLayoutFilter
{
    /// <summary>How long a requested switch is waited for before it is considered refused.</summary>
    public const long ExpectMs = 1500;

    private nint _window;
    private int _layout;
    private int _expected;
    private long _expectedUntil;

    /// <summary>Forgets everything: the next observation only sets the starting point.</summary>
    public void Reset()
    {
        _window = 0;
        _layout = 0;
        _expected = 0;
    }

    /// <summary>We are switching to <paramref name="layout"/> ourselves: that change is not the user's.</summary>
    public void Expect(int layout, long nowMs)
    {
        _expected = layout;
        _expectedUntil = nowMs + ExpectMs;
    }

    /// <summary>One look at the window in front; returns the layout the user just switched to, or null.</summary>
    public int? Observe(nint window, int layout, long nowMs)
    {
        if (window == 0 || layout == 0)
        {
            return null;
        }

        if (_expected != 0 && nowMs > _expectedUntil)
        {
            _expected = 0;
        }

        var previousWindow = _window;
        var previousLayout = _layout;
        _window = window;
        _layout = layout;

        if (layout == _expected)
        {
            _expected = 0;
            return null;
        }

        if (previousLayout == 0 || layout == previousLayout || window != previousWindow)
        {
            return null;
        }

        return layout;
    }
}
