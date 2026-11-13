using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WinMirroring.App.ViewModels;
using WinMirroring.Core;
using WinMirroring.Core.Settings;

namespace WinMirroring.App.Services;

/// <summary>
/// The settings page: one snapshot of everything that is not about a particular device, one way to change a
/// value by name, and the few buttons that do something. The view model keeps doing the work (it validates and
/// saves), so both the old window and this page stay in step.
/// </summary>
public sealed class SettingsUi
{
    private readonly MirroringNode _node;
    private readonly MainViewModel _vm;
    private readonly FeatureHealth _health;
    private readonly UiBridge _bridge;

    public SettingsUi(MirroringNode node, MainViewModel vm, FeatureHealth health, UiBridge bridge)
    {
        _node = node;
        _vm = vm;
        _health = health;
        _bridge = bridge;
    }

    public void Register()
    {
        _bridge.On("settings.get", _ => Snapshot());
        _bridge.On("settings.set", args =>
        {
            var change = args.Deserialize<SetArgs>(UiBridge.Json);
            return change is null ? "Непонятная настройка." : Set(change);
        });

        _bridge.OnAsync("settings.command", async args =>
        {
            var call = args.Deserialize<CommandArgs>(UiBridge.Json);
            return call is null ? "Непонятная команда." : await RunAsync(call);
        });

        _health.Changed += Changed;
        _vm.PropertyChanged += (_, e) =>
        {
            // The status line and the live texts are part of the page; everything else is pushed on demand.
            if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.AudioStatusText)
                or nameof(MainViewModel.ClipboardActivity) or nameof(MainViewModel.SmoothingText))
            {
                Changed();
            }
        };
    }

    private void Changed() => Application.Current?.Dispatcher.BeginInvoke(() => _bridge.Push("settings.changed"));

    private object Snapshot() => new
    {
        device = new
        {
            name = _vm.DeviceName,
            id = _vm.DeviceId,
            fingerprint = _vm.Fingerprint,
            network = _vm.NetworkSummary,
            version = DeviceInfoService.LocalVersion.ToString(3),
            versionText = _vm.VersionText,
            status = _vm.StatusText,
        },
        input = new
        {
            enabled = _vm.InputEnabled,
            edgeResistance = _vm.EdgeResistance,
            smoothing = _vm.SmoothingEnabled,
            maxSmoothingDelay = _vm.MaxSmoothingDelay,
            syncKeyboardLayout = _vm.SyncKeyboardLayout,
            smoothingText = _vm.SmoothingText,
            adminControl = _vm.AdminWindowControl,
            adminControlText = _vm.AdminWindowControlText,
            elevationText = _vm.ElevationText,
            canRestartElevated = _vm.CanRestartElevated,
        },
        clipboard = new
        {
            enabled = _vm.ClipboardEnabled,
            shareFiles = _vm.ClipboardShareFiles,
            dragDrop = _vm.ClipboardDragDrop,
            maxInline = _vm.ClipboardMaxInline,
            directPaste = _vm.ClipboardDirectPaste,
            activity = _vm.ClipboardActivity,
        },
        audio = new
        {
            send = _vm.AudioSendEnabled,
            followInput = _vm.AudioFollowInput,
            pinnedId = _vm.AudioPinnedTarget?.Id ?? string.Empty,
            muteLocal = _vm.AudioMuteLocal,
            receive = _vm.AudioReceiveEnabled,
            bitrate = _vm.AudioBitrate,
            buffer = _vm.AudioBuffer,
            status = _vm.AudioStatusText,
            targets = _vm.DeviceChoices.Select(c => new { id = c.Id, name = c.Name }).ToList(),
        },
        screen = new
        {
            allowViewing = _vm.ScreenAllowViewing,
            allowControl = _vm.ScreenAllowControl,
            windowDrag = _vm.ScreenWindowDrag,
            hideSentWindow = _vm.ScreenHideSentWindow,
            maxFps = _vm.ScreenMaxFps,
            quality = _vm.ScreenQuality,
            codec = _vm.ScreenVideoCodec.ToString(),
            codecs = _vm.ScreenVideoCodecOptions.Select(o => new { value = o.Value.ToString(), label = o.Label }).ToList(),
            videoBitrate = _vm.ScreenVideoBitrate,
            virtualDisplay = _vm.VirtualDisplayStatus,
            sources = _vm.ScreenSources.Select(s => new { streamId = s.StreamId, title = s.Title, peer = s.PeerName }).ToList(),
        },
        network = new
        {
            discoveryPort = _vm.DiscoveryPort,
            transportPort = _vm.TransportPort,
            interfaces = _vm.Interfaces
                .Select(i => new { id = i.Id, name = i.Name, address = i.Address, details = i.Details, selected = i.IsSelected, vpn = i.IsVpnLike })
                .ToList(),
            addresses = _node.Settings.ManualPeerAddresses.ToList(),
        },
        startup = new { withWindows = _vm.StartWithWindows, autoUpdate = _vm.AutoUpdate },
        health = _health.Snapshot.Select(f => new { name = f.Name, ok = f.Ok, detail = f.Detail }).ToList(),
    };

    private object? Set(SetArgs change)
    {
        var text = change.Value.ValueKind == JsonValueKind.String ? change.Value.GetString() ?? string.Empty : string.Empty;
        bool Flag() => change.Value.ValueKind == JsonValueKind.True;
        int Number() => change.Value.ValueKind == JsonValueKind.Number
            ? change.Value.GetInt32()
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        switch (change.Key)
        {
            case "device.name": _vm.DeviceName = text; break;

            case "input.enabled": _vm.InputEnabled = Flag(); break;
            case "input.edgeResistance": _vm.EdgeResistance = Number(); break;
            case "input.smoothing": _vm.SmoothingEnabled = Flag(); break;
            case "input.maxSmoothingDelay": _vm.MaxSmoothingDelay = Number(); break;
            case "input.syncKeyboardLayout": _vm.SyncKeyboardLayout = Flag(); break;
            case "input.adminControl": _vm.AdminWindowControl = Flag(); break;

            case "clipboard.enabled": _vm.ClipboardEnabled = Flag(); break;
            case "clipboard.shareFiles": _vm.ClipboardShareFiles = Flag(); break;
            case "clipboard.dragDrop": _vm.ClipboardDragDrop = Flag(); break;
            case "clipboard.maxInline": _vm.ClipboardMaxInline = Number(); break;
            case "clipboard.directPaste": _vm.ClipboardDirectPaste = Number(); break;

            case "audio.send": _vm.AudioSendEnabled = Flag(); break;
            case "audio.followInput": _vm.AudioFollowInput = Flag(); break;
            case "audio.muteLocal": _vm.AudioMuteLocal = Flag(); break;
            case "audio.receive": _vm.AudioReceiveEnabled = Flag(); break;
            case "audio.bitrate": _vm.AudioBitrate = Number(); break;
            case "audio.buffer": _vm.AudioBuffer = Number(); break;
            case "audio.pinnedId":
                _vm.AudioPinnedTarget = _vm.DeviceChoices.FirstOrDefault(c => c.Id == text) ?? _vm.DeviceChoices.FirstOrDefault();
                break;

            case "screen.allowViewing": _vm.ScreenAllowViewing = Flag(); break;
            case "screen.allowControl": _vm.ScreenAllowControl = Flag(); break;
            case "screen.windowDrag": _vm.ScreenWindowDrag = Flag(); break;
            case "screen.hideSentWindow": _vm.ScreenHideSentWindow = Flag(); break;
            case "screen.maxFps": _vm.ScreenMaxFps = Number(); break;
            case "screen.quality": _vm.ScreenQuality = Number(); break;
            case "screen.videoBitrate": _vm.ScreenVideoBitrate = Number(); break;
            case "screen.codec":
                if (Enum.TryParse<VideoCodec>(text, out var codec))
                {
                    _vm.ScreenVideoCodec = codec;
                }

                break;

            case "network.discoveryPort": _vm.DiscoveryPort = Number(); break;
            case "network.transportPort": _vm.TransportPort = Number(); break;
            case "network.interface":
                if (_vm.Interfaces.FirstOrDefault(i => i.Id == text) is { } nic)
                {
                    nic.IsSelected = change.On ?? !nic.IsSelected;
                }

                break;

            case "startup.withWindows": _vm.StartWithWindows = Flag(); break;
            case "startup.autoUpdate": _vm.AutoUpdate = Flag(); break;

            default: return $"Непонятная настройка: {change.Key}";
        }

        Changed();
        return null;
    }

    private async Task<object?> RunAsync(CommandArgs call)
    {
        switch (call.Name)
        {
            case "applyNetwork":
                await _vm.ApplyNetworkCommand.ExecuteAsync(null);
                break;

            case "refreshInterfaces":
                _vm.RefreshInterfacesCommand.Execute(null);
                break;

            case "addFirewall":
                await _vm.AddFirewallRulesCommand.ExecuteAsync(null);
                break;

            case "openLogs":
                _vm.OpenLogsCommand.Execute(null);
                break;

            case "checkUpdates":
                await _vm.CheckForUpdatesCommand.ExecuteAsync(null);
                break;

            case "openRepository":
                _vm.OpenRepositoryCommand.Execute(null);
                break;

            case "restartElevated":
                await _vm.RestartElevatedCommand.ExecuteAsync(null);
                break;

            case "stopSource":
                if (_vm.ScreenSources.FirstOrDefault(s => s.StreamId == call.StreamId) is { } source)
                {
                    await _vm.StopScreenSourceCommand.ExecuteAsync(source);
                }

                break;

            case "returnHome":
                _vm.ReturnHomeCommand.Execute(null);
                break;

            default:
                return $"Непонятная команда: {call.Name}";
        }

        Changed();
        return _vm.StatusText;
    }

    private sealed record SetArgs(string Key, JsonElement Value, bool? On);

    private sealed record CommandArgs(string Name, uint StreamId);
}
