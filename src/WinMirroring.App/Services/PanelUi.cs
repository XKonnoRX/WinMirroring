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
            }).ToList(),
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
                monitors = _screen.PeerMonitors(p.Id)
                    .Select(m => new { index = m.Index, label = m.Label, primary = m.Primary, key = m.Key })
                    .ToList(),
                screenOpen = _viewers.IsScreenOpen(p.Id),
                secondMonitor = _vm.IsSecondMonitor(p.Id),
                canBecome = _screen.PeerVirtualDisplay(p.Id),
                sound = new { here = _vm.SoundHereChoice(p.Id).ToString(), playing = _vm.IsSoundFrom(p.Id), sentTo = _vm.IsSoundSentTo(p.Id) },
            }).ToList(),
        };
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
