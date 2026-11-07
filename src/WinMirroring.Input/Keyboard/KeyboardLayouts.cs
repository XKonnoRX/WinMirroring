using System.Globalization;
using System.Runtime.InteropServices;

namespace WinMirroring.Input.Keyboard;

/// <summary>What happened to a request to switch the layout of the window in front.</summary>
public enum LayoutSwitchResult
{
    /// <summary>The window was asked to switch (it does so when it handles the request).</summary>
    Requested,

    /// <summary>That layout is already active there.</summary>
    AlreadyActive,

    /// <summary>No window in front (lock screen, UAC prompt, desktop switching).</summary>
    NoWindow,

    /// <summary>Windows refused: the window in front belongs to an elevated process.</summary>
    Refused,
}

/// <summary>
/// Input languages ("keyboard layouts", HKL) of this device. Keys travel between devices as scan codes, so the layout
/// active on the receiving device decides which letters they produce; these helpers read and switch the layout of
/// the window in front. A layout is passed around as the low 32 bits of its HKL: the low word is the language
/// (0x0419 Russian), the high word the layout itself (0x0419 the standard one, 0xF0xx variants such as Dvorak).
/// </summary>
public static class KeyboardLayouts
{
    private const uint WmInputLangChangeRequest = 0x0050;

    /// <summary>Layout of the window in front (of the thread that owns it).</summary>
    public static bool TryGetForeground(out nint window, out int layout)
    {
        layout = 0;
        window = GetForegroundWindow();
        if (window == 0)
        {
            return false;
        }

        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0)
        {
            return false;
        }

        layout = unchecked((int)(long)GetKeyboardLayout(thread));
        return layout != 0;
    }

    /// <summary>Layout of the calling thread, e.g. of our own window in front.</summary>
    public static int OfCurrentThread() => unchecked((int)(long)GetKeyboardLayout(0));

    /// <summary>Layouts installed for the user (the list in the language bar).</summary>
    public static int[] Installed()
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return [];
        }

        var list = new nint[count];
        count = GetKeyboardLayoutList(count, list);
        return list.Take(Math.Max(0, count)).Select(handle => unchecked((int)(long)handle)).ToArray();
    }

    /// <summary>
    /// The installed layout closest to <paramref name="wanted"/>: the same one, else one of the same language
    /// (US Dvorak → US), else one of the same base language (English UK → English US); null when there is none.
    /// </summary>
    public static int? Match(int wanted, IReadOnlyList<int> installed)
    {
        if (wanted == 0)
        {
            return null;
        }

        foreach (var layout in installed)
        {
            if (layout == wanted)
            {
                return layout;
            }
        }

        foreach (var layout in installed)
        {
            if ((layout & 0xFFFF) == (wanted & 0xFFFF))
            {
                return layout;
            }
        }

        foreach (var layout in installed)
        {
            if ((layout & 0x3FF) == (wanted & 0x3FF))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>
    /// Asks the window in front to switch to <paramref name="layout"/> (an installed one, see <see cref="Match"/>),
    /// the same request the language bar sends. With Windows' default "one input method for all windows" the whole
    /// session follows.
    /// </summary>
    public static LayoutSwitchResult Activate(int layout)
    {
        if (!TryGetForeground(out var window, out var current))
        {
            return LayoutSwitchResult.NoWindow;
        }

        if (current == layout)
        {
            return LayoutSwitchResult.AlreadyActive;
        }

        // The focused control first: some windows (consoles, editors with child windows) only listen there.
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var focus = GetGUIThreadInfo(thread, ref info) && info.hwndFocus != 0 ? info.hwndFocus : window;
        var handle = (nint)layout;
        if (PostMessageW(focus, WmInputLangChangeRequest, 0, handle)
            || (focus != window && PostMessageW(window, WmInputLangChangeRequest, 0, handle)))
        {
            return LayoutSwitchResult.Requested;
        }

        return LayoutSwitchResult.Refused;
    }

    /// <summary>Language of the layout for display: "Русский (Россия)", "English (United States)".</summary>
    public static string DisplayName(int layout)
    {
        var language = layout & 0xFFFF;
        try
        {
            var name = CultureInfo.GetCultureInfo(language).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..] : $"0x{language:X4}";
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentOutOfRangeException)
        {
            return $"0x{language:X4}";
        }
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
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? list);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
