using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Settings;

/// <summary>A device we completed PIN pairing with. The fingerprint is pinned for every later connection.</summary>
public sealed class PairedPeer
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset PairedAt { get; set; }

    /// <summary>
    /// True for a device that was trusted here on the word of a third one (see PeerIntroductions) and has not yet
    /// said that it has this device paired in return. Until it has, its "not paired" in a handshake is not news
    /// that it has forgotten this device — it never knew it — and the pairing is kept rather than undone. Cleared
    /// by the first session that comes up ready. A pairing confirmed on both screens never has it, and neither
    /// does one written by a version that did not know the difference, which leaves those behaving as before.
    /// </summary>
    public bool AwaitingTheirTrust { get; set; }

    public int TransportPort { get; set; } = ProtocolConstants.DefaultTransportPort;

    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;

    /// <summary>IPv4 addresses this peer was last reached at, newest first. Probed when the peer is not visible.</summary>
    public List<string> LastKnownAddresses { get; set; } = [];

    /// <summary>
    /// The hardware (MAC) address of the adapter this device was last connected through on a local network, as
    /// "D8-BB-C1-CC-D2-BE", and the broadcast address of that network. Read from this machine's own neighbour
    /// table whenever a session comes up over a local network; empty until then, and left as it was by a session
    /// that comes over a VPN. It is what waking the device over the network needs (see WakeOnLan).
    /// </summary>
    public string HardwareAddress { get; set; } = string.Empty;

    public string WakeBroadcast { get; set; } = string.Empty;

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
