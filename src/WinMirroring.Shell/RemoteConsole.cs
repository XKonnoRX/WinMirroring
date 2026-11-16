using Microsoft.Extensions.Logging;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell;

/// <summary>
/// One console open on another device, as the side that drives it sees it: lines go out, output comes back, and
/// nothing here waits — a command that takes a minute prints for a minute, and the window shows it printing.
///
/// Everything it raises arrives on whatever thread the session's receive loop is on, so a window hangs its own
/// marshalling on these rather than this knowing anything about windows.
/// </summary>
public sealed class RemoteConsole
{
    private readonly ILogger _logger;
    private int _finished;

    internal RemoteConsole(PeerSession session, uint id, ILogger logger)
    {
        Session = session;
        Id = id;
        _logger = logger;
    }

    internal PeerSession Session { get; }

    public uint Id { get; }

    public string PeerName => Session.PeerName;

    /// <summary>Where the console is; it follows what the commands do to it.</summary>
    public string Directory { get; private set; } = string.Empty;

    /// <summary>True between asking a command to run and being told it finished.</summary>
    public bool Running { get; private set; }

    /// <summary>The device agreed and the console is ready, in this directory.</summary>
    public event Action<string>? Opened;

    /// <summary>The device said no, and why — already in words to show.</summary>
    public event Action<string>? Refused;

    /// <summary>
    /// The device is asking a person something before it answers, and this is what — already in words to show.
    /// Not an ending: the console is still coming, and what follows is <see cref="Opened"/> or
    /// <see cref="Refused"/> whenever that person gets to it.
    /// </summary>
    public event Action<string>? Asking;

    /// <summary>
    /// The device stopped answering, which is not the same as refusing and must not read like one. A tablet that
    /// goes to sleep while a question is on its screen ends a console exactly like a person saying no — and the
    /// two call for opposite things from whoever asked: one to go and wake the device, the other not to ask again.
    /// </summary>
    public event Action<string>? Lost;

    public event Action<ShellStream, string>? Output;

    /// <summary>A command finished: its exit code and where the console is now.</summary>
    public event Action<int, string>? Done;

    /// <summary>The console is gone: closed, refused, or the connection went.</summary>
    public event Action? Finished;

    /// <summary>Runs one command. False when the console is gone or already busy with one.</summary>
    public bool Run(string command)
    {
        if (_finished != 0 || Running || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        Running = true;
        _ = SendInTurnAsync(ShellCodec.EncodeCommand(Id, command));
        return true;
    }

    /// <summary>
    /// Stops what is running. The device ends the shell to do it — one command of a shell reading from a pipe
    /// cannot be interrupted on its own — so the console closes and another has to be opened. Said plainly in the
    /// window rather than hidden, because a console that silently lost its directory would be worse.
    /// </summary>
    public void Cancel()
    {
        if (_finished == 0)
        {
            Send(ShellCodec.Encode(ShellMessageType.Cancel, Id));
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            Send(ShellCodec.Encode(ShellMessageType.Close, Id));
            Finished?.Invoke();
        }
    }

    internal void OnMessage(ShellMessageType type, ReadOnlySpan<byte> message)
    {
        switch (type)
        {
            case ShellMessageType.Opened when ShellCodec.TryDecodeText(message, out var directory):
                Directory = directory;
                Opened?.Invoke(directory);
                break;

            case ShellMessageType.Refused when ShellCodec.TryDecodeText(message, out var reason):
                OnClosed(reason);
                break;

            case ShellMessageType.Asking when ShellCodec.TryDecodeText(message, out var question):
                Asking?.Invoke(question);
                break;

            case ShellMessageType.Output when ShellCodec.TryDecodeOutput(message, out var stream, out var text):
                Output?.Invoke(stream, text);
                break;

            case ShellMessageType.Done when ShellCodec.TryDecodeDone(message, out var code, out var directory):
                Running = false;
                Directory = directory;
                Done?.Invoke(code, directory);
                break;

            case ShellMessageType.Close:
                OnClosed(null);
                break;
        }
    }

    /// <summary>
    /// The far side is finished with this console; <paramref name="reason"/> is shown when there is one.
    /// <paramref name="lost"/> says the console ended because the device stopped being there, rather than because
    /// anybody decided anything.
    /// </summary>
    internal void OnClosed(string? reason, bool lost = false)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        Running = false;
        if (reason is { Length: > 0 })
        {
            if (lost)
            {
                Lost?.Invoke(reason);
            }
            else
            {
                Refused?.Invoke(reason);
            }
        }

        Finished?.Invoke();
    }

    private void Send(byte[] message) => _ = SendAsync(message);

    /// <summary>
    /// One after another, each awaited before the next goes out. A long command travels as several messages and
    /// the last of them is the one that runs it, so their order is the difference between a script and a script
    /// with its middle somewhere else — and messages sent without waiting are not ordered by anything.
    /// </summary>
    private async Task SendInTurnAsync(IReadOnlyList<byte[]> messages)
    {
        foreach (var message in messages)
        {
            await SendAsync(message).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(byte[] message)
    {
        try
        {
            await Session.SendAsync(ChannelId.Shell, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Console {Console} lost its connection: {Message}", Id, ex.Message);
            OnClosed("соединение потеряно", lost: true);
        }
    }
}
