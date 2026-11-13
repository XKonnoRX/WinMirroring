using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using WinMirroring.App.Services;
using WinMirroring.App.Views;
using WinMirroring.Core;
using WinMirroring.Screen;

namespace WinMirroring.App;

/// <summary>Open screen views: one window per stream, shown full screen and switched between from the control panel.</summary>
public partial class App : IViewerControl
{
    private readonly List<ScreenViewerWindow> _openViewers = [];

    /// <summary>Stream of the view in front, or 0 when this device's own desktop is.</summary>
    private uint _activeView;

    private void OpenViewerWindow(ScreenViewer viewer)
    {
        var window = new ScreenViewerWindow(viewer, _services!);
        window.Activated += (_, _) =>
        {
            _activeView = viewer.StreamId;
            _unusedSince.Remove(window);
        };

        window.StateChanged += (_, _) =>
        {
            NoteViewUse(window);
            UpdateKeepAwake();
        };

        window.Closed += (_, _) =>
        {
            _openViewers.Remove(window);
            _unusedSince.Remove(window);
            if (_activeView == viewer.StreamId)
            {
                _activeView = 0;
            }

            UpdateKeepAwake();
        };

        _openViewers.Add(window);
        window.Show();
        window.OpenFullScreenExternal();
        window.BringToFront();
        _activeView = viewer.StreamId;
        KeepFloatingOnTop();
        UpdateKeepAwake();
        StartUnusedViewWatch();
    }

    /// <summary>When each minimised view stopped being looked at; a view in front is not in here at all.</summary>
    private readonly Dictionary<ScreenViewerWindow, long> _unusedSince = [];
    private System.Windows.Threading.DispatcherTimer? _unusedWatch;

    private void NoteViewUse(ScreenViewerWindow window)
    {
        if (window.WindowState == System.Windows.WindowState.Minimized || !window.IsVisible)
        {
            _unusedSince.TryAdd(window, Environment.TickCount64);
        }
        else
        {
            _unusedSince.Remove(window);
        }
    }

    private void StartUnusedViewWatch()
    {
        _unusedWatch ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(30),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => CloseUnusedViews(),
            Dispatcher);
        _unusedWatch.Start();
    }

    /// <summary>
    /// A screen left minimised long enough is closed: the other device keeps capturing and sending for a window
    /// nobody looks at, and opening it again is one press. Only screens — a window copy follows the real window
    /// and minimises with it, which is not the user walking away, and being someone's second monitor is their
    /// screen, given up explicitly.
    /// </summary>
    private void CloseUnusedViews()
    {
        if (_exiting || _services is null)
        {
            _unusedWatch?.Stop();
            return;
        }

        if (_openViewers.Count == 0)
        {
            _unusedWatch?.Stop();
            return;
        }

        var minutes = _services.GetRequiredService<MirroringNode>().Settings.Screen.CloseUnusedViewMinutes;
        if (minutes <= 0)
        {
            return;
        }

        var limit = TimeSpan.FromMinutes(minutes).TotalMilliseconds;
        foreach (var (window, since) in _unusedSince.ToList())
        {
            if (window.Viewer.Info.Kind != "monitor" || Environment.TickCount64 - since < limit)
            {
                continue;
            }

            Log.Information("Closing the view of {Peer} ({Title}): unused for {Minutes} min", window.Viewer.PeerName, window.Viewer.Info.Title, minutes);
            ShowNotification(
                "Трансляция закрыта",
                $"Экран {window.Viewer.PeerName} не использовался {minutes} мин. Откройте его снова, когда понадобится.");
            window.CloseView();
        }
    }

    /// <summary>
    /// The display stays on while a view of another device is on screen here, or while another device shows one of
    /// this device's screens (its capture would otherwise go dark with the display).
    /// </summary>
    private void UpdateKeepAwake()
    {
        var watching = _openViewers.Any(v => v.IsVisible && v.WindowState != System.Windows.WindowState.Minimized);
        var watched = _services?.GetService<ScreenStreamingService>()?.IsStreamingScreen == true;
        KeepAwake.Set((watching || watched) && !_exiting);
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

    private ScreenViewerWindow? FindScreenView(string peerId) =>
        _openViewers.FirstOrDefault(v => v.Viewer.Info.Kind == "monitor" && v.Viewer.Session.PeerId == peerId);

    public bool IsScreenOpen(string peerId) => FindScreenView(peerId) is not null;

    public void ShowOrSwitchScreen(string peerId, int monitorIndex)
    {
        if (FindScreenView(peerId) is { } existing)
        {
            if (existing.Viewer.Info.MonitorIndex != monitorIndex)
            {
                existing.SwitchToMonitor(monitorIndex);
            }

            SwitchTo(existing.Viewer.StreamId);
        }
        else if (_services?.GetService<ScreenStreamingService>() is { } screen)
        {
            _ = screen.RequestScreenAsync(peerId, monitorIndex);
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
            v.Viewer.StreamId == _activeView,
            v.InputAllowed,
            v.InputOn)).ToList();

    public bool IsLocalDesktopActive => _activeView == 0 || FindView(_activeView) is null;

    public void ShowLocalDesktop()
    {
        // Window copies behave like ordinary windows and stay; full-screen screens step aside.
        foreach (var window in _openViewers.Where(v => v.Viewer.Info.Kind != "window"))
        {
            window.MinimizeView();
        }

        _activeView = 0;
    }

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

    public void SetViewInput(uint streamId, bool on) => FindView(streamId)?.SetInput(on);
}
