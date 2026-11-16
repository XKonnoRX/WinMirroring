using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Local;

/// <summary>
/// The app's end of <see cref="LocalShellPipe"/>: it runs, on a device, what a tool on this machine asks it to,
/// using the identity and the pairings this app already has.
///
/// This is what makes the command-line tool stop being a device. The alternative was what it did before: a node of
/// its own, an identity of its own, and a pairing with every device it wanted to manage — which is a second entry
/// in the device list of each of them, and a stranger for the person at each to confirm, all for a tool. Here the
/// tool asks the app, and the device sees a command from the machine it has already agreed to.
///
/// Nothing is granted by this that the person at this machine did not already have: they can open a console on any
/// paired device from the app's own window. What the pipe adds is that a script can do it too — as them, on this
/// machine, which is why the pipe is theirs alone.
/// </summary>
public sealed class LocalShellServer : IAsyncDisposable
{
    /// <summary>
    /// Enough for a few tools at once and no more; each is one command being run somewhere. The number matters
    /// more than it looks: these are instances of one named pipe, and when they are all taken no new one can be
    /// created at all, so every tool still holding one is holding the whole local console shut.
    /// </summary>
    private const int AtOnce = 8;

    /// <summary>
    /// How long a tool that has connected has to say what it wants run. A connection that never says is not
    /// waiting for anything — it is a tool that died between connecting and asking, or something that opened the
    /// pipe and went away — and until this it sat here holding its instance of the pipe for ever.
    /// </summary>
    private static readonly TimeSpan SaysWhatToRun = TimeSpan.FromSeconds(30);

    /// <summary>How long before trying to listen again, when the pipe could not be opened.</summary>
    private static readonly TimeSpan ListenAgainAfter = TimeSpan.FromSeconds(5);

    private readonly MirroringNode _node;
    private readonly ShellService _shell;
    private readonly ILogger<LocalShellServer> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _pipeName;
    private readonly TimeSpan _saysWhatToRun;
    private Task? _accepting;
    private int _disposed;

    /// <summary>
    /// <paramref name="pipeName"/> is for tests, which must not put a second server on the name the app uses: a
    /// client would then reach whichever of the two was free, and on this machine one of them is the real app
    /// with the real devices behind it. <paramref name="saysWhatToRun"/> is for tests too: no test should sit out
    /// half a minute to watch a silent connection be let go of.
    /// </summary>
    public LocalShellServer(
        MirroringNode node,
        ShellService shell,
        ILogger<LocalShellServer> logger,
        string? pipeName = null,
        TimeSpan? saysWhatToRun = null)
    {
        _node = node;
        _shell = shell;
        _logger = logger;
        _pipeName = pipeName ?? LocalShellPipe.Name;
        _saysWhatToRun = saysWhatToRun ?? SaysWhatToRun;
    }

    public void Start()
    {
        _accepting ??= Task.Run(() => AcceptLoopAsync(_stopping.Token));
        _logger.LogInformation("Local command pipe listening as {Pipe}", _pipeName);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var failed = 0;
        string? alreadySaid = null;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = LocalShellPipe.CreateServer(AtOnce, _pipeName);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                // Once, with the trace, and then counted in silence. This loop tries again every few seconds for
                // as long as the reason lasts, and the reason can last all night: with every instance of the pipe
                // taken, creating another throws, and a full stack trace every five seconds turned one app's log
                // from 315 KB into 3.5 MB by morning — 3772 copies of one thing already said. The next line worth
                // writing is the one that says it is listening again.
                if (!string.Equals(alreadySaid, ex.Message, StringComparison.Ordinal))
                {
                    alreadySaid = ex.Message;
                    failed = 1;
                    _logger.LogWarning(ex, "The local command pipe could not be opened; trying again every {Seconds:F0}s and not saying so again", ListenAgainAfter.TotalSeconds);
                }
                else
                {
                    failed++;
                }

                await Task.Delay(ListenAgainAfter, ct).ConfigureAwait(false);
                continue;
            }

            if (failed > 0)
            {
                _logger.LogInformation("The local command pipe is listening again, after {Attempts} attempts that could not open it", failed);
                failed = 0;
                alreadySaid = null;
            }

            var connected = pipe;

            // Not started with the token: a task that is cancelled before it begins never runs its own finally,
            // and that finally is what gives this instance of the pipe back. The work inside stops on the token
            // anyway.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ServeAsync(connected, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "A local command ended badly");
                    }
                    finally
                    {
                        await connected.DisposeAsync().ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// One tool, one command. It says which device to act on and what to run; what comes back is what the command
    /// printed, as it prints it, and the code it ended with — or a refusal in words, which is the answer when
    /// there is no code to give.
    /// </summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = new FrameCodec.Reader();

        // Writes go through one loop of their own, because what fills it are events raised on a session's receive
        // loop: anything that waits there stops that session being read at all.
        var outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var writing = Task.Run(() => WriteLoopAsync(pipe, outbound.Reader, connection.Token), CancellationToken.None);
        void Send(byte[] message) => outbound.Writer.TryWrite(message);

        Task? watching = null;
        try
        {
            string target;
            bool elevated;
            string command;

            // Saying what to run has a deadline of its own. Everything after it may take as long as it takes —
            // a command can run for an hour and somebody's confirmation dialog can wait for a day — but a tool
            // that has not even said what it wants is waiting for nothing at all.
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
            {
                opening.CancelAfter(_saysWhatToRun);
                try
                {
                    // Every ending is a reason and then a code, including these: the reason is what a person reads
                    // and the code is what a script reads, and leaving either out makes one of the two guess.
                    if (await TargetAsync(reader, pipe, opening.Token).ConfigureAwait(false) is not ({ } device, var asAdministrator))
                    {
                        Send(ShellCodec.Encode(ShellMessageType.Refused, 0, "первым сообщением должно быть имя устройства."));
                        Send(ShellCodec.EncodeDone(0, ShellExit.CouldNotReach, string.Empty));
                        return;
                    }

                    if (await CommandAsync(reader, pipe, opening.Token).ConfigureAwait(false) is not { } line)
                    {
                        Send(ShellCodec.Encode(ShellMessageType.Refused, 0, "команда не пришла целиком."));
                        Send(ShellCodec.EncodeDone(0, ShellExit.CouldNotReach, string.Empty));
                        return;
                    }

                    (target, elevated, command) = (device, asAdministrator, line);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("A tool on this machine connected and never said what to run");
                    Send(ShellCodec.Encode(ShellMessageType.Refused, 0, "инструмент подключился и не сказал, что запускать."));
                    Send(ShellCodec.EncodeDone(0, ShellExit.CouldNotReach, string.Empty));
                    return;
                }
            }

            // From here nothing else is read from this pipe, so nothing would notice the tool going away — see
            // what this watches for and why it has to.
            watching = Task.Run(() => NoticeTheToolLeavingAsync(reader, pipe, connection), CancellationToken.None);

            _logger.LogInformation(
                "A tool on this machine runs something on {Device}{Elevated}",
                target,
                elevated ? " with administrator rights" : string.Empty);

            var code = await RemoteCommand.RunAsync(
                _node,
                _shell,
                target,
                command,
                (stream, text) => Send(ShellCodec.EncodeOutput(0, stream, text)),
                reason => Send(ShellCodec.Encode(ShellMessageType.Refused, 0, reason)),
                // Passed on rather than swallowed: the tool is the one with somebody watching it, and whether a
                // wait is a person being asked or a device gone quiet is the whole of what it needs to know.
                question => Send(ShellCodec.Encode(ShellMessageType.Asking, 0, question)),
                RemoteCommand.DefaultWait,
                elevated,
                connection.Token).ConfigureAwait(false);

            Send(ShellCodec.EncodeDone(0, code, string.Empty));
        }
        finally
        {
            outbound.Writer.TryComplete();
            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The tool is gone, or the pipe broke mid-write; there is nothing left to tell anyone.
            }

            await connection.CancelAsync().ConfigureAwait(false);

            // Awaited rather than abandoned: it holds a read on this pipe, and the pipe is disposed the moment
            // this returns. Not awaited for ever, though — this whole method exists to make sure the instance of
            // the pipe goes back, and waiting indefinitely here to tidy up would be the same bug again. A read
            // left pending is ended by the pipe being disposed.
            if (watching is not null)
            {
                try
                {
                    await watching.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Notices the tool going away while its command is still being waited for, and ends the connection when it
    /// does.
    ///
    /// Nothing is read from this pipe between the command arriving and the answer going back, and a pipe nobody
    /// is reading says nothing at all about the other end: a tool killed while waiting left this holding its
    /// instance of the pipe, and the waiting outlived it. There are only so many instances of one named pipe, and
    /// when they are all taken no new one can be created — so a handful of tools killed while waiting on
    /// confirmation dialogs was the whole local console dead, for everything, until those dialogs were answered.
    /// A night of exactly that is how this was found.
    /// </summary>
    private static async Task NoticeTheToolLeavingAsync(FrameCodec.Reader reader, Stream pipe, CancellationTokenSource connection)
    {
        try
        {
            // Nothing is expected to arrive now; what is being watched for is the read ending, which is what a
            // tool that is no longer there looks like from this end.
            while (await reader.ReadAsync(pipe, connection.Token).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
        }

        try
        {
            if (!connection.IsCancellationRequested)
            {
                await connection.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The device named by the opening message and whether the tool asked for administrator rights on it, or null
    /// when that is not what arrived.
    /// </summary>
    private static async Task<(string Target, bool Elevated)?> TargetAsync(FrameCodec.Reader reader, Stream pipe, CancellationToken ct)
    {
        if (await reader.ReadAsync(pipe, ct).ConfigureAwait(false) is not { } frame
            || frame.Channel != ChannelId.Shell
            || !ShellCodec.TryReadHeader(frame.Payload, out var type, out _)
            || type is not (ShellMessageType.Open or ShellMessageType.OpenElevated)
            || !ShellCodec.TryDecodeText(frame.Payload, out var target)
            || target.Length == 0)
        {
            return null;
        }

        return (target, type == ShellMessageType.OpenElevated);
    }

    /// <summary>
    /// The command, put back together from however many messages carried it. A whole script comes this way, and
    /// one message holds eight kilobytes of it.
    /// </summary>
    private static async Task<string?> CommandAsync(FrameCodec.Reader reader, Stream pipe, CancellationToken ct)
    {
        var arriving = new CommandAssembly();
        while (true)
        {
            if (await reader.ReadAsync(pipe, ct).ConfigureAwait(false) is not { } frame
                || frame.Channel != ChannelId.Shell
                || !ShellCodec.TryReadHeader(frame.Payload, out var type, out _)
                || !ShellCodec.TryDecodeText(frame.Payload, out var text))
            {
                return null;
            }

            switch (arriving.Add(type, text, out var command))
            {
                case CommandPiece.Whole:
                    return command;

                case CommandPiece.Ignored or CommandPiece.TooLong:
                    return null;
            }
        }
    }

    private static async Task WriteLoopAsync(Stream pipe, ChannelReader<byte[]> outbound, CancellationToken ct)
    {
        await foreach (var message in outbound.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await FrameCodec.WriteAsync(pipe, ChannelId.Shell, message, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        // A pipe waiting for a connection is woken by the token; one being served ends with it too.
        if (_accepting is { } accepting)
        {
            try
            {
                await accepting.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _stopping.Dispose();
    }
}
