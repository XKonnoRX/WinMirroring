using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core.Local;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Elevated;

/// <summary>
/// The whole body of an administrator console, inside the elevated helper. It connects back to the app over the
/// pipe it was started with, proves it is the helper and checks that the app is the app, starts a shell, and
/// carries words in both directions until either side is finished.
///
/// The shell it starts is the ordinary <see cref="RemoteShell"/> — the same marker, the same base64, the same four
/// places encoding has to be set — because it is running here that makes it elevated and nothing else about it
/// differs. The messages on the pipe are the console channel's own, for the same reason: the app's job is to pass
/// them on to whoever asked, and a second shape of message in the middle would be a second thing to keep in step.
///
/// It does nothing else. No network, no files of its own, no UI: it reads commands from one program it has checked,
/// runs them in a shell, and writes back what they print.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedShellHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (ArgValue(args, "--shell-pipe") is not { Length: > 0 } pipeName
            || ArgValue(args, "--shell-token") is not { Length: > 0 } tokenHex)
        {
            return 2;
        }

        byte[] token;
        try
        {
            token = Convert.FromHexString(tokenHex);
        }
        catch (FormatException)
        {
            return 2;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 3;
        }

        // The app is the only program this may serve. It holds administrator rights and hands them to one caller.
        if (!PipePeer.TryGetServerProcessId(pipe, out var pid) || !PipePeer.Is(pid, ElevatedShellPipe.AppPath))
        {
            return 4;
        }

        var reader = new FrameCodec.Reader();
        if (await reader.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is not { } hello
            || !hello.Payload.AsSpan().SequenceEqual(token))
        {
            return 5;
        }

        await FrameCodec.WriteAsync(pipe, ChannelId.Shell, token, CancellationToken.None).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);

        if (RemoteShell.TryStart(NullLogger.Instance) is not { } shell)
        {
            await FrameCodec.WriteAsync(pipe, ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Close, 0), CancellationToken.None).ConfigureAwait(false);
            return 6;
        }

        // Writes go through a loop of their own: what fills it are the shell's own events, raised on the thread
        // that reads the shell's output, and anything that waits there stops the shell being read at all.
        var outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        void Send(byte[] message) => outbound.Writer.TryWrite(message);

        shell.Output += (kind, text) => Send(ShellCodec.EncodeOutput(
            0,
            kind == ShellStreamKind.Error ? ShellStream.Error : ShellStream.Output,
            text));
        shell.Done += (code, directory) => Send(ShellCodec.EncodeDone(0, code, directory));
        shell.Ended += () =>
        {
            Send(ShellCodec.Encode(ShellMessageType.Close, 0));
            outbound.Writer.TryComplete();
        };

        var writing = Task.Run(() => WriteLoopAsync(pipe, outbound.Reader));

        try
        {
            await shell.Ready.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // It has started but has not said where it is; opening it anyway beats not opening it at all.
        }

        Send(ShellCodec.Encode(ShellMessageType.Opened, 0, shell.Directory));

        var arriving = new CommandAssembly();
        try
        {
            while (true)
            {
                if (await reader.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is not { } frame
                    || !ShellCodec.TryReadHeader(frame.Payload, out var type, out _))
                {
                    break;
                }

                if (type is ShellMessageType.Run or ShellMessageType.RunPart)
                {
                    if (!ShellCodec.TryDecodeText(frame.Payload, out var piece))
                    {
                        break;
                    }

                    switch (arriving.Add(type, piece, out var command))
                    {
                        case CommandPiece.Whole:
                            shell.Run(command);
                            break;

                        case CommandPiece.TooLong:
                            Send(ShellCodec.EncodeOutput(0, ShellStream.Error, "Команда слишком длинная."));
                            Send(ShellCodec.EncodeDone(0, 1, shell.Directory));
                            break;
                    }

                    continue;
                }

                if (type == ShellMessageType.Cancel)
                {
                    shell.Cancel();
                    continue;
                }

                if (type == ShellMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }

        shell.Dispose();
        outbound.Writer.TryComplete();
        try
        {
            await writing.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static async Task WriteLoopAsync(Stream pipe, ChannelReader<byte[]> outbound)
    {
        try
        {
            await foreach (var message in outbound.ReadAllAsync().ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(pipe, ChannelId.Shell, message, CancellationToken.None).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
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
}
