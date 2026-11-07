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

    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                $"Курсор можно увести обратно через край или Ctrl+Alt+Home. Чтобы управлять такими окнами, перезапустите WinMirroring на {peer} от имени администратора (вкладка «Настройки»)."));
        });
        health.Start("Буфер обмена", clipboard.Start);
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
        health.Start("Диагностика", _services.GetRequiredService<DiagnosticsService>().Start);

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

                menu.Items.Add(listen);
                menu.Items.Add(screens);
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

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
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
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, "WinMirroring: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
