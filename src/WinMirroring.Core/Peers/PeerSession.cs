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

    private TaskCompletionSource<ControlMessage>? _pairingReply;
    private ResponderPairing? _responderPairing;
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

    /// <summary>Fixed PIN for automated tests; null means a random PIN is generated and the UI is asked.</summary>
    public string? PinOverride { get; init; }

    public event Action<PeerSession>? StateChanged;

    public event Action<PeerSession, Exception?>? Closed;

    /// <summary>Control messages the session itself does not consume (feature-level messages such as the layout).</summary>
    public event Action<PeerSession, ControlMessage>? ControlMessageReceived;

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

    /// <summary>Runs PIN pairing as the initiator. The responder displays the PIN; this side asks the user for it.</summary>
    public async Task<bool> PairAsync(CancellationToken ct = default)
    {
        if (!TrySetState(SessionState.Unpaired, SessionState.Pairing))
        {
            return State == SessionState.Ready;
        }

        var prompt = new PairingPrompt(PeerId, PeerName, PeerFingerprint, _identity.Fingerprint, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        timeout.CancelAfter(ProtocolConstants.PairingTimeout);
        var token = timeout.Token;

        try
        {
            _pairingReply = NewReply();
            await SendControlAsync(new PairRequestMessage(), token).ConfigureAwait(false);

            var first = await _pairingReply.Task.WaitAsync(token).ConfigureAwait(false);
            if (first is not PairChallengeMessage challenge)
            {
                return Fail(prompt, first is PairResultMessage r ? r.Reason : "protocol-error");
            }

            var nonce = Convert.FromBase64String(challenge.Nonce);
            var pin = PinOverride ?? await _pairingUi.RequestPinAsync(prompt, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin))
            {
                await SendControlAsync(new PairCancelMessage("cancelled"), token).ConfigureAwait(false);
                return Fail(prompt, "cancelled");
            }

            var key = PairingCrypto.DeriveKey(pin);
            _pairingReply = NewReply();
            var proof = PairingCrypto.ComputeProof(key, PairingRole.Initiator, _identity.Fingerprint, PeerFingerprint, nonce);
            await SendControlAsync(new PairProofMessage(Convert.ToBase64String(proof)), token).ConfigureAwait(false);

            var second = await _pairingReply.Task.WaitAsync(token).ConfigureAwait(false);
            if (second is not PairResultMessage result)
            {
                return Fail(prompt, "protocol-error");
            }

            if (!result.Ok)
            {
                return Fail(prompt, result.Reason ?? "rejected");
            }

            var expected = PairingCrypto.ComputeProof(key, PairingRole.Responder, _identity.Fingerprint, PeerFingerprint, nonce);
            if (!PairingCrypto.ProofMatches(expected, result.Proof))
            {
                return Fail(prompt, "responder-proof-mismatch");
            }

            await _onPaired(this).ConfigureAwait(false);
            TrySetState(SessionState.Pairing, SessionState.Ready);
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

    private sealed record ResponderPairing(byte[] Key, byte[] Nonce, PairingPrompt Prompt);

    private async Task HandlePairRequestAsync()
    {
        if (!TrySetState(SessionState.Unpaired, SessionState.Pairing))
        {
            await SendControlAsync(new PairResultMessage(false, null, "not-pairable"), _cts.Token).ConfigureAwait(false);
            return;
        }

        var pin = PinOverride ?? PairingCrypto.GeneratePin();
        var prompt = new PairingPrompt(PeerId, PeerName, PeerFingerprint, _identity.Fingerprint, pin);

        bool accepted;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(ProtocolConstants.PairingTimeout);
            accepted = await _pairingUi.ConfirmIncomingPairingAsync(prompt, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            accepted = false;
        }

        if (State != SessionState.Pairing)
        {
            // The initiator cancelled or the link dropped while the dialog was open.
            return;
        }

        if (!accepted)
        {
            _responderPairing = null;
            TrySetState(SessionState.Pairing, SessionState.Unpaired);
            await SendControlAsync(new PairResultMessage(false, null, "rejected"), _cts.Token).ConfigureAwait(false);
            _pairingUi.PairingFinished(prompt, false, "rejected");
            return;
        }

        var nonce = PairingCrypto.GenerateNonce();
        _responderPairing = new ResponderPairing(PairingCrypto.DeriveKey(pin), nonce, prompt);
        await SendControlAsync(new PairChallengeMessage(Convert.ToBase64String(nonce)), _cts.Token).ConfigureAwait(false);
    }

    private async Task HandlePairProofAsync(PairProofMessage message)
    {
        var pending = _responderPairing;
        if (pending is null || State != SessionState.Pairing)
        {
            await SendControlAsync(new PairResultMessage(false, null, "unexpected"), _cts.Token).ConfigureAwait(false);
            return;
        }

        _responderPairing = null;
        var expected = PairingCrypto.ComputeProof(pending.Key, PairingRole.Initiator, PeerFingerprint, _identity.Fingerprint, pending.Nonce);
        if (!PairingCrypto.ProofMatches(expected, message.Proof))
        {
            TrySetState(SessionState.Pairing, SessionState.Unpaired);
            await SendControlAsync(new PairResultMessage(false, null, "wrong-pin"), _cts.Token).ConfigureAwait(false);
            _pairingUi.PairingFinished(pending.Prompt, false, "wrong-pin");
            _logger.LogWarning("Pairing with {Peer} failed: wrong PIN", PeerName);
            return;
        }

        var proof = PairingCrypto.ComputeProof(pending.Key, PairingRole.Responder, PeerFingerprint, _identity.Fingerprint, pending.Nonce);
        await _onPaired(this).ConfigureAwait(false);
        TrySetState(SessionState.Pairing, SessionState.Ready);
        await SendControlAsync(new PairResultMessage(true, Convert.ToBase64String(proof), null), _cts.Token).ConfigureAwait(false);
        _pairingUi.PairingFinished(pending.Prompt, true, null);
        _logger.LogInformation("Paired with {Peer} ({Fingerprint})", PeerName, CertificateFingerprint.ToDisplay(PeerFingerprint));
    }

    private void HandlePairCancel(PairCancelMessage message)
    {
        var pending = _responderPairing;
        _responderPairing = null;
        if (TrySetState(SessionState.Pairing, SessionState.Unpaired) && pending is not null)
        {
            _pairingUi.PairingFinished(pending.Prompt, false, message.Reason ?? "cancelled");
        }

        _pairingReply?.TrySetResult(new PairResultMessage(false, null, message.Reason ?? "cancelled"));
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

            case PairProofMessage proof:
                await GuardAsync(() => HandlePairProofAsync(proof)).ConfigureAwait(false);
                return false;

            case PairCancelMessage cancel:
                HandlePairCancel(cancel);
                return false;

            case PairChallengeMessage or PairResultMessage:
                if (_pairingReply?.TrySetResult(message) != true)
                {
                    _logger.LogDebug("Unexpected {Message} from {Peer}", message.GetType().Name, PeerName);
                }

                return false;

            case null:
                _logger.LogDebug("Unreadable control message from {Peer}", PeerName);
                return false;

            default:
                if (ControlMessageReceived is { } handlers)
                {
                    try
                    {
                        handlers(this, message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Control message handler failed for {Message}", message.GetType().Name);
                    }
                }
                else
                {
                    _logger.LogDebug("Unhandled control message {Message} from {Peer}", message.GetType().Name, PeerName);
                }

                return false;
        }
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
