using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.Native;

internal static class DragNative
{
    public const int DROPEFFECT_NONE = 0;
    public const int DROPEFFECT_COPY = 1;
    public const int MK_LBUTTON = 0x0001;
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    public const int VK_LBUTTON = 0x01;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int SW_HIDE = 0;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly nint HWND_TOPMOST = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropTarget
    {
        [PreserveSig]
        int DragEnter([In, MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);

        [PreserveSig]
        int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop([In, MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropSource
    {
        [PreserveSig]
        int QueryContinueDrag(int fEscapePressed, int grfKeyState);

        [PreserveSig]
        int GiveFeedback(int dwEffect);
    }

    [DllImport("ole32.dll")]
    public static extern int DoDragDrop(ComTypes.IDataObject pDataObj, IOleDropSource pDropSource, int dwOKEffects, out int pdwEffect);

    [DllImport("ole32.dll")]
    public static extern int RegisterDragDrop(nint hwnd, IOleDropTarget pDropTarget);

    [DllImport("ole32.dll")]
    public static extern int RevokeDragDrop(nint hwnd);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINTL lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
