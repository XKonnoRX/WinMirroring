using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell;

/// <summary>
/// Running commands on a paired device over the session it already has, in the place of logging into it.
///
/// Both sides live here. On the device being managed it keeps the consoles that peers have opened, and it is where
/// the two things that make this safe to have at all are enforced: it is off until turned on, and the first console
/// a given device opens is asked about here, by the person sitting in front of this machine. A paired device can
/// already move this one's mouse and type on its keyboard, so this adds no power it did not have — but it makes
/// that power convenient, and convenient power deserves to be asked for out loud.
///
/// Nothing runs elevated. A console is an ordinary PowerShell as the signed-in user, which is what managing a
/// device means nine times out of ten; anything needing administrator rights asks for them on that device, in front
/// of whoever is there, exactly as it would if they had started it themselves.
/// </summary>
public sealed class ShellService : IAsyncDisposable
{
    private readonly MirroringNode _node;
    private readonly ILogger<ShellService> _logger;

    /// <summary>Consoles other devices have opened here, by the device that opened each one.</summary>
    private readonly ConcurrentDictionary<(string PeerId, uint Id), RemoteShell> _hosted = new();

    /// <summary>Devices whose consoles the person here has already agreed to, until this app restarts.</summary>
    private readonly ConcurrentDictionary<string, byte> _allowed = new(StringComparer.Ordinal);

    /// <summary>Consoles this device has opened elsewhere, by id.</summary>
    private readonly ConcurrentDictionary<uint, RemoteConsole> _mine = new();

    private int _disposed;

    public ShellService(MirroringNode node, ILogger<ShellService> logger)
    {
        _node = node;
        _logger = logger;
    }

    /// <summary>Asks the user here a yes-or-no question (set by the app; without it nothing is allowed).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shows a notice here (title, text), so a console opened on this device is never a silent one.</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>True while any device has a console open here.</summary>
    public bool HasConsolesHere => !_hosted.IsEmpty;

    /// <summary>
    /// Whether this service is listening on that device's session yet. A ready session is not the same thing: the
    /// service attaches from the session-ready event, which is raised after the session already calls itself ready,
    /// so for a moment a message sent to it goes nowhere at all. Anything waiting to use a console waits for this.
    /// </summary>
    public bool IsAttachedTo(string peerId) => _attached.ContainsKey(peerId);

    private readonly ConcurrentDictionary<string, byte> _attached = new(StringComparer.Ordinal);

    public void Start()
    {
        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                OnSessionReady(session);
            }
        }
    }

    /// <summary>
    /// Opens a console on <paramref name="peerId"/>. Comes back at once with something to show; whether the device
    /// agreed arrives on the console itself, as <see cref="RemoteConsole.Opened"/> or
    /// <see cref="RemoteConsole.Refused"/>, because the person over there may be being asked about it.
    /// </summary>
    public RemoteConsole? Open(string peerId)
    {
        if (!_node.Sessions.TryGetSession(peerId, out var session) || session.State != SessionState.Ready)
        {
            return null;
        }

        var id = (uint)Random.Shared.Next(1, int.MaxValue);
        var console = new RemoteConsole(session, id, _logger);
        console.Finished += () => _mine.TryRemove(id, out _);
        _mine[id] = console;
        Send(session, ShellCodec.Encode(ShellMessageType.Open, id));
        _logger.LogInformation("Asked {Peer} for a console (console {Console})", session.PeerName, id);
        return console;
    }

    private void OnSessionReady(PeerSession session)
    {
        session.RegisterHandler(ChannelId.Shell, OnMessageAsync);
        _attached[session.PeerId] = 1;
    }

    private void OnSessionClosed(PeerSession session)
    {
        _attached.TryRemove(session.PeerId, out _);
        foreach (var key in _hosted.Keys.Where(k => k.PeerId == session.PeerId).ToList())
        {
            if (_hosted.TryRemove(key, out var shell))
            {
                shell.Dispose();
            }
        }

        foreach (var console in _mine.Values.Where(c => ReferenceEquals(c.Session, session)).ToList())
        {
            console.OnClosed("соединение закрыто");
        }
    }

    private ValueTask OnMessageAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var span = payload.Span;
        if (!ShellCodec.TryReadHeader(span, out var type, out var id))
        {
            return ValueTask.CompletedTask;
        }

        // Answers to a console of ours.
        if (type is ShellMessageType.Opened or ShellMessageType.Refused or ShellMessageType.Output or ShellMessageType.Done or ShellMessageType.Close)
        {
            if (_mine.TryGetValue(id, out var console) && ReferenceEquals(console.Session, session))
            {
                console.OnMessage(type, span);
            }

            return ValueTask.CompletedTask;
        }

        var key = (session.PeerId, id);
        switch (type)
        {
            case ShellMessageType.Open:
                // Off the receive loop: starting a shell takes a moment and the person here may be asked first.
                _ = Task.Run(() => OpenHereAsync(session, id), ct);
                break;

            case ShellMessageType.Run when _hosted.TryGetValue(key, out var running) && ShellCodec.TryDecodeText(span, out var command):
                _logger.LogInformation("{Peer} runs in console {Console}: {Command}", session.PeerName, id, command);
                running.Run(command);
                break;

            case ShellMessageType.Cancel when _hosted.TryRemove(key, out var cancelled):
                _logger.LogInformation("{Peer} stopped console {Console}", session.PeerName, id);
                cancelled.Dispose();
                _ = session.SendAsync(ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Close, id), ct);
                break;

            case ShellMessageType.Close when _hosted.TryRemove(key, out var closing):
                _logger.LogInformation("{Peer} closed console {Console}", session.PeerName, id);
                closing.Dispose();
                Told(session, "Консоль закрыта", $"{session.PeerName} больше не выполняет здесь команды.");
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A device wants a console here. Refused outright while the setting is off, and otherwise asked about once per
    /// device: a yes lasts until this app restarts, so managing a device does not mean answering a dialog per
    /// command, and a no is remembered for nothing.
    /// </summary>
    private async Task OpenHereAsync(PeerSession session, uint id)
    {
        if (!_node.Settings.Shell.AllowCommands)
        {
            await RefuseAsync(session, id, "выполнение команд выключено на этом устройстве").ConfigureAwait(false);
            return;
        }

        if (!_allowed.ContainsKey(session.PeerId))
        {
            if (Confirm is not { } ask)
            {
                await RefuseAsync(session, id, "здесь некому подтвердить запуск команд").ConfigureAwait(false);
                return;
            }

            var agreed = await ask(
                "Выполнение команд",
                $"{session.PeerName} хочет выполнять команды на этом устройстве.\n\n"
                + "Это то же, что дать ему консоль здесь: он сможет запускать всё, что можете вы, от вашего имени. "
                + "Разрешение действует до перезапуска приложения.\n\nРазрешить?").ConfigureAwait(false);
            if (!agreed)
            {
                _logger.LogInformation("Refused a console to {Peer}: the user said no", session.PeerName);
                await RefuseAsync(session, id, "пользователь отказался").ConfigureAwait(false);
                return;
            }

            _allowed[session.PeerId] = 1;
        }

        var shell = RemoteShell.TryStart(_logger);
        if (shell is null)
        {
            await RefuseAsync(session, id, "не удалось запустить PowerShell").ConfigureAwait(false);
            return;
        }

        shell.Output += (kind, text) => Send(session, ShellCodec.EncodeOutput(id, kind == ShellStreamKind.Error ? ShellStream.Error : ShellStream.Output, text));
        shell.Done += (code, directory) => Send(session, ShellCodec.EncodeDone(id, code, directory));
        shell.Ended += () =>
        {
            if (_hosted.TryRemove((session.PeerId, id), out var ended))
            {
                ended.Dispose();
                Send(session, ShellCodec.Encode(ShellMessageType.Close, id));
            }
        };

        _hosted[(session.PeerId, id)] = shell;

        // Once the shell has settled its own first command, so that the far side is told the directory it is
        // really in rather than an empty one.
        try
        {
            await shell.Ready.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("The console for {Peer} did not report where it is; opening it anyway", session.PeerName);
        }

        Send(session, ShellCodec.Encode(ShellMessageType.Opened, id, shell.Directory));
        _logger.LogInformation("{Peer} opened console {Console} here", session.PeerName, id);
        Told(session, "Консоль открыта", $"{session.PeerName} выполняет команды на этом устройстве.");
    }

    private async Task RefuseAsync(PeerSession session, uint id, string reason)
    {
        _logger.LogInformation("No console for {Peer}: {Reason}", session.PeerName, reason);
        try
        {
            await session.SendAsync(ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Refused, id, reason)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void Send(PeerSession session, byte[] message)
    {
        _ = SendAsync(session, message);
    }

    private async Task SendAsync(PeerSession session, byte[] message)
    {
        try
        {
            await session.SendAsync(ChannelId.Shell, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void Told(PeerSession session, string title, string text) => Notify?.Invoke(title, text);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        foreach (var shell in _hosted.Values)
        {
            shell.Dispose();
        }

        _hosted.Clear();
        foreach (var console in _mine.Values)
        {
            console.OnClosed(null);
        }

        _mine.Clear();
        await ValueTask.CompletedTask;
    }
}
