using WinMirroring.Core.Settings;

namespace WinMirroring.Input.Layout;

/// <summary>
/// Immutable view of the device grid with neighbour lookup. A device takes one cell as a whole, or one cell per
/// monitor (<see cref="LayoutCell.Monitor"/>); a cell without a monitor stands for all its monitors not placed on
/// their own.
/// </summary>
public sealed class DeviceLayout
{
    private readonly Dictionary<(int Column, int Row), LayoutCell> _byCell = [];
    private readonly Dictionary<(string Device, string Monitor), LayoutCell> _byMonitor = [];
    private readonly Dictionary<string, LayoutCell> _firstOfDevice = new(StringComparer.Ordinal);

    private DeviceLayout(IReadOnlyList<LayoutCell> cells)
    {
        Cells = cells;
        foreach (var cell in cells)
        {
            var key = (cell.DeviceId, cell.Monitor ?? string.Empty);
            if (string.IsNullOrEmpty(cell.DeviceId) || _byMonitor.ContainsKey(key) || _byCell.ContainsKey((cell.Column, cell.Row)))
            {
                continue;
            }

            _byMonitor[key] = cell;
            _byCell[(cell.Column, cell.Row)] = cell;
            _firstOfDevice.TryAdd(cell.DeviceId, cell);
        }
    }

    public static DeviceLayout Empty { get; } = new([]);

    public IReadOnlyList<LayoutCell> Cells { get; }

    public bool IsEmpty => _byCell.Count == 0;

    /// <summary>Some device has cells of its own monitors.</summary>
    public bool HasMonitorCells => _byMonitor.Keys.Any(k => k.Monitor.Length > 0);

    public static DeviceLayout FromCells(IEnumerable<LayoutCell> cells) => new(cells.ToList());

    public bool Contains(string deviceId) => _firstOfDevice.ContainsKey(deviceId);

    /// <summary>Cells of one device, in layout order.</summary>
    public IEnumerable<LayoutCell> CellsOf(string deviceId) => _byCell.Values.Where(c => c.DeviceId == deviceId);

    /// <summary>
    /// The cell a monitor of a device belongs to: its own, else the device's whole-device cell. Without a monitor
    /// (an older device does not say which) the whole-device cell, else the device's first cell. Null when the
    /// device is not in the layout or that monitor has no place in it.
    /// </summary>
    public LayoutCell? CellOf(string deviceId, string? monitor)
    {
        if (!string.IsNullOrEmpty(monitor) && _byMonitor.TryGetValue((deviceId, monitor), out var own))
        {
            return own;
        }

        if (_byMonitor.TryGetValue((deviceId, string.Empty), out var whole))
        {
            return whole;
        }

        return string.IsNullOrEmpty(monitor) && _firstOfDevice.TryGetValue(deviceId, out var first) ? first : null;
    }

    /// <summary>The cell on the other side of <paramref name="edge"/> of <paramref name="cell"/>, or null.</summary>
    public LayoutCell? Neighbor(LayoutCell cell, Edge edge)
    {
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

    /// <summary>Device next to <paramref name="deviceId"/> across <paramref name="edge"/> of any of its cells, or null.</summary>
    public string? Neighbor(string deviceId, Edge edge)
    {
        foreach (var cell in CellsOf(deviceId))
        {
            if (Neighbor(cell, edge) is { } next && next.DeviceId != deviceId)
            {
                return next.DeviceId;
            }
        }

        return null;
    }

    public bool TryGetCell(string deviceId, out int column, out int row)
    {
        if (CellOf(deviceId, null) is { } cell)
        {
            (column, row) = (cell.Column, cell.Row);
            return true;
        }

        column = row = 0;
        return false;
    }
}
