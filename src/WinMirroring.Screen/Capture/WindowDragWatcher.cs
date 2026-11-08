using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static WinMirroring.Screen.Native.ScreenNative;

namespace WinMirroring.Screen.Capture;

/// <summary>
/// Knows which window is being moved by its title bar right now (WinEvent move/size start and end), so that
/// carrying it across a screen edge can send it to that device. A window that was sent is put back where its
/// move started once the move ends, instead of staying wherever the parked cursor dragged it.
/// </summary>
internal sealed class WindowDragWatcher : IDisposable
{
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const uint WmQuit = 0x0012;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly ILogger _logger;
    private readonly WinEventProc _proc;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private uint _threadId;
    private nint _hook;
    private nint _moving;
    private Rectangle _startRect;
    private bool _sent;
    private bool _park;

    public WindowDragWatcher(ILogger logger)
    {
        _logger = logger;
        _proc = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinMirroring.WindowDrag" };
    }

    /// <summary>The window currently in a move/size loop, or 0.</summary>
    public nint Moving
    {
        get
        {
            lock (_gate)
            {
                return _moving;
            }
        }
    }

    /// <summary>A window marked to be parked (hidden) was released; the handler moves it onto the virtual monitor.</summary>
    public event Action<nint>? ReleasedForPark;

    public void Start() => _thread.Start();

    /// <summary>Remember to put <paramref name="hwnd"/> back when its move ends.</summary>
    public void MarkSent(nint hwnd)
    {
        lock (_gate)
        {
            if (_moving == hwnd)
            {
                _sent = true;
            }
        }
    }

    /// <summary>Park <paramref name="hwnd"/> on the virtual monitor (hide it) once its move ends, instead of putting it back.</summary>
    public void MarkParkOnRelease(nint hwnd)
    {
        lock (_gate)
        {
            if (_moving == hwnd)
            {
                _park = true;
                _sent = false;
            }
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, 0, _proc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        if (_hook == 0)
        {
            _logger.LogWarning("Window move tracking unavailable; dragging windows to other devices is off");
            return;
        }

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            DispatchMessageW(ref message);
        }

        UnhookWinEvent(_hook);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || hwnd == 0)
        {
            return;
        }

        if (eventType == EventSystemMoveSizeStart)
        {
            lock (_gate)
            {
                _moving = hwnd;
                _sent = false;
                _park = false;
                _startRect = GetWindowRect(hwnd, out var rect) ? rect.ToRectangle() : Rectangle.Empty;
            }

            return;
        }

        bool restore;
        bool park;
        Rectangle start;
        lock (_gate)
        {
            park = _park && _moving == hwnd;
            restore = _sent && _moving == hwnd && !_startRect.IsEmpty;
            start = _startRect;
            _moving = 0;
            _sent = false;
            _park = false;
        }

        if (park)
        {
            ReleasedForPark?.Invoke(hwnd);
        }
        else if (restore)
        {
            SetWindowPos(hwnd, 0, start.X, start.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
