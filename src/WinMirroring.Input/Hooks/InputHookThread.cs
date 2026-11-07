using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Input.Native;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Hooks;

/// <summary>
/// A dedicated thread that owns the WH_MOUSE_LL / WH_KEYBOARD_LL hooks and a message-only window registered for
/// Raw Input. Low-level hooks are delivered on the installing thread's message loop, so the loop lives here.
/// The filters run on this thread and must return quickly: Windows drops hooks that stall.
/// </summary>
internal sealed class InputHookThread : IDisposable
{
    // Unique per instance: a window class carries its window procedure (see ClipboardThread).
    private readonly string WindowClassName = $"WinMirroring.InputSink.{Guid.NewGuid():N}";
    private readonly string ShieldClassName = $"WinMirroring.CursorShield.{Guid.NewGuid():N}";
    private const uint WmShieldShow = WM_APP + 1;
    private const uint WmShieldHide = WM_APP + 2;
    private const int ShieldSize = 64;

    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly WndProc _windowProc;
    private readonly WndProc _shieldProc;

    private nint _shield;
    private bool _shieldClassRegistered;

    // Where and when the shield last warped the cursor; the hook lets that synthetic move through untouched.
    private int _warpX;
    private int _warpY;
    private long _warpTicks;

    private uint _threadId;
    private nint _instance;
    private nint _window;
    private nint _mouseHook;
    private nint _keyboardHook;
    private bool _classRegistered;
    private Exception? _startupError;
    private int _disposed;

    public InputHookThread(ILogger logger)
    {
        _logger = logger;
        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;
        _windowProc = WindowProc;
        _shieldProc = ShieldProc;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WinMirroring.InputHooks",
        };
    }

    /// <summary>Return true to swallow the event (it never reaches the local desktop).</summary>
    public Func<MouseHookEvent, bool>? MouseFilter { get; set; }

    /// <summary>Return true to swallow the event.</summary>
    public Func<KeyboardHookEvent, bool>? KeyboardFilter { get; set; }

    /// <summary>Relative mouse motion straight from the device, unaffected by cursor clamping or acceleration.</summary>
    public Action<int, int>? RawMouseMoved { get; set; }

    /// <summary>
    /// Parks the cursor at <paramref name="at"/> under a nearly transparent topmost window whose cursor is empty,
    /// so the local pointer disappears while input goes to another device.
    /// </summary>
    public void ShowCursorShield(System.Drawing.Point at)
    {
        if (_shield != 0)
        {
            PostMessageW(_shield, WmShieldShow, at.X, at.Y);
        }
    }

    public void HideCursorShield()
    {
        if (_shield != 0)
        {
            PostMessageW(_shield, WmShieldHide, 0, 0);
        }
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            throw new InvalidOperationException("Input hooks could not be installed.", _startupError);
        }
    }

    private void Run()
    {
        try
        {
            // Physical pixels everywhere; a no-op when the manifest already declares awareness.
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            _threadId = GetCurrentThreadId();
            _instance = GetModuleHandleW(null);

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                hInstance = _instance,
                lpszClassName = WindowClassName,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new Win32Exception(error, "RegisterClassEx failed");
                }
            }
            else
            {
                _classRegistered = true;
            }

            _window = CreateWindowExW(0, WindowClassName, "WinMirroring input sink", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, _instance, 0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
            }

            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = _window,
                },
            };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
            }

            CreateShield();

            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, _instance, 0);
            if (_mouseHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) failed");
            }

            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, _instance, 0);
            if (_keyboardHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            Cleanup();
            _ready.Set();
            return;
        }

        _ready.Set();
        _logger.LogInformation("Input hooks installed");

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        Cleanup();
        _logger.LogInformation("Input hooks removed");
    }

    private void CreateShield()
    {
        var shieldClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_shieldProc),
            hInstance = _instance,
            hCursor = 0,
            lpszClassName = ShieldClassName,
        };

        if (RegisterClassExW(ref shieldClass) != 0)
        {
            _shieldClassRegistered = true;
        }
        else if (Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            _logger.LogWarning("Cursor shield class not registered; the local cursor stays visible while controlling");
            return;
        }

        _shield = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ShieldClassName, string.Empty, WS_POPUP, 0, 0, ShieldSize, ShieldSize, 0, 0, _instance, 0);

        if (_shield == 0)
        {
            _logger.LogWarning("Cursor shield window not created; the local cursor stays visible while controlling");
            return;
        }

        // Alpha 1/255: invisible, yet still hit-testable so WM_SETCURSOR reaches it.
        SetLayeredWindowAttributes(_shield, 0, 1, LWA_ALPHA);
    }

    private nint ShieldProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(0);
                return 1;

            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            case WmShieldShow:
                var x = (int)wParam;
                var y = (int)lParam;
                SetWindowPos(hWnd, HWND_TOPMOST, x - ShieldSize / 2, y - ShieldSize / 2, ShieldSize, ShieldSize, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                _warpX = x;
                _warpY = y;
                _warpTicks = Environment.TickCount64;
                SetCursorPos(x, y);
                return 0;

            case WmShieldHide:
                ShowWindow(hWnd, SW_HIDE);
                return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Cleanup()
    {
        if (_shield != 0)
        {
            DestroyWindow(_shield);
            _shield = 0;
        }

        if (_shieldClassRegistered)
        {
            UnregisterClassW(ShieldClassName, _instance);
            _shieldClassRegistered = false;
        }

        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_window != 0)
        {
            var remove = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = 0,
                },
            };
            RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            DestroyWindow(_window);
            _window = 0;
        }

        if (_classRegistered)
        {
            UnregisterClassW(WindowClassName, _instance);
            _classRegistered = false;
        }
    }

    private unsafe nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && MouseFilter is { } filter)
        {
            var data = (MSLLHOOKSTRUCT*)(void*)lParam;
            if ((uint)wParam == WM_MOUSEMOVE && data->pt.X == _warpX && data->pt.Y == _warpY
                && Environment.TickCount64 - _warpTicks < 100)
            {
                // The shield's own SetCursorPos, if the system reports it here at all: never swallow or forward it.
                return CallNextHookEx(0, nCode, wParam, lParam);
            }

            var evt = new MouseHookEvent(
                (uint)wParam,
                data->pt.X,
                data->pt.Y,
                data->mouseData,
                (data->flags & LLMHF_INJECTED) != 0,
                data->dwExtraInfo == InjectionMarker);

            if (Invoke(filter, evt))
            {
                return 1;
            }
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private unsafe nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && KeyboardFilter is { } filter)
        {
            var data = (KBDLLHOOKSTRUCT*)(void*)lParam;
            var message = (uint)wParam;
            var evt = new KeyboardHookEvent(
                data->vkCode,
                data->scanCode,
                message is WM_KEYUP or WM_SYSKEYUP || (data->flags & LLKHF_UP) != 0,
                (data->flags & LLKHF_EXTENDED) != 0,
                (data->flags & LLKHF_INJECTED) != 0,
                data->dwExtraInfo == InjectionMarker);

            if (Invoke(filter, evt))
            {
                return 1;
            }
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private bool Invoke<T>(Func<T, bool> filter, T evt)
    {
        try
        {
            return filter(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hook filter failed; passing the event through");
            return false;
        }
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                HandleRawInput(lParam);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Raw input handling failed");
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private unsafe void HandleRawInput(nint handle)
    {
        var headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, 0, ref size, headerSize);
        if (size == 0 || size > 1024)
        {
            return;
        }

        var buffer = stackalloc byte[(int)size];
        if (GetRawInputData(handle, RID_INPUT, (nint)buffer, ref size, headerSize) != size)
        {
            return;
        }

        var header = (RAWINPUTHEADER*)buffer;
        if (header->dwType != RIM_TYPEMOUSE)
        {
            return;
        }

        // hDevice can be null for precision touchpads as well as for injected input, so it is not a filter here.
        // Our own replay is absolute (skipped below) and the service ignores raw motion while it is being controlled.

        var mouse = (RAWMOUSE*)(buffer + sizeof(RAWINPUTHEADER));
        if ((mouse->usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            // Absolute devices (pen, touch, remote desktop) do not report deltas; edge crossing uses mice only.
            return;
        }

        if (mouse->lLastX != 0 || mouse->lLastY != 0)
        {
            RawMouseMoved?.Invoke(mouse->lLastX, mouse->lLastY);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Input hook thread did not stop in time");
            }
        }

        _ready.Dispose();
    }
}
