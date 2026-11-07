using System.Net;

namespace WinMirroring.Core.Discovery;

/// <summary>A beacon we accepted, resolved against the address it arrived from.</summary>
public sealed record DiscoveredPeer(
    string Id,
    string Name,
    string Fingerprint,
    IPEndPoint TransportEndPoint,
    IPEndPoint DiscoveryEndPoint,
    DateTimeOffset SeenAt);
