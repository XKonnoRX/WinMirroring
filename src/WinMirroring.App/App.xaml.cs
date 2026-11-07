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
using WinMirroring.Input;

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
        try
        {
            await node.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Node failed to start");
            MessageBox.Show($"Не удалось запустить сетевую часть: {ex.Message}", "WinMirroring", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        try
        {
            _services.GetRequiredService<InputSharingService>().Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Input sharing failed to start");
            MessageBox.Show($"Не удалось включить общий ввод: {ex.Message}", "WinMirroring", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        CreateTrayIcon();
        _services.GetRequiredService<DialogPairingUi>().Notify = ShowNotification;

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
        services.AddSingleton<IPairingUserInterface>(sp => sp.GetRequiredService<DialogPairingUi>());
        services.AddSingleton(sp => new MirroringNode(
            new NodeOptions(),
            sp.GetRequiredService<IPairingUserInterface>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<InputSharingService>();
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
