namespace WinMirroring.Core.Settings;

/// <summary>
/// The button bar shown over a full-screen view: buttons that press keys (or type text) on the device being
/// viewed, grouped in named presets the user creates and switches between. Kept per device, not synced.
/// </summary>
public sealed class ButtonBarSettings
{
    public bool Visible { get; set; } = true;

    /// <summary>Name of the preset shown on the bar.</summary>
    public string ActivePreset { get; set; } = ButtonPreset.MainName;

    public List<ButtonPreset> Presets { get; set; } = ButtonPreset.CreateDefaults();
}

public sealed class ButtonPreset
{
    public const string MainName = "Основные";

    public string Name { get; set; } = string.Empty;

    public List<ButtonBinding> Buttons { get; set; } = [];

    public ButtonPreset Clone() => new() { Name = Name, Buttons = Buttons.Select(b => b.Clone()).ToList() };

    public static List<ButtonPreset> CreateDefaults() =>
    [
        new()
        {
            Name = MainName,
            Buttons =
            [
                new("Esc", "Esc"),
                new("Tab", "Tab"),
                new("Win", "Win"),
                new("Alt+Tab", "Alt+Tab"),
                new("Копировать", "Ctrl+C"),
                new("Вставить", "Ctrl+V"),
                new("Отменить", "Ctrl+Z"),
                new("Ctrl", "Ctrl"),
                new("Shift", "Shift"),
                new("Закрыть окно", "Alt+F4"),
            ],
        },
        new()
        {
            Name = "Браузер",
            Buttons =
            [
                new("← Назад", "Alt+Left"),
                new("Вперёд →", "Alt+Right"),
                new("Обновить", "F5"),
                new("Новая вкладка", "Ctrl+T"),
                new("Закрыть вкладку", "Ctrl+W"),
                new("Вернуть вкладку", "Ctrl+Shift+T"),
                new("След. вкладка", "Ctrl+Tab"),
                new("Во весь экран", "F11"),
            ],
        },
        new()
        {
            Name = "Текст",
            Buttons =
            [
                new("Выделить всё", "Ctrl+A"),
                new("Копировать", "Ctrl+C"),
                new("Вырезать", "Ctrl+X"),
                new("Вставить", "Ctrl+V"),
                new("Отменить", "Ctrl+Z"),
                new("Повторить", "Ctrl+Y"),
                new("Найти", "Ctrl+F"),
                new("Сохранить", "Ctrl+S"),
                new("Enter", "Enter"),
                new("⌫", "Backspace"),
            ],
        },
    ];
}

/// <summary>One button: <see cref="Keys"/> is pressed first (if any), then <see cref="Text"/> is typed (if any).</summary>
public sealed class ButtonBinding
{
    public ButtonBinding()
    {
    }

    public ButtonBinding(string label, string keys, string? text = null)
    {
        Label = label;
        Keys = keys;
        Text = text;
    }

    public string Label { get; set; } = string.Empty;

    /// <summary>Key chords in order, e.g. "Ctrl+Shift+T" or "Ctrl+K, Ctrl+C"; modifiers alone make a latching button.</summary>
    public string Keys { get; set; } = string.Empty;

    public string? Text { get; set; }

    public ButtonBinding Clone() => new(Label, Keys, Text);
}
