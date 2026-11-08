using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinMirroring.App.Services;
using WinMirroring.App.ViewModels;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// The main control surface: a panel that slides in from the right (like the screen viewer's quick panel) and
/// manages screens, sound, input and the rest. The floating button opens and closes it.
/// </summary>
public partial class ControlPanelWindow : Window
{
    public ControlPanelWindow() => InitializeComponent();

    public bool IsShown { get; private set; }

    /// <summary>Opens the panel (rebuilt from current state) or closes it if already open; returns whether it is now shown.</summary>
    public bool Toggle(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, Action openSettings, Action hideButton)
    {
        if (IsShown)
        {
            HidePanel();
            return false;
        }

        Populate(vm, screen, viewers, openSettings, hideButton);
        var work = SystemParameters.WorkArea;
        Height = work.Height;
        Top = work.Top;
        Left = work.Right - Width;
        Show();
        Topmost = true;
        IsShown = true;
        return true;
    }

    public void HidePanel()
    {
        Hide();
        IsShown = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => HidePanel();

    private Action? _repopulate;

    private void Populate(MainViewModel vm, ScreenStreamingService screen, IViewerControl viewers, Action openSettings, Action hideButton)
    {
        _repopulate = () => Populate(vm, screen, viewers, openSettings, hideButton);
        Sections.Children.Clear();
        var peers = vm.ReadyPeers;
        var hasLocalDriver = ScreenStreamingService.IsVirtualDisplayAvailable;

        // ---- Open views: pick which one is on screen and control the active one ----
        var open = viewers.OpenViews;
        if (open.Count > 0)
        {
            Sections.Children.Add(MakeHeader("ОТКРЫТЫЕ ТРАНСЛЯЦИИ"));
            foreach (var view in open)
            {
                Sections.Children.Add(MakeButton((view.IsActive ? "● " : "    ") + view.Title, () =>
                {
                    viewers.SwitchTo(view.StreamId);
                    _repopulate?.Invoke();
                }));

                if (view.IsActive)
                {
                    if (view.Kind == "monitor" && view.MonitorCount > 1)
                    {
                        Sections.Children.Add(MakeButton($"Следующий монитор ({view.MonitorIndex + 1}/{view.MonitorCount})",
                            () => viewers.SwitchMonitor(view.StreamId, (view.MonitorIndex + 1) % view.MonitorCount)));
                    }

                    Sections.Children.Add(MakeButton("Экранная клавиатура", () => viewers.ToggleKeyboard(view.StreamId)));
                    Sections.Children.Add(MakeButton("Закрыть трансляцию", () =>
                    {
                        viewers.CloseView(view.StreamId);
                        _repopulate?.Invoke();
                    }));
                }
            }
        }

        // ---- Screens / devices ----
        Sections.Children.Add(MakeHeader("ЭКРАНЫ УСТРОЙСТВ"));
        if (peers.Count == 0)
        {
            Sections.Children.Add(MakeNote("Нет синхронизированных устройств на связи"));
        }

        foreach (var peer in peers)
        {
            Sections.Children.Add(MakeDeviceHeader(peer.Name));
            Sections.Children.Add(MakeButton(viewers.IsScreenOpen(peer.Id) ? "Переключиться на экран" : "Показать экран", () =>
            {
                viewers.ShowOrSwitchScreen(peer.Id);
                _repopulate?.Invoke();
            }));
            Sections.Children.Add(MakeButton("Отправить окно", () => _ = vm.SendWindowToPeerAsync(peer.Id, peer.Name)));
            Sections.Children.Add(MakeButton("Управлять вводом", () => vm.ControlPeerById(peer.Id, peer.Name)));
            if (hasLocalDriver)
            {
                Sections.Children.Add(MakeToggle("Использовать как второй монитор", vm.IsSecondMonitor(peer.Id), on => _ = vm.ToggleSecondMonitorAsync(peer.Id, peer.Name)));
            }

            if (screen.PeerHasVirtualDisplay(peer.Id))
            {
                Sections.Children.Add(MakeButton("Стать вторым монитором этого устройства", () => _ = vm.BecomeSecondMonitorAsync(peer.Id, peer.Name)));
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

    private static TextBlock MakeNote(string text) =>
        new() { Text = text, Foreground = Brushes.Gray, FontSize = 13, Margin = new Thickness(2, 4, 0, 4), TextWrapping = TextWrapping.Wrap };

    private Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("QuickButton") };
        button.Click += (_, _) => onClick();
        return button;
    }

    private ToggleButton MakeToggle(string text, bool isChecked, Action<bool> onToggled)
    {
        var toggle = new ToggleButton { Content = text, Style = (Style)FindResource("QuickToggle"), IsChecked = isChecked };
        toggle.Click += (_, _) => onToggled(toggle.IsChecked == true);
        return toggle;
    }
}
