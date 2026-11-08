using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using WinMirroring.App.Services;
using WinMirroring.App.ViewModels;
using WinMirroring.App.Views;
using WinMirroring.Core;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Settings;
using WinMirroring.Audio;
using WinMirroring.Clipboard;
using WinMirroring.Input;
using WinMirroring.Screen;

namespace WinMirroring.App;

public partial class App : Application
{
    private const string MutexName = @"Local\WinMirroring.App.SingleInstance";
    private const string RestartedArgument = "--restarted";

    /// <summary>Set by the update script (or "WinMirroring.exe --exit") to make the running instance exit cleanly.</summary>
    private const string ExitEventName = @"Local\WinMirroring.App.Exit";

    private EventWaitHandle? _exitSignal;

    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper run: add a resolution to the virtual monitor driver, then exit (no UI, no single-instance lock).
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], WinMirroring.Screen.Display.VirtualDisplaySetup.Argument, StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(WinMirroring.Screen.Display.VirtualDisplaySetup.Run(e.Args[1]));
            return;
        }

        // "--exit": ask the running instance to close (used by the update script), nothing else.
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running))
            {
                using (running)
                {
                    running.Set();
                }
            }

            Shutdown();
            return;
        }

        // After "restart as administrator" the old instance is still shutting down: wait for it.
        var restarted = e.Args.Contains(RestartedArgument, StringComparer.OrdinalIgnoreCase);
        _singleInstance = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(restarted ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("WinMirroring уже запущен. Ищите значок в области уведомлений.", "WinMirroring", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ConfigureLogging();
        ListenForExitSignal();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _services = BuildServices();

        var node = _services.GetRequiredService<MirroringNode>();
        var health = _services.GetRequiredService<FeatureHealth>();
        var input = _services.GetRequiredService<InputSharingService>();
        var clipboard = _services.GetRequiredService<ClipboardSyncService>();
        var audio = _services.GetRequiredService<AudioService>();
        var screen = _services.GetRequiredService<ScreenStreamingService>();

        try
        {
            await node.StartAsync();
            health.Set("Сеть", true, "работает");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Node failed to start");
            health.Set("Сеть", false, ex.Message);
        }

        // Every feature starts on its own: one failing must not take the others down.
        health.Start("Общий ввод", () =>
        {
            input.Start();
            input.RemoteInputBlocked += (peer, window) => Dispatcher.BeginInvoke(() => ShowNotification(
                $"{peer}: окно администратора не принимает ввод",
                $"«{(window.Length > 0 ? window : "Окно")}» запущено с правами администратора, и Windows не пропускает в него ввод с другого устройства. " +
                $"Курсор можно увести обратно через край или Ctrl+Alt+Home. Чтобы управлять такими окнами, включите на {peer} «Управление окнами администратора» (вкладка «Настройки»)."));
            input.KeyboardLayoutMissing += name => Dispatcher.BeginInvoke(() => ShowNotification(
                "WinMirroring: нет такой раскладки",
                $"На этом устройстве нет раскладки «{name}», поэтому язык ввода не переключился вслед за другим устройством. " +
                "Добавьте этот язык: Параметры → Время и язык → Язык и регион."));
        });

        // Layouts only switch to ones installed here, so the list is worth comparing between devices.
        var layoutNames = WinMirroring.Input.Keyboard.KeyboardLayouts.Installed()
            .Select(WinMirroring.Input.Keyboard.KeyboardLayouts.DisplayName)
            .Distinct()
            .ToList();
        health.Set("Раскладка клавиатуры", true, layoutNames.Count == 0 ? "не удалось прочитать список раскладок" : "здесь: " + string.Join(", ", layoutNames));
        health.Start("Буфер обмена", clipboard.Start);
        clipboard.TransferProgress += p => Dispatcher.BeginInvoke(() => ShowFileTransfer(p));
        health.Start("Перетаскивание файлов", () =>
        {
            // Started regardless: it also hands dragged windows over. Only the file part needs the OLE surface.
            _services.GetRequiredService<DragDropCoordinator>().Start();
            if (clipboard.Drag is null)
            {
                throw new InvalidOperationException("OLE drag and drop is unavailable on this device");
            }
        });
        health.Start("Звук", () =>
        {
            audio.Start();
            input.ControlledByChanged += audio.SetFocusTarget;
            screen.ViewersChanged += audio.SetViewers;
        });
        health.Start("Экран и окна", () =>
        {
            screen.ViewerOpened += viewer => Dispatcher.BeginInvoke(() => new ScreenViewerWindow(viewer, _services!).Show());
            screen.Start();
            screen.AttachWindowDrag(input);
        });
        health.Start("Второй монитор", () =>
        {
            screen.AddDisplayModeAsync = (width, height) => Dispatcher.Invoke(() =>
            {
                ShowNotification("WinMirroring: разрешение для второго монитора",
                    $"Добавляю в драйвер виртуального монитора разрешение {width}×{height}. Windows попросит права администратора, мониторы на секунду мигнут. Это нужно один раз.");
                return WinMirroring.Screen.Display.VirtualDisplaySetup.AddModeElevatedAsync(width, height);
            });
            screen.SideOf = peerId => input.SideOf(peerId) ?? "right";
            input.IsCrossingSuppressed = screen.IsExtendedTo;
            screen.ExtendFailed += (peer, reason) => Dispatcher.BeginInvoke(() => ShowNotification($"Второй монитор: {peer}", $"Не получилось: {reason}."));
            screen.ExtendedChanged += peerId => Dispatcher.BeginInvoke(() =>
            {
                if (peerId is not null && _services?.GetService<MainViewModel>() is { } vm)
                {
                    var name = vm.ReadyPeers.FirstOrDefault(p => p.Id == peerId)?.Name ?? peerId;
                    ShowNotification("WinMirroring: второй монитор", $"Рабочий стол расширен на {name} и останется, пока устройство на связи. Перенести окно на него: правой кнопкой по устройству → «Отправить окно» (окно переедет по-настоящему). Вернуть — «Вернуть окна со второго монитора».");
                }
            });
            screen.DetachLeftoverDisplay();
        });
        if (health.Snapshot.Any(f => f.Name == "Второй монитор" && f.Ok))
        {
            health.Set("Второй монитор", true, ScreenStreamingService.IsVirtualDisplayAvailable
                ? "драйвер виртуального монитора найден"
                : "драйвер виртуального монитора не установлен (нужен только устройству, чей экран расширяется)");
        }
        health.Start("Диагностика", _services.GetRequiredService<DiagnosticsService>().Start);

        // Control of administrator windows: a separate elevated helper does the injecting while this app stays normal.
        var adminInjector = _services.GetRequiredService<WinMirroring.Input.Injection.ElevatedInjector>();
        adminInjector.StateChanged += (active, detail) => Dispatcher.BeginInvoke(() =>
        {
            health.Set("Управление окнами администратора", true, active ? detail : $"выключено: {detail}");
            _services?.GetService<MainViewModel>()?.SetAdminControlState(active, detail);
            ShowNotification("WinMirroring: окна администратора", detail);
        });
        if (WinMirroring.Input.Injection.ElevatedInjector.HelperPath is null)
        {
            health.Set("Управление окнами администратора", true, "вспомогательная программа WinMirroring.InputHost.exe не входит в эту сборку");
        }
        else if (node.Settings.Input.ElevatedInjector)
        {
            health.Set("Управление окнами администратора", true, "включаю…");
            _ = adminInjector.EnableAsync();
        }
        else
        {
            health.Set("Управление окнами администратора", true, "выключено (окна администратора не принимают ввод с других устройств)");
        }

        CreateTrayIcon();
        _services.GetRequiredService<DialogPairingUi>().Notify = ShowNotification;
        if (health.Failures.Any())
        {
            ShowNotification("WinMirroring: не всё запустилось", string.Join("; ", health.Failures.Select(f => $"{f.Name}: {f.Detail}")));
        }

        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            ShowMainWindow();
        }
    }

    /// <summary>The update script sets the event: exit the normal way (speakers unmuted, virtual monitor detached).</summary>
    private void ListenForExitSignal()
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var signal = _exitSignal;
        new Thread(() =>
        {
            try
            {
                signal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!_exiting)
            {
                Log.Information("Exit requested from outside (update)");
                Dispatcher.BeginInvoke(() => _ = ExitAsync());
            }
        })
        {
            IsBackground = true,
            Name = "WinMirroring.ExitSignal",
        }.Start();
    }

    private static void ConfigureLogging()
    {
        var logDirectory = Path.Combine(SettingsStore.DefaultDataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("WinMirroring {Version} starting", typeof(App).Assembly.GetName().Version);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<DialogPairingUi>();
        services.AddSingleton<FeatureHealth>();
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<IPairingUserInterface>(sp => sp.GetRequiredService<DialogPairingUi>());
        services.AddSingleton(sp => new MirroringNode(
            new NodeOptions(),
            sp.GetRequiredService<IPairingUserInterface>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<InputSharingService>();
        services.AddSingleton<WinMirroring.Input.Injection.ElevatedInjector>();
        services.AddSingleton<ClipboardSyncService>();
        services.AddSingleton<AudioService>();
        services.AddSingleton<DragDropCoordinator>();
        services.AddSingleton<ScreenStreamingService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => FillTrayMenu(menu);
        FillTrayMenu(menu);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WinMirroring",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _trayIcon.ForceCreate();
    }

    /// <summary>Quick actions in the tray menu, rebuilt each time it opens (devices and sound targets change).</summary>
    private void FillTrayMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Открыть WinMirroring", FontWeight = FontWeights.SemiBold, Command = new RelayCommand(ShowMainWindow) });

        if (_services?.GetService<MainViewModel>() is { } vm)
        {
            menu.Items.Add(new MenuItem { Header = "Вернуть курсор", Command = new RelayCommand(() => ShowNotification("WinMirroring", vm.ReturnCursor())) });
            menu.Items.Add(new MenuItem { Header = "Вернуть окна на место", Command = new RelayCommand(() => ShowNotification("WinMirroring", vm.ReturnWindows())) });
            menu.Items.Add(new Separator());

            var peers = vm.ReadyPeers;
            var sound = new MenuItem { Header = "Звук этого устройства" };
            sound.Items.Add(CheckItem("Автоматически: за вводом и просмотром", vm.SoundAutomatic, () => vm.SendSoundTo(null)));
            sound.Items.Add(CheckItem("Только здесь", vm.SoundKeptHere, vm.KeepSoundHere));
            foreach (var peer in peers)
            {
                sound.Items.Add(CheckItem($"Всегда на {peer.Name}", vm.IsSoundSentTo(peer.Id), () => vm.SendSoundTo(peer.Id, peer.Name)));
            }

            menu.Items.Add(sound);
            if (peers.Count > 0)
            {
                var listen = new MenuItem { Header = "Слушать здесь звук устройства" };
                var screens = new MenuItem { Header = "Показать экран устройства" };
                var screen = _services.GetRequiredService<ScreenStreamingService>();
                foreach (var peer in peers)
                {
                    var listening = vm.IsListeningTo(peer.Id);
                    listen.Items.Add(CheckItem(peer.Name, listening, () => _ = vm.SetListenAsync(peer.Id, peer.Name, !listening)));
                    screens.Items.Add(new MenuItem { Header = peer.Name, Command = new RelayCommand(() => _ = screen.RequestScreenAsync(peer.Id)) });
                }

                var extend = new MenuItem { Header = "Использовать как второй монитор" };
                var become = new MenuItem { Header = "Стать вторым монитором для" };
                foreach (var peer in peers)
                {
                    extend.Items.Add(CheckItem(peer.Name, vm.IsSecondMonitor(peer.Id), () => _ = vm.ToggleSecondMonitorAsync(peer.Id, peer.Name)));
                    become.Items.Add(new MenuItem { Header = peer.Name, Command = new RelayCommand(() => _ = vm.BecomeSecondMonitorAsync(peer.Id, peer.Name)) });
                }

                menu.Items.Add(listen);
                menu.Items.Add(screens);
                menu.Items.Add(extend);
                menu.Items.Add(become);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Выход", Command = new RelayCommand(() => _ = ExitAsync()) });
    }

    private static MenuItem CheckItem(string header, bool isChecked, Action action) =>
        new() { Header = header, IsChecked = isChecked, Command = new RelayCommand(action) };

    /// <summary>Starts an elevated copy (UAC prompt) and exits this one; false when the user declined.</summary>
    public async Task<bool> RestartElevatedAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, RestartedArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Information("Elevated restart declined: {Message}", ex.Message);
            return false;
        }

        await ExitAsync();
        return true;
    }

    /// <summary>Turns control of administrator windows (the elevated input helper) on or off and remembers the choice.</summary>
    public async Task SetAdminWindowControlAsync(bool enable)
    {
        if (_services is null)
        {
            return;
        }

        var node = _services.GetRequiredService<MirroringNode>();
        node.Settings.Input.ElevatedInjector = enable;
        node.SaveSettings();

        var injector = _services.GetRequiredService<WinMirroring.Input.Injection.ElevatedInjector>();
        if (enable)
        {
            await injector.EnableAsync();
        }
        else
        {
            injector.Disable();
        }
    }

    private void ShowMainWindow()
    {
        if (_services is null)
        {
            return;
        }

        _mainWindow ??= new MainWindow(_services.GetRequiredService<MainViewModel>());
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void ShowNotification(string title, string message)
    {
        try
        {
            _trayIcon?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Notification failed");
        }
    }

    private Views.FileTransferWindow? _fileTransfer;

    private void ShowFileTransfer(WinMirroring.Clipboard.FileTransferProgress p)
    {
        if (_exiting)
        {
            return;
        }

        if (_fileTransfer is null)
        {
            _fileTransfer = new Views.FileTransferWindow();
            _fileTransfer.Closed += (_, _) => _fileTransfer = null;
        }

        _fileTransfer.Update(p);
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        _fileTransfer?.Close();
        _trayIcon?.Dispose();

        if (_services is not null)
        {
            _services.GetRequiredService<DragDropCoordinator>().Dispose();

            try
            {
                await _services.GetRequiredService<ScreenStreamingService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Screen streaming shutdown failed");
            }

            try
            {
                _services.GetRequiredService<WinMirroring.Input.Injection.ElevatedInjector>().Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Elevated injector shutdown failed");
            }

            try
            {
                await _services.GetRequiredService<InputSharingService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Input sharing shutdown failed");
            }

            try
            {
                await _services.GetRequiredService<AudioService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Sound shutdown failed");
            }

            try
            {
                await _services.GetRequiredService<ClipboardSyncService>().DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard shutdown failed");
            }

            try
            {
                await _services.GetRequiredService<MirroringNode>().DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Node shutdown failed");
            }

            await _services.DisposeAsync();
        }

        Log.Information("WinMirroring exiting");
        await Log.CloseAndFlushAsync();
        _singleInstance?.ReleaseMutex();
        Shutdown();
        _exitSignal?.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, "WinMirroring: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
