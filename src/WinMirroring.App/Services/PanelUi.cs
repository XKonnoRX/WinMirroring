using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WinMirroring.App.ViewModels;
using WinMirroring.Core;
using WinMirroring.Screen;

namespace WinMirroring.App.Services;

/// <summary>
/// The side panel: what is on screen now, the devices, sound, input and the rest, as one snapshot plus commands.
/// Device commands are the field's, so both lead to the same place.
/// </summary>
public sealed class PanelUi
{
    private readonly MainViewModel _vm;
    private readonly HubUi _hub;
    private readonly ScreenStreamingService _screen;
    private readonly IViewerControl _viewers;
    private readonly ButtonBarService _bar;
    private readonly UiBridge _bridge;

    public PanelUi(MainViewModel vm, HubUi hub, ScreenStreamingService screen, IViewerControl viewers, ButtonBarService bar, UiBridge bridge)
    {
        _vm = vm;
        _hub = hub;
        _screen = screen;
        _viewers = viewers;
        _bar = bar;
        _bridge = bridge;
    }

    /// <summary>Closes the panel (a press that came from a device watching this screen, or a screen was chosen).</summary>
    public Action? Hide { get; set; }

    /// <summary>Whether the panel is on the screen now; the small pictures of monitors are only asked for while it is.</summary>
    public Func<bool>? IsShown { get; set; }

    private System.Windows.Threading.DispatcherTimer? _pictureTimer;
    private long _picturesPushedTicks;

    /// <summary>Opens the old settings window, the device field, and hides the floating button.</summary>
    public Action? OpenSettings { get; set; }

    public Action? OpenHub { get; set; }

    public Action? HideButton { get; set; }

    public Action? OpenButtonEditor { get; set; }

    public void Register()
    {
        _bridge.On("panel.get", _ => Snapshot());
        _bridge.OnAsync("panel.command", async args =>
        {
            var call = args.Deserialize<CommandArgs>(UiBridge.Json);
            if (call is null)
            {
                return "Непонятная команда.";
            }

            // A device watching this screen cannot see the panel (it is kept out of capture), so a press that
            // comes from where that device last pressed is not a press by the person sitting here.
            if (call.ScreenX is { } x && call.ScreenY is { } y && RemoteInputTracker.IsRemote(x, y))
            {
                Hide?.Invoke();
                return null;
            }

            return await RunAsync(call);
        });

        _bar.Changed += Changed;

        // Pictures of the other monitors, for the buttons that switch to them: wanted while the panel is open,
        // asked for again every second (the device sending them stops by itself when the asking stops), and
        // shown as they arrive — though not more often than once a second, since each showing redraws the panel.
        _viewers.MonitorPicturesChanged += () =>
        {
            var now = Environment.TickCount64;
            if (IsShown?.Invoke() == true && now - _picturesPushedTicks >= 1000)
            {
                _picturesPushedTicks = now;
                _bridge.Push("panel.changed");
            }
        };
        _pictureTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => _viewers.WantMonitorPictures(IsShown?.Invoke() == true),
            Application.Current.Dispatcher);
    }

    private void Changed() => Application.Current?.Dispatcher.BeginInvoke(() => _bridge.Push("panel.changed"));

    private object Snapshot()
    {
        var open = _viewers.OpenViews;
        return new
        {
            localActive = _viewers.IsLocalDesktopActive,
            ownSound = _vm.OwnSoundLine,
            inputFocus = _vm.InputFocusText,
            views = open.Select(v => new
            {
                streamId = v.StreamId,
                title = v.Title,
                peerId = v.PeerId,
                kind = v.Kind,
                active = v.IsActive,
                inputAllowed = v.InputAllowed,
                inputOn = v.InputOn,
                zoom = v.Zoom,
            }).ToList(),
            stats = _vm.ScreenShowStats,
            bar = new
            {
                visible = _bar.Visible,
                active = _bar.ActiveName,
                presets = _bar.Presets.Select(p => p.Name).ToList(),
            },
            input = new
            {
                enabled = _vm.InputEnabled,
                syncKeyboardLayout = _vm.SyncKeyboardLayout,
                adminControl = _vm.AdminWindowControl,
            },
            devices = _vm.ReadyPeers.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                monitors = MonitorsOf(p.Id),
                screenOpen = _viewers.IsScreenOpen(p.Id),
                secondMonitor = _vm.IsSecondMonitor(p.Id),
                canBecome = _screen.PeerVirtualDisplay(p.Id),
                sound = new { here = _vm.SoundHereChoice(p.Id).ToString(), playing = _vm.IsSoundFrom(p.Id), sentTo = _vm.IsSoundSentTo(p.Id) },
            }).ToList(),
        };
    }

    private object MonitorsOf(string peerId)
    {
        var pictures = _viewers.MonitorPictures(peerId);
        var shown = _viewers.ShownMonitorOf(peerId);
        return _screen.PeerMonitors(peerId)
            .Select(m => new
            {
                index = m.Index,
                label = m.Label,
                primary = m.Primary,
                key = m.Key,
                image = pictures.TryGetValue(m.Key, out var picture) ? picture : null,
                shown = shown is { Length: > 0 } && string.Equals(shown, m.Key, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
    }

    private async Task<object?> RunAsync(CommandArgs call)
    {
        switch (call.Name)
        {
            case "showLocal":
                _viewers.ShowLocalDesktop();
                break;

            case "switchView":
                _viewers.SwitchTo(call.StreamId);
                break;

            case "closeView":
                _viewers.CloseView(call.StreamId);
                break;

            case "keyboard":
                _viewers.ToggleKeyboard(call.StreamId);
                break;

            case "zoom":
                _viewers.ZoomView(call.StreamId, call.Text switch { "in" => 1, "out" => -1, _ => 0 });
                break;

            case "stats":
                _vm.ScreenShowStats = call.On ?? !_vm.ScreenShowStats;
                break;

            case "mark":
                _viewers.MarkHitch(call.StreamId);
                break;

            case "viewInput":
                _viewers.SetViewInput(call.StreamId, call.On ?? true);
                break;

            case "showScreen":
                _viewers.ShowOrSwitchScreen(call.DeviceId ?? string.Empty, call.Monitor ?? 0, call.Text);
                Hide?.Invoke();
                return null;

            case "barVisible":
                _bar.SetVisible(call.On ?? true);
                break;

            case "barPreset":
                _bar.SetActive(call.Text ?? string.Empty);
                break;

            case "editButtons":
                OpenButtonEditor?.Invoke();
                Hide?.Invoke();
                return null;

            case "inputEnabled":
                _vm.InputEnabled = call.On ?? true;
                break;

            case "syncKeyboardLayout":
                _vm.SyncKeyboardLayout = call.On ?? true;
                break;

            case "adminControl":
                _vm.AdminWindowControl = call.On ?? true;
                break;

            case "openHub":
                OpenHub?.Invoke();
                Hide?.Invoke();
                return null;

            case "openSettings":
                OpenSettings?.Invoke();
                Hide?.Invoke();
                return null;

            case "hideButton":
                HideButton?.Invoke();
                Hide?.Invoke();
                return null;

            case "close":
                Hide?.Invoke();
                return null;

            default:
                // Everything about a device is the field's, so there is one implementation of each action.
                return await _hub.RunCommandAsync(call.Name, call.DeviceId, null, call.Text);
        }

        Changed();
        return null;
    }

    private sealed record CommandArgs(
        string Name,
        uint StreamId,
        string? DeviceId,
        int? Monitor,
        string? Text,
        bool? On,
        double? ScreenX,
        double? ScreenY);
}
