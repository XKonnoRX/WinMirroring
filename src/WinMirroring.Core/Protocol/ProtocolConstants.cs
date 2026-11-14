using System.Net;

namespace WinMirroring.Core.Protocol;

/// <summary>Wire-level constants shared by every node. Bump <see cref="Version"/> on incompatible changes.</summary>
public static class ProtocolConstants
{
    /// <summary>
    /// 2: timestamped mouse motion and keepalives. 3: pairing by confirmation, no PIN. 4: big messages in parts,
    /// so they stop blocking the channel, and sound packets that can carry the previous frame as a spare.
    /// </summary>
    public const int Version = 4;

    public const int DefaultDiscoveryPort = 47620;
    public const int DefaultTransportPort = 47621;

    /// <summary>Upper bound for a single frame; the file channel chunks above this on its own.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>Host name used for TLS SNI; peers are identified by certificate fingerprint, not by name.</summary>
    public const string TlsTargetHost = "winmirroring";

    public static ReadOnlySpan<byte> BeaconMagic => "WMIR"u8;

    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.47.62");

    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PairingTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
}
