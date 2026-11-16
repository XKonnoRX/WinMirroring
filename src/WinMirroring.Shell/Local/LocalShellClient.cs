using System.IO.Pipes;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Local;

/// <summary>
/// A tool's end of <see cref="LocalShellPipe"/>: it hands the app running on this machine a device and a command,
/// and reports back what the app's own console on that device said.
///
/// The point of going this way is that nothing new exists anywhere: no second identity, no second entry in any
/// device list, nothing for anyone to confirm. When no app is running here there is nothing to ask, and the caller
/// falls back to being a device of its own — which on a machine with no app is exactly what it is.
/// </summary>
public sealed class LocalShellClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly FrameCodec.Reader _reader = new();

    private LocalShellClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>
    /// The app on this machine, or null when there is none listening. Null is an ordinary answer and not a
    /// failure: a headless machine has no app, and the caller then does the work itself.
    /// </summary>
    public static async Task<LocalShellClient?> TryConnectAsync(CancellationToken ct = default, string? pipeName = null)
    {
        var pipe = new NamedPipeClientStream(".", pipeName ?? LocalShellPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)LocalShellPipe.ConnectWait.TotalMilliseconds, ct).ConfigureAwait(false);
            return new LocalShellClient(pipe);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> on <paramref name="target"/> through the app and returns the command's own
    /// exit code — or one of <see cref="ShellExit"/>'s, with <paramref name="failed"/> told why in words.
    ///
    /// <paramref name="waiting"/> is told when the device is asking a person something before it answers. Without
    /// it a wait on somebody's dialog is indistinguishable from a wait on nothing, and the caller has no way to
    /// know that waiting is exactly the right thing to be doing.
    /// </summary>
    public async Task<int> RunAsync(
        string target,
        string command,
        Action<ShellStream, string> output,
        Action<string> failed,
        CancellationToken ct = default,
        bool elevated = false,
        Action<string>? waiting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(failed);

        var opening = elevated ? ShellMessageType.OpenElevated : ShellMessageType.Open;
        await FrameCodec.WriteAsync(_pipe, ChannelId.Shell, ShellCodec.Encode(opening, 0, target), ct).ConfigureAwait(false);
        foreach (var piece in ShellCodec.EncodeCommand(0, command))
        {
            await FrameCodec.WriteAsync(_pipe, ChannelId.Shell, piece, ct).ConfigureAwait(false);
        }

        await _pipe.FlushAsync(ct).ConfigureAwait(false);

        while (true)
        {
            Frame? frame;
            try
            {
                frame = await _reader.ReadAsync(_pipe, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Including this tool closing the client while the read was pending, which is an ordinary way for
                // a tool to stop waiting and not a thing to throw out of here.
                frame = null;
            }

            if (frame is not { } message || !ShellCodec.TryReadHeader(message.Payload, out var type, out _))
            {
                failed("приложение на этом устройстве закрыло канал, не ответив.");
                return ShellExit.CouldNotReach;
            }

            switch (type)
            {
                case ShellMessageType.Output when ShellCodec.TryDecodeOutput(message.Payload, out var stream, out var text):
                    output(stream, text);
                    break;

                case ShellMessageType.Done when ShellCodec.TryDecodeDone(message.Payload, out var code, out _):
                    return code;

                // A reason, not an answer: the code that goes with it comes as the Done after it, because whether
                // this was a device saying no or a device that was never reached is the difference between two
                // exit codes and a script may well be reading them.
                case ShellMessageType.Refused when ShellCodec.TryDecodeText(message.Payload, out var reason):
                    failed(reason);
                    break;

                // Not an ending and not an answer: somebody over there is being asked. The waiting goes on.
                case ShellMessageType.Asking when ShellCodec.TryDecodeText(message.Payload, out var question):
                    waiting?.Invoke(question);
                    break;
            }
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
