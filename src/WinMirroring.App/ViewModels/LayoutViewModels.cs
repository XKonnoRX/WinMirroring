using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMirroring.App.ViewModels;

/// <summary>An entry in the layout combo boxes: a device, or the empty choice with an empty id.</summary>
public sealed partial class DeviceChoice : ObservableObject
{
    public DeviceChoice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public bool IsEmpty => Id.Length == 0;

    [ObservableProperty]
    public partial string Name { get; set; }
}

/// <summary>One cell of the 3×3 layout grid.</summary>
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
