using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinMirroring.App.Services;
using WinMirroring.App.ViewModels;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// The main control surface: a panel that slides in from the right (like the screen viewer's quick panel). It shows
/// what is on screen and switches between the open views and this device's desktop, and manages the devices'
/// screens (a chosen monitor), sound, input and the rest. It follows changes on its own while it is open.
/// </summary>
public partial class ControlPanelWindow : Window
{
    private const int ResyncAfterClickMs = 2500;

    private readonly DispatcherTimer _refresh;
    private Action? _repopulate;
    private Func<string>? _signature;
    private string? _lastSignature;
    private long _resyncAt;

    public ControlPanelWindow()
    {
        InitializeComponent();
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refresh.Tick += (_, _) => RefreshIfChanged();
    }

    public bool IsShown { get; private set; }

    /// <summary>Opens the panel (rebuilt from current state) or closes it if already open; returns whether it is now shown.</summary>
    public bool Toggle(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, Action openSettings, Action hideButton)
    {
        if (IsShown)
        {
            HidePanel();
            return false;
        }

        _repopulate = () => Populate(vm, screen, viewers, openSettings, hideButton);
        _signature = () => BuildSignature(vm, screen, viewers);
        _repopulate();

        var work = SystemParameters.WorkArea;
        Height = work.Height;
        Top = work.Top;
        Left = work.Right - Width;
        Show();
        Topmost = true;
        IsShown = true;
        _refresh.Start();
        return true;
    }

    public void HidePanel()
    {
        _refresh.Stop();
        Hide();
        IsShown = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => HidePanel();

    /// <summary>
    /// Rebuilds the panel when what it shows has changed (a view opened, a mode switched, a device came or went),
    /// and once shortly after a click in case the action changed nothing; the scroll position is kept.
    /// </summary>
    private void RefreshIfChanged()
    {
        if (!IsShown || _repopulate is null || _signature is null || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        var resyncDue = _resyncAt != 0 && Environment.TickCount64 >= _resyncAt;
        if (!resyncDue && _signature() == _lastSignature)
        {
            return;
        }

        _resyncAt = 0;
        var offset = Scroller.VerticalOffset;
        _repopulate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroller.ScrollToVerticalOffset(offset));
    }

    /// <summary>Everything the panel shows, as one string: when it changes, the panel is rebuilt.</summary>
    private static string BuildSignature(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers)
    {
        var text = new StringBuilder();
        text.Append(ScreenStreamingService.IsVirtualDisplayAvailable).Append('|')
            .Append(vm.SoundAutomatic).Append(vm.SoundKeptHere).Append(vm.InputEnabled)
            .Append(vm.SyncKeyboardLayout).Append(vm.AdminWindowControl).Append('|')
            .Append(viewers.IsLocalDesktopActive);

        foreach (var view in viewers.OpenViews)
        {
            text.Append("|v").Append(view.StreamId).Append(view.Title).Append(view.Kind).Append(view.MonitorIndex).Append(view.IsActive);
        }

        foreach (var peer in vm.ReadyPeers)
        {
            text.Append("|p").Append(peer.Id).Append(peer.Name)
                .Append(vm.IsSecondMonitor(peer.Id))
                .Append(screen.PeerVirtualDisplay(peer.Id)?.ToString() ?? "?")
                .Append(vm.IsSoundSentTo(peer.Id))
                .Append(vm.IsListeningTo(peer.Id));
            foreach (var monitor in screen.PeerMonitors(peer.Id))
            {
                text.Append('m').Append(monitor.Index).Append(monitor.Label).Append(monitor.Primary);
            }
        }

        return text.ToString();
    }

    private void Populate(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, Action openSettings, Action hideButton)
    {
        _lastSignature = BuildSignature(vm, screen, viewers);
        Sections.Children.Clear();
        var peers = vm.ReadyPeers;
        var hasLocalDriver = ScreenStreamingService.IsVirtualDisplayAvailable;
        var open = viewers.OpenViews;

        // ---- What is on screen: switch between the open views and this device's own desktop ----
        if (open.Count > 0)
        {
            Sections.Children.Add(MakeHeader("НА ЭКРАНЕ"));
            if (viewers.IsLocalDesktopActive)
            {
                Sections.Children.Add(MakeCurrent("● Рабочий стол этого устройства"));
            }
            else
            {
                Sections.Children.Add(MakeButton("Рабочий стол этого устройства", viewers.ShowLocalDesktop));
            }

            foreach (var view in open)
            {
                if (!view.IsActive)
                {
                    Sections.Children.Add(MakeButton(view.Title, () => viewers.SwitchTo(view.StreamId)));
                    continue;
                }

                // The view in front is a caption, not a button: clicking it would do nothing.
                Sections.Children.Add(MakeCurrent("● " + view.Title));
                Sections.Children.Add(MakeButton("Экранная клавиатура", () => viewers.ToggleKeyboard(view.StreamId)));
                Sections.Children.Add(MakeButton(view.Kind == "display" ? "Отключить второй монитор" : "Закрыть трансляцию", () => viewers.CloseView(view.StreamId)));
            }
        }

        // ---- Devices: their screens (a chosen monitor), windows, input and virtual screens ----
        Sections.Children.Add(MakeHeader("УСТРОЙСТВА"));
        if (peers.Count == 0)
        {
            Sections.Children.Add(MakeNote("Нет синхронизированных устройств на связи"));
        }

        foreach (var peer in peers)
        {
            Sections.Children.Add(MakeDeviceHeader(peer.Name));

            var screenView = open.FirstOrDefault(v => v.Kind == "monitor" && v.PeerId == peer.Id);
            var monitors = screen.PeerMonitors(peer.Id);
            if (monitors.Count <= 1)
            {
                var index = monitors.Count == 1 ? monitors[0].Index : 0;
                if (screenView is { IsActive: true })
                {
                    Sections.Children.Add(MakeCurrent("● Экран показан"));
                }
                else
                {
                    Sections.Children.Add(MakeButton(screenView is null ? "Показать экран" : "Переключиться на экран", () => viewers.ShowOrSwitchScreen(peer.Id, index)));
                }
            }
            else
            {
                for (var i = 0; i < monitors.Count; i++)
                {
                    var monitor = monitors[i];
                    var label = monitor.Primary ? $"Основной экран ({monitor.Label})" : $"Монитор {i + 1} ({monitor.Label})";
                    var shown = screenView is not null && screenView.MonitorIndex == monitor.Index;
                    if (shown && screenView!.IsActive)
                    {
                        Sections.Children.Add(MakeCurrent("● " + label));
                    }
                    else
                    {
                        Sections.Children.Add(MakeButton(shown ? label + " — открыт" : label, () => viewers.ShowOrSwitchScreen(peer.Id, monitor.Index)));
                    }
                }
            }

            Sections.Children.Add(MakeButton("Отправить окно", () => _ = vm.SendWindowToPeerAsync(peer.Id, peer.Name)));
            Sections.Children.Add(MakeButton("Управлять вводом", () => vm.ControlPeerById(peer.Id, peer.Name)));

            // This device extends onto the peer (needs the driver here).
            if (hasLocalDriver)
            {
                Sections.Children.Add(MakeToggle("Использовать как второй монитор", vm.IsSecondMonitor(peer.Id), on => _ = vm.ToggleSecondMonitorAsync(peer.Id, peer.Name)));
            }

            // The peer extends onto this device (needs the driver there). Unknown counts as maybe: let it try.
            var displayView = open.FirstOrDefault(v => v.Kind == "display" && v.PeerId == peer.Id);
            if (displayView is not null)
            {
                Sections.Children.Add(MakeToggle("Стать вторым монитором этого устройства", true, on =>
                {
                    if (!on)
                    {
                        viewers.CloseView(displayView.StreamId);
                    }
                }));
            }
            else if (screen.PeerVirtualDisplay(peer.Id) != false)
            {
                Sections.Children.Add(MakeToggle("Стать вторым монитором этого устройства", false, on =>
                {
                    if (on)
                    {
                        _ = vm.BecomeSecondMonitorAsync(peer.Id, peer.Name);
                    }
                }));
            }
            else
            {
                Sections.Children.Add(MakeNote($"Стать вторым монитором: на {peer.Name} нет драйвера виртуального монитора"));
            }
        }

        // ---- Sound ----
        Sections.Children.Add(MakeHeader("ЗВУК"));
        Sections.Children.Add(MakeToggle("Автоматически: за вводом и просмотром", vm.SoundAutomatic, _ => vm.SendSoundTo(null)));
        Sections.Children.Add(MakeToggle("Звук этого устройства — только здесь", vm.SoundKeptHere, _ => vm.KeepSoundHere()));
        foreach (var peer in peers)
        {
            Sections.Children.Add(MakeToggle($"Звук этого устройства — на {peer.Name}", vm.IsSoundSentTo(peer.Id), _ => vm.SendSoundTo(peer.Id, peer.Name)));
            Sections.Children.Add(MakeToggle($"Слушать звук {peer.Name} здесь", vm.IsListeningTo(peer.Id), on => _ = vm.SetListenAsync(peer.Id, peer.Name, on)));
        }

        // ---- Input ----
        Sections.Children.Add(MakeHeader("ВВОД"));
        Sections.Children.Add(MakeToggle("Передавать мышь и клавиатуру на устройства", vm.InputEnabled, on => vm.InputEnabled = on));
        Sections.Children.Add(MakeToggle("Синхронизировать раскладку клавиатуры", vm.SyncKeyboardLayout, on => vm.SyncKeyboardLayout = on));
        Sections.Children.Add(MakeToggle("Управление окнами администратора", vm.AdminWindowControl, on => vm.AdminWindowControl = on));
        Sections.Children.Add(MakeButton("Вернуть курсор", () => vm.ReturnCursor()));

        // ---- Other ----
        Sections.Children.Add(MakeHeader("ПРОЧЕЕ"));
        Sections.Children.Add(MakeButton("Параметры и синхронизация", openSettings));
        Sections.Children.Add(MakeButton("Вернуть окна на место", () => vm.ReturnWindows()));
        Sections.Children.Add(MakeButton("Скрыть кнопку меню", hideButton));
    }

    private TextBlock MakeHeader(string text) => new() { Text = text, Style = (Style)FindResource("SectionHeader") };

    private TextBlock MakeDeviceHeader(string text) => new() { Text = text, Style = (Style)FindResource("DeviceHeader") };

    private TextBlock MakeCurrent(string text) => new() { Text = text, Style = (Style)FindResource("Current") };

    private static TextBlock MakeNote(string text) =>
        new() { Text = text, Foreground = Brushes.Gray, FontSize = 13, Margin = new Thickness(2, 4, 0, 4), TextWrapping = TextWrapping.Wrap };

    private Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("QuickButton") };
        button.Click += (_, _) =>
        {
            onClick();
            _resyncAt = Environment.TickCount64 + ResyncAfterClickMs;
        };
        return button;
    }

    private ToggleButton MakeToggle(string text, bool isChecked, Action<bool> onToggled)
    {
        var toggle = new ToggleButton { Content = text, Style = (Style)FindResource("QuickToggle"), IsChecked = isChecked };
        toggle.Click += (_, _) =>
        {
            onToggled(toggle.IsChecked == true);
            _resyncAt = Environment.TickCount64 + ResyncAfterClickMs;
        };
        return toggle;
    }
}
