using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinMirroring.Input.Injection;

/// <summary>
/// The channel between the app and its elevated input helper, and the checks that bind the two ends to each other.
///
/// The app is the one that listens: it runs at normal integrity, and Windows lets an elevated process reach down to
/// a pipe a normal one created, but not the other way round. The name is the same every time, because the helper is
/// started through a scheduled task and a task takes no arguments — which is the whole point of the task, since it
/// is what lets the helper start without a prompt every single time. A name that can be guessed is only safe
/// because neither end takes the other on trust: the helper refuses any server that is not this app's exe, and the
/// app refuses any client that is not the helper exe sitting next to it. A process of this user that squats on the
/// name gets nothing — the helper will not talk to it — and one that connects to the app gets nothing either.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InjectionPipe
{
    /// <summary>
    /// One name per user: two people signed in at once each get their own app, helper and channel, and neither can
    /// reach the other's (the pipe's own permissions already limit it to the user that created it).
    /// </summary>
    public static string NameForCurrentUser()
    {
        string who;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            who = identity.User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            who = Environment.UserName;
        }

        return $"WinMirroring.Inject.{who}";
    }

    /// <summary>The app's exe, which the helper expects on the other end of the pipe.</summary>
    public static string AppPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.exe");

    /// <summary>The helper's exe, which the app expects on the other end of the pipe.</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.InputHost.exe");

    /// <summary>The process at the other end of <paramref name="pipe"/> is <paramref name="expectedImage"/>.</summary>
    public static bool IsPeer(uint pid, string expectedImage)
    {
        if (pid == 0)
        {
            return false;
        }

        var image = ProcessImagePath(pid);
        return image is not null
            && string.Equals(Path.GetFullPath(image), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint pid) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) && pid != 0;

    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint pid) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) && pid != 0;

    private static string? ProcessImagePath(uint pid)
    {
        const uint queryLimited = 0x1000;
        var process = OpenProcess(queryLimited, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var capacity = 1024;
            var buffer = new char[capacity];
            return QueryFullProcessImageNameW(process, 0, buffer, ref capacity) ? new string(buffer, 0, capacity) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
