using System.Collections.Generic;

namespace WinMirroring.App.Services;

/// <summary>One open screen view, for listing it and switching to it in the control panel.</summary>
public sealed record OpenViewInfo(uint StreamId, string Title, string PeerId, string Kind, int MonitorIndex, int MonitorCount, bool IsActive);

/// <summary>
/// Lets the control panel be the single place to pick which view is on screen: it lists the open views, switches
/// between them, opens a device's screen without opening a second window for one that is already open, and carries
/// out the active view's own actions (switch monitor, on-screen keyboard, close).
/// </summary>
public interface IViewerControl
{
    bool IsScreenOpen(string peerId);

    void ShowOrSwitchScreen(string peerId);

    IReadOnlyList<OpenViewInfo> OpenViews { get; }

    void SwitchTo(uint streamId);

    void CloseView(uint streamId);

    void SwitchMonitor(uint streamId, int index);

    void ToggleKeyboard(uint streamId);
}
