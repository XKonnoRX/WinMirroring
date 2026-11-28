using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinMirroring.Core.Network;

/// <summary>
/// Waking a device that is asleep, from another one on the same local network.
///
/// A sleeping device has no program running to be asked anything. What still listens is its network adapter, for
/// one thing only: a "magic packet" — six bytes of 0xFF and then the adapter's own hardware (MAC) address sixteen
/// times over, anywhere inside any frame that reaches it. An adapter set up to wake its machine does so on seeing
/// that. Nothing in it is secret and nothing is answered: it is sent, and the device either comes back on the
/// network a few seconds later or does not.
///
/// So two things are needed, and both have to be had beforehand. The hardware address — which is not asked of the
/// other program but read from this machine's own neighbour table (ARP) while the device is up and connected, so
/// that it works whatever version runs over there. And somewhere to send to: a sleeping device does not answer
/// for its address, so the packet goes to everybody on the network it was last seen on (broadcast), from every
/// adapter this machine has on a local network, and to the address it last had as well, for an adapter that still
/// answers for it in its sleep (Wi-Fi in connected standby does).
///
/// It reaches as far as a broadcast does: the same local network. Not through a VPN, not across a router.
/// </summary>
public static class WakeOnLan
{
    /// <summary>The ports a magic packet is by custom sent to. Nothing listens on either; the adapter reads the frame itself.</summary>
    public static readonly IReadOnlyList<int> Ports = [9, 7];

    private const int HardwareAddressLength = 6;

    /// <summary>The packet for <paramref name="hardwareAddress"/>: 6 × 0xFF, then the address 16 times.</summary>
    public static byte[] MagicPacket(ReadOnlySpan<byte> hardwareAddress)
    {
        if (hardwareAddress.Length != HardwareAddressLength)
        {
            throw new ArgumentException("A hardware address is six bytes.", nameof(hardwareAddress));
        }

        var packet = new byte[6 + (16 * HardwareAddressLength)];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            hardwareAddress.CopyTo(packet.AsSpan(6 + (i * HardwareAddressLength)));
        }

        return packet;
    }

    /// <summary>Reads "D8-BB-C1-CC-D2-BE", "d8:bb:c1:cc:d2:be" or "D8BBC1CCD2BE". An address of all zeros or all ones is nobody's.</summary>
    public static bool TryParse(string? text, out byte[] hardwareAddress)
    {
        hardwareAddress = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = text.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        if (digits.Length != HardwareAddressLength * 2)
        {
            return false;
        }

        var bytes = new byte[HardwareAddressLength];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(digits.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        if (!IsSomebodys(bytes))
        {
            return false;
        }

        hardwareAddress = bytes;
        return true;
    }

    /// <summary>"D8-BB-C1-CC-D2-BE", the way Windows writes it.</summary>
    public static string Format(ReadOnlySpan<byte> hardwareAddress) => Convert.ToHexString(hardwareAddress) is { Length: 12 } hex
        ? string.Join('-', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
        : string.Empty;

    private static bool IsSomebodys(ReadOnlySpan<byte> hardwareAddress) =>
        hardwareAddress.Length == HardwareAddressLength && hardwareAddress.ContainsAnyExcept((byte)0) && hardwareAddress.ContainsAnyExcept((byte)0xFF);

    /// <summary>
    /// The adapter of this machine that <paramref name="remote"/> is a neighbour of: one on a local network (not a
    /// VPN, not a point-to-point link) whose subnet has that address in it. Null when it is reached some other
    /// way — and then there is no hardware address to learn, and no broadcast that would reach it.
    /// </summary>
    public static NetworkInterfaceInfo? NeighbourhoodOf(IPAddress remote, IEnumerable<NetworkInterfaceInfo> interfaces)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return interfaces.FirstOrDefault(i => IsLocalNetwork(i) && i.Subnet.Contains(remote) && !i.Address.Equals(remote));
    }

    /// <summary>An adapter a broadcast can be sent on and a neighbour found on: Ethernet or Wi-Fi, with a subnet wider than one link.</summary>
    public static bool IsLocalNetwork(NetworkInterfaceInfo nic) =>
        !nic.IsLoopback
        && !nic.IsVpnLike
        && !nic.Subnet.IsPointToPoint
        && nic.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;

    /// <summary>
    /// The hardware address <paramref name="remote"/> answers with on the local network, asked from
    /// <paramref name="local"/>. Null when nothing answers. This waits for the answer (a few seconds at most when
    /// there is none); for a device that has just been talked to it is in the table already and comes at once.
    /// </summary>
    public static byte[]? HardwareAddressOf(IPAddress remote, IPAddress local)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var address = new byte[HardwareAddressLength];
        var length = address.Length;
        int error;
        try
        {
#pragma warning disable CS0618 // IPAddress.Address: the one place that wants the address as the 32 bits SendARP takes
            error = SendARP((uint)remote.Address, (uint)local.Address, address, ref length);
#pragma warning restore CS0618
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return error == 0 && length == HardwareAddressLength && IsSomebodys(address) ? address : null;
    }

    /// <summary>Where a packet goes from, and to. <see cref="From"/> null: wherever the routing table says.</summary>
    public readonly record struct Shot(IPAddress? From, IPEndPoint To);

    /// <summary>
    /// Everywhere the packet for one device is sent: the broadcast address of every local network this machine is
    /// on and the address for "everybody here", each from the adapter that is on that network; the broadcast of
    /// the network the device was last seen on, in case this machine is not on it under that adapter any more;
    /// and the addresses the device last had, where those are neighbours.
    /// </summary>
    public static IReadOnlyList<Shot> Shots(
        IEnumerable<NetworkInterfaceInfo> interfaces,
        string? rememberedBroadcast,
        IEnumerable<string> lastKnownAddresses,
        IReadOnlyList<int>? ports = null)
    {
        ports ??= Ports;
        var local = interfaces.Where(IsLocalNetwork).ToList();
        var shots = new List<Shot>();

        void Add(IPAddress? from, IPAddress to)
        {
            foreach (var port in ports)
            {
                var shot = new Shot(from, new IPEndPoint(to, port));
                if (!shots.Contains(shot))
                {
                    shots.Add(shot);
                }
            }
        }

        foreach (var nic in local)
        {
            Add(nic.Address, nic.Subnet.Broadcast);
            Add(nic.Address, IPAddress.Broadcast);
        }

        if (IPAddress.TryParse(rememberedBroadcast, out var remembered)
            && remembered.AddressFamily == AddressFamily.InterNetwork
            && !local.Any(n => n.Subnet.Broadcast.Equals(remembered)))
        {
            Add(null, remembered);
        }

        foreach (var text in lastKnownAddresses)
        {
            if (IPAddress.TryParse(text, out var last) && NeighbourhoodOf(last, local) is { } nic)
            {
                Add(nic.Address, last);
            }
        }

        return shots;
    }

    /// <summary>
    /// Sends <paramref name="packet"/> along every one of <paramref name="shots"/>, <paramref name="times"/> over
    /// (a frame to a sleeping adapter, on Wi-Fi above all, is easily lost, and a second one costs nothing).
    /// Returns how many of the shots went out at least once.
    /// </summary>
    public static async Task<int> SendAsync(byte[] packet, IReadOnlyList<Shot> shots, int times = 3, CancellationToken ct = default)
    {
        var went = new bool[shots.Count];
        for (var round = 0; round < times; round++)
        {
            if (round > 0)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            for (var i = 0; i < shots.Count; i++)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                    if (shots[i].From is { } from)
                    {
                        // Bound to the adapter's own address: "everybody here" otherwise leaves by one adapter
                        // only, whichever the routing table likes best.
                        socket.Bind(new IPEndPoint(from, 0));
                    }

                    await socket.SendToAsync(packet, SocketFlags.None, shots[i].To, ct).ConfigureAwait(false);
                    went[i] = true;
                }
                catch (SocketException)
                {
                    // That adapter has gone, or that network is not reachable from here: the others still are.
                }
            }
        }

        return went.Count(w => w);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationAddress, uint sourceAddress, byte[] hardwareAddress, ref int hardwareAddressLength);
}
