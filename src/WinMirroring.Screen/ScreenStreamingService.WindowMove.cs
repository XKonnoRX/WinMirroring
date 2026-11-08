using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Screen.Display;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen;

/// <summary>
/// Transferring a window to a device that is this device's second monitor moves the <em>real</em> window onto the
/// virtual monitor, so it genuinely leaves this screen and the device shows it (no copy, no overlap with the
/// original). Each moved window's previous place is kept so it can be put back when it comes home, when the device
/// disconnects, or when the second monitor is turned off.
/// </summary>
public sealed partial class ScreenStreamingService
{
    private readonly ConcurrentDictionary<nint, MovedWindow> _movedWindows = new();

    private readonly record struct MovedWindow(Rectangle Rect, bool Maximized);

    /// <summary>Windows currently parked on the virtual monitor and waiting to be put back.</summary>
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

        return await SendWindowAsync(peerId, window).ConfigureAwait(false);
    }

    /// <summary>Moves a real window onto the virtual monitor; false when there is no virtual monitor to move it onto.</summary>
    public bool TryMoveToDisplay(nint window)
    {
        if (!IsWindow(window) || _displayDevice is not { } device || VirtualDisplay.FindMonitor(device) is not { } monitor)
        {
            return false;
        }

        if (!GetWindowRect(window, out var native))
        {
            return false;
        }

        var maximized = IsZoomed(window);
        if (maximized)
        {
            // A maximized window would cover the whole virtual monitor; move it as its restored size instead.
            ShowWindow(window, SW_RESTORE);
            GetWindowRect(window, out native);
        }

        var home = native.ToRectangle();
        var area = MonitorWorkArea(monitor.Handle) ?? monitor.Bounds;
        var target = Capture.WindowPlacer.PlaceInside(home, area);
        _movedWindows[window] = new MovedWindow(home, maximized);
        SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        _logger.LogInformation("Window {Window} moved onto the virtual monitor at {Target}", window, target);
        return true;
    }

    /// <summary>Puts one parked window back where it was before it was moved onto the virtual monitor.</summary>
    public void RestoreWindow(nint window)
    {
        if (_movedWindows.TryRemove(window, out var moved))
        {
            RestoreTo(window, moved);
        }
    }

    /// <summary>Puts every window parked on the virtual monitor back where it came from.</summary>
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
}
