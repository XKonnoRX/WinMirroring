using System.Collections.Generic;

namespace WinMirroring.App.Services;

/// <summary>One open screen view, for listing it and switching to it in the control panel.</summary>
/// <param name="InputAllowed">The other device accepts control from this view.</param>
/// <param name="InputOn">Mouse, touch and keys go to the other device (off: only watching).</param>
/// <param name="Zoom">How far the picture is zoomed in here (1: whole).</param>
public sealed record OpenViewInfo(uint StreamId, string Title, string PeerId, string Kind, int MonitorIndex, int MonitorCount, bool IsActive, bool InputAllowed, bool InputOn, double Zoom = 1);

/// <summary>
/// Lets the control panel be the single place to pick what is on screen: it lists the open views, switches between
/// them and back to this device's own desktop, opens a device's screen (a chosen monitor) without opening a second
/// window for one that is already open, and carries out the active view's own actions.
/// </summary>
public interface IViewerControl
{
    bool IsScreenOpen(string peerId);

    /// <summary>The key of the monitor an open view of this device shows, or null when none is open.</summary>
    string? ShownMonitorOf(string peerId);

    /// <summary>
    /// Shows a monitor of the device: switches the view that is already open to it, or opens one. The monitor is
    /// named by <paramref name="monitorKey"/> where it is known, since an index is only a position in a list.
    /// </summary>
    void ShowOrSwitchScreen(string peerId, int monitorIndex, string? monitorKey = null);

    IReadOnlyList<OpenViewInfo> OpenViews { get; }

    /// <summary>True when no view is in front: this device's own desktop is what is on screen.</summary>
    bool IsLocalDesktopActive { get; }

    /// <summary>Moves the full-screen views out of the way (they keep running) to show this device's desktop.</summary>
    void ShowLocalDesktop();

    void SwitchTo(uint streamId);

    void CloseView(uint streamId);

    void SwitchMonitor(uint streamId, int index);

    void ToggleKeyboard(uint streamId);

    /// <summary>Control the other device from this view, or only watch it.</summary>
    void SetViewInput(uint streamId, bool on);

    /// <summary>Zooms the view in (positive), out (negative), or back to the whole picture (zero).</summary>
    void ZoomView(uint streamId, int direction);

    /// <summary>
    /// Small pictures of the monitors of a device whose screen is open here, by monitor name, as images a page can
    /// show ("data:image/jpeg;base64,…"). Empty when no view of it is open or none have arrived.
    /// </summary>
    IReadOnlyDictionary<string, string> MonitorPictures(string peerId);

    /// <summary>Says whether those pictures are wanted just now (while the menu that shows them is open).</summary>
    void WantMonitorPictures(bool on);

    /// <summary>A picture arrived; raised on the UI thread.</summary>
    event Action? MonitorPicturesChanged;

    /// <summary>The person watching says this view hitched just now: both devices note it in their logs.</summary>
    void MarkHitch(uint streamId);
}
