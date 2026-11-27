using System.Net;

namespace WinMirroring.Core.Peers;

public enum PeerState
{
    /// <summary>Known (paired, or seen earlier) but not heard on the network now.</summary>
    Offline,

    /// <summary>Beacons arrive, no session. Taken back once they stop (see <see cref="PeerDirectory.ExpireUnseen"/>).</summary>
    Discovered,

    Connecting,

    /// <summary>Session established, not paired on both sides yet.</summary>
    Connected,

    Pairing,

    /// <summary>Paired on both sides; feature channels are open.</summary>
    Ready,
}

/// <summary>Everything the UI shows about one peer. Mutated only through <see cref="PeerDirectory"/>.</summary>
public sealed class PeerInfo
{
    public PeerInfo(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public string Name { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public bool IsPaired { get; set; }

    public PeerState State { get; set; } = PeerState.Offline;

    public IPEndPoint? TransportEndPoint { get; set; }

    public IPEndPoint? DiscoveryEndPoint { get; set; }

    public DateTimeOffset? LastSeen { get; set; }

    public string? LastError { get; set; }

    public bool HasSession => State is PeerState.Connected or PeerState.Pairing or PeerState.Ready;

    public PeerInfo Clone() => new(Id)
    {
        Name = Name,
        Fingerprint = Fingerprint,
        IsPaired = IsPaired,
        State = State,
        TransportEndPoint = TransportEndPoint,
        DiscoveryEndPoint = DiscoveryEndPoint,
        LastSeen = LastSeen,
        LastError = LastError,
    };
}
