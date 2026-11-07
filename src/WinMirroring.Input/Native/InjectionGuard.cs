using System.Runtime.InteropServices;
using System.Text;

namespace WinMirroring.Input.Native;

/// <summary>
/// Windows silently drops input that a process injects into a window of a higher integrity level (UIPI): with an
/// elevated window in front (Task Manager, an admin console) nothing we inject arrives, the cursor included.
/// </summary>
public static class InjectionGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int CursorSuppressed = 0x00000002;

    private static readonly int OwnLevel = GetIntegrityLevel(GetCurrentProcess()) ?? 0x2000;

    /// <summary>True when this process runs elevated (then UIPI does not stand in the way).</summary>
    public static bool IsElevated => OwnLevel >= 0x3000;

    /// <summary>True when the foreground window belongs to a process above our integrity level.</summary>
    public static bool IsForegroundAboveUs(out string title)
    {
        title = string.Empty;
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        title = GetTitle(window);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return !IsElevated; // cannot even look at it: protected or elevated
        }

        try
        {
            var level = GetIntegrityLevel(process);
            return level is null ? !IsElevated : level > OwnLevel;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// True when Windows hides the cursor because the last input was touch or pen; an injected relative move
    /// brings it back.
    /// </summary>
    public static bool IsCursorSuppressed()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && (info.flags & CursorSuppressed) != 0;
    }

    private static string GetTitle(nint window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowTextW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static int? GetIntegrityLevel(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var needed);
            if (needed <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label }: the SID pointer comes first.
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int infoClass, nint info, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);
}
