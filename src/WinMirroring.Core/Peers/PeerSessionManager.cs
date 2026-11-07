using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Discovery;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core.Peers;

/// <summary>
/// Owns one session per peer. Connects to paired peers as they are discovered, accepts inbound links,
/// resolves the case where both sides connect at once, and persists pairing results.
/// </summary>
public sealed class PeerSessionManager : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitiatorFallbackDelay = TimeSpan.FromSeconds(15);

    private readonly DeviceIdentity _identity;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly PeerDirectory _directory;
    private readonly IPairingUserInterface _pairingUi;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _connecting = new(StringComparer.Ordinal);
    private readonly object _sessionsGate = new();
    private readonly CancellationTokenSource _cts = new();

    public PeerSessionManager(
        DeviceIdentity identity,
        AppSettings settings,
        Action saveSettings,
        PeerDirectory directory,
        IPairingUserInterface pairingUi,
        ILogger logger)
    {
        _identity = identity;
        _settings = settings;
        _saveSettings = saveSettings;
        _directory = directory;
        _pairingUi = pairingUi;
        _logger = logger;
    }

    public IReadOnlyCollection<PeerSession> Sessions => _sessions.Values.ToList();

    public event Action<PeerSession>? SessionReady;

    public event Action<PeerSession>? SessionClosed;

    public bool TryGetSession(string peerId, out PeerSession session)
    {
        if (_sessions.TryGetValue(peerId, out var found) && found.State != SessionState.Closed)
        {
            session = found;
            return true;
        }

        session = null!;
        return false;
    }

    public bool IsPaired(string fingerprint, string peerId)
    {
        var record = _settings.FindPairedByFingerprint(fingerprint);
        return record is not null && string.Equals(record.Id, peerId, StringComparison.Ordinal);
    }

    /// <summary>Addresses to probe for paired peers that are not currently visible.</summary>
    public IReadOnlyCollection<IPEndPoint> RememberedDiscoveryEndPoints()
    {
        var result = new List<IPEndPoint>();
        foreach (var peer in _settings.PairedPeers)
        {
            if (TryGetSession(peer.Id, out _))
            {
                continue;
            }

            foreach (var address in peer.LastKnownAddresses)
            {
                if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add(new IPEndPoint(ip, peer.DiscoveryPort));
                }
            }
        }

        return result;
    }

    public bool IsRememberedAddress(IPAddress address)
    {
        var text = address.ToString();
        return _settings.PairedPeers.Any(p => p.LastKnownAddresses.Contains(text, StringComparer.Ordinal));
    }

    // ---- inbound -------------------------------------------------------------------------------------------

    public async Task HandleInboundAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint;
        PeerConnection connection;
        try
        {
            connection = await PeerConnection.AcceptAsync(client, _identity.Certificate, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "TLS accept from {Remote} failed", remote);
            return;
        }

        try
        {
            var handshake = await PeerHandshake.RunAsResponderAsync(
                connection, _identity, _settings.TransportPort, _settings.DiscoveryPort, IsPaired, _cts.Token).ConfigureAwait(false);
            await RegisterSessionAsync(connection, handshake).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HandshakeException or IOException or OperationCanceledException or InvalidDataException)
        {
            _logger.LogInformation(ex, "Inbound handshake from {Remote} failed", remote);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- outbound ------------------------------------------------------------------------------------------

    public void OnPeerSeen(DiscoveredPeer peer)
    {
        var paired = _settings.FindPairedById(peer.Id);
        var hasSession = TryGetSession(peer.Id, out _);

        _directory.Update(peer.Id, info =>
        {
            info.Name = peer.Name;
            info.Fingerprint = string.IsNullOrEmpty(info.Fingerprint) || !hasSession ? peer.Fingerprint : info.Fingerprint;
            info.IsPaired = paired is not null;
            info.TransportEndPoint = peer.TransportEndPoint;
            info.DiscoveryEndPoint = peer.DiscoveryEndPoint;
            info.LastSeen = peer.SeenAt;
            if (!info.HasSession && info.State != PeerState.Connecting)
            {
                info.State = PeerState.Discovered;
            }
        });

        if (paired is null || hasSession || _connecting.ContainsKey(peer.Id))
        {
            _waitingSince.TryRemove(peer.Id, out _);
            return;
        }

        if (!CertificateFingerprint.AreEqual(paired.Fingerprint, peer.Fingerprint))
        {
            _logger.LogWarning("{Peer} announces fingerprint {Seen} but we pinned {Pinned}; not connecting",
                peer.Name, CertificateFingerprint.ToDisplay(peer.Fingerprint), CertificateFingerprint.ToDisplay(paired.Fingerprint));
            return;
        }

        if (!ShouldInitiate(peer.Id))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastAttempt.TryGetValue(peer.Id, out var last) && now - last < ReconnectInterval)
        {
            return;
        }

        _lastAttempt[peer.Id] = now;
        _ = ConnectInBackgroundAsync(peer.Id, peer.TransportEndPoint, paired.Fingerprint);
    }

    private bool ShouldInitiate(string peerId)
    {
        // The device with the lower id connects; the other waits, but gives up waiting after a while
        // (one-way reachability, e.g. a VPN that only allows connections in one direction).
        if (string.CompareOrdinal(_identity.Id, peerId) < 0)
        {
            return true;
        }

        var since = _waitingSince.GetOrAdd(peerId, DateTimeOffset.UtcNow);
        return DateTimeOffset.UtcNow - since > InitiatorFallbackDelay;
    }

    private async Task ConnectInBackgroundAsync(string peerId, IPEndPoint endPoint, string expectedFingerprint)
    {
        if (!_connecting.TryAdd(peerId, 0))
        {
            return;
        }

        try
        {
            _directory.Update(peerId, info => info.State = PeerState.Connecting);
            await ConnectAsync(peerId, endPoint, expectedFingerprint, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Connecting to {Peer} at {EndPoint} failed: {Message}", peerId, endPoint, ex.Message);
            _directory.Update(peerId, info =>
            {
                info.LastError = ex.Message;
                if (!info.HasSession)
                {
                    info.State = PeerState.Discovered;
                }
            });
        }
        finally
        {
            _connecting.TryRemove(peerId, out _);
        }
    }

    /// <summary>Opens a session to <paramref name="peerId"/>. With a fingerprint, the TLS certificate must match it.</summary>
    public async Task<PeerSession> ConnectAsync(string peerId, IPEndPoint endPoint, string? expectedFingerprint, CancellationToken ct)
    {
        if (TryGetSession(peerId, out var existing))
        {
            return existing;
        }

        var connection = await PeerConnection.ConnectAsync(
            endPoint,
            certificate => expectedFingerprint is null
                || CertificateFingerprint.AreEqual(CertificateFingerprint.Compute(certificate), expectedFingerprint),
            ct).ConfigureAwait(false);

        try
        {
            var handshake = await PeerHandshake.RunAsInitiatorAsync(
                connection, _identity, _settings.TransportPort, _settings.DiscoveryPort, IsPaired, ct).ConfigureAwait(false);

            if (!string.Equals(handshake.PeerId, peerId, StringComparison.Ordinal))
            {
                throw new HandshakeException($"Expected device {peerId} but reached {handshake.PeerId}.");
            }

            return await RegisterSessionAsync(connection, handshake).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---- registration ---------------------------------------------------------------------------------------

    private async Task<PeerSession> RegisterSessionAsync(PeerConnection connection, HandshakeResult handshake)
    {
        var session = new PeerSession(connection, _identity, handshake, _pairingUi, OnPairedAsync, _logger);

        PeerSession? loser = null;
        PeerSession winner;
        lock (_sessionsGate)
        {
            if (_sessions.TryGetValue(handshake.PeerId, out var existing) && existing.State != SessionState.Closed)
            {
                // Both sides connected at once. Keep the session opened by the lower device id on both ends.
                if (IsCanonical(existing))
                {
                    loser = session;
                    winner = existing;
                }
                else
                {
                    loser = existing;
                    winner = session;
                    _sessions[handshake.PeerId] = session;
                }
            }
            else
            {
                winner = session;
                _sessions[handshake.PeerId] = session;
            }
        }

        if (loser is not null)
        {
            _logger.LogDebug("Duplicate session with {Peer}; closing the one opened by {Initiator}", handshake.PeerName, loser.InitiatorId);
            if (loser == session)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return winner;
            }

            _ = loser.CloseAsync("duplicate");
        }

        session.StateChanged += OnSessionStateChanged;
        session.Closed += OnSessionClosed;
        session.Start();
        _waitingSince.TryRemove(session.PeerId, out _);

        _directory.Update(session.PeerId, info =>
        {
            info.Name = session.PeerName;
            info.Fingerprint = session.PeerFingerprint;
            info.IsPaired = _settings.FindPairedById(session.PeerId) is not null;
            info.TransportEndPoint = session.PeerTransportEndPoint;
            info.DiscoveryEndPoint = session.PeerDiscoveryEndPoint;
            info.LastSeen = DateTimeOffset.UtcNow;
            info.LastError = null;
            info.State = ToPeerState(session.State);
        });

        _logger.LogInformation("Session with {Peer} ({Remote}) established as {Role}, state {State}, {Protocol}",
            session.PeerName, session.RemoteEndPoint, session.IsInitiator ? "initiator" : "responder", session.State, connection.NegotiatedProtocol);

        if (session.State == SessionState.Ready)
        {
            RememberAddress(session);
            SessionReady?.Invoke(session);
        }

        return session;
    }

    private bool IsCanonical(PeerSession session)
    {
        var responderId = session.IsInitiator ? session.PeerId : _identity.Id;
        return string.CompareOrdinal(session.InitiatorId, responderId) < 0;
    }

    private static PeerState ToPeerState(SessionState state) => state switch
    {
        SessionState.Ready => PeerState.Ready,
        SessionState.Pairing => PeerState.Pairing,
        SessionState.Unpaired => PeerState.Connected,
        _ => PeerState.Discovered,
    };

    private void OnSessionStateChanged(PeerSession session)
    {
        if (session.State == SessionState.Closed)
        {
            return;
        }

        _directory.Update(session.PeerId, info => info.State = ToPeerState(session.State));
        if (session.State == SessionState.Ready)
        {
            RememberAddress(session);
            SessionReady?.Invoke(session);
        }
    }

    private void OnSessionClosed(PeerSession session, Exception? error)
    {
        lock (_sessionsGate)
        {
            if (_sessions.TryGetValue(session.PeerId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.PeerId, out _);
            }
        }

        _directory.Update(session.PeerId, info =>
        {
            if (info.HasSession)
            {
                var seenRecently = info.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < Protocol.ProtocolConstants.PeerExpiry;
                info.State = seenRecently ? PeerState.Discovered : PeerState.Offline;
            }

            info.LastError = error?.Message;
        });

        SessionClosed?.Invoke(session);
    }

    // ---- pairing -------------------------------------------------------------------------------------------

    /// <summary>User action: connect to a discovered peer if needed and run PIN pairing as the initiator.</summary>
    public async Task<bool> PairAsync(string peerId, CancellationToken ct)
    {
        if (!_directory.TryGet(peerId, out var info))
        {
            throw new InvalidOperationException("Unknown peer.");
        }

        if (!TryGetSession(peerId, out var session))
        {
            if (info.TransportEndPoint is null)
            {
                throw new InvalidOperationException("The peer has no known address.");
            }

            _directory.Update(peerId, p => p.State = PeerState.Connecting);
            try
            {
                var expected = string.IsNullOrEmpty(info.Fingerprint) ? null : info.Fingerprint;
                session = await ConnectAsync(peerId, info.TransportEndPoint, expected, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _directory.Update(peerId, p =>
                {
                    p.State = PeerState.Discovered;
                    p.LastError = ex.Message;
                });
                throw;
            }
        }

        return session.State == SessionState.Ready || await session.PairAsync(ct).ConfigureAwait(false);
    }

    public async Task UnpairAsync(string peerId)
    {
        _settings.PairedPeers.RemoveAll(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
        _saveSettings();

        if (TryGetSession(peerId, out var session))
        {
            await session.CloseAsync("unpaired").ConfigureAwait(false);
        }

        _directory.Update(peerId, info =>
        {
            info.IsPaired = false;
            info.State = info.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < Protocol.ProtocolConstants.PeerExpiry
                ? PeerState.Discovered
                : PeerState.Offline;
        });
    }

    private Task OnPairedAsync(PeerSession session)
    {
        var record = _settings.FindPairedById(session.PeerId);
        if (record is null)
        {
            record = new PairedPeer { Id = session.PeerId };
            _settings.PairedPeers.Add(record);
        }

        record.Name = session.PeerName;
        record.Fingerprint = session.PeerFingerprint;
        record.PairedAt = DateTimeOffset.UtcNow;
        record.TransportPort = session.PeerTransportPort;
        record.DiscoveryPort = session.PeerDiscoveryPort;
        record.RememberAddress(session.RemoteEndPoint.Address.ToString());
        _saveSettings();

        _directory.Update(session.PeerId, info => info.IsPaired = true);
        return Task.CompletedTask;
    }

    private void RememberAddress(PeerSession session)
    {
        var record = _settings.FindPairedById(session.PeerId);
        if (record is null)
        {
            return;
        }

        record.Name = session.PeerName;
        record.TransportPort = session.PeerTransportPort;
        record.DiscoveryPort = session.PeerDiscoveryPort;
        record.RememberAddress(session.RemoteEndPoint.Address.ToString());
        _saveSettings();
    }

    public async Task CloseAllAsync(string? reason = null)
    {
        foreach (var session in Sessions)
        {
            await session.CloseAsync(reason).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseAllAsync("shutdown").ConfigureAwait(false);
        _cts.Dispose();
    }
}
