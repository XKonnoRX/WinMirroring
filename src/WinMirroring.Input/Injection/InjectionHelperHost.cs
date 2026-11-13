using System.IO.Pipes;
using System.Runtime.Versioning;
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
        // Started through the scheduled task there are no arguments at all — a task takes none — so the channel
        // has the name both sides can work out for themselves, and whatever the app sends as the token is echoed
        // back. Nothing is lost by that: what binds the two ends is each one checking the other's exe, below.
        var pipeName = ArgValue(args, "--inject-pipe") ?? InjectionPipe.NameForCurrentUser();
        var tokenHex = ArgValue(args, "--inject-token");

        byte[]? expectedToken = null;
        if (tokenHex is not null)
        {
            try
            {
                expectedToken = Convert.FromHexString(tokenHex);
            }
            catch (FormatException)
            {
                return 2;
            }
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
        if (token is null || (expectedToken is not null && !token.AsSpan().SequenceEqual(expectedToken)))
        {
            return 5;
        }

        await InjectionCodec.WriteMessageAsync(pipe, InjectionCodec.Handshake(token), CancellationToken.None).ConfigureAwait(false);

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

    /// <summary>
    /// The server has to be the app this helper was shipped next to, and nothing else. This is strict: the name of
    /// the channel is one another process of this user could work out as easily as we do, so a server we cannot
    /// identify is a server we refuse. Being unable to tell is itself the answer — this helper holds the power to
    /// type into administrator windows, and it hands that to one program only.
    /// </summary>
    private static bool VerifyServerIsOurApp(NamedPipeClientStream pipe) =>
        InjectionPipe.TryGetServerProcessId(pipe, out var pid) && InjectionPipe.IsPeer(pid, InjectionPipe.AppPath);
}
