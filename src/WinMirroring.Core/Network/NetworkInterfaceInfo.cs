using System.Net;
using System.Net.NetworkInformation;

namespace WinMirroring.Core.Network;

/// <summary>One IPv4 address of one adapter. An adapter with several addresses yields several entries sharing the same <see cref="Id"/>.</summary>
public sealed record NetworkInterfaceInfo(
    string Id,
    string Name,
    string Description,
    IPAddress Address,
    IPv4Subnet Subnet,
    NetworkInterfaceType Type,
    bool IsLoopback,
    bool IsVpnLike)
{
    public override string ToString() => $"{Name} ({Address}/{Subnet.PrefixLength})";
}
