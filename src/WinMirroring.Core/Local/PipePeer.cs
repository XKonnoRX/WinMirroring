using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinMirroring.Core.Local;

/// <summary>
/// Who is on the other end of a named pipe, by the exe they are running.
///
/// This is what makes a pipe between this app and a helper of its own safe to have at all. A pipe's name is
/// something any process of this user can work out or guess, so neither end may take the other on trust: the app
/// refuses a client that is not the helper it shipped with, and the helper refuses a server that is not this app.
/// The helpers hold powers the app cannot have on its own — typing into administrator windows, running a command
/// with administrator rights — and each hands that to one program only. Being unable to tell who is there is
/// itself an answer, and the answer is no.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipePeer
{
    /// <summary>The process <paramref name="pid"/> is running <paramref name="expectedImage"/>.</summary>
    public static bool Is(uint pid, string expectedImage)
    {
        if (pid == 0)
        {
            return false;
        }

        var image = ImagePath(pid);
        return image is not null
            && string.Equals(Path.GetFullPath(image), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint pid) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) && pid != 0;

    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint pid) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) && pid != 0;

    /// <summary>The exe a process is running, or null when it cannot be read.</summary>
    public static string? ImagePath(uint pid)
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
