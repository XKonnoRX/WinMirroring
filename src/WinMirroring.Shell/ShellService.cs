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
/// A console is an ordinary PowerShell as the signed-in user, which is what managing a device means nine times out
/// of ten. An administrator console is a thing of its own: a setting of its own, a question of its own here, and a
/// helper that Windows asks about — once per run of this app, because the prompt is a question about a program and
/// that program then keeps running (see <see cref="Elevated.ElevatedHelper"/>).
/// </summary>
public sealed class ShellService : IAsyncDisposable
{
    private readonly MirroringNode _node;
    private readonly ILogger<ShellService> _logger;

    /// <summary>Consoles other devices have opened here, by the device that opened each one.</summary>
    private readonly ConcurrentDictionary<(string PeerId, uint Id), Hosted> _hosted = new();

    /// <summary>Devices whose consoles the person here has already agreed to, until this app restarts.</summary>
    private readonly ConcurrentDictionary<string, byte> _allowed = new(StringComparer.Ordinal);

    /// <summary>The same for administrator consoles, which are agreed to separately and no more often.</summary>
    private readonly ConcurrentDictionary<string, byte> _allowedElevated = new(StringComparer.Ordinal);

    /// <summary>
    /// Only one helper is ever started, and only one attempt at starting one runs at a time. Without the gate,
    /// two devices asking at the same moment put two UAC prompts on the screen of whoever is at this device — for
    /// the same permission, which is the thing being got rid of.
    /// </summary>
    private readonly SemaphoreSlim _helperGate = new(1, 1);

    private Elevated.IElevatedHost? _helper;

    /// <summary>Consoles this device has opened elsewhere, by id.</summary>
    private readonly ConcurrentDictionary<uint, RemoteConsole> _mine = new();

    /// <summary>
    /// Consoles asked for here that are still being asked about, by the device that asked. Cancelled when that
    /// device gives up on one — which is how a question that nobody is waiting for any more stops turning into a
    /// PowerShell nobody will ever type in. A tool killed while a confirmation dialog was up used to leave exactly
    /// that behind: the dialog was answered an hour later, a shell started for it, and it sat there until the
    /// session closed.
    /// </summary>
    private readonly ConcurrentDictionary<(string PeerId, uint Id), CancellationTokenSource> _opening = new();

    /// <summary>
    /// What has arrived so far of a command too long for one message, by the console it is for. A whole script
    /// comes this way; nothing is run until the message that ends it.
    /// </summary>
    private readonly ConcurrentDictionary<(string PeerId, uint Id), CommandAssembly> _arriving = new();

    private int _disposed;

    public ShellService(MirroringNode node, ILogger<ShellService> logger)
    {
        _node = node;
        _logger = logger;
        StartElevatedHost = async ct => await Elevated.ElevatedHelper.TryStartAsync(_logger, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the user here a yes-or-no question (set by the app; without it nothing is allowed).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shows a notice here (title, text), so a console opened on this device is never a silent one.</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>
    /// Where the elevated helper comes from. Replaced in tests, which cannot answer a UAC prompt and still have to
    /// hold what matters here: that it is started once and serves every administrator console afterwards.
    /// </summary>
    internal Func<CancellationToken, Task<Elevated.IElevatedHost?>> StartElevatedHost { get; set; }

    /// <summary>True while any device has a console open here.</summary>
    public bool HasConsolesHere => !_hosted.IsEmpty;

    /// <summary>How many consoles are open here. For tests that have to see that nothing extra was opened.</summary>
    internal int ConsolesHere => _hosted.Count;

    /// <summary>How many consoles were given up on here before they opened. For tests, which must not guess.</summary>
    internal int GivenUpOnHere => Volatile.Read(ref _givenUpOn);

    /// <summary>How many consoles are still being asked about here. For tests, so they can wait rather than race.</summary>
    internal int OpeningHere => _opening.Count;

    private int _givenUpOn;

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
    public RemoteConsole? Open(string peerId, bool elevated = false)
    {
        if (!_node.Sessions.TryGetSession(peerId, out var session) || session.State != SessionState.Ready)
        {
            return null;
        }

        var id = (uint)Random.Shared.Next(1, int.MaxValue);
        var console = new RemoteConsole(session, id, _logger);
        console.Finished += () => _mine.TryRemove(id, out _);
        _mine[id] = console;
        Send(session, ShellCodec.Encode(elevated ? ShellMessageType.OpenElevated : ShellMessageType.Open, id));
        _logger.LogInformation(
            "Asked {Peer} for {Kind} console (console {Console})",
            session.PeerName,
            elevated ? "an administrator" : "a",
            id);
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
            if (_hosted.TryRemove(key, out var hosted))
            {
                hosted.Shell.Dispose();
            }
        }

        foreach (var key in _arriving.Keys.Where(k => k.PeerId == session.PeerId).ToList())
        {
            _arriving.TryRemove(key, out _);
        }

        // Questions still on the screen for a device that is no longer there. Cancelled, not removed: whoever is
        // waiting on the answer owns the entry and tidies it up.
        foreach (var key in _opening.Keys.Where(k => k.PeerId == session.PeerId).ToList())
        {
            if (_opening.TryGetValue(key, out var waiting))
            {
                waiting.Cancel();
            }
        }

        foreach (var console in _mine.Values.Where(c => ReferenceEquals(c.Session, session)).ToList())
        {
            console.OnClosed("соединение с устройством закрылось", lost: true);
        }
    }

    private ValueTask OnMessageAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var span = payload.Span;
        if (!ShellCodec.TryReadHeader(span, out var type, out var id))
        {
            return ValueTask.CompletedTask;
        }

        // Whether a device gave us a console is worth keeping, because asking was already written down and the
        // answer was not: a refusal — commands turned off over there, or whoever is sitting at it saying no —
        // reached the screen of whoever asked and nowhere else. Afterwards, with the window closed, there was no
        // way to tell a device that said no from one that never answered.
        if (type is ShellMessageType.Opened)
        {
            _logger.LogInformation("{Peer} gave us console {Console}", session.PeerName, id);
        }
        else if (type is ShellMessageType.Refused)
        {
            _logger.LogInformation(
                "{Peer} gave us no console {Console}: {Reason}",
                session.PeerName,
                id,
                ShellCodec.TryDecodeText(span, out var reason) ? reason : "причина не названа");
        }

        // Which side of a console this is about cannot be told from the kind of message alone: Close is sent both
        // ways, by whoever is finished with one. What tells them apart is whose console it is. Deciding by kind
        // instead meant a Close arriving at the device being managed was taken for an answer to a console of its
        // own, found none, and was dropped — so the device-side half of closing a console had never once run, and
        // every console closed from the other end left its PowerShell running here until the session itself went.
        if (_mine.TryGetValue(id, out var ours) && ReferenceEquals(ours.Session, session))
        {
            ours.OnMessage(type, span);
            return ValueTask.CompletedTask;
        }

        // An answer to a console of ours that is already gone. Nothing to do with it; it is certainly not a
        // request for one of these.
        if (type is ShellMessageType.Opened or ShellMessageType.Refused or ShellMessageType.Output
            or ShellMessageType.Done or ShellMessageType.Asking)
        {
            return ValueTask.CompletedTask;
        }

        var key = (session.PeerId, id);
        switch (type)
        {
            case ShellMessageType.Open or ShellMessageType.OpenElevated:
                // Off the receive loop: starting a shell takes a moment and the person here may be asked first —
                // and for an administrator one, Windows asks as well.
                var elevated = type == ShellMessageType.OpenElevated;
                _ = Task.Run(() => OpenHereAsync(session, id, elevated), ct);
                break;

            case ShellMessageType.RunPart or ShellMessageType.Run when _hosted.TryGetValue(key, out var running)
                && ShellCodec.TryDecodeText(span, out var piece):
                switch (Arriving(key).Add(type, piece, out var command))
                {
                    case CommandPiece.Whole:
                        // The first line and its length, not the whole thing: a command here is as often a script
                        // of a hundred lines as it is one line, and a log is not the place to keep a copy of it.
                        _logger.LogInformation(
                            "{Peer} runs in console {Console}: {Command}",
                            session.PeerName,
                            id,
                            Describe(command));
                        running.Shell.Run(command);
                        break;

                    case CommandPiece.TooLong:
                        _logger.LogWarning("{Peer} sent a command longer than this will put back together", session.PeerName);
                        _ = session.SendAsync(ChannelId.Shell, ShellCodec.EncodeOutput(id, ShellStream.Error, CommandAssembly.TooLongText), ct);
                        _ = session.SendAsync(ChannelId.Shell, ShellCodec.EncodeDone(id, 1, running.Shell.Directory), ct);
                        break;
                }

                break;

            case ShellMessageType.Cancel when _hosted.TryRemove(key, out var cancelled):
                _logger.LogInformation("{Peer} stopped console {Console}", session.PeerName, id);
                _arriving.TryRemove(key, out _);
                cancelled.Shell.Dispose();
                _ = session.SendAsync(ChannelId.Shell, ShellCodec.Encode(ShellMessageType.Close, id), ct);
                break;

            case ShellMessageType.Close when _hosted.TryRemove(key, out var closing):
                _logger.LogInformation("{Peer} closed console {Console}", session.PeerName, id);
                _arriving.TryRemove(key, out _);
                closing.Shell.Dispose();
                Told(session, "Консоль закрыта", $"{session.PeerName} больше не выполняет здесь команды.");
                break;

            // Given up on before it was ever opened: the question here may still be on the screen, and whatever it
            // is answered, there is no longer anybody to open a console for.
            case ShellMessageType.Cancel or ShellMessageType.Close when _opening.TryGetValue(key, out var waiting):
                _logger.LogInformation("{Peer} gave up on console {Console} before it opened", session.PeerName, id);
                Interlocked.Increment(ref _givenUpOn);
                waiting.Cancel();
                break;
        }

        return ValueTask.CompletedTask;
    }

    private CommandAssembly Arriving((string PeerId, uint Id) key) =>
        _arriving.GetOrAdd(key, _ => new CommandAssembly());

    /// <summary>A command as one line for the log: the first line of it, and how long the rest is.</summary>
    private static string Describe(string command)
    {
        var end = command.IndexOfAny(['\r', '\n']);
        var first = end < 0 ? command : command[..end];
        return end < 0 ? first : $"{first} … ({command.Length} символов)";
    }

    /// <summary>
    /// A device wants a console here. Refused outright while the setting is off, and otherwise asked about once per
    /// device: a yes lasts until this app restarts, so managing a device does not mean answering a dialog per
    /// command, and a no is remembered for nothing.
    ///
    /// An administrator console needs a setting of its own and a yes of its own. It is a different thing to agree
    /// to: an ordinary console can do what the person at this device can do, and that one can do anything to it.
    /// But it is asked the same once, and so is Windows — the helper that runs these is started once per run of
    /// this app and kept. A question asked again for an answer that has not changed is not extra safety; it is a
    /// dialog on a device that is usually on a shelf in another room, and the way out of it that people actually
    /// find is to turn the whole thing on permanently or to stop locking the device at all.
    ///
    /// While somebody is being asked, the device that asked is told so (<see cref="ShellMessageType.Asking"/>).
    /// Waiting for a person and waiting for nothing look identical from the other end, and a script that cannot
    /// tell them apart spends the night calling a device broken.
    ///
    /// And the asking can outlive the asker, so it is watched: a device that gives up on a console before it opens
    /// gets no console, whatever the answer turns out to be.
    /// </summary>
    private async Task OpenHereAsync(PeerSession session, uint id, bool elevated)
    {
        var waiting = (session.PeerId, id);
        using var gaveUp = new CancellationTokenSource();
        _opening[waiting] = gaveUp;
        try
        {
            await OpenHereAsync(session, id, elevated, gaveUp.Token).ConfigureAwait(false);
        }
        finally
        {
            _opening.TryRemove(waiting, out _);
        }
    }

    private async Task OpenHereAsync(PeerSession session, uint id, bool elevated, CancellationToken gaveUp)
    {
        if (TurnedOff(elevated) is { } off)
        {
            await RefuseAsync(session, id, off).ConfigureAwait(false);
            return;
        }

        var agreedBefore = elevated ? _allowedElevated : _allowed;
        if (!agreedBefore.ContainsKey(session.PeerId))
        {
            if (Confirm is not { } ask)
            {
                await RefuseAsync(session, id, "здесь некому подтвердить запуск команд").ConfigureAwait(false);
                return;
            }

            Asking(
                session,
                id,
                elevated
                    ? $"{session.PeerName}: на устройстве спрашивают разрешение на команды с правами администратора — ждём ответа человека за ним"
                    : $"{session.PeerName}: на устройстве спрашивают разрешение на выполнение команд — ждём ответа человека за ним");

            var agreed = elevated
                ? await ask(
                    "Команды с правами администратора",
                    $"{session.PeerName} хочет выполнять здесь команды с правами администратора.\n\n"
                    + "Это больше, чем обычная консоль: с такими правами можно изменить в этой системе всё что угодно, "
                    + "включая службы, драйверы и защиту.\n\n"
                    + "Windows спросит отдельно, один раз — помощник с правами администратора после этого работает до "
                    + "перезапуска приложения, и это устройство сможет открывать такие консоли без новых вопросов.\n\n"
                    + "Разрешить?").ConfigureAwait(false)
                : await ask(
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

            agreedBefore[session.PeerId] = 1;

            // A question can sit on the screen for an hour, and the setting can be switched off under it. A yes
            // to something that is no longer allowed opens nothing and is not kept — looked at after the yes was
            // written down and not before, so that it cannot be written down just behind the switching-off that
            // forgets every yes (see ApplySettings), and outlive it.
            if (TurnedOff(elevated) is { } offMeanwhile)
            {
                agreedBefore.TryRemove(session.PeerId, out _);
                await RefuseAsync(session, id, offMeanwhile).ConfigureAwait(false);
                return;
            }
        }

        // The answer arrived; whoever wanted it may not have. Nothing is started for a device that has stopped
        // waiting — a shell started for nobody is a PowerShell sitting on this device until the session closes,
        // and for an administrator console it would be a UAC prompt for a command that no longer exists.
        if (GaveUp(session, id, gaveUp))
        {
            return;
        }

        var shell = elevated
            ? await ElevatedShellAsync(session, id).ConfigureAwait(false)
            : RemoteShell.TryStart(_logger);
        if (shell is not null && GaveUp(session, id, gaveUp))
        {
            shell.Dispose();
            return;
        }

        if (shell is null)
        {
            await RefuseAsync(
                session,
                id,
                TurnedOff(elevated)
                    ?? (elevated ? "права администратора здесь не подтвердили" : "не удалось запустить PowerShell")).ConfigureAwait(false);
            return;
        }

        shell.Output += (kind, text) =>
        {
            // As many messages as the line takes, each started from here in order: a line too long for one used to
            // be cut to its front. Not awaited one by one — the next line would then overtake the rest of this one.
            foreach (var piece in ShellCodec.EncodeOutputLine(id, kind == ShellStreamKind.Error ? ShellStream.Error : ShellStream.Output, text))
            {
                Send(session, piece);
            }
        };
        shell.Done += (code, directory) => Send(session, ShellCodec.EncodeDone(id, code, directory));
        shell.Ended += () =>
        {
            if (_hosted.TryRemove((session.PeerId, id), out var ended))
            {
                ended.Shell.Dispose();
                Send(session, ShellCodec.Encode(ShellMessageType.Close, id));
            }
        };

        _hosted[(session.PeerId, id)] = new Hosted(shell, elevated);

        // Asked once more, now that this is the thing holding the console. Before this line a give-up stopped the
        // shell being started; after it, a give-up arrives as an ordinary close and closes it. In between — the
        // moment between starting a shell and putting it where closing can find it — nothing owned it, and a
        // give-up landing exactly there would have left it running with nobody waiting on it.
        if (GaveUp(session, id, gaveUp) && _hosted.TryRemove((session.PeerId, id), out var unwanted))
        {
            unwanted.Shell.Dispose();
            return;
        }

        // And the setting, for the same reason and at the same place: switching it off closes the consoles that
        // can be found (see ApplySettings), and until the line above this one could not be. Switched off before
        // that line, it is seen here; after it, the console is found there.
        if (TurnedOff(elevated) is { } offNow)
        {
            // Not found means the switching-off got to it first, and has already said so to the device.
            if (_hosted.TryRemove((session.PeerId, id), out var forbidden))
            {
                forbidden.Shell.Dispose();
                await RefuseAsync(session, id, offNow).ConfigureAwait(false);
            }

            return;
        }

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

        // Closed while it was settling — the setting switched off under it, most likely — and whoever closed it
        // has told the device so. Announcing it open after that would be a notice here about a console that is
        // not there.
        if (!_hosted.TryGetValue((session.PeerId, id), out var still) || !ReferenceEquals(still.Shell, shell))
        {
            return;
        }

        Send(session, ShellCodec.Encode(ShellMessageType.Opened, id, shell.Directory));
        _logger.LogInformation("{Peer} opened console {Console} here", session.PeerName, id);
        Told(session, "Консоль открыта", $"{session.PeerName} выполняет команды на этом устройстве.");
    }

    /// <summary>
    /// An administrator console, out of the one helper this run of the app has. The first one starts it, which is
    /// where the Windows prompt happens — and where the device that asked is told to expect a wait. After that
    /// there is nothing left to prompt about: the program Windows agreed to is still running, and a console is a
    /// shell inside it.
    ///
    /// A helper that has gone — ended, killed, fell over — is replaced, prompt and all. That is not a hole in
    /// one-prompt-per-run: nothing of an agreement about a program outlives the program.
    /// </summary>
    private async Task<IHostedShell?> ElevatedShellAsync(PeerSession session, uint id)
    {
        if (_disposed != 0)
        {
            return null;
        }

        try
        {
            await _helperGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            // Switched off while this waited its turn — the turn can be behind another device's Windows prompt,
            // which is minutes. Nothing is started, and nobody is prompted, for what is no longer allowed.
            if (TurnedOff(elevated: true) is not null)
            {
                return null;
            }

            if (_helper is { IsAlive: false } gone)
            {
                _logger.LogInformation("The elevated helper is no longer running; the next administrator console needs the prompt again");
                gone.Dispose();
                _helper = null;
            }

            if (_helper is null)
            {
                Asking(
                    session,
                    id,
                    $"{session.PeerName}: Windows спрашивает на устройстве про права администратора — ждём ответа человека за ним");
                _helper = await StartElevatedHost(CancellationToken.None).ConfigureAwait(false);
                if (_helper is not null)
                {
                    Told(
                        session,
                        "Права администратора",
                        "Помощник с правами администратора запущен — он работает до перезапуска приложения.");
                }
            }

            return _helper?.Open();
        }
        finally
        {
            try
            {
                _helperGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Why a console of this kind may not be open here as the settings now stand, in words for the device that
    /// asked — or null when it may. An administrator console needs both settings: with commands off altogether
    /// there is no kind of console left.
    /// </summary>
    private string? TurnedOff(bool elevated)
    {
        var settings = _node.Settings.Shell;
        if (!settings.AllowCommands)
        {
            return "выполнение команд выключено на этом устройстве";
        }

        return elevated && !settings.AllowElevatedCommands
            ? "команды с правами администратора выключены на этом устройстве"
            : null;
    }

    /// <summary>
    /// Re-reads the settings that say whether other devices may run commands here, and ends what they no longer
    /// allow. Called by whoever changes them, after changing them.
    ///
    /// The settings used to be looked at only when a console was opened, so switching commands off was a promise
    /// about the next console and said nothing to the ones already open: a device that had one kept it, and kept
    /// running things here, for as long as it liked. And a yes once given was remembered until the app restarted
    /// whatever the switch said in between — off and on again, and that device was back in without a question.
    /// Whoever switches this off means now, and means everybody.
    ///
    /// So, with commands off: every console here is closed and every yes forgotten. With only administrator
    /// commands off: the same for administrator consoles and their yes, and the helper that holds administrator
    /// rights is ended too, since there is nothing left for it to be running for — the next administrator console,
    /// if this is ever switched back on, is asked about here and by Windows again.
    ///
    /// The device whose console is closed is told why, in the words it was refused with in the first place: the
    /// refusal it would have had, had it asked a moment later.
    /// </summary>
    public void ApplySettings()
    {
        var ordinaryOff = TurnedOff(elevated: false);
        var elevatedOff = TurnedOff(elevated: true);
        if (elevatedOff is null)
        {
            return;
        }

        if (ordinaryOff is not null)
        {
            _allowed.Clear();
        }

        _allowedElevated.Clear();

        var closed = new List<IHostedShell>();
        foreach (var entry in _hosted)
        {
            if ((ordinaryOff ?? (entry.Value.Elevated ? elevatedOff : null)) is not { } reason || !_hosted.TryRemove(entry))
            {
                continue;
            }

            _arriving.TryRemove(entry.Key, out _);
            closed.Add(entry.Value.Shell);
            if (_node.Sessions.TryGetSession(entry.Key.PeerId, out var session))
            {
                _logger.LogInformation("Closed console {Console} of {Peer}: {Reason}", entry.Key.Id, session.PeerName, reason);
                Send(session, ShellCodec.Encode(ShellMessageType.Refused, entry.Key.Id, reason));
                Told(session, "Консоль закрыта", $"{session.PeerName} больше не выполняет здесь команды.");
            }
        }

        // Off this thread: it is whoever flipped the switch, most likely a window, and ending a shell that is in
        // the middle of something waits out its grace period — per console.
        if (closed.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var shell in closed)
                {
                    shell.Dispose();
                }
            });
        }

        _ = EndElevatedHostAsync();
    }

    /// <summary>
    /// Ends the helper that holds administrator rights, once administrator commands are no longer allowed. Behind
    /// the same gate that starts it, so that a helper being started at this very moment — a Windows prompt may be
    /// on the screen for it — is ended when it arrives rather than missed.
    /// </summary>
    private async Task EndElevatedHostAsync()
    {
        if (_disposed != 0)
        {
            return;
        }

        try
        {
            await _helperGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Looked at again here: switched back on while this waited, and the helper stays.
            if (TurnedOff(elevated: true) is not null && _helper is { } helper)
            {
                _logger.LogInformation("Administrator commands are no longer allowed here; the elevated helper is ended");
                _helper = null;
                helper.Dispose();
            }
        }
        finally
        {
            try
            {
                _helperGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>True when the device that asked for this console has stopped waiting for it.</summary>
    private bool GaveUp(PeerSession session, uint id, CancellationToken gaveUp)
    {
        if (!gaveUp.IsCancellationRequested)
        {
            return false;
        }

        _logger.LogInformation("Nothing opened for {Peer}: console {Console} was given up on while being asked about", session.PeerName, id);
        return true;
    }

    /// <summary>
    /// Tells the device that asked that somebody here is being asked, and what. A notice and not an answer: the
    /// console is still coming, and what follows is an opening or a refusal whenever that person gets to it.
    /// </summary>
    private void Asking(PeerSession session, uint id, string what)
    {
        _logger.LogInformation("Keeping {Peer} waiting on console {Console}: {What}", session.PeerName, id, what);
        Send(session, ShellCodec.Encode(ShellMessageType.Asking, id, what));
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

    /// <summary>
    /// One console open here: its shell, and whether it is an administrator one — which is what decides whether
    /// switching administrator commands off closes it.
    /// </summary>
    private sealed record Hosted(IHostedShell Shell, bool Elevated);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        foreach (var hosted in _hosted.Values)
        {
            hosted.Shell.Dispose();
        }

        _hosted.Clear();
        foreach (var console in _mine.Values)
        {
            console.OnClosed(null);
        }

        _mine.Clear();

        // And the administrator rights go with the app, which is what "until the app restarts" means.
        _helper?.Dispose();
        _helper = null;
        _helperGate.Dispose();
        await ValueTask.CompletedTask;
    }
}
