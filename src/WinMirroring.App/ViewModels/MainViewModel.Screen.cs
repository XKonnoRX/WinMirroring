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

        var onSecondMonitor = _screen.IsExtendedTo(peer.Id);
        StatusText = await _screen.TransferWindowAsync(peer.Id, window.Handle)
            ? onSecondMonitor
                ? $"Окно «{window.Title}» перенесено на {peer.Name} (второй монитор)."
                : $"Окно «{window.Title}» показывается на {peer.Name}."
            : $"Не удалось отправить окно на {peer.Name}.";
    }

    /// <summary>Returns windows parked on the virtual monitor back to this device.</summary>
    public string ReturnWindows()
    {
        var count = _screen.MovedWindowCount;
        _screen.RestoreMovedWindows();
        var text = count > 0 ? $"Окон возвращено с второго монитора: {count}." : "На втором мониторе нет перенесённых окон.";
        StatusText = text;
        return text;
    }

    public string VirtualDisplayStatus => ScreenStreamingService.IsVirtualDisplayAvailable
        ? "Драйвер виртуального монитора установлен: экран этого устройства можно расширить на другое (правая кнопка по нему → «Использовать как второй монитор»)."
        : "Драйвер виртуального монитора не установлен. Он нужен только устройству, чей экран расширяется (например, ноутбуку). Установка: winget install --id=VirtualDrivers.Virtual-Display-Driver -e, или приложение Virtual Driver Control с github.com/VirtualDrivers. После установки перезапустите WinMirroring.";

    /// <summary>Turns the device into this device's second monitor (kept attached while it is connected), or stops that.</summary>
    public async Task<string> ToggleSecondMonitorAsync(string peerId, string peerName)
    {
        string text;
        if (IsSecondMonitor(peerId))
        {
            SaveScreen(s => s.SecondMonitorPeerId = string.Empty);
            await _screen.StopExtendingAsync();
            _screen.RestoreMovedWindows();
            text = $"{peerName} больше не второй монитор.";
        }
        else if (!ScreenStreamingService.IsVirtualDisplayAvailable)
        {
            text = "Для второго монитора на этом устройстве нужен драйвер виртуального монитора (см. вкладку «Экран»).";
        }
        else
        {
            SaveScreen(s => s.SecondMonitorPeerId = peerId);
            text = await _screen.InviteAsDisplayAsync(peerId)
                ? $"Подключаю {peerName} как второй монитор (останется, пока устройство на связи)…"
                : $"Не удалось связаться с {peerName}.";
        }

        StatusText = text;
        return text;
    }

    /// <summary>Asks the device to extend its desktop onto this screen.</summary>
    public async Task<string> BecomeSecondMonitorAsync(string peerId, string peerName)
    {
        var text = await _screen.RequestExtendAsync(peerId)
            ? $"Прошу {peerName} расширить рабочий стол на этот экран…"
            : $"Не удалось связаться с {peerName}.";
        StatusText = text;
        return text;
    }

    public bool IsSecondMonitor(string peerId) =>
        string.Equals(_node.Settings.Screen.SecondMonitorPeerId, peerId, StringComparison.Ordinal) || _screen.IsExtendedTo(peerId);

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task ToggleSecondMonitorForSelectedAsync()
    {
        if (SelectedPeer is { } peer)
        {
            await ToggleSecondMonitorAsync(peer.Id, peer.Name);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task BecomeSecondMonitorOfSelectedAsync()
    {
        if (SelectedPeer is { } peer)
        {
            await BecomeSecondMonitorAsync(peer.Id, peer.Name);
        }
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
