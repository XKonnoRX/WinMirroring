using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Tests;

public sealed class ButtonBarSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinMirroring.ButtonBarTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AFreshDeviceHasTheStandardPresetsShown()
    {
        var settings = new SettingsStore(_directory).Load();

        Assert.True(settings.ButtonBar.Visible);
        Assert.Equal(["Основные", "Браузер", "Текст"], settings.ButtonBar.Presets.Select(p => p.Name));
        Assert.Equal(ButtonPreset.MainName, settings.ButtonBar.ActivePreset);
    }

    [Fact]
    public void SavedPresetsComeBackAsTheyWereWithoutTheStandardOnesAddedAgain()
    {
        var store = new SettingsStore(_directory);
        var settings = store.Load();
        settings.ButtonBar.Visible = false;
        settings.ButtonBar.Presets = [new ButtonPreset { Name = "Игры", Buttons = [new ButtonBinding("Прыжок", "Space"), new ButtonBinding("Чат", "Enter", "gg")] }];
        settings.ButtonBar.ActivePreset = "Игры";
        store.Save(settings);

        var loaded = new SettingsStore(_directory).Load().ButtonBar;

        Assert.False(loaded.Visible);
        Assert.Equal("Игры", loaded.ActivePreset);
        var preset = Assert.Single(loaded.Presets);
        Assert.Equal(["Прыжок", "Чат"], preset.Buttons.Select(b => b.Label));
        Assert.Equal("gg", preset.Buttons[1].Text);
        Assert.Null(preset.Buttons[0].Text);
    }

    [Fact]
    public void SettingsFromAnOlderVersionGetTheStandardPresets()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{ "deviceName": "PC" }""");

        var settings = new SettingsStore(_directory).Load();

        Assert.Equal("PC", settings.DeviceName);
        Assert.Equal(3, settings.ButtonBar.Presets.Count);
    }

    [Fact]
    public void ACloneDoesNotShareButtons()
    {
        var preset = ButtonPreset.CreateDefaults()[0];
        var copy = preset.Clone();
        copy.Buttons[0].Label = "changed";
        copy.Buttons.RemoveAt(1);

        Assert.Equal("Esc", preset.Buttons[0].Label);
        Assert.Equal(preset.Buttons.Count - 1, copy.Buttons.Count);
    }
}
