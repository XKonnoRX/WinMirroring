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

    /// <summary>
    /// How often the list of devices is looked over for ones that have stopped announcing themselves. A quarter of
    /// the silence that counts, so a device is said to be off the network within ten seconds of its last beacon.
    /// </summary>
    private static readonly TimeSpan LookForSilentPeers = Protocol.ProtocolConstants.PeerExpiry / 4;

    private readonly DeviceIdentity _identity;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly PeerDirectory _directory;
    private readonly IPairingUserInterface _pairingUi;
    private readonly Power.PowerMode _power;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _connecting = new(StringComparer.Ordinal);
    private readonly object _sessionsGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _silentPeers;
    private int _disposed;

    public PeerSessionManager(
        DeviceIdentity identity,
        AppSettings settings,
        Action saveSettings,
        PeerDirectory directory,
        IPairingUserInterface pairingUi,
        Power.PowerMode power,
        ILogger logger)
    {
        _identity = identity;
        _settings = settings;
        _saveSettings = saveSettings;
        _directory = directory;
        _pairingUi = pairingUi;
        _power = power;
        _logger = logger;
        _silentPeers = new Timer(_ => ExpireSilentPeers(), null, LookForSilentPeers, LookForSilentPeers);
    }

    /// <summary>
    /// A device is listed as on the network because its beacons arrive, and nothing used to take that back when
    /// they stopped: only a session closing or a pairing being removed looked at how long ago the last one was.
    /// So this is looked at regularly instead, on a clock of its own — a beacon that does not come is not an
    /// event anything can wait for.
    /// </summary>
    private void ExpireSilentPeers()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _directory.ExpireUnseen(DateTimeOffset.UtcNow, Protocol.ProtocolConstants.PeerExpiry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whoever listens to the list is called from here, on a timer's thread, where an exception that got
            // away would take the whole program with it.
            _logger.LogError(ex, "Looking for devices that have stopped announcing themselves failed");
        }
    }

    public IReadOnlyCollection<PeerSession> Sessions => _sessions.Values.ToList();

    public event Action<PeerSession>? SessionReady;

    public event Action<PeerSession>? SessionClosed;

    /// <summary>A device was added to or removed from the list this one is paired with.</summary>
    public event Action? PairedPeersChanged;

    /// <summary>
    /// That device, by name, has unpaired from this one, so this one has now forgotten it as well. Worth saying
    /// out loud: a pairing disappearing is something the person here should hear about rather than discover.
    /// </summary>
    public event Action<string>? UnpairedByPeer;

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

            // And where Tailscale knows a machine by this device's name, there: away from the local network that
            // is the only place it can be found without its address being typed in (see TailscalePeers).
            if (_settings.FindPeersOnTailscale)
            {
                foreach (var ip in Network.TailscalePeers.AddressesOf([peer.Name]))
                {
                    var there = new IPEndPoint(ip, peer.DiscoveryPort);
                    if (!result.Contains(there))
                    {
                        result.Add(there);
                    }
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
        var session = new PeerSession(connection, _identity, handshake, _pairingUi, OnPairedAsync, _power, _logger);

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
        if (connection.PinnedTo is { } held)
        {
            _logger.LogInformation("The connection to {Peer} is held to {Interface}; routes that come and go (a VPN) should not move it", session.PeerName, held);
        }

        ForgetIfTheyHaveForgottenUs(session);

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
        ForgetIfTheyHaveForgottenUs(session);
        if (session.State == SessionState.Ready)
        {
            RememberAddress(session);
            SessionReady?.Invoke(session);
        }
    }

    /// <summary>
    /// Forgetting a device used to be one-sided: it was removed here and went on listing this one over there,
    /// offering it in its menus and introducing it to other devices as something it knows. The device that had
    /// been forgotten had no way to find out — and if it was switched off at the time, no way to be told either.
    ///
    /// Nothing new had to be said for it. The handshake already asks both sides whether they have the other
    /// paired, and a session is only Ready when both say yes; one that is merely Unpaired means at least one said
    /// no. So if this device's own answer was yes then the no was theirs, the pairing is over, and keeping half of
    /// it only means offering a device that will refuse.
    ///
    /// Nothing here takes anyone's word for anything new: that answer came over a connection whose certificate
    /// was pinned when the two were paired, and the only thing it can do is end a pairing with the device that
    /// sent it. A device can make this one forget it. It cannot make it forget anyone else.
    ///
    /// Two things have to be true for "their no" to mean "they have forgotten us", and this used to check neither
    /// (see <see cref="WhatUnpairedMeans"/>). It looked at whether the record is there now, not at what this
    /// device answered when the handshake asked; and it took for granted that they had this device paired at some
    /// point, which a device trusted on a third one's word has not, until that word reaches it. Pressing "trust"
    /// on an introduced device and connecting to it before the introduction had travelled back — or with nobody
    /// able to carry it back at all — deleted the record just made and announced that the device had unpaired.
    /// </summary>
    private void ForgetIfTheyHaveForgottenUs(PeerSession session)
    {
        if (session.State != SessionState.Unpaired)
        {
            return;
        }

        var record = _settings.FindPairedByFingerprint(session.PeerFingerprint);
        var pairedHere = record is not null && string.Equals(record.Id, session.PeerId, StringComparison.Ordinal);
        switch (WhatUnpairedMeans(pairedHere, session.SaidPairedInHandshake, record?.AwaitingTheirTrust ?? false))
        {
            case UnpairedMeans.Nothing:
                return;

            case UnpairedMeans.AskAgain:
                // What Trust does to a session that is open when it is called, done for one that was still being
                // made at the time: whether the two are paired is settled in the handshake, so it is asked again.
                _logger.LogInformation(
                    "{Peer} was paired here while this connection was being made; connecting again so that both sides are asked afresh",
                    session.PeerName);
                _ = session.CloseAsync("trusted");
                return;

            case UnpairedMeans.NotTheirsYet:
                _logger.LogInformation(
                    "{Peer} does not have this device paired yet: it was trusted here on another device's word, and that word has not reached it. The pairing is kept",
                    session.PeerName);
                return;
        }

        _settings.PairedPeers.RemoveAll(p => string.Equals(p.Id, session.PeerId, StringComparison.Ordinal));
        _saveSettings();
        _logger.LogInformation(
            "{Peer} no longer has this device paired, so the pairing is forgotten here as well",
            session.PeerName);

        _directory.Update(session.PeerId, info => info.IsPaired = false);
        PairedPeersChanged?.Invoke();
        UnpairedByPeer?.Invoke(session.PeerName);
    }

    /// <summary>What a session that is not paired on both sides says about a pairing this device holds.</summary>
    internal enum UnpairedMeans
    {
        /// <summary>This device has no pairing with the other one: two strangers, and nothing to conclude.</summary>
        Nothing,

        /// <summary>
        /// The pairing here is newer than this session's handshake, in which this device therefore said no itself.
        /// The session says nothing about them; it has to be made again for the question to be asked properly.
        /// </summary>
        AskAgain,

        /// <summary>
        /// They said no, and were never known to have said yes: this device trusted them on a third one's word
        /// and they have not been told yet. Nothing was forgotten, because nothing was there to forget.
        /// </summary>
        NotTheirsYet,

        /// <summary>This device said yes, they said no, and they had it paired once. They have forgotten it.</summary>
        Forgotten,
    }

    /// <summary>
    /// The rule behind <see cref="ForgetIfTheyHaveForgottenUs"/>, apart from it so that it can be read and tested
    /// as a table: a pairing is only ever given up when this device really answered yes in the handshake and the
    /// other one is known to have had it paired before.
    /// </summary>
    internal static UnpairedMeans WhatUnpairedMeans(bool pairedHereNow, bool saidPairedInHandshake, bool awaitingTheirTrust) =>
        !pairedHereNow ? UnpairedMeans.Nothing
        : !saidPairedInHandshake ? UnpairedMeans.AskAgain
        : awaitingTheirTrust ? UnpairedMeans.NotTheirsYet
        : UnpairedMeans.Forgotten;

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

    /// <summary>
    /// Pins a device's certificate without a confirmation of its own, on the word of a device that was confirmed by
    /// hand. False when the record is unusable. Any session already open with that device is closed, because
    /// whether the two sides are paired is settled during the handshake: the reconnect a moment later comes up
    /// ready instead of unpaired.
    ///
    /// <paramref name="awaitingTheirs"/> is for the side that does this first, before the other device has heard
    /// of it (see <see cref="PairedPeer.AwaitingTheirTrust"/>): until that device has said yes once, its no is
    /// not taken to mean that it has forgotten this one.
    /// </summary>
    public bool Trust(string id, string name, string fingerprint, int transportPort, int discoveryPort, IEnumerable<string>? addresses = null, bool awaitingTheirs = false)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fingerprint) || string.Equals(id, _identity.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var record = _settings.FindPairedById(id);
        var sameAsBefore = record is not null && CertificateFingerprint.AreEqual(record.Fingerprint, fingerprint);
        if (record is null)
        {
            record = new PairedPeer { Id = id };
            _settings.PairedPeers.Add(record);
        }

        // A pairing that was already there, by the same certificate, does not start waiting again because it was
        // trusted a second time; and one the other device is known to hold is not waiting, whatever it was before.
        record.AwaitingTheirTrust = awaitingTheirs && (!sameAsBefore || record.AwaitingTheirTrust);
        record.Name = string.IsNullOrWhiteSpace(name) ? id : name;
        record.Fingerprint = fingerprint;
        record.PairedAt = DateTimeOffset.UtcNow;
        record.TransportPort = transportPort > 0 ? transportPort : Protocol.ProtocolConstants.DefaultTransportPort;
        record.DiscoveryPort = discoveryPort > 0 ? discoveryPort : Protocol.ProtocolConstants.DefaultDiscoveryPort;
        foreach (var address in (addresses ?? []).Reverse())
        {
            record.RememberAddress(address);
        }

        _saveSettings();
        _directory.Update(id, info =>
        {
            info.Name = record.Name;
            info.Fingerprint = fingerprint;
            info.IsPaired = true;
        });

        if (TryGetSession(id, out var open) && open.State != SessionState.Ready)
        {
            _ = open.CloseAsync("trusted");
        }

        _logger.LogInformation("Trusting {Peer} ({Fingerprint}) without a confirmation of its own", record.Name, CertificateFingerprint.ToDisplay(fingerprint));
        PairedPeersChanged?.Invoke();
        return true;
    }

    public async Task UnpairAsync(string peerId)
    {
        _settings.PairedPeers.RemoveAll(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
        _saveSettings();
        PairedPeersChanged?.Invoke();

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
        record.AwaitingTheirTrust = false;
        record.PairedAt = DateTimeOffset.UtcNow;
        record.TransportPort = session.PeerTransportPort;
        record.DiscoveryPort = session.PeerDiscoveryPort;
        record.RememberAddress(session.RemoteEndPoint.Address.ToString());
        _saveSettings();

        _directory.Update(session.PeerId, info => info.IsPaired = true);
        PairedPeersChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void RememberAddress(PeerSession session)
    {
        var record = _settings.FindPairedById(session.PeerId);
        if (record is null)
        {
            return;
        }

        // Ready means they said yes: from here on, a no from them is a pairing they have ended.
        record.AwaitingTheirTrust = false;
        record.Name = session.PeerName;
        record.TransportPort = session.PeerTransportPort;
        record.DiscoveryPort = session.PeerDiscoveryPort;
        record.RememberAddress(session.RemoteEndPoint.Address.ToString());
        _saveSettings();
        LearnHardwareAddress(session);
    }

    /// <summary>
    /// Notes the hardware address the device answers with on the local network, while it is there to answer: it
    /// is what will wake it once it is asleep, and by then it cannot be asked. Read from this machine's own
    /// neighbour table, not asked of the other program, so it is learnt whatever version runs there. Nothing is
    /// learnt of a device reached through a VPN: it is nobody's neighbour, and what was known stays.
    /// </summary>
    private void LearnHardwareAddress(PeerSession session)
    {
        var remote = session.RemoteEndPoint.Address;
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        var peerId = session.PeerId;
        var peerName = session.PeerName;
        _ = Task.Run(() =>
        {
            try
            {
                if (Network.WakeOnLan.NeighbourhoodOf(remote, Network.NetworkInterfaceCatalog.Enumerate()) is not { } nic
                    || Network.WakeOnLan.HardwareAddressOf(remote, nic.Address) is not { } address)
                {
                    return;
                }

                var text = Network.WakeOnLan.Format(address);
                var broadcast = nic.Subnet.Broadcast.ToString();
                var record = _settings.FindPairedById(peerId);
                if (record is null || (record.HardwareAddress == text && record.WakeBroadcast == broadcast))
                {
                    return;
                }

                record.HardwareAddress = text;
                record.WakeBroadcast = broadcast;
                _saveSettings();
                _logger.LogInformation("{Peer} can be woken over the local network from here: its adapter is {Address}, on {Network}", peerName, text, nic.Subnet);
            }
            catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or System.Net.Sockets.SocketException or IOException)
            {
                _logger.LogDebug(ex, "The hardware address of {Peer} could not be read", peerName);
            }
        });
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _silentPeers.DisposeAsync().ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseAllAsync("shutdown").ConfigureAwait(false);
        _cts.Dispose();
    }
}
