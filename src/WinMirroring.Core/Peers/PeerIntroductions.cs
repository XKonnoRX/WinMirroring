using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Peers;

/// <summary>A device a trusted peer is paired with, offered so this one can recognise it too.</summary>
public sealed record PeerIntroduction(
    string Id,
    string Name,
    string Fingerprint,
    int TransportPort,
    int DiscoveryPort,
    List<string> Addresses,
    string ViaId,
    string ViaName);

/// <summary>
/// Pairing in a chain. Pairing two devices is a deliberate act — someone confirms the same code on both screens —
/// and with several devices that means doing it again for every pair. But a device that was confirmed by hand is
/// already trusted completely: it can see the screen and move the pointer. So its word on who else it is paired
/// with is worth having.
///
/// Every pair of paired devices tells each other who else they are paired with. Those devices then show up as
/// known through so-and-so, and trusting one takes a single press instead of a second confirmation on both
/// screens: the certificate fingerprint came from a device that was already confirmed, which is what a
/// confirmation establishes in the first place. The press also travels back through the device in the middle, so
/// the far end trusts this one in return and the link works from either side.
/// </summary>
public sealed class PeerIntroductions
{
    private readonly PeerSessionManager _sessions;
    private readonly AppSettings _settings;
    private readonly DeviceIdentity _identity;
    private readonly Func<IEnumerable<string>> _localAddresses;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PeerIntroduction> _known = new(StringComparer.Ordinal);

    public PeerIntroductions(
        PeerSessionManager sessions,
        AppSettings settings,
        DeviceIdentity identity,
        Func<IEnumerable<string>> localAddresses,
        ILogger logger)
    {
        _sessions = sessions;
        _settings = settings;
        _identity = identity;
        _localAddresses = localAddresses;
        _logger = logger;
    }

    /// <summary>A device became known through a peer, or stopped being merely known.</summary>
    public event Action? Changed;

    /// <summary>Devices known through a trusted peer and not yet trusted here.</summary>
    public IReadOnlyList<PeerIntroduction> Snapshot() => _known.Values.ToList();

    public bool TryGet(string peerId, out PeerIntroduction introduction) => _known.TryGetValue(peerId, out introduction!);

    public void Start()
    {
        _sessions.SessionReady += OnSessionReady;
        _sessions.SessionClosed += OnSessionClosed;
        _sessions.PairedPeersChanged += OnPairedPeersChanged;
        foreach (var session in _sessions.Sessions.Where(s => s.State == SessionState.Ready))
        {
            OnSessionReady(session);
        }
    }

    /// <summary>
    /// Trusts a device that a peer vouched for, and asks that peer to tell it about us so the trust is mutual.
    /// False when the device is no longer offered.
    /// </summary>
    public async Task<bool> TrustAsync(string peerId)
    {
        if (!_known.TryGetValue(peerId, out var introduction))
        {
            return false;
        }

        if (!_sessions.Trust(introduction.Id, introduction.Name, introduction.Fingerprint, introduction.TransportPort, introduction.DiscoveryPort, introduction.Addresses))
        {
            return false;
        }

        Forget(peerId);

        // The device in the middle is the only one that can reach the far end right now, and it is paired with
        // both, so it is the one that carries our record across.
        if (_sessions.TryGetSession(introduction.ViaId, out var via) && via.State == SessionState.Ready)
        {
            await SendAsync(via, new VouchRelayMessage(introduction.Id, Self()), "the introduction back").ConfigureAwait(false);
        }

        return true;
    }

    private void OnSessionReady(PeerSession session)
    {
        session.ControlMessageReceived += OnControlMessage;
        _ = SendVouchAsync(session, isReply: false);
    }

    /// <summary>
    /// Pairing with one more device changes what there is to vouch for, and the devices already connected are the
    /// ones that want to hear it: a chain is usually built one link at a time, so without this the end that was
    /// paired first would never learn about the one paired after it.
    /// </summary>
    private void OnPairedPeersChanged()
    {
        foreach (var session in _sessions.Sessions.Where(s => s.State == SessionState.Ready))
        {
            // Marked as a reply, so an update does not set both sides answering each other.
            _ = SendVouchAsync(session, isReply: true);
        }
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;

        // What this peer vouched for is only as good as the link to it.
        var dropped = false;
        foreach (var introduction in _known.Values.Where(i => string.Equals(i.ViaId, session.PeerId, StringComparison.Ordinal)))
        {
            dropped |= _known.TryRemove(introduction.Id, out _);
        }

        if (dropped)
        {
            Changed?.Invoke();
        }
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        switch (message)
        {
            case PeerVouchMessage vouch:
                Record(session, vouch.Peers);
                if (!vouch.IsReply)
                {
                    _ = SendVouchAsync(session, isReply: true);
                }

                break;

            case VouchRelayMessage relay:
                _ = HandleRelayAsync(session, relay);
                break;
        }
    }

    private void Record(PeerSession session, List<VouchedPeer>? peers)
    {
        if (peers is null || !IsTrusted(session))
        {
            return;
        }

        // What this peer says now replaces what it said before, so a device it has since unpaired from stops
        // being offered here too.
        var offered = peers.Where(IsUsable).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var stale in _known.Values.Where(i => string.Equals(i.ViaId, session.PeerId, StringComparison.Ordinal) && !offered.Contains(i.Id)))
        {
            changed |= _known.TryRemove(stale.Id, out _);
        }

        foreach (var peer in peers)
        {
            if (!IsUsable(peer) || _settings.FindPairedById(peer.Id) is not null)
            {
                continue;
            }

            var introduction = new PeerIntroduction(
                peer.Id,
                string.IsNullOrWhiteSpace(peer.Name) ? peer.Id : peer.Name,
                peer.Fingerprint,
                peer.TransportPort,
                peer.DiscoveryPort,
                peer.Addresses ?? [],
                session.PeerId,
                session.PeerName);

            if (_known.TryGetValue(peer.Id, out var existing) && existing == introduction)
            {
                continue;
            }

            _known[peer.Id] = introduction;
            changed = true;
            _logger.LogInformation("{Peer} is known through {Via}", introduction.Name, session.PeerName);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task HandleRelayAsync(PeerSession session, VouchRelayMessage relay)
    {
        if (!IsTrusted(session) || !IsUsable(relay.Peer))
        {
            return;
        }

        if (string.Equals(relay.ToPeerId, _identity.Id, StringComparison.Ordinal))
        {
            // The far end took this peer's word about us and sent its own record back the same way. This peer is
            // trusted here, so its word is good — and it already has every power over this device that trusting
            // one more brings.
            if (_settings.FindPairedById(relay.Peer.Id) is not null)
            {
                return;
            }

            if (_sessions.Trust(relay.Peer.Id, relay.Peer.Name, relay.Peer.Fingerprint, relay.Peer.TransportPort, relay.Peer.DiscoveryPort, relay.Peer.Addresses))
            {
                _logger.LogInformation("{Peer} is trusted from now on: {Via} vouched for it and it accepted us", relay.Peer.Name, session.PeerName);
                Forget(relay.Peer.Id);
            }

            return;
        }

        var mayPass = MayPassOn(
            relay,
            session.PeerId,
            session.PeerFingerprint,
            senderIsPaired: true,
            targetIsPaired: _settings.FindPairedById(relay.ToPeerId) is not null);

        if (!mayPass || !_sessions.TryGetSession(relay.ToPeerId, out var onward) || onward.State != SessionState.Ready)
        {
            _logger.LogDebug("Not passing {Peer}'s introduction on to {To}", session.PeerName, relay.ToPeerId);
            return;
        }

        await SendAsync(onward, relay, $"{session.PeerName}'s introduction").ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a device in the middle of a chain may carry an introduction onward. Both sides have to be paired
    /// with it, and a device may only ever hand on <em>its own</em> record — the one whose certificate it has
    /// already proved on this very connection. Without that last rule a paired device could use the one in the
    /// middle to push any fingerprint it liked into a third device's trust store, which is a far larger power
    /// than being paired was ever meant to grant.
    /// </summary>
    public static bool MayPassOn(VouchRelayMessage relay, string senderId, string senderFingerprint, bool senderIsPaired, bool targetIsPaired) =>
        senderIsPaired
        && targetIsPaired
        && !string.IsNullOrWhiteSpace(relay.ToPeerId)
        && !string.Equals(relay.ToPeerId, senderId, StringComparison.Ordinal)
        && string.Equals(relay.Peer.Id, senderId, StringComparison.Ordinal)
        && CertificateFingerprint.AreEqual(relay.Peer.Fingerprint, senderFingerprint);

    private void Forget(string peerId)
    {
        if (_known.TryRemove(peerId, out _))
        {
            Changed?.Invoke();
        }
    }

    private Task SendVouchAsync(PeerSession session, bool isReply)
    {
        var peers = _settings.PairedPeers
            .Where(p => !string.Equals(p.Id, session.PeerId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Fingerprint))
            .Select(p => new VouchedPeer(p.Id, p.Name, p.Fingerprint, p.TransportPort, p.DiscoveryPort, p.LastKnownAddresses.ToList()))
            .ToList();

        return SendAsync(session, new PeerVouchMessage(peers, isReply), "who else is paired here");
    }

    /// <summary>Our own record, as another device would need it to recognise us.</summary>
    private VouchedPeer Self() => new(
        _identity.Id,
        _settings.DeviceName,
        _identity.Fingerprint,
        _settings.TransportPort,
        _settings.DiscoveryPort,
        _localAddresses().Distinct(StringComparer.Ordinal).ToList());

    /// <summary>A vouch counts only from a device paired with this one — a session alone proves nothing.</summary>
    private bool IsTrusted(PeerSession session) =>
        session.State == SessionState.Ready && _sessions.IsPaired(session.PeerFingerprint, session.PeerId);

    private bool IsUsable(VouchedPeer? peer) =>
        peer is not null
        && !string.IsNullOrWhiteSpace(peer.Id)
        && !string.IsNullOrWhiteSpace(peer.Fingerprint)
        && !string.Equals(peer.Id, _identity.Id, StringComparison.Ordinal);

    private async Task SendAsync(PeerSession session, ControlMessage message, string what)
    {
        try
        {
            await session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Did not tell {Peer} {What}: {Message}", session.PeerName, what, ex.Message);
        }
    }
}
