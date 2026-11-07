using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMirroring.App.Views;
using WinMirroring.Screen;

namespace WinMirroring.App.ViewModels;

/// <summary>Screen and window streaming: device actions, settings and outgoing streams.</summary>
public sealed partial class MainViewModel
{
    private readonly ScreenStreamingService _screen;
    private bool _screenInitialized;

    public ObservableCollection<ActiveScreenSource> ScreenSources { get; } = [];

    [ObservableProperty]
    public partial bool ScreenAllowViewing { get; set; }

    [ObservableProperty]
    public partial bool ScreenAllowControl { get; set; }

    [ObservableProperty]
    public partial bool ScreenWindowDrag { get; set; }

    [ObservableProperty]
    public partial int ScreenMaxFps { get; set; }

    [ObservableProperty]
    public partial int ScreenQuality { get; set; }

    private void InitializeScreen()
    {
        var settings = _node.Settings.Screen;
        ScreenAllowViewing = settings.AllowViewing;
        ScreenAllowControl = settings.AllowControl;
        ScreenWindowDrag = settings.WindowDragEnabled;
        ScreenMaxFps = settings.MaxFps;
        ScreenQuality = settings.Quality;
        _screen.SourcesChanged += () => _dispatcher.InvokeAsync(ReloadScreenSources);
        ReloadScreenSources();
        _screenInitialized = true;
    }

    private void ReloadScreenSources()
    {
        ScreenSources.Clear();
        foreach (var source in _screen.Sources)
        {
            ScreenSources.Add(source);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task ShowScreenAsync()
    {
        if (SelectedPeer is not { } peer)
        {
            return;
        }

        StatusText = await _screen.RequestScreenAsync(peer.Id)
            ? $"Запрошен экран {peer.Name}…"
            : $"Не удалось запросить экран {peer.Name}.";
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task SendWindowAsync()
    {
        if (SelectedPeer is not { } peer)
        {
            return;
        }

        var picker = new WindowPickerWindow(peer.Name) { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.Selected is not { } window)
        {
            return;
        }

        StatusText = await _screen.SendWindowAsync(peer.Id, window.Handle)
            ? $"Окно «{window.Title}» показывается на {peer.Name}."
            : $"Не удалось отправить окно на {peer.Name}.";
    }

    [RelayCommand]
    private async Task StopScreenSourceAsync(ActiveScreenSource? source)
    {
        if (source is not null)
        {
            await _screen.StopSourceAsync(source.StreamId);
        }
    }

    private void SaveScreen(Action<Core.Settings.ScreenSettings> change)
    {
        if (_screenInitialized)
        {
            change(_node.Settings.Screen);
            _node.SaveSettings();
        }
    }

    partial void OnScreenAllowViewingChanged(bool value) => SaveScreen(s => s.AllowViewing = value);

    partial void OnScreenAllowControlChanged(bool value) => SaveScreen(s => s.AllowControl = value);

    partial void OnScreenWindowDragChanged(bool value) => SaveScreen(s => s.WindowDragEnabled = value);

    partial void OnScreenMaxFpsChanged(int value)
    {
        if (value is >= 1 and <= 60)
        {
            SaveScreen(s => s.MaxFps = value);
        }
    }

    partial void OnScreenQualityChanged(int value)
    {
        if (value is >= 30 and <= 95)
        {
            SaveScreen(s => s.Quality = value);
        }
    }
}
