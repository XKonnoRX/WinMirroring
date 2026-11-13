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

    public WebWindow(UiBridge bridge, string route, string title)
    {
        InitializeComponent();
        _bridge = bridge;
        _route = route;
        Title = title;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>The built interface is next to the app; without it (npm never ran) these windows cannot open.</summary>
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public static bool IsAvailable => File.Exists(Path.Combine(RootDirectory, "index.html"));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UseDarkTitleBar();
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
