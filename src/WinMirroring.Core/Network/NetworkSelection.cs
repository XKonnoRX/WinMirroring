using System.Net;

namespace WinMirroring.Core.Network;

/// <summary>The interfaces this node binds to and advertises on, resolved from settings against the live adapter list.</summary>
public sealed class NetworkSelection
{
    private NetworkSelection(IReadOnlyList<NetworkInterfaceInfo> all, IReadOnlyList<NetworkInterfaceInfo> selected, bool includeLoopback)
    {
        AllInterfaces = all;
        SelectedInterfaces = selected;
        IncludeLoopback = includeLoopback;
    }

    public IReadOnlyList<NetworkInterfaceInfo> AllInterfaces { get; }

    public IReadOnlyList<NetworkInterfaceInfo> SelectedInterfaces { get; }

    public bool IncludeLoopback { get; }

    /// <summary>Distinct addresses to bind listeners to.</summary>
    public IReadOnlyList<IPAddress> BindAddresses =>
        SelectedInterfaces.Select(i => i.Address).Distinct().ToList();

    public static NetworkSelection Resolve(IReadOnlyCollection<string> selectedInterfaceIds, bool includeLoopback)
    {
        ArgumentNullException.ThrowIfNull(selectedInterfaceIds);
        var all = NetworkInterfaceCatalog.Enumerate(includeLoopback);
        var selected = selectedInterfaceIds.Count == 0
            ? all.ToList()
            : all.Where(i => (i.IsLoopback && includeLoopback) || selectedInterfaceIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        return new NetworkSelection(all, selected, includeLoopback);
    }

    /// <summary>True when <paramref name="address"/> belongs to one of the selected subnets (or loopback when enabled).</summary>
    public bool IsInSelectedSubnet(IPAddress address)
    {
        if (IncludeLoopback && IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var nic in SelectedInterfaces)
        {
            if (nic.Subnet.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
