using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Serilog;
using WinMirroring.App.Services;

namespace WinMirroring.App.Views;

/// <summary>
/// A window whose contents are one of the React pages, shown through WebView2. The files are served from the
/// app's own folder under a made-up host name, so there is no local web server and no file:// restrictions.
/// </summary>
public partial class WebWindow : Window
{
    private const string VirtualHost = "winmirroring.local";

    private readonly UiBridge _bridge;
    private readonly string _route;
    private CoreWebView2? _page;

    /// <param name="panel">
    /// A side panel rather than an ordinary window: docked to the right edge, over everything, without stealing
    /// focus, and kept out of screen capture so a device watching this screen does not see it.
    /// </param>
    public WebWindow(UiBridge bridge, string route, string title, bool panel = false)
    {
        InitializeComponent();
        _bridge = bridge;
        _route = route;
        Title = title;
        if (panel)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 360;
            MinWidth = 0;
            MinHeight = 0;

            // The panel sets its own place. Without this the window is centred on the first Show, because WPF
            // applies the startup location from the markup after the position has already been set.
            WindowStartupLocation = WindowStartupLocation.Manual;
            CaptureExclusion.Apply(this);
        }

        _panel = panel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private readonly bool _panel;
    private bool _closingForGood;

    /// <summary>
    /// Closing this window only puts it away. Building a WebView2 and loading a page into it takes a noticeable
    /// moment, and a window that is thrown away on closing pays that again every single time it is opened; this one
    /// is built once and kept, so opening it again is immediate and it keeps where it was scrolled to. The
    /// application closes it for real on the way out, through <see cref="CloseForGood"/>.
    /// </summary>
    public bool KeepLoaded { get; init; }

    /// <summary>Closes the window for good, however <see cref="KeepLoaded"/> is set.</summary>
    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (KeepLoaded && !_closingForGood)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Starts the WebView2 runtime before any window needs it. Most of the wait on first opening a window is this
    /// much, and doing it while the application is starting anyway means the first window does not have to.
    /// </summary>
    public static void WarmUp()
    {
        if (!IsAvailable)
        {
            return;
        }

        _ = EnvironmentAsync().ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    Log.Debug(task.Exception, "WebView2 did not warm up");
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>Puts the panel against the right edge of the work area and shows it.</summary>
    public void ShowDocked()
    {
        Dock();
        Show();

        // Again after showing: the work area is only known for certain once the window is on a screen.
        Dock();
        Topmost = true;
    }

    private void Dock()
    {
        var work = SystemParameters.WorkArea;
        Height = work.Height;
        Top = work.Top;
        Left = work.Right - Width;
    }

    /// <summary>The built interface is next to the app; without it (npm never ran) these windows cannot open.</summary>
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public static bool IsAvailable => File.Exists(Path.Combine(RootDirectory, "index.html"));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_panel)
        {
            UseDarkTitleBar();
        }

        try
        {
            await View.EnsureCoreWebView2Async(await EnvironmentAsync());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 could not start");
            LoadingText.Text = "Не удалось открыть окно: WebView2 недоступен.";
            return;
        }

        var page = View.CoreWebView2;
        _page = page;
        page.SetVirtualHostNameToFolderMapping(VirtualHost, RootDirectory, CoreWebView2HostResourceAccessKind.Allow);

        var settings = page.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        // Nothing here should ever leave for a real site.
        page.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };

        page.NewWindowRequested += (_, args) => args.Handled = true;
        page.NavigationCompleted += (_, _) =>
        {
            LoadingText.Visibility = Visibility.Collapsed;
            View.Visibility = Visibility.Visible;
        };

        _bridge.Attach(page);
        page.Navigate($"https://{VirtualHost}/index.html#{_route}");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_page is { } page)
        {
            _bridge.Detach(page);
            _page = null;
        }

        View.Dispose();
    }

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>One WebView2 environment for every page, with its data kept beside the app's own settings.</summary>
    private static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        return _environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMirroring",
                "webview"),
            options: new CoreWebView2EnvironmentOptions
            {
                // The page draws its own interface; the browser's own UI language and features stay out of the way.
                Language = "ru",
            });
    }

    /// <summary>A light title bar over a near-black page looks broken; Windows can draw a dark one.</summary>
    private void UseDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
