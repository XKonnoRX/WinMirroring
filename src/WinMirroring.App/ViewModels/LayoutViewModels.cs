using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMirroring.App.ViewModels;

/// <summary>
/// An entry in the device combo boxes: a device, one monitor of a device (layout only), or the empty choice with an
/// empty id.
/// </summary>
public sealed partial class DeviceChoice : ObservableObject
{
    public DeviceChoice(string id, string name, string? monitor = null)
    {
        Id = id;
        Name = name;
        Monitor = monitor;
    }

    public string Id { get; }

    /// <summary>The monitor's key for a single-monitor entry, null for the whole device.</summary>
    public string? Monitor { get; }

    public bool IsEmpty => Id.Length == 0;

    [ObservableProperty]
    public partial string Name { get; set; }
}

/// <summary>One cell of the layout grid.</summary>
public sealed partial class LayoutCellViewModel : ObservableObject
{
    public LayoutCellViewModel(int column, int row)
    {
        Column = column;
        Row = row;
    }

    public int Column { get; }

    public int Row { get; }

    [ObservableProperty]
    public partial DeviceChoice? Selected { get; set; }
}
