using WinMirroring.Core.Settings;

namespace WinMirroring.Input.Layout;

/// <summary>Immutable view of the device grid with neighbour lookup.</summary>
public sealed class DeviceLayout
{
    private readonly Dictionary<string, (int Column, int Row)> _byDevice;
    private readonly Dictionary<(int Column, int Row), string> _byCell;

    private DeviceLayout(IReadOnlyList<LayoutCell> cells)
    {
        Cells = cells;
        _byDevice = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        _byCell = [];
        foreach (var cell in cells)
        {
            if (string.IsNullOrEmpty(cell.DeviceId) || _byDevice.ContainsKey(cell.DeviceId) || _byCell.ContainsKey((cell.Column, cell.Row)))
            {
                continue;
            }

            _byDevice[cell.DeviceId] = (cell.Column, cell.Row);
            _byCell[(cell.Column, cell.Row)] = cell.DeviceId;
        }
    }

    public static DeviceLayout Empty { get; } = new([]);

    public IReadOnlyList<LayoutCell> Cells { get; }

    public bool IsEmpty => _byDevice.Count == 0;

    public static DeviceLayout FromCells(IEnumerable<LayoutCell> cells) => new(cells.ToList());

    public bool Contains(string deviceId) => _byDevice.ContainsKey(deviceId);

    public bool TryGetCell(string deviceId, out int column, out int row)
    {
        if (_byDevice.TryGetValue(deviceId, out var cell))
        {
            (column, row) = cell;
            return true;
        }

        column = row = 0;
        return false;
    }

    /// <summary>Device on the other side of <paramref name="edge"/> of <paramref name="deviceId"/>, or null.</summary>
    public string? Neighbor(string deviceId, Edge edge)
    {
        if (!_byDevice.TryGetValue(deviceId, out var cell))
        {
            return null;
        }

        var (dc, dr) = edge switch
        {
            Edge.Left => (-1, 0),
            Edge.Right => (1, 0),
            Edge.Top => (0, -1),
            Edge.Bottom => (0, 1),
            _ => (0, 0),
        };

        return _byCell.TryGetValue((cell.Column + dc, cell.Row + dr), out var neighbor) ? neighbor : null;
    }
}
