using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using WinMirroring.App.Services;
using WinMirroring.App.Views;
using WinMirroring.Screen;

namespace WinMirroring.App;

/// <summary>Open screen views: one window per stream, shown full screen and switched between from the control panel.</summary>
public partial class App : IViewerControl
{
    private readonly List<ScreenViewerWindow> _openViewers = [];
    private uint _activeView;

    private void OpenViewerWindow(ScreenViewer viewer)
    {
        var window = new ScreenViewerWindow(viewer, _services!);
        window.Closed += (_, _) =>
        {
            _openViewers.Remove(window);
            if (_activeView == viewer.StreamId)
            {
                _activeView = _openViewers.Count > 0 ? _openViewers[^1].Viewer.StreamId : 0;
            }
        };

        _openViewers.Add(window);
        window.Show();
        window.OpenFullScreenExternal();
        window.BringToFront();
        _activeView = viewer.StreamId;
        KeepFloatingOnTop();
    }

    private void KeepFloatingOnTop()
    {
        // Re-raise the floating button above the just-shown view, so the panel stays reachable over full screen.
        if (_floatingMenu is { IsVisible: true } button)
        {
            button.Topmost = false;
            button.Topmost = true;
        }
    }

    private ScreenViewerWindow? FindView(uint streamId) => _openViewers.FirstOrDefault(v => v.Viewer.StreamId == streamId);

    public bool IsScreenOpen(string peerId) =>
        _openViewers.Any(v => v.Viewer.Info.Kind == "monitor" && v.Viewer.Session.PeerId == peerId);

    public void ShowOrSwitchScreen(string peerId)
    {
        var existing = _openViewers.FirstOrDefault(v => v.Viewer.Info.Kind == "monitor" && v.Viewer.Session.PeerId == peerId);
        if (existing is not null)
        {
            SwitchTo(existing.Viewer.StreamId);
        }
        else if (_services?.GetService<ScreenStreamingService>() is { } screen)
        {
            _ = screen.RequestScreenAsync(peerId);
        }
    }

    public IReadOnlyList<OpenViewInfo> OpenViews =>
        _openViewers.Select(v => new OpenViewInfo(
            v.Viewer.StreamId,
            $"{v.Viewer.PeerName}: {v.Viewer.Info.Title}",
            v.Viewer.Session.PeerId,
            v.Viewer.Info.Kind,
            v.Viewer.Info.MonitorIndex,
            v.Viewer.Info.MonitorCount,
            v.Viewer.StreamId == _activeView)).ToList();

    public void SwitchTo(uint streamId)
    {
        if (FindView(streamId) is { } window)
        {
            window.BringToFront();
            _activeView = streamId;
            KeepFloatingOnTop();
        }
    }

    public void CloseView(uint streamId) => FindView(streamId)?.CloseView();

    public void SwitchMonitor(uint streamId, int index) => FindView(streamId)?.SwitchToMonitor(index);

    public void ToggleKeyboard(uint streamId) => FindView(streamId)?.ToggleOnScreenKeyboard();
}
