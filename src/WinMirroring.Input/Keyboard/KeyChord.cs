using System.Globalization;

namespace WinMirroring.Input.Keyboard;

/// <summary>
/// One key combination as virtual-key codes: modifiers held while <see cref="Key"/> is tapped. A chord with no key
/// is modifiers alone (a latching button holds them). Written as "Ctrl+Shift+T"; a sequence as "Ctrl+K, Ctrl+C".
/// </summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key)
{
    public const ushort Ctrl = 0xA2;
    public const ushort Shift = 0xA0;
    public const ushort Alt = 0xA4;
    public const ushort Win = 0x5B;

    private static readonly ushort[] ModifierOrder = [Ctrl, Shift, Alt, Win];

    private static readonly Dictionary<string, ushort> ByName = BuildNames(out NameByKey);

    private static readonly Dictionary<ushort, string> NameByKey;

    /// <summary>Keys sent with the extended-key flag (the navigation block, right-hand modifiers, Win, …).</summary>
    private static readonly HashSet<ushort> Extended =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,
        0xA6, 0xA7, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    ];

    public bool IsModifiersOnly => Key == 0;

    public static bool IsExtended(ushort virtualKey) => Extended.Contains(virtualKey);

    /// <summary>The left-hand code for any Shift/Ctrl/Alt/Win key (generic, left or right), else null.</summary>
    public static ushort? AsModifier(ushort virtualKey) => virtualKey switch
    {
        0x11 or 0xA2 or 0xA3 => Ctrl,
        0x10 or 0xA0 or 0xA1 => Shift,
        0x12 or 0xA4 or 0xA5 => Alt,
        0x5B or 0x5C => Win,
        _ => null,
    };

    /// <summary>Parses "Ctrl+Shift+T" or a sequence "Ctrl+K, Ctrl+C". Returns false, with the part it did not understand.</summary>
    public static bool TryParseSequence(string? text, out IReadOnlyList<KeyChord> chords, out string? error)
    {
        var result = new List<KeyChord>();
        chords = result;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParse(part, out var chord, out error))
            {
                return false;
            }

            result.Add(chord);
        }

        return true;
    }

    public static bool TryParse(string text, out KeyChord chord, out string? error)
    {
        chord = new KeyChord([], 0);
        error = null;
        var modifiers = new List<ushort>();
        ushort key = 0;

        // "Ctrl++" means Ctrl and the plus key: an empty token after a '+' is the '+' itself.
        var tokens = text.Replace("++", "+Plus", StringComparison.Ordinal).Split('+', StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            if (!ByName.TryGetValue(token, out var code))
            {
                error = $"непонятная клавиша «{token}»";
                return false;
            }

            if (AsModifier(code) is { } modifier)
            {
                if (!modifiers.Contains(modifier))
                {
                    modifiers.Add(modifier);
                }
            }
            else if (key != 0)
            {
                error = $"в «{text}» две обычные клавиши; для нескольких нажатий подряд разделите их запятой";
                return false;
            }
            else
            {
                key = code;
            }
        }

        if (modifiers.Count == 0 && key == 0)
        {
            error = $"в «{text}» нет ни одной клавиши";
            return false;
        }

        chord = new KeyChord(ModifierOrder.Where(modifiers.Contains).ToList(), key);
        return true;
    }

    public static string FormatSequence(IEnumerable<KeyChord> chords) => string.Join(", ", chords.Select(c => c.ToString()));

    /// <summary>"Ctrl+Shift+T".</summary>
    public override string ToString()
    {
        var parts = ModifierOrder.Where(Modifiers.Contains).Select(NameOf).ToList();
        if (Key != 0)
        {
            parts.Add(NameOf(Key));
        }

        return string.Join("+", parts);
    }

    public static string NameOf(ushort virtualKey) =>
        NameByKey.TryGetValue(virtualKey, out var name) ? name : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>The first name of each key is how it is written; the rest are accepted when reading.</summary>
    private static Dictionary<string, ushort> BuildNames(out Dictionary<ushort, string> nameByKey)
    {
        var names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        var written = new Dictionary<ushort, string>();
        nameByKey = written;

        void Add(ushort code, params string[] aliases)
        {
            written.TryAdd(code, aliases[0]);
            foreach (var alias in aliases)
            {
                names.TryAdd(alias, code);
            }
        }

        Add(Ctrl, "Ctrl", "Control", "Ctl");
        Add(Shift, "Shift");
        Add(Alt, "Alt", "Menu");
        Add(Win, "Win", "Windows", "Meta", "Super");
        for (var c = 'A'; c <= 'Z'; c++)
        {
            Add(c, c.ToString());
        }

        for (var d = 0; d <= 9; d++)
        {
            Add((ushort)('0' + d), d.ToString(CultureInfo.InvariantCulture), "D" + d.ToString(CultureInfo.InvariantCulture));
            Add((ushort)(0x60 + d), "Num" + d.ToString(CultureInfo.InvariantCulture), "NumPad" + d.ToString(CultureInfo.InvariantCulture));
        }

        for (var f = 1; f <= 24; f++)
        {
            Add((ushort)(0x6F + f), "F" + f.ToString(CultureInfo.InvariantCulture));
        }

        Add(0x1B, "Esc", "Escape");
        Add(0x09, "Tab");
        Add(0x0D, "Enter", "Return");
        Add(0x20, "Space", "Пробел");
        Add(0x08, "Backspace", "Back", "BS");
        Add(0x2E, "Delete", "Del");
        Add(0x2D, "Insert", "Ins");
        Add(0x24, "Home");
        Add(0x23, "End");
        Add(0x21, "PageUp", "PgUp", "Prior");
        Add(0x22, "PageDown", "PgDn", "Next");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x2C, "PrintScreen", "PrtSc", "Snapshot");
        Add(0x13, "Pause", "Break");
        Add(0x14, "CapsLock", "Capital");
        Add(0x90, "NumLock");
        Add(0x91, "ScrollLock", "Scroll");
        Add(0x5D, "Apps", "ContextMenu");
        Add(0xBD, "Minus", "-", "OemMinus");
        Add(0xBB, "Plus", "=", "Equals", "OemPlus");
        Add(0xBC, "Comma", "OemComma");
        Add(0xBE, "Period", ".", "OemPeriod");
        Add(0xBF, "Slash", "/", "OemQuestion");
        Add(0xBA, "Semicolon", ";", "OemSemicolon");
        Add(0xDE, "Quote", "'", "OemQuotes");
        Add(0xDB, "BracketLeft", "[", "OemOpenBrackets");
        Add(0xDD, "BracketRight", "]", "OemCloseBrackets");
        Add(0xDC, "Backslash", "\\", "OemPipe");
        Add(0xC0, "Backquote", "`", "Tilde", "OemTilde");
        Add(0x6B, "NumAdd", "Add");
        Add(0x6D, "NumSubtract", "Subtract");
        Add(0x6A, "NumMultiply", "Multiply");
        Add(0x6F, "NumDivide", "Divide");
        Add(0x6E, "NumDecimal", "Decimal");
        Add(0xAD, "VolumeMute");
        Add(0xAE, "VolumeDown");
        Add(0xAF, "VolumeUp");
        Add(0xB3, "MediaPlayPause", "PlayPause");
        Add(0xB0, "MediaNext", "NextTrack");
        Add(0xB1, "MediaPrevious", "PrevTrack");
        Add(0xB2, "MediaStop");
        Add(0xA6, "BrowserBack");
        Add(0xA7, "BrowserForward");
        Add(0xA3, "RCtrl", "RightCtrl");
        Add(0xA1, "RShift", "RightShift");
        Add(0xA5, "RAlt", "RightAlt", "AltGr");
        Add(0x5C, "RWin");
        return names;
    }
}
