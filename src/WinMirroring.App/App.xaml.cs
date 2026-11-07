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

    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
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
        health.Start("Общий ввод", input.Start);
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
        menu.Items.Add(new MenuItem { Header = "Открыть WinMirroring", FontWeight = FontWeights.SemiBold, Command = new RelayCommand(ShowMainWindow) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Выход", Command = new RelayCommand(() => _ = ExitAsync()) });

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WinMirroring",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _trayIcon.ForceCreate();
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
