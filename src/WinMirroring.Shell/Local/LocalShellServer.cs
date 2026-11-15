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
    /// <summary>Enough for a few tools at once and no more; each is one command being run somewhere.</summary>
    private const int AtOnce = 4;

    private readonly MirroringNode _node;
    private readonly ShellService _shell;
    private readonly ILogger<LocalShellServer> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _pipeName;
    private Task? _accepting;
    private int _disposed;

    /// <summary>
    /// <paramref name="pipeName"/> is for tests, which must not put a second server on the name the app uses: a
    /// client would then reach whichever of the two was free, and on this machine one of them is the real app
    /// with the real devices behind it.
    /// </summary>
    public LocalShellServer(MirroringNode node, ShellService shell, ILogger<LocalShellServer> logger, string? pipeName = null)
    {
        _node = node;
        _shell = shell;
        _logger = logger;
        _pipeName = pipeName ?? LocalShellPipe.Name;
    }

    public void Start()
    {
        _accepting ??= Task.Run(() => AcceptLoopAsync(_stopping.Token));
        _logger.LogInformation("Local command pipe listening as {Pipe}", _pipeName);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
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

                _logger.LogWarning(ex, "The local command pipe could not be opened");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            var connected = pipe;
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
                ct);
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

        try
        {
            // Every ending is a reason and then a code, including these two: the reason is what a person reads and
            // the code is what a script reads, and leaving either out makes one of the two guess.
            if (await TargetAsync(reader, pipe, connection.Token).ConfigureAwait(false) is not ({ } target, var elevated))
            {
                Send(ShellCodec.Encode(ShellMessageType.Refused, 0, "первым сообщением должно быть имя устройства."));
                Send(ShellCodec.EncodeDone(0, ShellExit.CouldNotReach, string.Empty));
                return;
            }

            if (await CommandAsync(reader, pipe, connection.Token).ConfigureAwait(false) is not { } command)
            {
                Send(ShellCodec.Encode(ShellMessageType.Refused, 0, "команда не пришла целиком."));
                Send(ShellCodec.EncodeDone(0, ShellExit.CouldNotReach, string.Empty));
                return;
            }

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
