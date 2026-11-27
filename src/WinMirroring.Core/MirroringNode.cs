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

    /// <summary>
    /// How often the machine's own network is looked at. Looked at rather than waited to be told about, which is
    /// the whole of this: the telling used to come from Windows's address-changed event and from a beacon failing
    /// to send, each of which asked for a look in three seconds' time — and asking again reset those three
    /// seconds. A beacon fails every two seconds for as long as the address is wrong, so the look was pushed back
    /// for ever and never happened once. This laptop went on announcing itself on a network it had left five
    /// hours earlier, ignoring every beacon from the machine standing next to it as "outside selected subnets",
    /// while the tablet kept working because it is reached by a manual address and those bypass the check. A
    /// device simply dropping off its own network with nothing in the log is not a thing to leave to an event.
    /// </summary>
    private static readonly TimeSpan LookAtTheNetwork = TimeSpan.FromSeconds(3);

    /// <summary>The same on a battery: a change of network is still noticed within seconds, for almost nothing.</summary>
    private static readonly TimeSpan LookAtTheNetworkOnBattery = TimeSpan.FromSeconds(10);

    /// <summary>Interfaces and addresses the network was started with; a change of these restarts it.</summary>
    private string _networkSignature = string.Empty;
    private Timer? _networkCheck;
    private int _networkRestarts;

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
        if (SettingsStore.Unreadable is { } unreadable)
        {
            // The loudest thing in the log, because of what follows from it: with no settings this device is a
            // new one, and every device it was paired with will be looking for a certificate nobody presents.
            // The old file is out of the way of the save below, so putting it back, mended, undoes all of it.
            if (unreadable.KeptAs is { } kept)
            {
                _logger.LogError(
                    "The settings in {File} could not be read ({Reason}). The file is kept as {Kept}; this device starts as a new one, with a new id and certificate and no paired devices, until that file is mended and put back",
                    SettingsStore.FilePath, unreadable.Reason, kept);
            }
            else
            {
                _logger.LogError(
                    "The settings in {File} could not be read ({Reason}), and the file could not be moved or copied out of the way: it will be written over. This device starts as a new one, with a new id and certificate and no paired devices",
                    SettingsStore.FilePath, unreadable.Reason);
            }
        }

        Transport.InterfacePin.Enabled = Settings.PinConnectionsToInterface;

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

        Power = new Power.PowerMode(Settings);
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

        Sessions = new PeerSessionManager(Identity, Settings, SaveSettings, Peers, pairingUi, Power, loggerFactory.CreateLogger<PeerSessionManager>());
        Introductions = new PeerIntroductions(
            Sessions,
            Settings,
            Identity,
            () => ListeningEndPoints.Select(e => e.Address.ToString()),
            loggerFactory.CreateLogger<PeerIntroductions>());
        Introductions.Start();

        ResolveNetwork = () => NetworkSelection.Resolve(Settings.SelectedInterfaceIds, _options.IncludeLoopback);
        Network = ResolveNetwork();
    }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public DeviceIdentity Identity { get; }

    /// <summary>Whether this device is spending less at the moment, and what that means in numbers.</summary>
    public Power.PowerMode Power { get; }

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

    /// <summary>
    /// Asks for the media datagrams going to this peer to travel ahead of this device's ordinary traffic. That is
    /// all of them, whoever asks: sound and a screen stream's video leave by the one media socket for the one
    /// address, and so cannot be told apart or put in any order between themselves.
    /// </summary>
    public void PrioritizeMedia(IPEndPoint peer) => _media?.Prioritize(peer);

    /// <summary>Gives that priority up again; it lasts until everyone who asked for it has.</summary>
    public void ReleaseMediaPriority(IPEndPoint peer) => _media?.ReleasePriority(peer);

    private void OnMediaPacket(IPEndPoint from, ReadOnlySpan<byte> packet) => MediaPacketReceived?.Invoke(from, packet);

    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>
    /// Renames this device and returns the name it ended up with: the one given, trimmed, or this machine's own
    /// name when nothing was given.
    ///
    /// Both halves of a rename are here because having one without the other looks exactly like the rename not
    /// working. Written to disk, so it survives a restart — it used to be saved only by the button that applies
    /// network settings, two sections away, so a name typed and left alone came back as it was and looked as if
    /// the device refused to be renamed. And put on the identity, which is what the handshake introduces this
    /// device with and what every announcement carries, so peers hear the new name within a beacon or two rather
    /// than at the next restart. Nothing is rebound: announcements are built one at a time from the identity.
    /// </summary>
    public string Rename(string name)
    {
        var wanted = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        if (!string.Equals(Settings.DeviceName, wanted, StringComparison.Ordinal))
        {
            Settings.DeviceName = wanted;
            SaveSettings();
            _logger.LogInformation("This device is now called {Name}", wanted);
        }

        Identity.Name = wanted;
        return wanted;
    }

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
            var every = Power.Every(LookAtTheNetwork, LookAtTheNetworkOnBattery);
            _networkCheck ??= new Timer(_ => _ = CheckNetworkAsync(), null, every, every);
        }
        finally
        {
            _networkGate.Release();
        }
    }

    /// <summary>
    /// The listener and discovery are bound to the interfaces' addresses as they were at start. When an address
    /// changes (after sleep, on another network) they would keep failing, so the network is started again, but only
    /// when the selected interfaces or their addresses really changed. Existing sessions survive.
    ///
    /// Run on a timer of its own rather than from whatever notices an address change. Everything that could notice
    /// one is a thing that repeats — Windows's event comes in bursts, a beacon fails every couple of seconds — and
    /// a look scheduled for "three seconds from the last of those" is a look that never happens. This asks the
    /// machine what its addresses are, regularly, and the answer is cheap.
    /// </summary>
    private async Task CheckNetworkAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (!IsRunning)
            {
                return;
            }

            var signature = Signature(ResolveNetwork());
            if (signature == _networkSignature)
            {
                return;
            }

            _logger.LogInformation("Network addresses changed ({Old} → {New}); restarting discovery and the listener", _networkSignature, signature);
            Interlocked.Increment(ref _networkRestarts);
            await RestartNetworkAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Restarting the network after an address change failed");
        }
        finally
        {
            // Armed again each time, at the interval the power state calls for now — so a device that has just
            // been unplugged starts looking less often without being restarted.
            var every = Power.Every(LookAtTheNetwork, LookAtTheNetworkOnBattery);
            try
            {
                _networkCheck?.Change(every, every);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// What the machine's network looks like now. One place, so that the regular look and the restart it leads to
    /// can never disagree — and so that a test can move a node onto another network, which is the one thing this
    /// is all about and the one thing a test cannot do for real.
    /// </summary>
    internal Func<NetworkSelection> ResolveNetwork { get; set; }

    /// <summary>How many times the network has been started again because the machine's addresses changed.</summary>
    internal int NetworkRestarts => Volatile.Read(ref _networkRestarts);

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
        Network = ResolveNetwork();
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
            Power,
            _loggerFactory.CreateLogger<DiscoveryService>());
        discovery.PeerSeen += Sessions.OnPeerSeen;
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

        if (_networkCheck is { } check)
        {
            await check.DisposeAsync().ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        SettingsStore.Dispose();
        _networkGate.Dispose();
    }
}
