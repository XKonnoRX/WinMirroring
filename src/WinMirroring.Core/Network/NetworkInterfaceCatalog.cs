using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WinMirroring.Core.Network;

/// <summary>Enumerates usable IPv4 interfaces and flags the ones that look like VPN tunnels.</summary>
public static partial class NetworkInterfaceCatalog
{
    public static IReadOnlyList<NetworkInterfaceInfo> Enumerate(bool includeLoopback = false)
    {
        var result = new List<NetworkInterfaceInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var isLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            if (isLoopback && !includeLoopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var isVpnLike = !isLoopback && (
                nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                || VpnPattern().IsMatch(nic.Name)
                || VpnPattern().IsMatch(nic.Description));

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IsLinkLocal(address))
                {
                    continue;
                }

                var prefix = unicast.PrefixLength is >= 0 and <= 32 ? unicast.PrefixLength : 32;
                result.Add(new NetworkInterfaceInfo(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    address,
                    new IPv4Subnet(address, prefix),
                    nic.NetworkInterfaceType,
                    isLoopback,
                    isVpnLike));
            }
        }

        return result;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) && bytes[0] == 169 && bytes[1] == 254;
    }

    [GeneratedRegex(@"wireguard|tailscale|zerotier|openvpn|hamachi|radmin|\btap\b|\btun\b|vpn", RegexOptions.IgnoreCase)]
    private static partial Regex VpnPattern();
}
