using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Settings;

/// <summary>A device we completed PIN pairing with. The fingerprint is pinned for every later connection.</summary>
public sealed class PairedPeer
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset PairedAt { get; set; }

    public int TransportPort { get; set; } = ProtocolConstants.DefaultTransportPort;

    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;

    /// <summary>IPv4 addresses this peer was last reached at, newest first. Probed when the peer is not visible.</summary>
    public List<string> LastKnownAddresses { get; set; } = [];

    public void RememberAddress(string address, int keep = 5)
    {
        LastKnownAddresses.Remove(address);
        LastKnownAddresses.Insert(0, address);
        if (LastKnownAddresses.Count > keep)
        {
            LastKnownAddresses.RemoveRange(keep, LastKnownAddresses.Count - keep);
        }
    }
}
