using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinMirroring.App.Services;
using WinMirroring.Core.Settings;
using WinMirroring.Input.Keyboard;

namespace WinMirroring.App.Views;

/// <summary>
/// Creates and edits the presets of the views' button bar: a preset is a named row of buttons, each pressing keys
/// (typed, or recorded by pressing them) and/or typing text on the device being viewed. Works on a copy; nothing
/// changes until saved, and the saved preset becomes the one shown.
/// </summary>
public partial class ButtonPresetsWindow : Window
{
    private readonly ButtonBarService _bar;
    private readonly List<ButtonPreset> _presets;
    private readonly ObservableCollection<string> _items = [];
    private readonly List<ushort> _recordModifiers = [];
    private ButtonPreset? _preset;
    private bool _updating;
    private bool _recording;
    private bool _recordSawKey;

    public ButtonPresetsWindow(ButtonBarService bar)
    {
        InitializeComponent();
        _bar = bar;
        _presets = bar.CopyPresets();
        ButtonList.ItemsSource = _items;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        ReloadPresets(_presets.FirstOrDefault(p => p.Name == bar.ActiveName) ?? _presets.FirstOrDefault());
    }

    private ButtonBinding? SelectedButton =>
        _preset is not null && ButtonList.SelectedIndex >= 0 && ButtonList.SelectedIndex < _preset.Buttons.Count
            ? _preset.Buttons[ButtonList.SelectedIndex]
            : null;

    // ---- presets -----------------------------------------------------------------------------------------

    private void ReloadPresets(ButtonPreset? select)
    {
        _updating = true;
        PresetBox.ItemsSource = null;
        PresetBox.ItemsSource = _presets;
        PresetBox.SelectedItem = select;
        _updating = false;
        ShowPreset(select);
    }

    private void ShowPreset(ButtonPreset? preset)
    {
        _preset = preset;
        _updating = true;
        PresetNameBox.Text = preset?.Name ?? string.Empty;
        PresetNameBox.IsEnabled = preset is not null;
        _updating = false;
        ReloadButtons(preset is { Buttons.Count: > 0 } ? 0 : -1);
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            ShowPreset(PresetBox.SelectedItem as ButtonPreset);
        }
    }

    private void OnPresetNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _preset is null)
        {
            return;
        }

        _preset.Name = PresetNameBox.Text.Trim();
        _updating = true;
        PresetBox.Items.Refresh();
        PresetBox.SelectedItem = _preset;
        _updating = false;
        StatusText.Text = string.Empty;
    }

    private void OnNewPreset(object sender, RoutedEventArgs e)
    {
        var preset = new ButtonPreset { Name = UniqueName("Новый набор") };
        _presets.Add(preset);
        ReloadPresets(preset);
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
    }

    private void OnCopyPreset(object sender, RoutedEventArgs e)
    {
        if (_preset is null)
        {
            return;
        }

        var copy = _preset.Clone();
        copy.Name = UniqueName(_preset.Name + " (копия)");
        _presets.Add(copy);
        ReloadPresets(copy);
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (_preset is null
            || MessageBox.Show(this, $"Удалить набор «{_preset.Name}»?", "Кнопки трансляции", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var index = _presets.IndexOf(_preset);
        _presets.Remove(_preset);
        ReloadPresets(_presets.Count == 0 ? null : _presets[Math.Clamp(index, 0, _presets.Count - 1)]);
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        ButtonPreset? first = null;
        foreach (var preset in ButtonBarService.DefaultPresets())
        {
            preset.Name = UniqueName(preset.Name);
            _presets.Add(preset);
            first ??= preset;
        }

        ReloadPresets(first);
    }

    private string UniqueName(string wanted)
    {
        var name = wanted;
        for (var n = 2; _presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
        {
            name = $"{wanted} ({n})";
        }

        return name;
    }

    // ---- buttons -----------------------------------------------------------------------------------------

    private void ReloadButtons(int select)
    {
        _updating = true;
        _items.Clear();
        foreach (var button in _preset?.Buttons ?? [])
        {
            _items.Add(Describe(button));
        }

        ButtonList.SelectedIndex = select;
        _updating = false;
        ShowButton();
    }

    private static string Describe(ButtonBinding button)
    {
        var parsed = ButtonBarService.ToButton(button, out _);
        var label = string.IsNullOrWhiteSpace(button.Label) ? "(без подписи)" : button.Label.Trim();
        var action = string.Join(" + ", new[] { button.Keys.Trim(), string.IsNullOrEmpty(button.Text) ? null : $"«{button.Text}»" }.Where(s => !string.IsNullOrEmpty(s)));
        var latch = parsed?.Latch == true ? " (залипает)" : string.Empty;
        return parsed is null ? $"⚠ {label} — {action}" : $"{label} — {action}{latch}";
    }

    private void OnButtonSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            StopRecording();
            ShowButton();
        }
    }

    private void ShowButton()
    {
        var button = SelectedButton;
        EditorPanel.IsEnabled = button is not null;
        _updating = true;
        LabelBox.Text = button?.Label ?? string.Empty;
        KeysBox.Text = button?.Keys ?? string.Empty;
        TypedTextBox.Text = button?.Text ?? string.Empty;
        _updating = false;
        Validate(button);
    }

    private void OnButtonEdited(object sender, TextChangedEventArgs e)
    {
        if (_updating || SelectedButton is not { } button)
        {
            return;
        }

        button.Label = LabelBox.Text;
        button.Keys = KeysBox.Text;
        button.Text = TypedTextBox.Text.Length == 0 ? null : TypedTextBox.Text;
        Validate(button);

        // Keep the list's line in step without losing the selection (and the caret in the box being typed in).
        var index = ButtonList.SelectedIndex;
        _updating = true;
        _items[index] = Describe(button);
        ButtonList.SelectedIndex = index;
        _updating = false;
        StatusText.Text = string.Empty;
    }

    private void Validate(ButtonBinding? button)
    {
        if (button is null)
        {
            ButtonError.Text = string.Empty;
            return;
        }

        var parsed = ButtonBarService.ToButton(button, out var error);
        ButtonError.Foreground = parsed is null ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.SeaGreen;
        ButtonError.Text = parsed is null
            ? $"Эта кнопка не появится: {error}."
            : parsed.Latch
                ? $"Залипающая: {KeyChord.FormatSequence(parsed.Chords)} держится там, пока не нажмёте её снова."
                : parsed.Chords.Count > 0
                    ? $"Нажмёт там: {KeyChord.FormatSequence(parsed.Chords)}{(parsed.Text is null ? string.Empty : ", затем наберёт текст")}."
                    : "Наберёт там текст.";
    }

    private void OnAddButton(object sender, RoutedEventArgs e)
    {
        if (_preset is null)
        {
            OnNewPreset(sender, e);
        }

        var index = Math.Min(ButtonList.SelectedIndex < 0 ? _preset!.Buttons.Count : ButtonList.SelectedIndex + 1, _preset!.Buttons.Count);
        _preset.Buttons.Insert(index, new ButtonBinding("Кнопка", string.Empty));
        ReloadButtons(index);
        LabelBox.Focus();
        LabelBox.SelectAll();
    }

    private void OnRemoveButton(object sender, RoutedEventArgs e)
    {
        if (_preset is null || SelectedButton is not { } button)
        {
            return;
        }

        var index = ButtonList.SelectedIndex;
        _preset.Buttons.Remove(button);
        ReloadButtons(Math.Min(index, _preset.Buttons.Count - 1));
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int step)
    {
        if (_preset is null || SelectedButton is not { } button)
        {
            return;
        }

        var index = ButtonList.SelectedIndex;
        var target = index + step;
        if (target < 0 || target >= _preset.Buttons.Count)
        {
            return;
        }

        _preset.Buttons.RemoveAt(index);
        _preset.Buttons.Insert(target, button);
        ReloadButtons(target);
    }

    // ---- recording keys ----------------------------------------------------------------------------------

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (RecordToggle.IsChecked == true)
        {
            _recording = true;
            _recordSawKey = false;
            _recordModifiers.Clear();
            ButtonError.Foreground = System.Windows.Media.Brushes.SteelBlue;
            ButtonError.Text = "Нажмите сочетание клавиш…";
        }
        else
        {
            StopRecording();
            Validate(SelectedButton);
        }
    }

    private void StopRecording()
    {
        _recording = false;
        _recordSawKey = false;
        _recordModifiers.Clear();
        RecordToggle.IsChecked = false;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
        {
            return;
        }

        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (KeyChord.AsModifier(vk) is { } modifier)
        {
            if (!_recordModifiers.Contains(modifier))
            {
                _recordModifiers.Add(modifier);
            }

            return; // wait for the key that goes with it (or for it alone to be let go)
        }

        _recordSawKey = true;
        Record(new KeyChord(_recordModifiers.Concat(CurrentModifiers()).Distinct().ToList(), vk));
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) && !_recordSawKey && _recordModifiers.Count > 0)
        {
            Record(new KeyChord(_recordModifiers.ToList(), 0));
        }
    }

    private static IEnumerable<ushort> CurrentModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            yield return KeyChord.Ctrl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            yield return KeyChord.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            yield return KeyChord.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            yield return KeyChord.Win;
        }
    }

    private void Record(KeyChord chord)
    {
        StopRecording();
        KeysBox.Text = chord.ToString();
        if (SelectedButton is { } button && (string.IsNullOrWhiteSpace(button.Label) || button.Label == "Кнопка"))
        {
            LabelBox.Text = chord.ToString();
        }
    }

    // ---- saving ------------------------------------------------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_presets.Any(p => string.IsNullOrWhiteSpace(p.Name)))
        {
            StatusText.Text = "У каждого набора должно быть название.";
            return;
        }

        var duplicate = _presets.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            StatusText.Text = $"Два набора называются «{duplicate.Key}».";
            return;
        }

        foreach (var preset in _presets)
        {
            preset.Name = preset.Name.Trim();
            foreach (var button in preset.Buttons)
            {
                if (ButtonBarService.ToButton(button, out var error) is null)
                {
                    ReloadPresets(preset);
                    ButtonList.SelectedIndex = preset.Buttons.IndexOf(button);
                    StatusText.Text = $"«{preset.Name}», кнопка «{button.Label}»: {error}.";
                    return;
                }
            }
        }

        _bar.SavePresets(_presets, _preset?.Name);
        Close();
    }
}
