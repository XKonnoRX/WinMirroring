using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMirroring.App.Views;
using WinMirroring.Core.Settings;
using WinMirroring.Screen;

namespace WinMirroring.App.ViewModels;

/// <summary>A selectable screen codec with a Russian label for the settings combo box.</summary>
public sealed record VideoCodecChoice(VideoCodec Value, string Label);

/// <summary>Screen and window streaming: device actions, settings and outgoing streams.</summary>
public sealed partial class MainViewModel
{
    private readonly ScreenStreamingService _screen;
    private bool _screenInitialized;

    public ObservableCollection<ActiveScreenSource> ScreenSources { get; } = [];

    public IReadOnlyList<VideoCodecChoice> ScreenVideoCodecOptions { get; } =
    [
        new(VideoCodec.Auto, "Авто — H.264, иначе JPEG"),
        new(VideoCodec.H264, "H.264 (аппаратный)"),
        new(VideoCodec.Jpeg, "JPEG (изменённые участки)"),
    ];

    [ObservableProperty]
    public partial bool ScreenAllowViewing { get; set; }

    [ObservableProperty]
    public partial bool ScreenAllowControl { get; set; }

    [ObservableProperty]
    public partial bool ScreenWindowDrag { get; set; }

    [ObservableProperty]
    public partial bool ScreenHideSentWindow { get; set; }

    [ObservableProperty]
    public partial bool ScreenAutoKeyboard { get; set; }

    [ObservableProperty]
    public partial bool ScreenKeyboardButton { get; set; }

    [ObservableProperty]
    public partial int ScreenMaxFps { get; set; }

    [ObservableProperty]
    public partial int ScreenQuality { get; set; }

    [ObservableProperty]
    public partial VideoCodec ScreenVideoCodec { get; set; }

    [ObservableProperty]
    public partial int ScreenVideoBitrate { get; set; }

    private void InitializeScreen()
    {
        var settings = _node.Settings.Screen;
        ScreenAllowViewing = settings.AllowViewing;
        ScreenAllowControl = settings.AllowControl;
        ScreenWindowDrag = settings.WindowDragEnabled;
        ScreenHideSentWindow = settings.HideSentWindow;
        ScreenAutoKeyboard = settings.AutoKeyboardOnTap;
        ScreenKeyboardButton = settings.KeyboardButton;
        ScreenMaxFps = settings.MaxFps;
        ScreenQuality = settings.Quality;
        ScreenVideoCodec = settings.VideoCodec;
        ScreenVideoBitrate = settings.VideoBitrateKbps;
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

        StatusText = await SendWindowToAsync(peer.Id, peer.Name, window.Handle, window.Title);
    }

    /// <summary>Sends a window of this device to <paramref name="peerId"/> (picks it first), used from the floating menu.</summary>
    public async Task SendWindowToPeerAsync(string peerId, string peerName)
    {
        var picker = new WindowPickerWindow(peerName) { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.Selected is not { } window)
        {
            return;
        }

        StatusText = await SendWindowToAsync(peerId, peerName, window.Handle, window.Title);
    }

    /// <summary>
    /// Sends one window to a device and, with it, the mouse and keyboard.
    ///
    /// A window and the hands working in it are one thing to the person doing it: sending the window and then
    /// having to find the edge of the screen to follow it is the application's idea of two steps, not theirs.
    /// So input goes where the window went — unless the window went onto this device's own second monitor, which
    /// that device is only showing: the pointer reaches that by moving onto it, as it would any other screen, and
    /// handing input to the device itself would be the wrong place entirely.
    ///
    /// Nothing is forced: with input sharing off, the window still goes and the service says so and does nothing.
    /// </summary>
    private async Task<string> SendWindowToAsync(string peerId, string peerName, nint window, string title)
    {
        var onSecondMonitor = _screen.IsExtendedTo(peerId);
        var hidesHere = _node.Settings.Screen.HideSentWindow;
        if (!await _screen.TransferWindowAsync(peerId, window))
        {
            return $"Не удалось отправить окно на {peerName}.";
        }

        if (onSecondMonitor)
        {
            return $"Окно «{title}» перенесено на {peerName} (второй монитор).";
        }

        var follows = _input.IsEnabled;
        if (follows)
        {
            _input.ControlPeer(peerId);
        }

        var where = hidesHere
            ? $"Окно «{title}» показывается на {peerName}, с этого экрана скрыто."
            : $"Окно «{title}» показывается на {peerName}.";
        return follows ? $"{where} Управление ушло туда же." : where;
    }

    /// <summary>Returns windows that were hidden off-screen or parked on the virtual monitor back to this device.</summary>
    public string ReturnWindows()
    {
        var count = _screen.MovedWindowCount;
        _screen.RestoreMovedWindows();
        var text = count > 0 ? $"Окон возвращено на место: {count}." : "Нет скрытых или перенесённых окон.";
        StatusText = text;
        return text;
    }

    public string VirtualDisplayStatus => ScreenStreamingService.IsVirtualDisplayAvailable
        ? "Драйвер виртуального монитора установлен: экран этого устройства можно расширить на другое (правая кнопка по нему → «Использовать как второй монитор»)."
        : "Драйвер виртуального монитора не установлен. Он нужен только устройству, чей экран расширяется (например, ноутбуку). Установка: winget install --id=VirtualDrivers.Virtual-Display-Driver -e, или приложение Virtual Driver Control с github.com/VirtualDrivers. После установки перезапустите WinMirroring.";

    public bool VirtualDisplayInstalled => ScreenStreamingService.IsVirtualDisplayAvailable;

    private const string VirtualDisplayPackage = "VirtualDrivers.Virtual-Display-Driver";
    private const string VirtualDisplayReleases = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases";
    private int _installingDriver;

    /// <summary>
    /// Installs (or updates) the Virtual Display Driver with winget, so that it does not have to be looked up and
    /// typed on every device. winget asks Windows for administrator rights itself, once, for the driver. The
    /// monitor the driver brings with it is taken off the desktop again at once — it is only wanted while another
    /// device actually shows it — and the new driver is used without restarting the app, since it is looked for
    /// every time it is needed.
    /// </summary>
    public async Task<string> InstallVirtualDisplayDriverAsync()
    {
        if (Interlocked.Exchange(ref _installingDriver, 1) == 1)
        {
            return Say("Драйвер уже устанавливается.");
        }

        try
        {
            var update = ScreenStreamingService.IsVirtualDisplayAvailable;
            Say(update
                ? "Проверяю обновление драйвера виртуального монитора через winget…"
                : "Устанавливаю драйвер виртуального монитора через winget — Windows спросит права администратора…");

            var verb = update ? "upgrade" : "install";
            var start = new System.Diagnostics.ProcessStartInfo("winget.exe",
                $"{verb} --id={VirtualDisplayPackage} -e --source winget --silent --accept-source-agreements --accept-package-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // winget writes UTF-8; read as the system's code page its Russian came out as mojibake in the log.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            System.Diagnostics.Process? process;
            try
            {
                process = System.Diagnostics.Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogInformation("winget is not available: {Message}", ex.Message);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VirtualDisplayReleases) { UseShellExecute = true });
                return Say("На этом устройстве нет winget. Открыл страницу драйвера: скачайте установщик оттуда.");
            }

            if (process is null)
            {
                return Say("winget не запустился.");
            }

            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(true);
                var text = (await output.ConfigureAwait(true)) + (await errors.ConfigureAwait(true));
                _logger.LogInformation("winget {Verb} {Package} exited with {Code}: {Output}", verb, VirtualDisplayPackage, process.ExitCode, text.Trim());

                // 0x8A15002B: no newer version; 0x8A150061: already installed. Either way the package is there.
                var nothingToDo = unchecked((uint)process.ExitCode) is 0x8A15002B or 0x8A150061;
                if (process.ExitCode != 0 && !nothingToDo)
                {
                    return Say($"winget не скачал драйвер (код 0x{process.ExitCode:X8}). Подробности в журнале приложения.");
                }

                if (update && nothingToDo)
                {
                    return Say("Драйвер виртуального монитора уже последней версии.");
                }

                // What winget installs is the driver's control app with the driver inside, not the driver: the
                // driver itself goes in now, from that package, behind one administrator prompt.
                if (WinMirroring.Screen.Display.VirtualDisplaySetup.FindPackage() is not { } package)
                {
                    return Say("winget отработал, но пакета с драйвером не видно. Подробности в журнале приложения.");
                }

                Say("Устанавливаю сам драйвер — Windows спросит права администратора…");
                var (code, report) = await WinMirroring.Screen.Display.VirtualDisplaySetup.InstallElevatedAsync(package).ConfigureAwait(true);
                _logger.LogInformation("Virtual display driver from {Package}: exit {Code}. {Report}", package, code, report.Trim());
                if (code == -1)
                {
                    return Say("Установка драйвера отменена: Windows не получила права администратора.");
                }

                // The device comes up a moment after it is created.
                for (var i = 0; i < 30 && !ScreenStreamingService.IsVirtualDisplayAvailable; i++)
                {
                    await Task.Delay(500).ConfigureAwait(true);
                }

                _screen.DetachLeftoverDisplay();
                OnPropertyChanged(nameof(VirtualDisplayStatus));
                OnPropertyChanged(nameof(VirtualDisplayInstalled));

                if (ScreenStreamingService.IsVirtualDisplayAvailable)
                {
                    return Say(update
                        ? "Драйвер виртуального монитора обновлён."
                        : "Драйвер виртуального монитора установлен — этот экран можно расширять на другие устройства.");
                }

                return Say(code == 0
                    ? "Драйвер установлен, но Windows его ещё не показывает. Если она просила перезагрузку — перезагрузите устройство."
                    : $"Драйвер не установился (код {code}). Подробности в журнале приложения.");
            }
        }
        finally
        {
            Volatile.Write(ref _installingDriver, 0);
        }
    }

    /// <summary>Turns the device into this device's second monitor, or stops that. Nothing is remembered for next time.</summary>
    public async Task<string> ToggleSecondMonitorAsync(string peerId, string peerName)
    {
        string text;
        if (IsSecondMonitor(peerId))
        {
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
            text = await _screen.InviteAsDisplayAsync(peerId)
                ? $"Подключаю {peerName} как второй монитор…"
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

    public bool IsSecondMonitor(string peerId) => _screen.IsExtendedTo(peerId);

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

    partial void OnScreenHideSentWindowChanged(bool value) => SaveScreen(s => s.HideSentWindow = value);

    partial void OnScreenAutoKeyboardChanged(bool value) => SaveScreen(s => s.AutoKeyboardOnTap = value);

    partial void OnScreenKeyboardButtonChanged(bool value) => SaveScreen(s => s.KeyboardButton = value);

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

    partial void OnScreenVideoCodecChanged(VideoCodec value) => SaveScreen(s => s.VideoCodec = value);

    partial void OnScreenVideoBitrateChanged(int value)
    {
        if (value is >= 1000 and <= 100_000)
        {
            SaveScreen(s => s.VideoBitrateKbps = value);
        }
    }
}
