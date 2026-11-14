using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Peers;

public enum SessionState
{
    /// <summary>Authenticated, but not paired on both sides. Only control and pairing messages flow.</summary>
    Unpaired,

    Pairing,

    /// <summary>Paired on both sides; feature channels are open.</summary>
    Ready,

    Closed,
}

public delegate ValueTask ChannelHandler(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct);

/// <summary>
/// One authenticated link to a peer: receive loop, heartbeat, channel dispatch and the pairing state machine.
/// </summary>
public sealed class PeerSession : IAsyncDisposable
{
    private readonly PeerConnection _connection;
    private readonly DeviceIdentity _identity;
    private readonly IPairingUserInterface _pairingUi;
    private readonly Func<PeerSession, Task> _onPaired;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelHandler> _handlers = new();
    private readonly object _stateGate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long a peer's opening statements are kept for handlers that have not subscribed yet.</summary>
    private static readonly TimeSpan OpeningStatementLife = TimeSpan.FromSeconds(30);
    private const int MaxOpeningStatements = 32;

    private readonly object _openingGate = new();
    private readonly List<(long Ticks, ControlMessage Message)> _opening = [];
    private Action<PeerSession, ControlMessage>? _controlHandlers;

    private TaskCompletionSource<ControlMessage>? _pairingReply;
    private CancellationTokenSource? _incomingConfirmation;
    private Task? _run;
    private long _lastReceivedTicks = Environment.TickCount64;
    private int _closeSignaled;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TimeSpan>> _pingWaiters = new();

    public PeerSession(
        PeerConnection connection,
        DeviceIdentity identity,
        HandshakeResult handshake,
        IPairingUserInterface pairingUi,
        Func<PeerSession, Task> onPaired,
        ILogger logger)
    {
        _connection = connection;
        _identity = identity;
        _pairingUi = pairingUi;
        _onPaired = onPaired;
        _logger = logger;

        PeerId = handshake.PeerId;
        PeerName = handshake.PeerName;
        PeerFingerprint = handshake.PeerFingerprint;
        PeerCertificate = handshake.PeerCertificate;
        PeerTransportPort = handshake.PeerTransportPort;
        PeerDiscoveryPort = handshake.PeerDiscoveryPort;
        State = handshake.PairedOnBothSides ? SessionState.Ready : SessionState.Unpaired;
    }

    public string PeerId { get; }

    public string PeerName { get; }

    public string PeerFingerprint { get; }

    public X509Certificate2 PeerCertificate { get; }

    public int PeerTransportPort { get; }

    public int PeerDiscoveryPort { get; }

    public bool IsInitiator => _connection.IsInitiator;

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    /// <summary>Address the peer's listener is reachable at, as learned from this session.</summary>
    public IPEndPoint PeerTransportEndPoint => new(_connection.RemoteEndPoint.Address, PeerTransportPort);

    public IPEndPoint PeerDiscoveryEndPoint => new(_connection.RemoteEndPoint.Address, PeerDiscoveryPort);

    /// <summary>Device id of the side that opened the TCP connection; used to resolve duplicate sessions.</summary>
    public string InitiatorId => IsInitiator ? _identity.Id : PeerId;

    public SessionState State { get; private set; }

    public TimeSpan? LastRoundTrip { get; private set; }

    /// <summary>Environment.TickCount64 when the last frame of any channel arrived from the peer.</summary>
    public long LastReceivedTicks => Volatile.Read(ref _lastReceivedTicks);

    public event Action<PeerSession>? StateChanged;

    public event Action<PeerSession, Exception?>? Closed;

    /// <summary>
    /// Control messages the session itself does not consume (feature-level messages such as the layout).
    ///
    /// A handler that subscribes also receives the peer's opening statements, even the ones that arrived before it
    /// did. A peer starts talking the instant the session turns Ready and the features attach their handlers from
    /// that same event, so its first messages could be read with nobody listening yet and were simply logged as
    /// unhandled — which is how a device that had never saved a layout could fail to learn one at all. Each
    /// handler sees each held message once, replayed only to whoever was not there for it.
    /// </summary>
    public event Action<PeerSession, ControlMessage>? ControlMessageReceived
    {
        add
        {
            if (value is null)
            {
                return;
            }

            List<ControlMessage> held;
            lock (_openingGate)
            {
                _controlHandlers += value;
                held = PruneOpeningStatements();
            }

            foreach (var message in held)
            {
                try
                {
                    value(this, message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Held control message handler failed for {Message}", message.GetType().Name);
                }
            }
        }

        remove
        {
            lock (_openingGate)
            {
                _controlHandlers -= value;
            }
        }
    }

    public Task WaitForCloseAsync() => _closed.Task;

    public void RegisterHandler(ChannelId channel, ChannelHandler handler) => _handlers[channel] = handler;

    public void UnregisterHandler(ChannelId channel) => _handlers.TryRemove(channel, out _);

    public void Start()
    {
        if (_run is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        _run = RunAsync();
    }

    public ValueTask SendAsync(ChannelId channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (State == SessionState.Closed)
        {
            throw new InvalidOperationException("Session is closed.");
        }

        if (channel != ChannelId.Control && State != SessionState.Ready)
        {
            throw new InvalidOperationException($"Channel {channel} requires a paired session.");
        }

        return _connection.SendAsync(channel, payload, ct);
    }

    public ValueTask SendControlAsync(ControlMessage message, CancellationToken ct = default) =>
        SendAsync(ChannelId.Control, ControlMessageCodec.Encode(message), ct);

    /// <summary>One application-level round trip over the session (works where ICMP is blocked). Null on timeout.</summary>
    public async Task<TimeSpan?> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var token = Stopwatch.GetTimestamp();
        var waiter = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pingWaiters[token] = waiter;
        try
        {
            await SendControlAsync(new PingMessage(token), ct).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pingWaiters.TryRemove(token, out _);
        }
    }

    public async Task CloseAsync(string? reason = null)
    {
        if (State != SessionState.Closed)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendControlAsync(new ByeMessage(reason), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await _run.ConfigureAwait(false);
        }
    }

    // ---- initiator side of pairing -------------------------------------------------------------------------

    /// <summary>The confirmation code both sides show for this pair of certificates.</summary>
    public string ConfirmationCode => PairingCrypto.ConfirmationCode(_identity.Fingerprint, PeerFingerprint);

    /// <summary>
    /// Asks the peer to pair; its user only has to press Accept. Both certificates are already proven by the
    /// handshake, so accepting simply pins them on both sides. Both screens show the same confirmation code.
    /// </summary>
    public async Task<bool> PairAsync(CancellationToken ct = default)
    {
        if (!TrySetState(SessionState.Unpaired, SessionState.Pairing))
        {
            return State == SessionState.Ready;
        }

        var prompt = new PairingPrompt(PeerId, PeerName, PeerFingerprint, _identity.Fingerprint, ConfirmationCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        timeout.CancelAfter(ProtocolConstants.PairingTimeout);
        var token = timeout.Token;

        try
        {
            _pairingReply = NewReply();
            _pairingUi.PairingStarted(prompt);
            await SendControlAsync(new PairRequestMessage(), token).ConfigureAwait(false);

            var reply = await _pairingReply.Task.WaitAsync(token).ConfigureAwait(false);
            if (reply is not PairResultMessage result)
            {
                return Fail(prompt, "protocol-error");
            }

            if (!result.Ok)
            {
                return Fail(prompt, result.Reason ?? "rejected");
            }

            // The receive loop already stored the pairing and switched to Ready (see HandleControlAsync).
            _pairingUi.PairingFinished(prompt, true, null);
            _logger.LogInformation("Paired with {Peer} ({Fingerprint})", PeerName, CertificateFingerprint.ToDisplay(PeerFingerprint));
            return true;
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            try
            {
                await SendControlAsync(new PairCancelMessage("timeout")).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            return Fail(prompt, ct.IsCancellationRequested ? "cancelled" : "timeout");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Pairing with {Peer} failed", PeerName);
            return Fail(prompt, "connection-lost");
        }
        finally
        {
            _pairingReply = null;
        }
    }

    private bool Fail(PairingPrompt prompt, string? reason)
    {
        TrySetState(SessionState.Pairing, SessionState.Unpaired);
        _pairingUi.PairingFinished(prompt, false, reason);
        _logger.LogInformation("Pairing with {Peer} did not complete: {Reason}", PeerName, reason);
        return false;
    }

    private static TaskCompletionSource<ControlMessage> NewReply() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- responder side of pairing -------------------------------------------------------------------------

    private async Task HandlePairRequestAsync()
    {
        if (!TrySetState(SessionState.Unpaired, SessionState.Pairing))
        {
            await SendControlAsync(new PairResultMessage(false, "not-pairable"), _cts.Token).ConfigureAwait(false);
            return;
        }

        var prompt = new PairingPrompt(PeerId, PeerName, PeerFingerprint, _identity.Fingerprint, ConfirmationCode);
        var confirmation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        confirmation.CancelAfter(ProtocolConstants.PairingTimeout);
        _incomingConfirmation = confirmation;

        bool accepted;
        try
        {
            accepted = await _pairingUi.ConfirmIncomingPairingAsync(prompt, confirmation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            accepted = false;
        }
        finally
        {
            _incomingConfirmation = null;
            confirmation.Dispose();
        }

        if (State != SessionState.Pairing)
        {
            // The initiator cancelled or the link dropped while the dialog was open.
            return;
        }

        if (!accepted)
        {
            TrySetState(SessionState.Pairing, SessionState.Unpaired);
            await SendControlAsync(new PairResultMessage(false, "rejected"), _cts.Token).ConfigureAwait(false);
            _pairingUi.PairingFinished(prompt, false, "rejected");
            return;
        }

        await _onPaired(this).ConfigureAwait(false);

        // Result first, Ready second: becoming Ready makes the features start talking, and the initiator must
        // see the result before any of that traffic.
        await SendControlAsync(new PairResultMessage(true, null), _cts.Token).ConfigureAwait(false);
        TrySetState(SessionState.Pairing, SessionState.Ready);
        _pairingUi.PairingFinished(prompt, true, null);
        _logger.LogInformation("Paired with {Peer} ({Fingerprint})", PeerName, CertificateFingerprint.ToDisplay(PeerFingerprint));
    }

    private void HandlePairCancel(PairCancelMessage message)
    {
        // The initiator gave up: close our confirmation dialog, if it is still open.
        try
        {
            _incomingConfirmation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _pairingReply?.TrySetResult(new PairResultMessage(false, message.Reason ?? "cancelled"));
    }

    // ---- loops ---------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        Exception? error = null;
        try
        {
            var receive = ReceiveLoopAsync(ct);
            var heartbeat = HeartbeatLoopAsync(ct);
            var finished = await Task.WhenAny(receive, heartbeat).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            SetStateClosed();
            _pairingReply?.TrySetCanceled();
            await _connection.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _closeSignaled, 1) == 0)
            {
                if (error is not null)
                {
                    _logger.LogInformation(error, "Session with {Peer} closed", PeerName);
                }
                else
                {
                    _logger.LogInformation("Session with {Peer} closed", PeerName);
                }

                Closed?.Invoke(this, error);
                _closed.TrySetResult();
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var frame = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            Volatile.Write(ref _lastReceivedTicks, Environment.TickCount64);
            var (channel, payload) = frame.Value;

            if (channel == ChannelId.Control)
            {
                if (await HandleControlAsync(payload, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            if (State == SessionState.Ready && _handlers.TryGetValue(channel, out var handler))
            {
                try
                {
                    await handler(this, payload, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Handler for {Channel} failed", channel);
                }
            }
            else
            {
                _logger.LogDebug("Dropping {Channel} frame ({Bytes} bytes) in state {State}", channel, payload.Length, State);
            }
        }
    }

    /// <summary>Returns true when the session should end.</summary>
    private async Task<bool> HandleControlAsync(byte[] payload, CancellationToken ct)
    {
        var message = ControlMessageCodec.TryDecode(payload);
        switch (message)
        {
            case PingMessage ping:
                await SendControlAsync(new PongMessage(ping.Timestamp), ct).ConfigureAwait(false);
                return false;

            case PongMessage pong:
                // Ping tokens are Stopwatch timestamps from this process, so the echo gives a sub-millisecond RTT.
                var rtt = Stopwatch.GetElapsedTime(pong.Timestamp);
                if (rtt >= TimeSpan.Zero && rtt < TimeSpan.FromMinutes(1))
                {
                    LastRoundTrip = rtt;
                    if (_pingWaiters.TryRemove(pong.Timestamp, out var waiter))
                    {
                        waiter.TrySetResult(rtt);
                    }
                }

                return false;

            case ByeMessage bye:
                _logger.LogInformation("{Peer} said bye: {Reason}", PeerName, bye.Reason ?? "-");
                return true;

            case PairRequestMessage:
                // Runs detached: the dialog may stay open for a while and must not block ping/pong.
                _ = Task.Run(() => GuardAsync(HandlePairRequestAsync), ct);
                return false;

            case PairCancelMessage cancel:
                HandlePairCancel(cancel);
                return false;

            case PairResultMessage result:
                if (_pairingReply is not { } reply)
                {
                    _logger.LogDebug("Unexpected pairing result from {Peer}", PeerName);
                    return false;
                }

                if (result.Ok && State == SessionState.Pairing)
                {
                    // Become Ready here, before the next frame is read: the peer starts sending feature traffic
                    // right behind this message, and handlers are registered when the session turns Ready.
                    try
                    {
                        await _onPaired(this).ConfigureAwait(false);
                        TrySetState(SessionState.Pairing, SessionState.Ready);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _logger.LogError(ex, "Storing the pairing with {Peer} failed", PeerName);
                        reply.TrySetResult(new PairResultMessage(false, "storage-error"));
                        return false;
                    }
                }

                reply.TrySetResult(result);
                return false;

            case null:
                _logger.LogDebug("Unreadable control message from {Peer}", PeerName);
                return false;

            default:
                Action<PeerSession, ControlMessage>? handlers;
                lock (_openingGate)
                {
                    handlers = _controlHandlers;
                    if (IsOpeningStatement(message))
                    {
                        PruneOpeningStatements();
                        if (_opening.Count < MaxOpeningStatements)
                        {
                            _opening.Add((Environment.TickCount64, message));
                        }
                    }
                }

                if (handlers is null)
                {
                    _logger.LogDebug("Nothing is listening for {Message} from {Peer} yet", message.GetType().Name, PeerName);
                    return false;
                }

                try
                {
                    handlers(this, message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Control message handler failed for {Message}", message.GetType().Name);
                }

                return false;
        }
    }

    /// <summary>
    /// What a peer says about itself the moment a session is ready: the device grid, its monitors, what it can do,
    /// which version it runs, who it knows. These are the messages that can be read before the feature that wants
    /// them is listening, and all of them are small and may be applied more than once without harm, which is what
    /// makes them safe to hold and replay. Everything else follows a user's action, by which time every handler is
    /// long attached.
    /// </summary>
    private static bool IsOpeningStatement(ControlMessage message) =>
        message is LayoutMessage or InputMonitorsMessage or DeviceCapabilitiesMessage or DeviceInfoMessage or PeerVouchMessage;

    /// <summary>Drops the ones that are too old to matter and returns what is left. Call under the gate.</summary>
    private List<ControlMessage> PruneOpeningStatements()
    {
        var cutoff = Environment.TickCount64 - (long)OpeningStatementLife.TotalMilliseconds;
        _opening.RemoveAll(held => held.Ticks < cutoff);
        return _opening.Select(held => held.Message).ToList();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Pairing step with {Peer} aborted", PeerName);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProtocolConstants.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var silence = Environment.TickCount64 - LastReceivedTicks;
            if (silence > ProtocolConstants.IdleTimeout.TotalMilliseconds)
            {
                throw new TimeoutException($"No data from {PeerName} for {silence / 1000} s.");
            }

            await SendControlAsync(new PingMessage(Stopwatch.GetTimestamp()), ct).ConfigureAwait(false);
        }
    }

    // ---- state ---------------------------------------------------------------------------------------------

    private bool TrySetState(SessionState expected, SessionState next)
    {
        lock (_stateGate)
        {
            if (State != expected)
            {
                return false;
            }

            State = next;
        }

        StateChanged?.Invoke(this);
        return true;
    }

    private void SetStateClosed()
    {
        lock (_stateGate)
        {
            if (State == SessionState.Closed)
            {
                return;
            }

            State = SessionState.Closed;
        }

        StateChanged?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        _cts.Dispose();
    }
}
