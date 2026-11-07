using System.Net;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Discovery;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;

namespace WinMirroring.Core;

/// <summary>Construction-time options; everything else lives in <see cref="AppSettings"/>.</summary>
public sealed class NodeOptions
{
    public string DataDirectory { get; init; } = SettingsStore.DefaultDataDirectory;

    /// <summary>Bind to loopback too, so several nodes can talk on one machine (tests, CLI).</summary>
    public bool IncludeLoopback { get; init; }

    public string? DeviceName { get; init; }

    public int? DiscoveryPort { get; init; }

    public int? TransportPort { get; init; }

    public IReadOnlyList<string> ManualAddresses { get; init; } = [];

    /// <summary>Fixed PIN for automated tests.</summary>
    public string? PinOverride { get; init; }
}

/// <summary>
/// The whole stage-1 node: identity, settings, network selection, discovery, listener and sessions.
/// The app and the CLI differ only in the <see cref="IPairingUserInterface"/> they pass in.
/// </summary>
public sealed class MirroringNode : IAsyncDisposable
{
    private readonly NodeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _networkGate = new(1, 1);

    private DiscoveryService? _discovery;
    private PeerListener? _listener;

    public MirroringNode(NodeOptions options, IPairingUserInterface pairingUi, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairingUi);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MirroringNode>();

        SettingsStore = new SettingsStore(options.DataDirectory);
        Settings = SettingsStore.Load();

        var changed = false;
        if (options.DeviceName is { Length: > 0 } name && Settings.DeviceName != name)
        {
            Settings.DeviceName = name;
            changed = true;
        }

        if (options.DiscoveryPort is { } discoveryPort && Settings.DiscoveryPort != discoveryPort)
        {
            Settings.DiscoveryPort = discoveryPort;
            changed = true;
        }

        if (options.TransportPort is { } transportPort && Settings.TransportPort != transportPort)
        {
            Settings.TransportPort = transportPort;
            changed = true;
        }

        foreach (var address in options.ManualAddresses)
        {
            if (!Settings.ManualPeerAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                Settings.ManualPeerAddresses.Add(address);
                changed = true;
            }
        }

        changed |= DeviceIdentityStore.LoadOrCreate(Settings, out var identity);
        Identity = identity;
        if (changed)
        {
            SettingsStore.Save(Settings);
        }

        Peers = new PeerDirectory();
        foreach (var paired in Settings.PairedPeers)
        {
            Peers.Update(paired.Id, info =>
            {
                info.Name = paired.Name;
                info.Fingerprint = paired.Fingerprint;
                info.IsPaired = true;
                info.State = PeerState.Offline;
            });
        }

        Sessions = new PeerSessionManager(Identity, Settings, SaveSettings, Peers, pairingUi, loggerFactory.CreateLogger<PeerSessionManager>())
        {
            PinOverride = options.PinOverride,
        };

        Network = NetworkSelection.Resolve(Settings.SelectedInterfaceIds, options.IncludeLoopback);
    }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public DeviceIdentity Identity { get; }

    public PeerDirectory Peers { get; }

    public PeerSessionManager Sessions { get; }

    public NetworkSelection Network { get; private set; }

    public bool IsRunning => _discovery is not null;

    public IReadOnlyList<IPEndPoint> ListeningEndPoints => _listener?.BoundEndPoints ?? [];

    public void SaveSettings() => SettingsStore.Save(Settings);

    public async Task StartAsync()
    {
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            StartNetwork();
        }
        finally
        {
            _networkGate.Release();
        }
    }

    /// <summary>Re-reads interface selection and ports from settings and rebinds. Existing sessions survive.</summary>
    public async Task RestartNetworkAsync()
    {
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopNetworkAsync().ConfigureAwait(false);
            Identity.Name = Settings.DeviceName;
            StartNetwork();
        }
        finally
        {
            _networkGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopNetworkAsync().ConfigureAwait(false);
            await Sessions.CloseAllAsync("shutdown").ConfigureAwait(false);
        }
        finally
        {
            _networkGate.Release();
        }
    }

    public Task<bool> PairAsync(string peerId, CancellationToken ct = default) => Sessions.PairAsync(peerId, ct);

    public Task UnpairAsync(string peerId) => Sessions.UnpairAsync(peerId);

    public bool IsSourceAllowed(IPAddress address) =>
        Network.IsInSelectedSubnet(address)
        || (_discovery?.IsProbedAddress(address) ?? false)
        || Sessions.IsRememberedAddress(address);

    private void StartNetwork()
    {
        Network = NetworkSelection.Resolve(Settings.SelectedInterfaceIds, _options.IncludeLoopback);
        if (Network.SelectedInterfaces.Count == 0)
        {
            _logger.LogWarning("No usable network interface is selected; the node is idle");
        }

        var listener = new PeerListener(
            Network.BindAddresses,
            Settings.TransportPort,
            Sessions.HandleInboundAsync,
            IsSourceAllowed,
            _loggerFactory.CreateLogger<PeerListener>());
        listener.Start();
        _listener = listener;

        var discovery = new DiscoveryService(
            Identity,
            Settings.DiscoveryPort,
            Settings.TransportPort,
            Network,
            () => Settings.ManualPeerAddresses.ToList(),
            Sessions.RememberedDiscoveryEndPoints,
            _loggerFactory.CreateLogger<DiscoveryService>());
        discovery.PeerSeen += Sessions.OnPeerSeen;
        discovery.Start();
        _discovery = discovery;
    }

    private async Task StopNetworkAsync()
    {
        if (_discovery is { } discovery)
        {
            discovery.PeerSeen -= Sessions.OnPeerSeen;
            await discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
        }

        if (_listener is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        _networkGate.Dispose();
    }
}
