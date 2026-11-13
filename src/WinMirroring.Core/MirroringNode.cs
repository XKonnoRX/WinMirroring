using System.Net;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Discovery;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Media;
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
    private int _disposed;

    private DiscoveryService? _discovery;
    private PeerListener? _listener;
    private MediaSocket? _media;

    /// <summary>Interfaces and addresses the network was started with; a change of these restarts it.</summary>
    private string _networkSignature = string.Empty;
    private Timer? _networkCheck;
    private bool _watchingAddresses;

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

        Sessions = new PeerSessionManager(Identity, Settings, SaveSettings, Peers, pairingUi, loggerFactory.CreateLogger<PeerSessionManager>());
        Introductions = new PeerIntroductions(
            Sessions,
            Settings,
            Identity,
            () => ListeningEndPoints.Select(e => e.Address.ToString()),
            loggerFactory.CreateLogger<PeerIntroductions>());
        Introductions.Start();

        Network = NetworkSelection.Resolve(Settings.SelectedInterfaceIds, options.IncludeLoopback);
    }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public DeviceIdentity Identity { get; }

    public PeerDirectory Peers { get; }

    public PeerSessionManager Sessions { get; }

    /// <summary>Devices known through a paired peer, which can be trusted without a confirmation of their own.</summary>
    public PeerIntroductions Introductions { get; }

    public NetworkSelection Network { get; private set; }

    public bool IsRunning => _discovery is not null;

    public IReadOnlyList<IPEndPoint> ListeningEndPoints => _listener?.BoundEndPoints ?? [];

    /// <summary>UDP port of the media socket, or 0 when it is not running.</summary>
    public int MediaPort => _media?.Port ?? 0;

    /// <summary>Every UDP media datagram; stream owners authenticate and filter.</summary>
    public event MediaPacketHandler? MediaPacketReceived;

    /// <summary>Sends a sealed media datagram; false when the media socket is not running.</summary>
    public bool SendMedia(IPEndPoint to, ReadOnlySpan<byte> datagram)
    {
        var media = _media;
        if (media is null)
        {
            return false;
        }

        media.Send(to, datagram);
        return true;
    }

    private void OnMediaPacket(IPEndPoint from, ReadOnlySpan<byte> packet) => MediaPacketReceived?.Invoke(from, packet);

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
            if (!_watchingAddresses)
            {
                _watchingAddresses = true;
                _networkCheck = new Timer(_ => _ = CheckNetworkAsync(), null, Timeout.Infinite, Timeout.Infinite);
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
            }
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => ScheduleNetworkCheck();

    /// <summary>Address changes come in bursts (sleep, Wi-Fi, a VPN reconnecting): look once things settle.</summary>
    private void ScheduleNetworkCheck()
    {
        try
        {
            _networkCheck?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The listener and discovery are bound to the interfaces' addresses as they were at start. When an address
    /// changes (after sleep, on another network) they would keep failing, so the network is started again, but only
    /// when the selected interfaces or their addresses really changed. Existing sessions survive.
    /// </summary>
    private async Task CheckNetworkAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsRunning)
        {
            return;
        }

        try
        {
            var fresh = NetworkSelection.Resolve(Settings.SelectedInterfaceIds, _options.IncludeLoopback);
            var signature = Signature(fresh);
            if (signature == _networkSignature)
            {
                return;
            }

            _logger.LogInformation("Network addresses changed ({Old} → {New}); restarting discovery and the listener", _networkSignature, signature);
            await RestartNetworkAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Restarting the network after an address change failed");
        }
    }

    private static string Signature(NetworkSelection network) =>
        string.Join("; ", network.SelectedInterfaces.Select(i => $"{i.Name} {i.Address}").Order(StringComparer.Ordinal));

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
        _networkSignature = Signature(Network);
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
        discovery.AddressLost += ScheduleNetworkCheck;
        discovery.Start();
        _discovery = discovery;

        try
        {
            var media = new MediaSocket(Settings.TransportPort, _loggerFactory.CreateLogger<MediaSocket>());
            media.PacketReceived += OnMediaPacket;
            media.Start();
            _media = media;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            _logger.LogWarning(ex, "UDP media port {Port} is unavailable; sound cannot be received", Settings.TransportPort);
        }
    }

    private async Task StopNetworkAsync()
    {
        if (_media is { } media)
        {
            media.PacketReceived -= OnMediaPacket;
            _media = null;
            await media.DisposeAsync().ConfigureAwait(false);
        }

        if (_discovery is { } discovery)
        {
            discovery.PeerSeen -= Sessions.OnPeerSeen;
            discovery.AddressLost -= ScheduleNetworkCheck;
            await discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
        }

        if (_listener is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }

    /// <summary>Safe to call more than once: the app disposes it itself, then its service container does again.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        if (_networkCheck is { } check)
        {
            await check.DisposeAsync().ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        _networkGate.Dispose();
    }
}
