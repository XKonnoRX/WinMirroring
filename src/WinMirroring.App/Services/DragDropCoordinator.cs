using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard;
using WinMirroring.Clipboard.Files;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Input;
using WinMirroring.Input.Protocol;

namespace WinMirroring.App.Services;

/// <summary>
/// Drag and drop of files across screen edges. Joins what input sharing knows (where the cursor is, which
/// buttons are held) with what the clipboard drag engine does (catching a drag, running one with virtual files).
/// <list type="bullet">
/// <item><b>Out of this device</b>: capture to a peer starts while the left button is held → a catch surface under
/// the parked cursor learns what the local drag carries → the peer is told to start the same drag under its cursor.</item>
/// <item><b>Out of a controlled device</b>: it catches its own drag at the edge and hands it to us before the cursor
/// comes back; when the cursor lands here (or on a third device), the drag starts there.</item>
/// </list>
/// Contents are pulled from the device that owns the files only when the drop happens.
/// </summary>
public sealed class DragDropCoordinator : IDisposable
{
    private const int VkLButton = 0x01;
    private static readonly TimeSpan CatchTimeout = TimeSpan.FromMilliseconds(300);

    private readonly MirroringNode _node;
    private readonly InputSharingService _input;
    private readonly ClipboardSyncService _clipboard;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private string? _focus;
    private DragPayload? _handedToUs;

    public DragDropCoordinator(MirroringNode node, InputSharingService input, ClipboardSyncService clipboard, ILogger<DragDropCoordinator> logger)
    {
        _node = node;
        _input = input;
        _clipboard = clipboard;
        _logger = logger;
    }

    private bool Enabled => _node.Settings.Clipboard.DragDropEnabled && _clipboard.Drag is not null;

    public void Start()
    {
        _input.FocusChanged += OnFocusChanged;
        _input.ControlEnding += OnControlEnding;
        _input.BeforeEdgeHandOff = HandOffAsync;
        _clipboard.DragStartReceived += OnDragStart;
        _clipboard.DragOutReceived += OnDragOut;
    }

    // ---- controller side ---------------------------------------------------------------------------------

    private void OnFocusChanged(string? peerId)
    {
        string? previous;
        DragPayload? handed;
        lock (_gate)
        {
            previous = _focus;
            _focus = peerId;
            handed = _handedToUs;
            _handedToUs = null;
        }

        if (!Enabled)
        {
            return;
        }

        var leftHeld = _input.IsLocalButtonPhysicallyDown(MouseButton.Left);
        if (peerId is not null)
        {
            if (handed is not null)
            {
                // A drag came out of one controlled device and the cursor went straight on to another.
                if (leftHeld && handed.SourceId != peerId)
                {
                    _ = SendStartAsync(peerId, handed);
                }
            }
            else if (previous is null && leftHeld)
            {
                _ = Guard(() => CatchLocalDragAsync(peerId));
            }

            return;
        }

        _ = Guard(() => _clipboard.Drag!.HideSurfaceAsync());

        // The cursor is back here. A drag of our own files is still running locally (we never dropped it), so only
        // a drag of another device's files needs to be started.
        if (handed is not null && leftHeld && handed.SourceId != _node.Identity.Id)
        {
            _ = Guard(() => StartLocalDragAsync(handed));
        }
    }

    private void OnDragOut(PeerSession from, DragPayload payload)
    {
        lock (_gate)
        {
            if (_focus == from.PeerId)
            {
                _handedToUs = payload;
            }
        }
    }

    private async Task CatchLocalDragAsync(string peerId)
    {
        var caught = await _clipboard.Drag!.CatchAsync(_input.CursorParkPoint, CatchTimeout, _input.NudgeCursor);
        var payload = ToPayload(caught);
        if (payload is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_focus != peerId)
            {
                return;
            }
        }

        if (_input.IsLocalButtonPhysicallyDown(MouseButton.Left))
        {
            await SendStartAsync(peerId, payload);
        }
    }

    private async Task StartLocalDragAsync(DragPayload payload)
    {
        var source = _clipboard.SourceFor(payload.SourceId);
        if (source is null)
        {
            _logger.LogInformation("Cannot drag files from {Source}: not connected to it", payload.Manifest.SourceName);
            return;
        }

        var drag = _clipboard.Drag!;
        await drag.ShowOriginAsync();

        // The user still holds the physical button, but this device never saw it go down (it was captured).
        _input.InjectLocalButton(MouseButton.Left, down: true, track: false);
        await drag.StartDragAsync(payload, source, _clipboard.AllowsDirectPaste(payload.Manifest));

        if (!_input.IsLocalButtonPhysicallyDown(MouseButton.Left) && (GetAsyncKeyState(VkLButton) & 0x8000) != 0)
        {
            // The physical release slipped in before our press: do not leave the button stuck down.
            _input.InjectLocalButton(MouseButton.Left, down: false, track: false);
        }
    }

    private async Task SendStartAsync(string peerId, DragPayload payload)
    {
        if (_node.Sessions.TryGetSession(peerId, out var session))
        {
            await _clipboard.SendDragAsync(session, payload, start: true);
        }
    }

    // ---- controlled side ---------------------------------------------------------------------------------

    private void OnDragStart(PeerSession from, DragPayload payload)
    {
        if (!Enabled || _input.ControllerId != from.PeerId)
        {
            return;
        }

        var source = _clipboard.SourceFor(payload.SourceId);
        if (source is null)
        {
            _logger.LogInformation("Cannot drag files from {Source}: not connected to it", payload.Manifest.SourceName);
            return;
        }

        _ = Guard(() => RunControlledDragAsync(payload, source));
    }

    private async Task RunControlledDragAsync(DragPayload payload, IRemoteFileSource source)
    {
        var drag = _clipboard.Drag!;
        await drag.ShowOriginAsync();

        // Stands for the controller's button, which was already down when the cursor arrived. Tracked, so it is
        // released (and the drag cancelled first) if control leaves before the drop.
        _input.InjectLocalButton(MouseButton.Left, down: true, track: true);
        await drag.StartDragAsync(payload, source, _clipboard.AllowsDirectPaste(payload.Manifest));
    }

    private async Task HandOffAsync(PeerSession controller, System.Drawing.Point at)
    {
        if (!Enabled)
        {
            return;
        }

        var payload = ToPayload(await _clipboard.Drag!.CatchAsync(at, CatchTimeout, _input.NudgeCursor));
        if (payload is not null)
        {
            await _clipboard.SendDragAsync(controller, payload, start: false);
        }
    }

    private void OnControlEnding(string controllerId) => _clipboard.Drag?.CancelActiveDrag();

    // ---- helpers -----------------------------------------------------------------------------------------

    private DragPayload? ToPayload(DragCatch? caught) =>
        caught?.Forwarded ?? (caught?.LocalPaths is { Count: > 0 } paths ? _clipboard.CreateLocalDragPayload(paths) : null);

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drag and drop step failed");
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        _input.FocusChanged -= OnFocusChanged;
        _input.ControlEnding -= OnControlEnding;
        _input.BeforeEdgeHandOff = null;
        _clipboard.DragStartReceived -= OnDragStart;
        _clipboard.DragOutReceived -= OnDragOut;
    }
}
