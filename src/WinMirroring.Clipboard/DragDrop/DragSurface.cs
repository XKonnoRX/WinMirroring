using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Files;
using static WinMirroring.Clipboard.Native.ClipboardNative;
using static WinMirroring.Clipboard.Native.DragNative;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.DragDrop;

/// <summary>
/// A small, practically invisible topmost window that lives on the clipboard STA thread and plays two roles:
/// <list type="bullet">
/// <item><b>Catch</b>: placed under the cursor while a drag is in progress, it is the drop target the drag hovers
/// over, which is how we learn what is being dragged. It never accepts the drop.</item>
/// <item><b>Origin</b>: placed under the cursor right before we press the mouse button to start a drag of our own,
/// so that press lands on us instead of clicking whatever window is there.</item>
/// </list>
/// </summary>
[ComVisible(true)]
internal sealed class DragSurface : IOleDropTarget
{
    private readonly string ClassName = $"WinMirroring.DragSurface.{Guid.NewGuid():N}";
    private const int SurfaceSize = 96;
    private const nuint HideTimerId = 11;
    private const nuint CatchTimeoutTimerId = 12;
    private const uint OriginLifetimeMs = 500;
    private const uint HideAfterLeaveMs = 1500;

    private enum Mode
    {
        Hidden,
        Catch,
        Origin,
    }

    private readonly ILogger _logger;
    private readonly WndProc _wndProc;
    private nint _hwnd;
    private nint _instance;
    private Mode _mode;
    private TaskCompletionSource<DragCatch?>? _pendingCatch;
    private bool _caught;

    public DragSurface(ILogger logger)
    {
        _logger = logger;
        _wndProc = WindowProc;
    }

    /// <summary>Creates the window; must run on the (OLE-initialised) clipboard thread.</summary>
    public void Create()
    {
        _instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref windowClass);

        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, string.Empty, WS_POPUP, 0, 0, SurfaceSize, SurfaceSize, 0, 0, _instance, 0);
        if (_hwnd == 0)
        {
            _logger.LogWarning("Drag surface window not created; drag and drop between devices is unavailable");
            return;
        }

        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);
        var hr = RegisterDragDrop(_hwnd, this);
        if (hr < 0)
        {
            _logger.LogWarning("RegisterDragDrop failed (0x{Hr:X8})", hr);
        }
    }

    public bool IsAvailable => _hwnd != 0;

    public void ShowCatch(Point at, TimeSpan timeout, TaskCompletionSource<DragCatch?> completion)
    {
        _pendingCatch?.TrySetResult(null);
        if (_hwnd == 0)
        {
            completion.TrySetResult(null);
            return;
        }

        _pendingCatch = completion;
        _caught = false;
        _mode = Mode.Catch;
        Place(at);
        KillTimer(_hwnd, HideTimerId);
        SetTimer(_hwnd, CatchTimeoutTimerId, (uint)Math.Clamp(timeout.TotalMilliseconds, 50, 10_000), 0);
    }

    public void ShowOrigin(Point at)
    {
        if (_hwnd == 0)
        {
            return;
        }

        _pendingCatch?.TrySetResult(null);
        _pendingCatch = null;
        _mode = Mode.Origin;
        Place(at);
        KillTimer(_hwnd, CatchTimeoutTimerId);
        SetTimer(_hwnd, HideTimerId, OriginLifetimeMs, 0);
    }

    public void Hide()
    {
        if (_hwnd == 0)
        {
            return;
        }

        KillTimer(_hwnd, HideTimerId);
        KillTimer(_hwnd, CatchTimeoutTimerId);
        _pendingCatch?.TrySetResult(null);
        _pendingCatch = null;
        _mode = Mode.Hidden;
        ShowWindow(_hwnd, SW_HIDE);
    }

    public void Destroy()
    {
        if (_hwnd == 0)
        {
            return;
        }

        Hide();
        RevokeDragDrop(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = 0;
        UnregisterClassW(ClassName, _instance);
    }

    private void Place(Point at) =>
        SetWindowPos(_hwnd, HWND_TOPMOST, at.X - SurfaceSize / 2, at.Y - SurfaceSize / 2, SurfaceSize, SurfaceSize, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            return MA_NOACTIVATE;
        }

        if (msg == WM_TIMER && wParam == (nint)HideTimerId)
        {
            Hide();
            return 0;
        }

        if (msg == WM_TIMER && wParam == (nint)CatchTimeoutTimerId)
        {
            KillTimer(hWnd, CatchTimeoutTimerId);
            if (!_caught)
            {
                Hide();
            }

            return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---- IDropTarget -------------------------------------------------------------------------------------

    public int DragEnter(ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        KillTimer(_hwnd, HideTimerId);
        if (_mode == Mode.Catch && _pendingCatch is { } pending && !_caught)
        {
            var caught = Inspect(pDataObj);
            if (caught is not null)
            {
                _caught = true;
                KillTimer(_hwnd, CatchTimeoutTimerId);
                pending.TrySetResult(caught);
                _pendingCatch = null;
            }
        }

        pdwEffect = _mode == Mode.Catch && _caught ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }

    public int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        pdwEffect = _mode == Mode.Catch && _caught ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }

    public int DragLeave()
    {
        if (_hwnd != 0 && _mode != Mode.Hidden)
        {
            SetTimer(_hwnd, HideTimerId, HideAfterLeaveMs, 0);
        }

        return S_OK;
    }

    public int Drop(ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        // The drop happened on the other device (or the drag was abandoned): accept nothing here.
        pdwEffect = DROPEFFECT_NONE;
        Hide();
        return S_OK;
    }

    private DragCatch? Inspect(ComTypes.IDataObject dataObject)
    {
        if (dataObject is VirtualFileDataObject ours)
        {
            // A drag we started ourselves on behalf of another device is moving on: pass it along unchanged.
            return new DragCatch(null, new DragPayload(ours.SourceId, ours.Manifest));
        }

        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)CF_HDROP),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };

        try
        {
            if (dataObject.QueryGetData(ref format) != S_OK)
            {
                return null;
            }

            dataObject.GetData(ref format, out var medium);
            try
            {
                var paths = ClipboardThread.ReadDropFiles(medium.unionmember);
                return paths.Count > 0 ? new DragCatch(paths, null) : null;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException ex)
        {
            _logger.LogDebug("Dragged data has no file list: {Message}", ex.Message);
            return null;
        }
    }
}
