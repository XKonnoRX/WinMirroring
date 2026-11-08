using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Settings;

/// <summary>Persisted per-device configuration. Serialized as JSON in the data directory.</summary>
public sealed class AppSettings
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Thumbprint of the identity certificate in CurrentUser\My.</summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;

    public int TransportPort { get; set; } = ProtocolConstants.DefaultTransportPort;

    /// <summary>Network interface ids to use. Empty means every interface that is up.</summary>
    public List<string> SelectedInterfaceIds { get; set; } = [];

    /// <summary>Peers that cannot be found by broadcast (VPN): "host" or "host:discoveryPort".</summary>
    public List<string> ManualPeerAddresses { get; set; } = [];

    public List<PairedPeer> PairedPeers { get; set; } = [];

    public bool StartWithWindows { get; set; }

    /// <summary>Check GitHub Releases and install new versions automatically (when the app is idle).</summary>
    public bool AutoUpdate { get; set; } = true;

    public InputSettings Input { get; set; } = new();

    public ClipboardSettings Clipboard { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public ScreenSettings Screen { get; set; } = new();

    public PairedPeer? FindPairedById(string peerId) =>
        PairedPeers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));

    public PairedPeer? FindPairedByFingerprint(string fingerprint) =>
        PairedPeers.FirstOrDefault(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
}
