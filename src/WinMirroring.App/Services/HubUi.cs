using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WinMirroring.App.ViewModels;
using WinMirroring.Audio;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Input;
using WinMirroring.Screen;

namespace WinMirroring.App.Services;

/// <summary>
/// Everything the main window shows: this device and the others (paired, and the ones just found nearby), where
/// they stand on the field, and what can be done with each one. The page asks for a snapshot, sends back a
/// layout, and runs commands by name; anything that changes here is pushed so the page follows along.
/// </summary>
public sealed class HubUi
{
    private readonly MirroringNode _node;
    private readonly InputSharingService _input;
    private readonly ScreenStreamingService _screen;
    private readonly DeviceInfoService _devices;
    private readonly MainViewModel _vm;
    private readonly IViewerControl _viewers;
    private readonly UiBridge _bridge;

    public HubUi(
        MirroringNode node,
        InputSharingService input,
        ScreenStreamingService screen,
        DeviceInfoService devices,
        MainViewModel vm,
        IViewerControl viewers,
        UiBridge bridge)
    {
        _node = node;
        _input = input;
        _screen = screen;
        _devices = devices;
        _vm = vm;
        _viewers = viewers;
        _bridge = bridge;
    }

    /// <summary>Opens the old settings window (still the place for everything not about a device).</summary>
    public Action? OpenSettings { get; set; }

    public void Register()
    {
        _bridge.On("hub.get", _ => Snapshot());
        _bridge.On("hub.save", args =>
        {
            var saved = args.Deserialize<SaveArgs>(UiBridge.Json);
            _input.ApplyLayout((saved?.Cells ?? []).Select(c =>
                new LayoutCell(c.DeviceId, c.Column, c.Row, string.IsNullOrEmpty(c.Monitor) ? null : c.Monitor)));
            return "Раскладка сохранена и отправлена устройствам.";
        });

        _bridge.OnAsync("hub.command", async args =>
        {
            var call = args.Deserialize<CommandArgs>(UiBridge.Json);
            return call is null ? "Непонятная команда." : await RunAsync(call);
        });

        _bridge.On("hub.devices", _ => Snapshot());

        _input.LayoutChanged += Changed;
        _input.MonitorsChanged += Changed;
        _input.FocusChanged += _ => Changed();
        _devices.Changed += Changed;
        _node.Introductions.Changed += Changed;
        _node.Peers.Changed += _ => Changed();
        _node.Sessions.SessionReady += _ => Changed();
        _node.Sessions.SessionClosed += _ => Changed();
        _screen.SourcesChanged += Changed;
    }

    /// <summary>These arrive on worker threads; the pages are served on the UI thread like every other window.</summary>
    private void Changed() => Application.Current?.Dispatcher.BeginInvoke(() => _bridge.Push("hub.changed"));

    // ---- what the page sees -------------------------------------------------------------------------------

    private object Snapshot()
    {
        // A device known only through a paired peer is not in the directory at all until it is seen on the
        // network, so the two lists are merged: whichever way it is known, it is one card on the field.
        var introduced = _node.Introductions.Snapshot().ToDictionary(i => i.Id, StringComparer.Ordinal);
        var devices = new List<DeviceDto> { Describe(null, null) };
        foreach (var peer in _node.Peers.Snapshot().Where(p => p.Id != _node.Identity.Id))
        {
            introduced.Remove(peer.Id, out var via);
            devices.Add(Describe(peer, via));
        }

        foreach (var via in introduced.Values)
        {
            devices.Add(Introduced(via));
        }

        var known = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        return new
        {
            self = _node.Identity.Id,
            inputFocus = _vm.InputFocusText,
            ownSound = _vm.OwnSoundLine,
            network = _vm.NetworkSummary,
            manualAddresses = _node.Settings.ManualPeerAddresses.ToList(),
            devices,
            cells = _input.Layout.Cells
                .Where(c => known.Contains(c.DeviceId))
                .Select(c => new CellDto(c.DeviceId, c.Monitor, c.Column, c.Row))
                .ToList(),
        };
    }

    /// <summary>A device only a paired peer has told us about: nothing of it is known first-hand yet.</summary>
    private static DeviceDto Introduced(PeerIntroduction via) => new(
        via.Id,
        via.Name,
        false,
        false,
        false,
        $"известно через {via.ViaName}",
        via.Addresses.FirstOrDefault() ?? string.Empty,
        string.Empty,
        string.Empty,
        Core.Identity.CertificateFingerprint.ToDisplay(via.Fingerprint),
        null,
        null,
        false,
        [],
        null,
        null,
        null,
        via.ViaName);

    private DeviceDto Describe(PeerInfo? peer, PeerIntroduction? via)
    {
        var id = peer?.Id ?? _node.Identity.Id;
        var self = peer is null;
        var item = _vm.Peers.FirstOrDefault(p => p.Id == id);
        var online = !self && _node.Sessions.TryGetSession(id, out var session) && session.State == SessionState.Ready;
        var version = self ? DeviceInfoService.LocalVersion.ToString(3) : _devices.PeerVersion(id)?.ToString(3);

        return new DeviceDto(
            id,
            self ? _node.Settings.DeviceName : peer!.Name,
            self,
            self || peer!.IsPaired,
            self || online,
            self ? "это устройство" : item?.StateText ?? peer!.State.ToString(),
            self ? string.Empty : item?.Address ?? peer!.TransportEndPoint?.ToString() ?? string.Empty,
            self ? string.Empty : _vm.HostOf(id),
            self ? string.Empty : item?.RttText ?? string.Empty,
            self ? Core.Identity.CertificateFingerprint.ToDisplay(_node.Identity.Fingerprint) : item?.FingerprintDisplay ?? string.Empty,
            self ? null : peer!.LastError,
            version,
            !self && _devices.CanUpdate(id),
            Monitors(id, self),
            self ? ScreenStreamingService.IsVirtualDisplayAvailable : _screen.PeerVirtualDisplay(id),
            self ? null : SoundOf(id),
            self ? null : _viewers.ShownMonitorOf(id),
            via?.ViaName);
    }

    private List<MonitorDto> Monitors(string id, bool self)
    {
        var monitors = self ? _input.LocalMonitors : _input.PeerMonitors(id);
        return monitors
            .Select(m => new MonitorDto(
                m.Key,
                ShortLabel(m.Label),
                m.Width > 0 && m.Height > 0 ? $"{m.Width}×{m.Height}" : string.Empty,
                m.Width,
                m.Height,
                m.Left,
                m.Top,
                m.Primary))
            .ToList();
    }

    private SoundDto SoundOf(string id) => new(
        _vm.SoundHereChoice(id) switch
        {
            IncomingSoundChoice.Always => "always",
            IncomingSoundChoice.Never => "never",
            _ => "auto",
        },
        _vm.IsSoundFrom(id),
        _vm.IsSoundSentTo(id));

    /// <summary>"Монитор 2 · 1920×1080 · справа" → "Монитор 2 · справа": the size is drawn on the card itself.</summary>
    private static string ShortLabel(string label)
    {
        var parts = label.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(p => !p.Contains('×', StringComparison.Ordinal)).ToList();
        return kept.Count > 0 ? string.Join(" · ", kept) : label;
    }

    // ---- what the page can do -----------------------------------------------------------------------------

    /// <summary>The same device actions the field offers, for the side panel.</summary>
    public Task<object?> RunCommandAsync(string name, string? deviceId, string? monitor, string? text) =>
        RunAsync(new CommandArgs(name, deviceId, monitor, text));

    private async Task<object?> RunAsync(CommandArgs call)
    {
        var id = call.DeviceId ?? string.Empty;
        var name = _node.Peers.TryGet(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name
            : _node.Introductions.TryGet(id, out var known) ? known.Name
            : id;
        switch (call.Name)
        {
            case "pair":
                await _node.PairAsync(id);
                return $"Запрос на сопряжение отправлен на {name}.";

            case "trust":
                return await _node.Introductions.TrustAsync(id)
                    ? $"{name} сопряжено — подтверждение дало доверенное устройство."
                    : $"{name} больше не предлагается доверенным устройством.";

            case "unpair":
                if (MessageBox.Show(
                        $"Разорвать сопряжение с {name}? Для повторного подключения понадобится новый PIN.",
                        "WinMirroring",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return null;
                }

                await _node.UnpairAsync(id);
                return $"Сопряжение с {name} разорвано.";

            case "showScreen":
                // Through the viewer control, so a device that is already shown switches monitor instead of
                // getting a second stream of its own.
                _viewers.ShowOrSwitchScreen(id, MonitorIndexOf(id, call.Monitor), call.Monitor);
                return null;

            case "sendWindow":
                await _vm.SendWindowToPeerAsync(id, name);
                return null;

            case "controlInput":
                _vm.ControlPeerById(id, name);
                return null;

            case "secondMonitor":
                await _vm.ToggleSecondMonitorAsync(id, name);
                return null;

            case "becomeSecondMonitor":
                await _vm.BecomeSecondMonitorAsync(id, name);
                return null;

            case "openOnDevice":
                await _vm.OpenOnDeviceAsync(id, name);
                return null;

            case "updateDevice":
                await _vm.UpdatePeerAsync(id, name);
                return null;

            case "soundHere":
                await _vm.SetSoundHereAsync(id, name, call.Text switch
                {
                    "always" => IncomingSoundChoice.Always,
                    "never" => IncomingSoundChoice.Never,
                    _ => IncomingSoundChoice.Automatic,
                });
                return null;

            case "soundTo":
                if (call.Text == "here")
                {
                    _vm.KeepSoundHere();
                }
                else
                {
                    _vm.SendSoundTo(call.Text == "auto" ? null : id, name);
                }

                return null;

            case "returnCursor":
                return _vm.ReturnCursor() ?? "Общий ввод сейчас никуда не передан.";

            case "returnWindows":
                _vm.ReturnWindows();
                return null;

            case "addAddress":
                return AddAddress(call.Text);

            case "removeAddress":
                _node.Settings.ManualPeerAddresses.RemoveAll(a => string.Equals(a, call.Text, StringComparison.OrdinalIgnoreCase));
                _node.SaveSettings();
                Changed();
                return $"Адрес {call.Text} убран.";

            case "openSettings":
                OpenSettings?.Invoke();
                return null;

            // The tools that were in the old window's context menu and nowhere else after it.
            case "copyAddress":
                return _vm.CopyAddressOf(id);

            case "openShare":
                return _vm.OpenShareOf(id);

            case "remoteDesktop":
                return _vm.RemoteDesktopTo(id);

            case "ping":
                return _vm.PingTo(id);

            case "latency":
                return await _vm.MeasureLatencyToAsync(id);

            case "peerLog":
                return await _vm.FetchLogOfAsync(id);

            case "reconnect":
                return await _vm.ReconnectToAsync(id);

            default:
                return $"Непонятная команда: {call.Name}";
        }
    }

    private string AddAddress(string? text)
    {
        var address = text?.Trim() ?? string.Empty;
        if (address.Length == 0)
        {
            return "Введите имя или IP устройства.";
        }

        if (_node.Settings.ManualPeerAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            return $"{address} уже в списке.";
        }

        _node.Settings.ManualPeerAddresses.Add(address);
        _node.SaveSettings();
        Changed();
        return $"{address} добавлен. Устройство появится, когда ответит (это может занять несколько секунд).";
    }

    /// <summary>
    /// The index that goes with a monitor key, for a device too old to understand keys. Matched on the key the
    /// device itself reports where it sends one, and only otherwise by position in the two lists.
    /// </summary>
    private int MonitorIndexOf(string deviceId, string? monitorKey)
    {
        if (string.IsNullOrEmpty(monitorKey))
        {
            return 0;
        }

        var remote = _screen.PeerMonitors(deviceId);
        if (remote.FirstOrDefault(m => m.Key == monitorKey) is { } named)
        {
            return named.Index;
        }

        var position = _input.PeerMonitors(deviceId).ToList().FindIndex(m => m.Key == monitorKey);
        return position >= 0 && position < remote.Count ? remote[position].Index : 0;
    }

    private sealed record MonitorDto(string Key, string Label, string Size, int Width, int Height, int Left, int Top, bool Primary);

    private sealed record SoundDto(string Here, bool Playing, bool SentTo);

    private sealed record DeviceDto(
        string Id,
        string Name,
        bool IsThisDevice,
        bool Paired,
        bool Online,
        string State,
        string Address,

        /// <summary>The address without a port: what RDP, a shared folder and a ping need, empty when unknown.</summary>
        string Host,
        string Rtt,
        string Fingerprint,
        string? LastError,
        string? Version,
        bool CanUpdate,
        List<MonitorDto> Monitors,
        bool? HasVirtualDisplay,
        SoundDto? Sound,
        string? ShownMonitor,
        string? IntroducedBy);

    private sealed record CellDto(string DeviceId, string? Monitor, int Column, int Row);

    private sealed record SaveArgs(List<CellDto>? Cells);

    private sealed record CommandArgs(string Name, string? DeviceId, string? Monitor, string? Text);
}
