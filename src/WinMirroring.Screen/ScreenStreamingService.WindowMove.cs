using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Screen.Display;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen;

/// <summary>
/// Putting a window on another device moves the <em>real</em> window onto the virtual monitor — a real monitor with
/// no physical screen. There the window still renders and takes input (so the picture and remote control keep
/// working), while it is gone from this device's screens. Used both for the second monitor and to hide a window that
/// is only shown as a copy elsewhere, so the same window is not visible in two places. Each moved window's previous
/// place is kept so it can be put back when it comes home, when the device disconnects, or on exit.
/// </summary>
public sealed partial class ScreenStreamingService
{
    private readonly ConcurrentDictionary<nint, MovedWindow> _movedWindows = new();

    private readonly record struct MovedWindow(Rectangle Rect, bool Maximized);

    /// <summary>Windows currently on the virtual monitor and waiting to be put back.</summary>
    public int MovedWindowCount => _movedWindows.Count;

    /// <summary>
    /// Sends <paramref name="window"/> to <paramref name="peerId"/>: the real window is moved onto the virtual
    /// monitor when that peer is this device's second monitor, otherwise a copy of the window is streamed there.
    /// </summary>
    public async Task<bool> TransferWindowAsync(string peerId, nint window)
    {
        if (IsExtendedTo(peerId) && TryMoveToDisplay(window))
        {
            return true;
        }

        return await SendWindowAsync(peerId, window, hideSource: true).ConfigureAwait(false);
    }

    /// <summary>Moves a real window onto the already-attached virtual monitor (second-monitor case).</summary>
    public bool TryMoveToDisplay(nint window)
    {
        if (_displayDevice is not { } device || VirtualDisplay.FindMonitor(device) is not { } monitor)
        {
            return false;
        }

        return MoveWindowOnto(window, monitor);
    }

    /// <summary>
    /// Parks a window on the virtual monitor to hide it while it is shown as a copy elsewhere: attaches the monitor
    /// if needed. A no-op when there is no driver (the window stays visible) or the monitor is busy as a second
    /// monitor.
    /// </summary>
    private async Task ParkOnVirtualMonitorAsync(nint window)
    {
        if (_movedWindows.ContainsKey(window) || ExtendedTo is not null)
        {
            return;
        }

        var monitor = await EnsureVirtualMonitorAttachedAsync().ConfigureAwait(false);
        if (monitor is not null)
        {
            MoveWindowOnto(window, monitor);
        }
    }

    private bool MoveWindowOnto(nint window, Capture.MonitorDescription monitor)
    {
        if (!TryCaptureHome(window, out var home, out var maximized))
        {
            return false;
        }

        var area = MonitorWorkArea(monitor.Handle) ?? monitor.Bounds;
        var target = Capture.WindowPlacer.PlaceInside(home, area);
        _movedWindows[window] = new MovedWindow(home, maximized);
        SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        _logger.LogInformation("Window {Window} moved onto the virtual monitor at {Target}", window, target);
        return true;
    }

    /// <summary>Attaches the virtual monitor so a window can live on it; returns it, or null when there is no driver.</summary>
    private async Task<Capture.MonitorDescription?> EnsureVirtualMonitorAttachedAsync()
    {
        if (VirtualDisplay.FindDeviceName() is not { } device)
        {
            return null;
        }

        await _displayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _displayDevice = device;
            if (!VirtualDisplay.IsAttached(device))
            {
                var (width, height, _) = VirtualDisplay.PrimaryScreen();
                var mode = VirtualDisplay.PickMode(VirtualDisplay.Modes(device), width, height, out _) ?? new DisplayMode(width, height, 60);
                var others = Capture.DisplayCatalog.Monitors().Where(m => !string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase)).ToList();
                var position = VirtualDisplay.PlaceBeside(others, new Size(mode.Width, mode.Height), "right");
                if (!VirtualDisplay.Attach(device, mode, position, out _))
                {
                    VirtualDisplay.ExtendTopology();
                }

                await WaitForMonitorAsync(device, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            return VirtualDisplay.FindMonitor(device);
        }
        finally
        {
            _displayGate.Release();
        }
    }

    /// <summary>Takes the virtual monitor off the desktop once nothing is parked on it and it is not a second monitor.</summary>
    private async Task DetachVirtualMonitorIfIdleAsync()
    {
        await _displayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ExtendedTo is null && _movedWindows.IsEmpty && _displayDevice is { } device && VirtualDisplay.IsAttached(device))
            {
                _logger.LogInformation("No windows left on the virtual monitor; detaching {Device}: {Result}", device, VirtualDisplay.Detach(device));
                _displayDevice = null;
            }
        }
        finally
        {
            _displayGate.Release();
        }
    }

    /// <summary>Puts one window back where it was before it was moved onto the virtual monitor.</summary>
    public void RestoreWindow(nint window)
    {
        if (_movedWindows.TryRemove(window, out var moved))
        {
            RestoreTo(window, moved);
        }
    }

    /// <summary>Puts every window on the virtual monitor back where it came from.</summary>
    public void RestoreMovedWindows()
    {
        foreach (var entry in _movedWindows.ToArray())
        {
            if (_movedWindows.TryRemove(entry.Key, out var moved))
            {
                RestoreTo(entry.Key, moved);
            }
        }
    }

    private void RestoreTo(nint window, MovedWindow moved)
    {
        if (!IsWindow(window))
        {
            return;
        }

        SetWindowPos(window, 0, moved.Rect.X, moved.Rect.Y, moved.Rect.Width, moved.Rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (moved.Maximized)
        {
            ShowWindow(window, SW_MAXIMIZE);
        }

        _logger.LogInformation("Window {Window} brought back to {Rect}", window, moved.Rect);
    }

    /// <summary>Reads a window's current place to return it to later, restoring it from maximised first.</summary>
    private static bool TryCaptureHome(nint window, out Rectangle home, out bool maximized)
    {
        home = Rectangle.Empty;
        maximized = false;
        if (!IsWindow(window) || !GetWindowRect(window, out var native))
        {
            return false;
        }

        maximized = IsZoomed(window);
        if (maximized)
        {
            // A maximised window is tied to its monitor; move it as its restored size instead.
            ShowWindow(window, SW_RESTORE);
            if (!GetWindowRect(window, out native))
            {
                return false;
            }
        }

        home = native.ToRectangle();
        return true;
    }
}
