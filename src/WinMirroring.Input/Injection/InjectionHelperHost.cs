using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input.Injection;

/// <summary>
/// The whole body of the elevated helper process (WinMirroring.InputHost.exe). It connects back to the app's pipe,
/// confirms the one-time token, then replays injection messages through a <see cref="LocalInjectionSink"/> until the
/// app asks it to stop or goes away. It does nothing else: no network, no files, no UI — only input injection.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InjectionHelperHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var pipeName = ArgValue(args, "--inject-pipe");
        var tokenHex = ArgValue(args, "--inject-token");
        if (pipeName is null || tokenHex is null)
        {
            return 2;
        }

        byte[] expectedToken;
        try
        {
            expectedToken = Convert.FromHexString(tokenHex);
        }
        catch (FormatException)
        {
            return 2;
        }

        // Match the app's coordinate space so absolute moves land on the right pixel.
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 3;
        }

        if (!VerifyServerIsOurApp(pipe))
        {
            return 4;
        }

        var hello = await InjectionCodec.ReadMessageAsync(pipe, CancellationToken.None).ConfigureAwait(false);
        var token = hello is null ? null : InjectionCodec.ReadHandshake(hello);
        if (token is null || !token.AsSpan().SequenceEqual(expectedToken))
        {
            return 5;
        }

        await InjectionCodec.WriteMessageAsync(pipe, InjectionCodec.Handshake(expectedToken), CancellationToken.None).ConfigureAwait(false);

        var sink = new TrackingInjectionSink(new LocalInjectionSink());
        try
        {
            while (true)
            {
                var message = await InjectionCodec.ReadMessageAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                if (message is null || InjectionCodec.PeekOp(message) == InjectionCodec.Op.Bye)
                {
                    break;
                }

                InjectionCodec.Apply(message, sink);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            // The app went away: fall through and release anything still held.
        }
        finally
        {
            sink.ReleaseAll();
        }

        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Best-effort: the pipe server should be WinMirroring.exe in the same folder as this helper.</summary>
    private static bool VerifyServerIsOurApp(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
        {
            return true; // cannot check; the pipe ACL already limits this to our user
        }

        var image = ProcessImagePath(pid);
        if (image is null)
        {
            return true;
        }

        var expected = Path.Combine(AppContext.BaseDirectory, "WinMirroring.exe");
        return string.Equals(Path.GetFullPath(image), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

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
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
