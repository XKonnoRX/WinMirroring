using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Discovery;

/// <summary>
/// Announces this node on every selected subnet (directed broadcast + multicast) and listens for other nodes.
/// Peers behind a VPN are reached by unicast probes to manual addresses and to addresses remembered from earlier sessions.
/// Only datagrams from selected subnets or probed addresses are accepted.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    // Windows reports ICMP "port unreachable" as a ConnectionReset on the next receive; this ioctl turns that off.
    private const int SioUdpConnReset = -1744830452;

    private static readonly TimeSpan DnsCacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DnsFailureLifetime = TimeSpan.FromSeconds(15);
    private const long FailureReportInterval = 10 * 60_000;

    private readonly DeviceIdentity _identity;
    private readonly int _discoveryPort;
    private readonly int _transportPort;
    private readonly NetworkSelection _network;
    private readonly Func<IReadOnlyCollection<string>> _manualAddresses;
    private readonly Func<IReadOnlyCollection<IPEndPoint>> _rememberedPeers;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(NetworkInterfaceInfo Nic, Socket Socket)> _senders = [];
    private readonly ConcurrentDictionary<IPAddress, byte> _probedAddresses = new();
    private readonly Dictionary<string, (IPAddress[] Addresses, DateTimeOffset ResolvedAt)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPEndPoint, SendFailure> _sendFailures = [];

    private Socket? _receiver;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    public DiscoveryService(
        DeviceIdentity identity,
        int discoveryPort,
        int transportPort,
        NetworkSelection network,
        Func<IReadOnlyCollection<string>> manualAddresses,
        Func<IReadOnlyCollection<IPEndPoint>> rememberedPeers,
        ILogger logger)
    {
        _identity = identity;
        _discoveryPort = discoveryPort;
        _transportPort = transportPort;
        _network = network;
        _manualAddresses = manualAddresses;
        _rememberedPeers = rememberedPeers;
        _logger = logger;
    }

    public event Action<DiscoveredPeer>? PeerSeen;

    /// <summary>A beacon could not be sent because an interface's address is no longer valid.</summary>
    public event Action? AddressLost;

    /// <summary>True when we actively probed this address, so its answers are welcome even outside selected subnets.</summary>
    public bool IsProbedAddress(IPAddress address) => _probedAddresses.ContainsKey(address);

    public bool IsSourceAllowed(IPAddress address) =>
        _network.IsInSelectedSubnet(address) || IsProbedAddress(address);

    public void Start()
    {
        if (_receiver is not null)
        {
            throw new InvalidOperationException("Discovery is already running.");
        }

        var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.EnableBroadcast = true;
        receiver.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        try
        {
            receiver.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Not fatal: the receive loop also tolerates ConnectionReset.
        }

        foreach (var nic in _network.SelectedInterfaces.Where(n => !n.IsLoopback))
        {
            try
            {
                receiver.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(ProtocolConstants.MulticastGroup, nic.Address));
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Multicast join failed on {Interface}", nic);
            }

            if (nic.Subnet.IsPointToPoint)
            {
                continue;
            }

            try
            {
                var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                sender.Bind(new IPEndPoint(nic.Address, 0));
                try
                {
                    sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, nic.Address.GetAddressBytes());
                    sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Multicast interface option failed on {Interface}", nic);
                }

                _senders.Add((nic, sender));
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Cannot announce on {Interface}", nic);
            }
        }

        _receiver = receiver;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _announceLoop = Task.Run(() => AnnounceLoopAsync(_cts.Token));

        _logger.LogInformation(
            "Discovery listening on UDP {Port}; announcing on {Interfaces}",
            _discoveryPort,
            string.Join(", ", _senders.Select(s => s.Nic.ToString())));
    }

    private Beacon BuildBeacon(bool isProbe) =>
        new(_identity.Id, _identity.Name, _transportPort, _discoveryPort, _identity.Fingerprint, isProbe);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _receiver!.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Discovery receive failed");
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint remote)
            {
                continue;
            }

            var beacon = Beacon.TryDecode(buffer.AsSpan(0, result.ReceivedBytes));
            if (beacon is null || string.Equals(beacon.Id, _identity.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsSourceAllowed(remote.Address))
            {
                _logger.LogDebug("Ignoring beacon from {Address} outside selected subnets", remote.Address);
                continue;
            }

            var peer = new DiscoveredPeer(
                beacon.Id,
                beacon.Name,
                beacon.Fingerprint,
                new IPEndPoint(remote.Address, beacon.TransportPort),
                new IPEndPoint(remote.Address, beacon.DiscoveryPort),
                DateTimeOffset.UtcNow);

            try
            {
                PeerSeen?.Invoke(peer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PeerSeen handler failed for {Peer}", peer.Name);
            }

            if (beacon.IsProbe)
            {
                await SendUnicastAsync(Beacon.Encode(BuildBeacon(isProbe: false)), peer.DiscoveryEndPoint, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProtocolConstants.BeaconInterval);
        do
        {
            try
            {
                await AnnounceOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Announce failed");
            }
        }
        while (await WaitNextTickAsync(timer, ct).ConfigureAwait(false));
    }

    private static async ValueTask<bool> WaitNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task AnnounceOnceAsync(CancellationToken ct)
    {
        var broadcast = Beacon.Encode(BuildBeacon(isProbe: false));
        foreach (var (nic, socket) in _senders)
        {
            await TrySendAsync(socket, broadcast, new IPEndPoint(nic.Subnet.Broadcast, _discoveryPort), ct).ConfigureAwait(false);
            await TrySendAsync(socket, broadcast, new IPEndPoint(ProtocolConstants.MulticastGroup, _discoveryPort), ct).ConfigureAwait(false);
        }

        var probe = Beacon.Encode(BuildBeacon(isProbe: true));
        foreach (var target in await CollectProbeTargetsAsync(ct).ConfigureAwait(false))
        {
            _probedAddresses[target.Address] = 0;
            await SendUnicastAsync(probe, target, ct).ConfigureAwait(false);
        }
    }

    private async Task<List<IPEndPoint>> CollectProbeTargetsAsync(CancellationToken ct)
    {
        var targets = new List<IPEndPoint>();

        foreach (var text in _manualAddresses())
        {
            if (!ManualAddress.TryParse(text, _discoveryPort, out var host, out var port))
            {
                continue;
            }

            foreach (var address in await ResolveAsync(host, ct).ConfigureAwait(false))
            {
                targets.Add(new IPEndPoint(address, port));
            }
        }

        foreach (var remembered in _rememberedPeers())
        {
            if (!targets.Contains(remembered))
            {
                targets.Add(remembered);
            }
        }

        return targets;
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? [literal] : [];
        }

        var now = DateTimeOffset.UtcNow;
        lock (_dnsCache)
        {
            if (_dnsCache.TryGetValue(host, out var cached))
            {
                var lifetime = cached.Addresses.Length > 0 ? DnsCacheLifetime : DnsFailureLifetime;
                if (now - cached.ResolvedAt < lifetime)
                {
                    return cached.Addresses;
                }
            }
        }

        IPAddress[] resolved;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            resolved = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Cannot resolve manual address {Host}", host);
            resolved = [];
        }

        lock (_dnsCache)
        {
            _dnsCache[host] = (resolved, now);
        }

        return resolved;
    }

    private ValueTask SendUnicastAsync(byte[] datagram, IPEndPoint target, CancellationToken ct) =>
        TrySendAsync(_receiver!, datagram, target, ct);

    private async ValueTask TrySendAsync(Socket socket, byte[] datagram, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            await socket.SendToAsync(datagram, SocketFlags.None, target, ct).ConfigureAwait(false);
            NoteSendWorked(target);
        }
        catch (SocketException ex)
        {
            NoteSendFailed(target, ex);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// A beacon goes out every couple of seconds, so a failure that persists (an address that went away with sleep
    /// or a network change) is logged once, then summed up every few minutes, and its recovery is logged too.
    /// </summary>
    private void NoteSendFailed(IPEndPoint target, SocketException ex)
    {
        var now = Environment.TickCount64;
        bool report;
        int count;
        long since;
        lock (_sendFailures)
        {
            if (!_sendFailures.TryGetValue(target, out var failure) || failure.Error != ex.SocketErrorCode)
            {
                failure = new SendFailure(ex.SocketErrorCode, 0, now, now - FailureReportInterval);
            }

            failure = failure with { Count = failure.Count + 1 };
            report = failure.Count == 1 || now - failure.ReportedTicks >= FailureReportInterval;
            if (report)
            {
                failure = failure with { ReportedTicks = now };
            }

            _sendFailures[target] = failure;
            count = failure.Count;
            since = failure.FirstTicks;
        }

        if (!report)
        {
            return;
        }

        if (count == 1)
        {
            _logger.LogInformation("Beacon to {Target} failed: {Error} ({Code}); repeats are counted, not logged", target, ex.Message, (int)ex.SocketErrorCode);
        }
        else
        {
            _logger.LogInformation("Beacon to {Target} still failing: {Count} times in {Minutes:F0} min ({Code})", target, count, (now - since) / 60_000.0, (int)ex.SocketErrorCode);
        }

        if (ex.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            // The interface's address is gone (sleep, another network): the node re-reads its interfaces.
            AddressLost?.Invoke();
        }
    }

    private void NoteSendWorked(IPEndPoint target)
    {
        if (_sendFailures.Count == 0)
        {
            return;
        }

        SendFailure failure;
        lock (_sendFailures)
        {
            if (!_sendFailures.Remove(target, out failure))
            {
                return;
            }
        }

        _logger.LogInformation("Beacon to {Target} works again after {Count} failure(s)", target, failure.Count);
    }

    private readonly record struct SendFailure(SocketError Error, int Count, long FirstTicks, long ReportedTicks);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _receiver?.Close();
        foreach (var (_, socket) in _senders)
        {
            socket.Close();
        }

        foreach (var loop in new[] { _receiveLoop, _announceLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _cts.Dispose();
    }
}
