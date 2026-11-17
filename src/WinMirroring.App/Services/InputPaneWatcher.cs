using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinMirroring.App.Services;

/// <summary>
/// Where the Windows touch keyboard is on the screen, and when it comes and goes — through the shell's own
/// <c>IFrameworkInputPane</c>, which is what a desktop window has instead of UWP's InputPane. Rectangles are in screen
/// pixels. On a system without it (Server Core, say) the watcher simply never reports anything.
/// </summary>
internal sealed class InputPaneWatcher : IDisposable
{
    private static readonly Guid FrameworkInputPaneClsid = new("D5120AA3-46BA-44C5-822D-CA8092C1FC72");

    private readonly IFrameworkInputPane? _pane;
    private readonly Handler? _handler;
    private int _cookie;

    public InputPaneWatcher(nint window)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(FrameworkInputPaneClsid, throwOnError: true)!;
            _pane = (IFrameworkInputPane)Activator.CreateInstance(type)!;
            _handler = new Handler(this);
            if (_pane.AdviseWithHWND(window, _handler, out _cookie) < 0)
            {
                _cookie = 0;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or FileNotFoundException)
        {
            _pane = null;
        }
    }

    /// <summary>The keyboard came up (or moved) and covers this rectangle of the screen.</summary>
    public event Action<InputPaneRect>? Showing;

    public event Action? Hiding;

    /// <summary>Where the keyboard is right now; null when it is not on the screen.</summary>
    public InputPaneRect? Location
    {
        get
        {
            try
            {
                if (_pane is not null && _pane.Location(out var rect) >= 0 && rect.Right > rect.Left && rect.Bottom > rect.Top)
                {
                    return rect;
                }
            }
            catch (COMException)
            {
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_pane is not null && _cookie != 0)
        {
            try
            {
                _pane.Unadvise(_cookie);
            }
            catch (COMException)
            {
            }

            _cookie = 0;
        }
    }

    [ComVisible(true)]
    private sealed class Handler(InputPaneWatcher owner) : IFrameworkInputPaneHandler
    {
        public int Showing(ref InputPaneRect location, bool ensureFocusedElementInView)
        {
            owner.Showing?.Invoke(location);
            return 0;
        }

        public int Hiding(bool ensureFocusedElementInView)
        {
            owner.Hiding?.Invoke();
            return 0;
        }
    }
}

[ComImport]
[Guid("5752238B-24F0-495A-82F1-2FD593056796")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFrameworkInputPane
{
    [PreserveSig]
    int Advise([MarshalAs(UnmanagedType.IUnknown)] object window, IFrameworkInputPaneHandler handler, out int cookie);

    [PreserveSig]
    int AdviseWithHWND(nint window, IFrameworkInputPaneHandler handler, out int cookie);

    [PreserveSig]
    int Unadvise(int cookie);

    [PreserveSig]
    int Location(out InputPaneRect location);
}

[ComImport]
[Guid("226C537B-1E76-4D9E-A760-33DB29922F18")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFrameworkInputPaneHandler
{
    [PreserveSig]
    int Showing(ref InputPaneRect location, [MarshalAs(UnmanagedType.Bool)] bool ensureFocusedElementInView);

    [PreserveSig]
    int Hiding([MarshalAs(UnmanagedType.Bool)] bool ensureFocusedElementInView);
}

/// <summary>A rectangle on the screen, in pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct InputPaneRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
