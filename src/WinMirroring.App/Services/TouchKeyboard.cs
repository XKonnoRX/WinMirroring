using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WinMirroring.App.Services;

/// <summary>Shows or hides the Windows touch keyboard (TabTip), falling back to the classic on-screen keyboard.</summary>
internal static class TouchKeyboard
{
    private static readonly Guid TipInvocationClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");

    [ComImport]
    [Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(nint window);
    }

    public static void Toggle()
    {
        try
        {
            EnsureTabTipRunning();
            var type = Type.GetTypeFromCLSID(TipInvocationClsid, throwOnError: true)!;
            var tip = (ITipInvocation)Activator.CreateInstance(type)!;
            try
            {
                tip.Toggle(GetDesktopWindow());
            }
            finally
            {
                Marshal.ReleaseComObject(tip);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static void EnsureTabTipRunning()
    {
        if (Process.GetProcessesByName("TabTip").Length > 0)
        {
            return;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Thread.Sleep(300);
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
