using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace WinMirroring.App.Services;

/// <summary>
/// Keeps this app's own overlays (the floating button, the control panel, the progress window) out of screen capture:
/// a device viewing this screen does not see them, so it never shows a second menu button next to its own.
/// </summary>
public static class CaptureExclusion
{
    private const uint WdaExcludeFromCapture = 0x11;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
        {
            Exclude(handle);
            return;
        }

        window.SourceInitialized += (_, _) => Exclude(new WindowInteropHelper(window).Handle);
    }

    private static void Exclude(nint hwnd)
    {
        if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            Log.Debug("Window {Window} stays visible to screen capture (error {Error})", hwnd, Marshal.GetLastWin32Error());
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
