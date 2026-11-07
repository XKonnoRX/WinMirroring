using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Files;
using static WinMirroring.Clipboard.Native.DragNative;

namespace WinMirroring.Clipboard.DragDrop;

/// <summary>Our side of an OLE drag: ends with a drop when the left button is released, or is cancelled on request.</summary>
[ComVisible(true)]
internal sealed class DropSource : IOleDropSource
{
    private volatile bool _cancel;

    public void Cancel() => _cancel = true;

    public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
    {
        if (_cancel || fEscapePressed != 0)
        {
            return DRAGDROP_S_CANCEL;
        }

        return (grfKeyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
    }

    public int GiveFeedback(int dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
}

/// <summary>
/// Drag and drop between devices, on the clipboard STA thread: catches what a local drag carries and runs drags
/// of virtual files that another device serves. The mouse-button choreography lives with the caller, which knows
/// who controls the cursor.
/// </summary>
public sealed class DragDropEngine
{
    private static readonly TimeSpan ButtonWait = TimeSpan.FromMilliseconds(250);

    private readonly ClipboardThread _thread;
    private readonly DragSurface _surface;
    private readonly ILogger _logger;
    private DropSource? _active;

    internal DragDropEngine(ClipboardThread thread, ILogger logger)
    {
        _thread = thread;
        _logger = logger;
        _surface = new DragSurface(logger);
    }

    /// <summary>True while a drag we started is in progress on this device.</summary>
    public bool IsDragging => Volatile.Read(ref _active) is not null;

    internal Task InitializeAsync() => _thread.InvokeAsync(_surface.Create);

    internal Task ShutdownAsync() => _thread.InvokeAsync(_surface.Destroy);

    /// <summary>
    /// Shows the catch surface at <paramref name="at"/> and waits until a drag hovers over it (or the timeout).
    /// <paramref name="afterShown"/> runs once the surface is up, typically a cursor nudge so the drag notices it.
    /// </summary>
    public async Task<DragCatch?> CatchAsync(Point at, TimeSpan timeout, Action? afterShown)
    {
        var completion = new TaskCompletionSource<DragCatch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _thread.InvokeAsync(() => _surface.ShowCatch(at, timeout, completion)).ConfigureAwait(false);
        afterShown?.Invoke();
        return await completion.Task.WaitAsync(timeout + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the origin surface under the cursor (or at <paramref name="at"/>) so the button press that starts our
    /// drag lands on it.
    /// </summary>
    public Task ShowOriginAsync(Point? at = null) => _thread.InvokeAsync(() =>
    {
        if (at is { } point)
        {
            _surface.ShowOrigin(point);
        }
        else if (GetCursorPos(out var cursor))
        {
            _surface.ShowOrigin(new Point(cursor.X, cursor.Y));
        }
    });

    public Task HideSurfaceAsync() => _thread.InvokeAsync(_surface.Hide);

    /// <summary>
    /// Runs DoDragDrop with virtual files from another device. The left button must be (or be about to be) down;
    /// the drag drops when it is released. Returns the drop effect (0 when cancelled or not dropped).
    /// </summary>
    public Task<int> StartDragAsync(DragPayload payload, IRemoteFileSource source, bool offerDropFiles)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _thread.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _active) is not null)
            {
                completion.TrySetResult(DROPEFFECT_NONE);
                return;
            }

            var dropSource = new DropSource();
            Volatile.Write(ref _active, dropSource);
            try
            {
                if (!WaitForLeftButton())
                {
                    _logger.LogInformation("Left button is not down; drag of {Count} item(s) not started", payload.Manifest.Entries.Count);
                    completion.TrySetResult(DROPEFFECT_NONE);
                    return;
                }

                var dataObject = new VirtualFileDataObject(payload.Manifest, source, offerDropFiles, payload.SourceId, _logger);
                var hr = DoDragDrop(dataObject, dropSource, DROPEFFECT_COPY, out var effect);
                _logger.LogInformation("Drag of files from {Source} ended: 0x{Hr:X8}, effect {Effect}", payload.Manifest.SourceName, hr, effect);
                completion.TrySetResult(hr == DRAGDROP_S_DROP ? effect : DROPEFFECT_NONE);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref _active, null);
            }
        });

        return completion.Task;
    }

    /// <summary>Makes a drag we started end without dropping (the cursor left this device).</summary>
    public void CancelActiveDrag() => Volatile.Read(ref _active)?.Cancel();

    private static bool WaitForLeftButton()
    {
        var deadline = Environment.TickCount64 + (long)ButtonWait.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }
}
