using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Injection;

/// <summary>One touch contact in an injected frame: position in physical screen pixels and the POINTER_FLAG bits.</summary>
public readonly record struct TouchContactInfo(uint Id, int X, int Y, uint Flags);

/// <summary>
/// Where injected input finally reaches the OS. The default sink (<see cref="LocalInjectionSink"/>) calls SendInput and
/// InjectTouchInput in this process. When "control of administrator windows" is on, an <see cref="InjectionRouter"/>
/// forwards the same calls to a separate elevated helper process instead, so only that tiny helper needs the extra
/// rights while this process stays at normal integrity (and keeps file drag-and-drop working).
/// </summary>
public interface IInjectionSink
{
    /// <summary>False when a remote sink has lost its helper; the router then falls back to local injection.</summary>
    bool IsConnected { get; }

    /// <summary>Absolute move, coordinates in physical screen pixels (the sink normalizes to the virtual desktop).</summary>
    void MoveAbsolute(int x, int y);

    /// <summary>Relative move in raw units (used to wiggle the cursor back into view).</summary>
    void MoveRelative(int dx, int dy);

    void Button(MouseButton button, bool down);

    void Wheel(int delta, bool horizontal);

    /// <summary>Key by scan code (<paramref name="scanCode"/> != 0) or by virtual key.</summary>
    void Key(ushort virtualKey, ushort scanCode, bool up, bool extended);

    /// <summary>A single character as a Unicode packet (no layout involved).</summary>
    void Unicode(char ch, bool up);

    /// <summary>All touch contacts currently down, injected as one frame.</summary>
    void TouchFrame(IReadOnlyList<TouchContactInfo> contacts);
}
