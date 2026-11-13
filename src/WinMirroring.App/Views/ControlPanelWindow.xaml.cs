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
using WinMirroring.Audio;
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
        CaptureExclusion.Apply(this);
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refresh.Tick += (_, _) => RefreshIfChanged();

        // A device viewing this screen cannot see the panel (it is kept out of capture): its presses do not count,
        // and the panel steps aside so that device's next presses reach what it can see.
        PreviewMouseDown += (_, e) => IgnoreRemotePress(e, e.GetPosition(this));
        PreviewTouchDown += (_, e) => IgnoreRemotePress(e, e.GetTouchPoint(this).Position);
    }

    private void IgnoreRemotePress(RoutedEventArgs e, Point position)
    {
        var onScreen = PointToScreen(position);
        if (RemoteInputTracker.IsRemote(onScreen.X, onScreen.Y))
        {
            e.Handled = true;
            HidePanel();
        }
    }

    public bool IsShown { get; private set; }

    /// <summary>Opens the panel (rebuilt from current state) or closes it if already open; returns whether it is now shown.</summary>
    public bool Toggle(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, ButtonBarService bar, Action openSettings, Action openButtonEditor, Action hideButton)
    {
        if (IsShown)
        {
            HidePanel();
            return false;
        }

        _repopulate = () => Populate(vm, screen, viewers, bar, openSettings, openButtonEditor, hideButton);
        _signature = () => BuildSignature(vm, screen, viewers, bar);
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
    private static string BuildSignature(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, ButtonBarService bar)
    {
        var text = new StringBuilder();
        text.Append(bar.Visible).Append(bar.ActiveName).AppendJoin(',', bar.Presets.Select(p => p.Name)).Append('|');
        text.Append(ScreenStreamingService.IsVirtualDisplayAvailable).Append('|')
            .Append(vm.SoundAutomatic).Append(vm.SoundKeptHere).Append(vm.InputEnabled)
            .Append(vm.SyncKeyboardLayout).Append(vm.AdminWindowControl).Append('|')
            .Append(viewers.IsLocalDesktopActive);

        foreach (var view in viewers.OpenViews)
        {
            text.Append("|v").Append(view.StreamId).Append(view.Title).Append(view.Kind).Append(view.MonitorIndex).Append(view.IsActive)
                .Append(view.InputAllowed).Append(view.InputOn);
        }

        text.Append("|s").Append(vm.OwnSoundLine);
        foreach (var peer in vm.ReadyPeers)
        {
            text.Append("|p").Append(peer.Id).Append(peer.Name)
                .Append(vm.IsSecondMonitor(peer.Id))
                .Append(screen.PeerVirtualDisplay(peer.Id)?.ToString() ?? "?")
                .Append(vm.IsSoundSentTo(peer.Id))
                .Append(vm.IsSoundFrom(peer.Id))
                .Append(vm.SoundHereChoice(peer.Id))
                .Append(vm.VersionLineOf(peer.Id));
            foreach (var monitor in screen.PeerMonitors(peer.Id))
            {
                text.Append('m').Append(monitor.Index).Append(monitor.Label).Append(monitor.Primary);
            }
        }

        return text.ToString();
    }

    private void Populate(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, ButtonBarService bar, Action openSettings, Action openButtonEditor, Action hideButton)
    {
        _lastSignature = BuildSignature(vm, screen, viewers, bar);
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
                Sections.Children.Add(MakeButton("Рабочий стол этого устройства", viewers.ShowLocalDesktop, choosesScreen: true));
            }

            foreach (var view in open)
            {
                if (!view.IsActive)
                {
                    Sections.Children.Add(MakeButton(view.Title, () => viewers.SwitchTo(view.StreamId), choosesScreen: true));
                    continue;
                }

                // The view in front is a caption, not a button: clicking it would do nothing.
                Sections.Children.Add(MakeCurrent("● " + view.Title));
                if (view.InputAllowed)
                {
                    Sections.Children.Add(MakeChoices(
                        closesPanel: true,
                        ("Управлять", view.InputOn, () => viewers.SetViewInput(view.StreamId, true)),
                        ("Только смотреть", !view.InputOn, () => viewers.SetViewInput(view.StreamId, false))));
                }
                else
                {
                    Sections.Children.Add(MakeNote("Только просмотр: управление запрещено на том устройстве"));
                }

                if (view.Kind is "monitor" or "display")
                {
                    // The bar of keys over full-screen views: shown or not, which preset, and editing them.
                    Sections.Children.Add(MakeToggle("Панель кнопок внизу", bar.Visible, bar.SetVisible));
                    if (bar.Visible && bar.Presets.Count > 1)
                    {
                        foreach (var preset in bar.Presets)
                        {
                            Sections.Children.Add(MakeChoice($"Набор «{preset.Name}»", preset.Name == bar.ActiveName, () => bar.SetActive(preset.Name)));
                        }
                    }

                    Sections.Children.Add(MakeButton("Настроить кнопки и наборы…", openButtonEditor, choosesScreen: true));
                }

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
            Sections.Children.Add(MakeNote(vm.VersionLineOf(peer.Id)));

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
                    Sections.Children.Add(MakeButton(screenView is null ? "Показать экран" : "Переключиться на экран", () => viewers.ShowOrSwitchScreen(peer.Id, index), choosesScreen: true));
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
                        Sections.Children.Add(MakeButton(shown ? label + " — открыт" : label, () => viewers.ShowOrSwitchScreen(peer.Id, monitor.Index), choosesScreen: true));
                    }
                }
            }

            Sections.Children.Add(MakeButton("Отправить окно", () => _ = vm.SendWindowToPeerAsync(peer.Id, peer.Name)));
            Sections.Children.Add(MakeButton("Открыть файл на устройстве…", () => _ = vm.OpenOnDeviceAsync(peer.Id, peer.Name), choosesScreen: true));
            Sections.Children.Add(MakeButton("Управлять вводом", () => vm.ControlPeerById(peer.Id, peer.Name)));
            if (vm.CanUpdatePeer(peer.Id))
            {
                // This build is newer: hand it over, so a device with no access to GitHub can be updated too.
                Sections.Children.Add(MakeButton($"Обновить до {vm.LocalVersionText}", () => _ = vm.UpdatePeerAsync(peer.Id, peer.Name)));
            }

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
                        HidePanel();
                    }
                }));
            }
            else
            {
                Sections.Children.Add(MakeNote($"Стать вторым монитором: на {peer.Name} нет драйвера виртуального монитора"));
            }
        }

        // ---- Sound: where this device's sound goes, and whether each device's sound plays here ----
        // Choices, not on/off toggles: the current one is highlighted, and pressing it again changes nothing.
        Sections.Children.Add(MakeHeader("ЗВУК"));
        Sections.Children.Add(MakeDeviceHeader("Звук этого устройства"));
        Sections.Children.Add(MakeNote(vm.OwnSoundLine));
        Sections.Children.Add(MakeChoices(
            closesPanel: false,
            ("Автоматически", vm.SoundAutomatic, () => vm.SendSoundTo(null)),
            ("Только здесь", vm.SoundKeptHere, vm.KeepSoundHere)));
        foreach (var peer in peers)
        {
            Sections.Children.Add(MakeChoice($"Всегда на {peer.Name}", vm.IsSoundSentTo(peer.Id), () => vm.SendSoundTo(peer.Id, peer.Name)));
        }

        foreach (var peer in peers)
        {
            Sections.Children.Add(MakeDeviceHeader($"Звук {peer.Name} здесь"));
            var choice = vm.SoundHereChoice(peer.Id);
            Sections.Children.Add(vm.IsSoundFrom(peer.Id)
                ? MakeCurrent("● Сейчас играет здесь")
                : MakeNote(choice switch
                {
                    IncomingSoundChoice.Never => "Выключен здесь",
                    IncomingSoundChoice.Always => $"Запрошен, но пока не идёт: на {peer.Name} звук выключен или закреплён за другим устройством",
                    _ => "Сейчас не играет. Авто: играет, пока открыт его экран или вы управляете им",
                }));
            Sections.Children.Add(MakeChoices(
                closesPanel: false,
                ("Авто", choice == IncomingSoundChoice.Automatic, () => _ = vm.SetSoundHereAsync(peer.Id, peer.Name, IncomingSoundChoice.Automatic)),
                ("Всегда", choice == IncomingSoundChoice.Always, () => _ = vm.SetSoundHereAsync(peer.Id, peer.Name, IncomingSoundChoice.Always)),
                ("Никогда", choice == IncomingSoundChoice.Never, () => _ = vm.SetSoundHereAsync(peer.Id, peer.Name, IncomingSoundChoice.Never))));
        }

        // ---- Input ----
        Sections.Children.Add(MakeHeader("ВВОД"));
        Sections.Children.Add(MakeToggle("Передавать мышь и клавиатуру на устройства", vm.InputEnabled, on => vm.InputEnabled = on));
        Sections.Children.Add(MakeToggle("Синхронизировать раскладку клавиатуры", vm.SyncKeyboardLayout, on => vm.SyncKeyboardLayout = on));
        Sections.Children.Add(MakeToggle("Управление окнами администратора", vm.AdminWindowControl, on => vm.AdminWindowControl = on));
        Sections.Children.Add(MakeButton("Вернуть курсор", () => vm.ReturnCursor()));

        // ---- Other ----
        Sections.Children.Add(MakeHeader("ПРОЧЕЕ"));
        Sections.Children.Add(MakeButton("Раскладка экранов…", () => ((App)Application.Current).ShowLayoutEditor(), choosesScreen: true));
        Sections.Children.Add(MakeButton("Параметры и синхронизация", openSettings));
        Sections.Children.Add(MakeButton("Вернуть окна на место", () => vm.ReturnWindows()));
        Sections.Children.Add(MakeButton("Скрыть кнопку меню", hideButton));
    }

    private TextBlock MakeHeader(string text) => new() { Text = text, Style = (Style)FindResource("SectionHeader") };

    private TextBlock MakeDeviceHeader(string text) => new() { Text = text, Style = (Style)FindResource("DeviceHeader") };

    private TextBlock MakeCurrent(string text) => new() { Text = text, Style = (Style)FindResource("Current") };

    private static TextBlock MakeNote(string text) =>
        new() { Text = text, Foreground = Brushes.Gray, FontSize = 13, Margin = new Thickness(2, 4, 0, 4), TextWrapping = TextWrapping.Wrap };

    /// <param name="choosesScreen">Picking what is on screen: the panel closes right after, out of the way.</param>
    private Button MakeButton(string text, Action onClick, bool choosesScreen = false)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("QuickButton") };
        button.Click += (_, _) =>
        {
            onClick();
            if (choosesScreen)
            {
                HidePanel();
            }
            else
            {
                _resyncAt = Environment.TickCount64 + ResyncAfterClickMs;
            }
        };
        return button;
    }

    /// <summary>One of several choices, full width; the current one is highlighted and marked.</summary>
    private Button MakeChoice(string text, bool current, Action onClick, bool closesPanel = false)
    {
        var button = MakeButton(current ? "● " + text : text, current ? () => { } : onClick, closesPanel);
        if (current)
        {
            button.Background = CurrentChoiceBrush;
            button.Foreground = Brushes.White;
            button.FontWeight = FontWeights.SemiBold;
        }

        return button;
    }

    /// <summary>Choices side by side (a segmented control), the current one highlighted.</summary>
    private UniformGrid MakeChoices(bool closesPanel, params (string Text, bool Current, Action OnClick)[] choices)
    {
        var row = new UniformGrid { Rows = 1, Columns = choices.Length };
        for (var i = 0; i < choices.Length; i++)
        {
            var (text, current, onClick) = choices[i];
            var button = MakeChoice(text, current, onClick, closesPanel);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(4, 6, 4, 6);
            button.Margin = new Thickness(i == 0 ? 0 : 3, 3, i == choices.Length - 1 ? 0 : 3, 3);
            row.Children.Add(button);
        }

        return row;
    }

    private static readonly SolidColorBrush CurrentChoiceBrush = CreateFrozen(Color.FromRgb(0x25, 0x63, 0xEB));

    private static SolidColorBrush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
