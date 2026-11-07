using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard;
using WinMirroring.Clipboard.Protocol;
using WinMirroring.Core.Peers;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen;

namespace WinMirroring.App.Services;

/// <summary>
/// Files through a screen viewer window.
/// <list type="bullet">
/// <item><b>Into a viewer</b>: files dropped on the picture go to the streaming device, which presses the button at
/// that point itself, starts a drag of virtual files there and releases it over the window under the point.</item>
/// <item><b>Out of a viewer</b>: a drag started in the picture (on the other device) and pulled past the viewer's edge
/// is caught there; the viewer ends that drag and starts the same one here, under the real cursor.</item>
/// </list>
/// </summary>
public sealed partial class DragDropCoordinator
{
    private static readonly TimeSpan ViewerCatchTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ViewerReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DragPayload?>> _viewerCatches = new();
    private readonly SemaphoreSlim _viewerDropGate = new(1, 1);
    private int _viewerRequestId;

    /// <summary>True when files can be dragged into and out of screen viewer windows on this device.</summary>
    public bool ViewerDragAvailable => Enabled;

    // ---- viewer side -------------------------------------------------------------------------------------

    /// <summary>Sends files dropped on a viewer to its device, to be dropped at the same point there.</summary>
    public async Task<bool> DropIntoViewerAsync(ScreenViewer viewer, IReadOnlyList<string> paths, float x, float y)
    {
        if (!Enabled || paths.Count == 0)
        {
            return false;
        }

        var payload = await Task.Run(() => _clipboard.CreateLocalDragPayload(paths)).ConfigureAwait(false);
        if (payload is null)
        {
            return false;
        }

        var id = (uint)Interlocked.Increment(ref _viewerRequestId);
        await _clipboard.SendViewerDragAsync(viewer.Session, ClipboardCodec.ViewerDropMessage, new ViewerDragMessage(viewer.StreamId, x, y, id, payload)).ConfigureAwait(false);
        _logger.LogInformation("Dropped {Count} item(s) into the viewer of {Peer}", payload.Manifest.TopLevel.Count(), viewer.PeerName);
        return true;
    }

    /// <summary>Asks the streaming device what is being dragged at a point of its picture (null: nothing).</summary>
    public async Task<DragPayload?> CatchFromViewerAsync(ScreenViewer viewer, float x, float y)
    {
        if (!Enabled)
        {
            return null;
        }

        var id = (uint)Interlocked.Increment(ref _viewerRequestId);
        var reply = new TaskCompletionSource<DragPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _viewerCatches[id] = reply;
        try
        {
            await _clipboard.SendViewerDragAsync(viewer.Session, ClipboardCodec.ViewerCatchMessage, new ViewerDragMessage(viewer.StreamId, x, y, id, null)).ConfigureAwait(false);
            return await reply.Task.WaitAsync(ViewerReplyTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("No answer from {Peer} about a drag out of its viewer", viewer.PeerName);
            return null;
        }
        finally
        {
            _viewerCatches.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Starts a drag of files caught in a viewer here. The user still holds the left button, but that press went to
    /// the viewer window; the caller releases its mouse capture first.
    /// </summary>
    public Task StartViewerDragAsync(DragPayload payload) =>
        (GetAsyncKeyState(VkLButton) & 0x8000) != 0 ? Guard(() => StartLocalDragAsync(payload)) : Task.CompletedTask;

    // ---- streaming side ----------------------------------------------------------------------------------

    private void OnViewerDrag(PeerSession from, byte type, ViewerDragMessage message)
    {
        switch (type)
        {
            case ClipboardCodec.ViewerCaughtMessage:
                if (_viewerCatches.TryGetValue(message.RequestId, out var reply))
                {
                    reply.TrySetResult(message.Payload);
                }

                break;

            case ClipboardCodec.ViewerDropMessage when message.Payload is { } payload:
                _ = Guard(() => DropFromViewerAsync(from, message, payload));
                break;

            case ClipboardCodec.ViewerCatchMessage:
                _ = Guard(() => CatchForViewerAsync(from, message));
                break;
        }
    }

    private async Task DropFromViewerAsync(PeerSession from, ViewerDragMessage message, DragPayload payload)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Files dropped in the viewer of {Peer} ignored: file drag and drop is off here", from.PeerName);
            return;
        }

        var source = _clipboard.SourceFor(payload.SourceId);
        if (source is null)
        {
            _logger.LogInformation("Cannot drop files from {Source}: not connected to it", payload.Manifest.SourceName);
            return;
        }

        if (!await _viewerDropGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!_screen.TryMapToScreen(from.PeerId, message.StreamId, message.X, message.Y, out var point, bringForward: true))
            {
                _logger.LogInformation("Files from the viewer of {Peer} ignored: the stream is gone or control is not allowed", from.PeerName);
                return;
            }

            var drag = _clipboard.Drag!;
            _input.MoveCursorTo(point);
            await drag.ShowOriginAsync(point).ConfigureAwait(false);
            _input.InjectLocalButton(MouseButton.Left, down: true, track: false);
            var running = drag.StartDragAsync(payload, source, _clipboard.AllowsDirectPaste(payload.Manifest));

            try
            {
                // The drag loop starts on the origin surface; take the surface away and wiggle the cursor so the
                // window under the point becomes the drop target, then let go of the button there.
                await Task.Delay(120).ConfigureAwait(false);
                await drag.HideSurfaceAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                _input.MoveCursorTo(new System.Drawing.Point(point.X + 4, point.Y + 4));
                await Task.Delay(60).ConfigureAwait(false);
                _input.MoveCursorTo(point);
                await Task.Delay(250).ConfigureAwait(false);
            }
            finally
            {
                _input.InjectLocalButton(MouseButton.Left, down: false, track: false);
            }

            var effect = await running.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            _logger.LogInformation("Files from the viewer of {Peer} dropped here with effect {Effect}", from.PeerName, effect);
        }
        finally
        {
            _viewerDropGate.Release();
        }
    }

    private async Task CatchForViewerAsync(PeerSession from, ViewerDragMessage message)
    {
        DragPayload? payload = null;
        try
        {
            if (Enabled && _screen.TryMapToScreen(from.PeerId, message.StreamId, message.X, message.Y, out var point))
            {
                payload = ToPayload(await _clipboard.Drag!.CatchAsync(point, ViewerCatchTimeout, _input.NudgeCursor).ConfigureAwait(false));
                _logger.LogInformation("Drag out of the viewer of {Peer}: {Result}", from.PeerName, payload is null ? "nothing caught" : $"{payload.Manifest.TopLevel.Count()} item(s)");
            }
        }
        finally
        {
            await _clipboard.SendViewerDragAsync(from, ClipboardCodec.ViewerCaughtMessage, message with { Payload = payload }).ConfigureAwait(false);
        }
    }
}
