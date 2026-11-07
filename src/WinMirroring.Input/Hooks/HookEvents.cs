namespace WinMirroring.Input.Hooks;

/// <summary>One low-level mouse hook message. <see cref="IsOurInjection"/> is true for events this process injected itself.</summary>
public readonly record struct MouseHookEvent(uint Message, int X, int Y, uint MouseData, bool IsInjected, bool IsOurInjection)
{
    /// <summary>Signed high word of mouseData: wheel delta, or 1/2 for X buttons.</summary>
    public short HighWord => unchecked((short)(MouseData >> 16));
}

/// <summary>One low-level keyboard hook message.</summary>
public readonly record struct KeyboardHookEvent(uint VirtualKey, uint ScanCode, bool IsUp, bool IsExtended, bool IsInjected, bool IsOurInjection);
