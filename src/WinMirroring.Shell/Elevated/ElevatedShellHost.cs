using System.Collections.Concurrent;
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
/// The whole body of the administrator consoles, inside the elevated helper. It connects back to the app over the
/// pipe it was started with, proves it is the helper and checks that the app is the app, and then starts a shell
/// whenever the app asks for one and carries words in both directions until either side is finished.
///
/// It serves consoles rather than a console, and that is what makes one prompt enough for a run of the app: the
/// program Windows asked about keeps running, and a second console is a second shell inside it rather than a second
/// program to ask about. Each shell is one <see cref="ShellMessageType.OpenElevated"/> and one id, and every
/// message afterwards carries that id, so two devices managing this one at the same time never find themselves in
/// each other's console. Id 0 is not a console but this process: a <see cref="ShellMessageType.Close"/> for it is
/// the app saying it is finished with administrator rights altogether.
///
/// The shells it starts are the ordinary <see cref="RemoteShell"/> — the same marker, the same base64, the same
/// four places encoding has to be set — because it is running here that makes them elevated and nothing else about
/// them differs. The messages on the pipe are the console channel's own, for the same reason: the app's job is to
/// pass them on to whoever asked, and a second shape of message in the middle would be a second thing to keep in
/// step.
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

        // Writes go through a loop of their own: what fills it are the shells' own events, raised on the threads
        // that read their output, and anything that waits there stops a shell being read at all.
        var outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        void Send(byte[] message) => outbound.Writer.TryWrite(message);
        var writing = Task.Run(() => WriteLoopAsync(pipe, outbound.Reader));

        var consoles = new ConcurrentDictionary<uint, Hosted>();
        try
        {
            while (true)
            {
                if (await reader.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is not { } frame
                    || !ShellCodec.TryReadHeader(frame.Payload, out var type, out var id))
                {
                    break;
                }

                // The app is finished with administrator rights for this run; everything goes with it.
                if (type == ShellMessageType.Close && id == ElevatedShellPipe.HelperItself)
                {
                    break;
                }

                if (type is ShellMessageType.Open or ShellMessageType.OpenElevated)
                {
                    Start(id, consoles, Send);
                    continue;
                }

                if (!consoles.TryGetValue(id, out var console))
                {
                    continue;
                }

                if (type is ShellMessageType.Run or ShellMessageType.RunPart)
                {
                    if (!ShellCodec.TryDecodeText(frame.Payload, out var piece))
                    {
                        break;
                    }

                    switch (console.Arriving.Add(type, piece, out var command))
                    {
                        case CommandPiece.Whole:
                            console.Shell.Run(command);
                            break;

                        case CommandPiece.TooLong:
                            // The words are the ones the app itself uses, kept in one place: a copy of them
                            // here was once saved in the wrong encoding and went out as mojibake.
                            Send(ShellCodec.EncodeOutput(id, ShellStream.Error, CommandAssembly.TooLongText));
                            Send(ShellCodec.EncodeDone(id, 1, console.Shell.Directory));
                            break;
                    }

                    continue;
                }

                if (type == ShellMessageType.Cancel)
                {
                    console.Shell.Cancel();
                    continue;
                }

                if (type == ShellMessageType.Close && consoles.TryRemove(id, out var closing))
                {
                    closing.Shell.Dispose();
                    Send(ShellCodec.Encode(ShellMessageType.Close, id));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }

        foreach (var console in consoles.Values)
        {
            console.Shell.Dispose();
        }

        consoles.Clear();
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

    /// <summary>
    /// One more console. Its shell is told to the app only once it has said where it is, and that waiting happens
    /// off the loop that reads the pipe: a shell being slow to start is no reason for the consoles already open to
    /// stop being fed.
    /// </summary>
    private static void Start(uint id, ConcurrentDictionary<uint, Hosted> consoles, Action<byte[]> send)
    {
        if (consoles.ContainsKey(id))
        {
            return;
        }

        if (RemoteShell.TryStart(NullLogger.Instance) is not { } shell)
        {
            send(ShellCodec.Encode(ShellMessageType.Close, id));
            return;
        }

        shell.Output += (kind, text) =>
        {
            // As many messages as the line takes, queued together from the one thread that read it.
            foreach (var piece in ShellCodec.EncodeOutputLine(
                id,
                kind == ShellStreamKind.Error ? ShellStream.Error : ShellStream.Output,
                text))
            {
                send(piece);
            }
        };
        shell.Done += (code, directory) => send(ShellCodec.EncodeDone(id, code, directory));
        shell.Ended += () =>
        {
            if (consoles.TryRemove(id, out _))
            {
                send(ShellCodec.Encode(ShellMessageType.Close, id));
            }
        };

        consoles[id] = new Hosted(shell);
        _ = Task.Run(async () =>
        {
            try
            {
                await shell.Ready.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // It has started but has not said where it is; opening it anyway beats not opening it at all.
            }

            send(ShellCodec.Encode(ShellMessageType.Opened, id, shell.Directory));
        });
    }

    /// <summary>One console here: its shell, and whatever has arrived so far of a command too long for one message.</summary>
    private sealed class Hosted(RemoteShell shell)
    {
        public RemoteShell Shell { get; } = shell;

        public CommandAssembly Arriving { get; } = new();
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
