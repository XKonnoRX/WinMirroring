using System;
using System.Collections.Generic;
using System.Linq;
using WinMirroring.Core;
using WinMirroring.Core.Settings;
using WinMirroring.Input.Keyboard;

namespace WinMirroring.App.Services;

/// <summary>A button of the bar, ready to press: its key chords, then its text.</summary>
/// <param name="Latch">Ctrl, Shift or Alt alone: the button holds them until pressed again.</param>
public sealed record BarButton(string Label, IReadOnlyList<KeyChord> Chords, string? Text, bool Latch);

/// <summary>
/// The button bar over full-screen views: whether it is shown, which preset it shows, and the presets themselves
/// (kept in this device's settings). Views and the control panel follow <see cref="Changed"/>.
/// </summary>
public sealed class ButtonBarService
{
    private readonly MirroringNode _node;

    public ButtonBarService(MirroringNode node)
    {
        _node = node;
        if (Settings.Presets.Count > 0 && Active is null)
        {
            Settings.ActivePreset = Settings.Presets[0].Name;
        }
    }

    /// <summary>Raised on the thread that made the change (the UI thread: only the UI changes these).</summary>
    public event Action? Changed;

    private ButtonBarSettings Settings => _node.Settings.ButtonBar;

    public bool Visible => Settings.Visible;

    public IReadOnlyList<ButtonPreset> Presets => Settings.Presets;

    public string ActiveName => Active?.Name ?? string.Empty;

    private ButtonPreset? Active => Settings.Presets.FirstOrDefault(p => p.Name == Settings.ActivePreset) ?? Settings.Presets.FirstOrDefault();

    /// <summary>The active preset's buttons; a button whose keys cannot be read is left out.</summary>
    public IReadOnlyList<BarButton> ActiveButtons
    {
        get
        {
            var buttons = new List<BarButton>();
            foreach (var binding in Active?.Buttons ?? [])
            {
                if (ToButton(binding, out _) is { } button)
                {
                    buttons.Add(button);
                }
            }

            return buttons;
        }
    }

    /// <summary>Checks one binding; null with the reason when its keys cannot be read or it does nothing.</summary>
    public static BarButton? ToButton(ButtonBinding binding, out string? error)
    {
        if (!KeyChord.TryParseSequence(binding.Keys, out var chords, out error))
        {
            return null;
        }

        var text = string.IsNullOrEmpty(binding.Text) ? null : binding.Text;
        if (chords.Count == 0 && text is null)
        {
            error = "нет ни клавиш, ни текста";
            return null;
        }

        // Ctrl, Shift or Alt alone latch (held until pressed again); Win alone is a tap, which opens Start.
        var latch = chords.Count == 1 && chords[0].IsModifiersOnly && !chords[0].Modifiers.Contains(KeyChord.Win) && text is null;
        var label = string.IsNullOrWhiteSpace(binding.Label) ? (chords.Count > 0 ? KeyChord.FormatSequence(chords) : text!) : binding.Label.Trim();
        return new BarButton(label, chords, text, latch);
    }

    public void SetVisible(bool visible)
    {
        if (Settings.Visible == visible)
        {
            return;
        }

        Settings.Visible = visible;
        Save();
    }

    public void SetActive(string name)
    {
        if (Settings.ActivePreset == name || Settings.Presets.All(p => p.Name != name))
        {
            return;
        }

        Settings.ActivePreset = name;
        Save();
    }

    /// <summary>Replaces all presets (from the editor) and shows <paramref name="active"/>.</summary>
    public void SavePresets(IEnumerable<ButtonPreset> presets, string? active)
    {
        Settings.Presets = presets.Select(p => p.Clone()).ToList();
        Settings.ActivePreset = active is not null && Settings.Presets.Any(p => p.Name == active)
            ? active
            : Settings.Presets.FirstOrDefault()?.Name ?? string.Empty;
        Save();
    }

    /// <summary>A copy of every preset for editing.</summary>
    public List<ButtonPreset> CopyPresets() => Settings.Presets.Select(p => p.Clone()).ToList();

    public static List<ButtonPreset> DefaultPresets() => ButtonPreset.CreateDefaults();

    private void Save()
    {
        _node.SaveSettings();
        Changed?.Invoke();
    }
}
