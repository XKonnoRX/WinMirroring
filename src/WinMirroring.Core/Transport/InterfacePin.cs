using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinMirroring.Core.Transport;

/// <summary>
/// Holds a connection to the network interface its peer is on.
///
/// A connection is normally sent wherever the machine's routing table says, and is looked up afresh as the table
/// changes. Switching a VPN on or off changes it: a tunnel adapter appears and takes the default route, or more —
/// and a session to a device two metres away, on the same switch, was reset every time, taking the screen being
/// watched with it. Both devices' logs show the session closing a few seconds before either notices its addresses
/// have changed.
///
/// A socket can be told which interface to use regardless of the table (IP_UNICAST_IF). That is done here for a
/// peer that is on one of this machine's own subnets, which is the only case where the right interface is not a
/// matter of opinion. Whether it is enough depends on what the VPN does when it comes up — one that only changes
/// routes will no longer touch the connection; one that actively cuts existing connections still will — so this
/// is an attempt, it can be switched off, and nothing depends on it having worked.
/// </summary>
public static class InterfacePin
{
    // ws2ipdef.h: IP_UNICAST_IF. The interface index goes in network byte order for IPv4.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;

    /// <summary>Off, and connections go wherever the routing table sends them, as they always did.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>One address of one interface of this machine.</summary>
    public sealed record Candidate(string Name, int Index, IPAddress Address, int PrefixLength);

    /// <summary>
    /// The interface <paramref name="remote"/> is directly on: the one whose own subnet contains it, and the
    /// narrowest such subnet when several do. Null when it is on none of them — reached through a router, or
    /// through a tunnel whose far end it is — and then there is nothing to hold the connection to.
    /// </summary>
    public static Candidate? Choose(IEnumerable<Candidate> candidates, IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(remote))
        {
            return null;
        }

        var target = ToUInt32(remote);
        Candidate? best = null;
        foreach (var candidate in candidates)
        {
            // A /32 is a point-to-point or tunnel address: it has no neighbours to be on a subnet with.
            if (candidate.Address.AddressFamily != AddressFamily.InterNetwork || candidate.PrefixLength is < 1 or > 31)
            {
                continue;
            }

            var mask = uint.MaxValue << (32 - candidate.PrefixLength);
            if ((ToUInt32(candidate.Address) & mask) == (target & mask) && (best is null || candidate.PrefixLength > best.PrefixLength))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The addresses of this machine's interfaces that are up.</summary>
    public static IReadOnlyList<Candidate> Local()
    {
        var found = new List<Candidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var index = properties.GetIPv4Properties()?.Index ?? 0;
                if (index <= 0)
                {
                    continue;
                }

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        found.Add(new Candidate(nic.Name, index, address.Address, address.PrefixLength));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
            // No list, no pin: the connection goes by the table.
        }

        return found;
    }

    /// <summary>
    /// Holds <paramref name="socket"/> to the interface <paramref name="remote"/> is on, binding it to that
    /// interface's address first when <paramref name="bind"/> is set (a socket that is about to connect). Returns
    /// what it was held to, for the log, or null when it was not held to anything.
    /// </summary>
    public static string? TryPin(Socket socket, IPAddress remote, bool bind)
    {
        if (!Enabled || Choose(Local(), remote) is not { } pick)
        {
            return null;
        }

        try
        {
            if (bind)
            {
                socket.Bind(new IPEndPoint(pick.Address, 0));
            }

            socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, IPAddress.HostToNetworkOrder(pick.Index));
            return $"{pick.Name} ({pick.Address})";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
