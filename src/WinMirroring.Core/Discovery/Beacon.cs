using System.Text.Json;
using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Discovery;

/// <summary>
/// The datagram every node sends on its selected subnets. Layout: 4-byte magic, 1-byte protocol version, UTF-8 JSON body.
/// A probe is a unicast beacon that asks the receiver to answer with its own beacon (used for manual/VPN addresses).
/// </summary>
public sealed record Beacon(
    string Id,
    string Name,
    int TransportPort,
    int DiscoveryPort,
    string Fingerprint,
    bool IsProbe)
{
    private const int HeaderLength = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Encode(Beacon beacon)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        var body = JsonSerializer.SerializeToUtf8Bytes(beacon, Options);
        var datagram = new byte[HeaderLength + body.Length];
        ProtocolConstants.BeaconMagic.CopyTo(datagram);
        datagram[4] = ProtocolConstants.Version;
        body.CopyTo(datagram, HeaderLength);
        return datagram;
    }

    public static Beacon? TryDecode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length <= HeaderLength
            || !datagram[..4].SequenceEqual(ProtocolConstants.BeaconMagic)
            || datagram[4] != ProtocolConstants.Version)
        {
            return null;
        }

        Beacon? beacon;
        try
        {
            beacon = JsonSerializer.Deserialize<Beacon>(datagram[HeaderLength..], Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (beacon is null
            || string.IsNullOrWhiteSpace(beacon.Id)
            || string.IsNullOrWhiteSpace(beacon.Fingerprint)
            || beacon.TransportPort is <= 0 or > ushort.MaxValue
            || beacon.DiscoveryPort is <= 0 or > ushort.MaxValue)
        {
            return null;
        }

        return beacon;
    }
}
