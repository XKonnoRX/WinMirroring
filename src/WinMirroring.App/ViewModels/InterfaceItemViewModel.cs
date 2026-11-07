using CommunityToolkit.Mvvm.ComponentModel;
using WinMirroring.Core.Network;

namespace WinMirroring.App.ViewModels;

public sealed partial class InterfaceItemViewModel : ObservableObject
{
    public InterfaceItemViewModel(NetworkInterfaceInfo info, bool isSelected)
    {
        Id = info.Id;
        Name = info.Name;
        Address = $"{info.Address}/{info.Subnet.PrefixLength}";
        Details = info.IsVpnLike ? $"{info.Description} — VPN/туннель" : info.Description;
        IsVpnLike = info.IsVpnLike;
        IsSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public string Address { get; }

    public string Details { get; }

    public bool IsVpnLike { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
