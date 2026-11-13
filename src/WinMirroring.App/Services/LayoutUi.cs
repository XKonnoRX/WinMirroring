using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Input;

namespace WinMirroring.App.Services;

/// <summary>What the layout page shows and saves: the devices with their monitors, and where each one stands.</summary>
public sealed class LayoutUi
{
    private readonly MirroringNode _node;
    private readonly InputSharingService _input;
    private readonly UiBridge _bridge;

    public LayoutUi(MirroringNode node, InputSharingService input, UiBridge bridge)
    {
        _node = node;
        _input = input;
        _bridge = bridge;
    }

    public void Register()
    {
        _bridge.On("layout.get", _ => (object?)Snapshot());
        _bridge.On("layout.save", args =>
        {
            var saved = args.Deserialize<SaveArgs>(UiBridge.Json);
            _input.ApplyLayout((saved?.Cells ?? []).Select(c => new LayoutCell(c.DeviceId, c.Column, c.Row, Empty(c.Monitor))));
            return null;
        });

        // The page follows the app: a device connects, its monitors change, another device sends a new layout.
        _input.LayoutChanged += Changed;
        _input.MonitorsChanged += Changed;
        _node.Sessions.SessionReady += _ => Changed();
        _node.Sessions.SessionClosed += _ => Changed();
    }

    /// <summary>These come from worker threads; the pages are served on the UI thread like every other window.</summary>
    private void Changed() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _bridge.Push("layout.changed"));

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private object Snapshot()
    {
        var devices = new List<DeviceDto>
        {
            new(_node.Identity.Id, _node.Settings.DeviceName, true, true, MonitorsOf(_node.Identity.Id)),
        };

        // Every paired device, connected or not: a layout can be built before the other one is switched on.
        foreach (var peer in _node.Peers.Snapshot().Where(p => p.IsPaired && p.Id != _node.Identity.Id))
        {
            var online = _node.Sessions.TryGetSession(peer.Id, out var session) && session.State == SessionState.Ready;
            devices.Add(new DeviceDto(peer.Id, peer.Name, false, online, MonitorsOf(peer.Id)));
        }

        var known = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var cells = _input.Layout.Cells
            .Where(c => known.Contains(c.DeviceId))
            .Select(c => new CellDto(c.DeviceId, c.Monitor, c.Column, c.Row))
            .ToList();

        return new { devices, cells };
    }

    private List<MonitorDto> MonitorsOf(string deviceId)
    {
        var monitors = deviceId == _node.Identity.Id ? _input.LocalMonitors : _input.PeerMonitors(deviceId);
        return monitors
            .Select(m => new MonitorDto(
                m.Key,
                ShortLabel(m.Label),
                m.Width > 0 && m.Height > 0 ? $"{m.Width}×{m.Height}" : string.Empty,
                m.Width,
                m.Height,
                m.Primary))
            .ToList();
    }

    /// <summary>"Монитор 2 · 1920×1080 · справа" → "Монитор 2 · справа": the size is shown on the card itself.</summary>
    private static string ShortLabel(string label)
    {
        var parts = label.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(p => !p.Contains('×', StringComparison.Ordinal)).ToList();
        return kept.Count > 0 ? string.Join(" · ", kept) : label;
    }

    private sealed record MonitorDto(string Key, string Label, string Size, int Width, int Height, bool Primary);

    private sealed record DeviceDto(string Id, string Name, bool IsThisDevice, bool Online, List<MonitorDto> Monitors);

    private sealed record CellDto(string DeviceId, string? Monitor, int Column, int Row);

    private sealed record SaveArgs(List<CellDto>? Cells);
}
