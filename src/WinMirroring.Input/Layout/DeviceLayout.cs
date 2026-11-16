using System.Drawing;
using WinMirroring.Core.Settings;

namespace WinMirroring.Input.Layout;

/// <summary>Where the cursor goes on: which device, onto which of its monitors, through which edge and where along it.</summary>
public readonly record struct Crossing(string DeviceId, string? Monitor, Edge Entry, float Relative);

/// <summary>One monitor of a device as it stands on the field.</summary>
public sealed record PlacedMonitor(string DeviceId, string Key, Rectangle Bounds);

/// <summary>
/// Immutable view of where the devices stand, and the one question the input side asks of it: crossing this edge
/// here, where does the cursor come out?
///
/// Two ways of standing. The free one (<see cref="LayoutPlacement"/>): every monitor of every device is a rectangle
/// on one field, and the cursor crosses only where an edge of one device meets an edge of another, along the part
/// they share, coming out at the same height (or the same horizontal position) it left at — Windows' own rule for
/// its monitors. The grid of older versions (<see cref="LayoutCell"/>): a device or a monitor takes a square, and a
/// whole edge leads to the whole edge of the square next to it, proportionally. A layout made by an older device
/// has only the grid, and is followed as such until it is saved again from here.
/// </summary>
public sealed class DeviceLayout
{
    /// <summary>How far apart two edges may be and still meet: what a hand dragging a device leaves between them.</summary>
    public const int EdgeTolerance = 48;

    /// <summary>The size a device is taken to have when nothing says what its monitors are.</summary>
    public static readonly Size DefaultScreen = new(1920, 1080);

    private readonly Dictionary<(int Column, int Row), LayoutCell> _byCell = [];
    private readonly Dictionary<(string Device, string Monitor), LayoutCell> _byMonitor = [];
    private readonly Dictionary<string, LayoutCell> _firstOfDevice = new(StringComparer.Ordinal);
    private readonly List<PlacedMonitor> _placed = [];
    private readonly HashSet<string> _placedDevices = new(StringComparer.Ordinal);

    private DeviceLayout(IReadOnlyList<LayoutCell> cells, IReadOnlyList<LayoutPlacement> placements, Func<string, IReadOnlyList<LayoutRect>?>? monitorsOf)
    {
        Cells = cells;
        Placements = placements;
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

        foreach (var placement in placements)
        {
            if (string.IsNullOrEmpty(placement.DeviceId) || !_placedDevices.Add(placement.DeviceId))
            {
                continue;
            }

            foreach (var rect in MonitorsOf(placement, monitorsOf))
            {
                _placed.Add(new PlacedMonitor(
                    placement.DeviceId,
                    rect.Key,
                    new Rectangle(placement.X + rect.Left, placement.Y + rect.Top, Math.Max(1, rect.Width), Math.Max(1, rect.Height))));
            }
        }
    }

    public static DeviceLayout Empty { get; } = new([], [], null);

    public IReadOnlyList<LayoutCell> Cells { get; }

    public IReadOnlyList<LayoutPlacement> Placements { get; }

    /// <summary>Every monitor on the field (free placement only).</summary>
    public IReadOnlyList<PlacedMonitor> Monitors => _placed;

    /// <summary>The devices stand freely, not on the grid.</summary>
    public bool IsFree => _placedDevices.Count > 0;

    public bool IsEmpty => _byCell.Count == 0 && !IsFree;

    /// <summary>Some device has cells of its own monitors.</summary>
    public bool HasMonitorCells => _byMonitor.Keys.Any(k => k.Monitor.Length > 0);

    public static DeviceLayout FromCells(IEnumerable<LayoutCell> cells) => new(cells.ToList(), [], null);

    /// <summary>
    /// The free placement when there is one, else the grid. <paramref name="monitorsOf"/> answers for the monitors of
    /// a device as they are now (null: not known, and the copy kept in the placement is used).
    /// </summary>
    public static DeviceLayout From(
        IEnumerable<LayoutCell> cells,
        IEnumerable<LayoutPlacement>? placements,
        Func<string, IReadOnlyList<LayoutRect>?>? monitorsOf) =>
        new(cells.ToList(), placements?.ToList() ?? [], monitorsOf);

    public bool Contains(string deviceId) => IsFree ? _placedDevices.Contains(deviceId) : _firstOfDevice.ContainsKey(deviceId);

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

    /// <summary>
    /// Where the cursor comes out when it leaves <paramref name="deviceId"/> through <paramref name="edge"/> of
    /// <paramref name="monitor"/>, at <paramref name="relativeOnMonitor"/> along that monitor's edge (or
    /// <paramref name="relativeOnScreen"/> along the whole desktop's, for a device that does not say which monitor).
    /// Null when nothing is there — on the free field, also when something is next to that edge but not at that
    /// point of it.
    /// </summary>
    public Crossing? Across(string deviceId, string? monitor, Edge edge, float relativeOnMonitor, float relativeOnScreen)
    {
        if (!IsFree)
        {
            var from = CellOf(deviceId, monitor);
            var next = from is null ? null : Neighbor(from, edge);
            if (from is null || next is null || next.DeviceId == deviceId)
            {
                return null;
            }

            return new Crossing(next.DeviceId, next.Monitor, EdgeMath.Opposite(edge), from.Monitor is null ? relativeOnScreen : relativeOnMonitor);
        }

        var own = _placed.Where(m => m.DeviceId == deviceId).ToList();
        if (own.Count == 0)
        {
            return null;
        }

        var source = own.FirstOrDefault(m => !string.IsNullOrEmpty(monitor) && m.Key == monitor);
        var (bounds, relative) = source is not null ? (source.Bounds, relativeOnMonitor) : (Union(own), relativeOnScreen);
        var along = AlongAt(bounds, edge, relative);
        return CrossFrom(deviceId, bounds, edge, along);
    }

    /// <summary>
    /// The monitor of another device that meets <paramref name="bounds"/> at <paramref name="edge"/> where the cursor
    /// is (<paramref name="along"/>: its y for a side edge, its x for the top or bottom), on the free field.
    /// </summary>
    private Crossing? CrossFrom(string deviceId, Rectangle bounds, Edge edge, int along)
    {
        PlacedMonitor? best = null;
        var bestGap = int.MaxValue;
        foreach (var candidate in _placed)
        {
            if (candidate.DeviceId == deviceId)
            {
                continue;
            }

            var c = candidate.Bounds;
            var (gap, covers) = edge switch
            {
                Edge.Right => (c.Left - bounds.Right, along >= c.Top && along < c.Bottom),
                Edge.Left => (bounds.Left - c.Right, along >= c.Top && along < c.Bottom),
                Edge.Bottom => (c.Top - bounds.Bottom, along >= c.Left && along < c.Right),
                _ => (bounds.Top - c.Bottom, along >= c.Left && along < c.Right),
            };

            if (covers && Math.Abs(gap) <= EdgeTolerance && Math.Abs(gap) < bestGap)
            {
                best = candidate;
                bestGap = Math.Abs(gap);
            }
        }

        if (best is null)
        {
            return null;
        }

        var entry = EdgeMath.Opposite(edge);
        var target = best.Bounds;
        var relative = edge is Edge.Left or Edge.Right
            ? (along - target.Top) / (float)Math.Max(1, target.Height - 1)
            : (along - target.Left) / (float)Math.Max(1, target.Width - 1);
        return new Crossing(best.DeviceId, best.Key.Length > 0 ? best.Key : null, entry, Math.Clamp(relative, 0f, 1f));
    }

    private static int AlongAt(Rectangle bounds, Edge edge, float relative)
    {
        relative = Math.Clamp(relative, 0f, 1f);
        return edge is Edge.Left or Edge.Right
            ? bounds.Top + (int)MathF.Round(relative * Math.Max(0, bounds.Height - 1))
            : bounds.Left + (int)MathF.Round(relative * Math.Max(0, bounds.Width - 1));
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

    /// <summary>Device next to <paramref name="deviceId"/> across <paramref name="edge"/> of any of its cells or monitors, or null.</summary>
    public string? Neighbor(string deviceId, Edge edge)
    {
        if (IsFree)
        {
            foreach (var mine in _placed.Where(m => m.DeviceId == deviceId))
            {
                foreach (var other in _placed.Where(m => m.DeviceId != deviceId))
                {
                    if (Meets(mine.Bounds, other.Bounds, edge))
                    {
                        return other.DeviceId;
                    }
                }
            }

            return null;
        }

        foreach (var cell in CellsOf(deviceId))
        {
            if (Neighbor(cell, edge) is { } next && next.DeviceId != deviceId)
            {
                return next.DeviceId;
            }
        }

        return null;
    }

    /// <summary>
    /// Which way <paramref name="otherId"/> lies from <paramref name="deviceId"/>: the edge they meet at, or, when
    /// they do not meet, the side the other one is mostly towards. Null when either is not on the field.
    /// </summary>
    public Edge? DirectionOf(string deviceId, string otherId)
    {
        foreach (var edge in (ReadOnlySpan<Edge>)[Edge.Right, Edge.Left, Edge.Top, Edge.Bottom])
        {
            if (Neighbor(deviceId, edge) == otherId)
            {
                return edge;
            }
        }

        if (!IsFree)
        {
            var a = CellOf(deviceId, null);
            var b = CellOf(otherId, null);
            if (a is null || b is null)
            {
                return null;
            }

            return Dominant(b.Column - a.Column, b.Row - a.Row);
        }

        var mine = _placed.Where(m => m.DeviceId == deviceId).ToList();
        var theirs = _placed.Where(m => m.DeviceId == otherId).ToList();
        if (mine.Count == 0 || theirs.Count == 0)
        {
            return null;
        }

        var x = Union(mine);
        var y = Union(theirs);
        return Dominant((y.Left + (y.Width / 2.0)) - (x.Left + (x.Width / 2.0)), (y.Top + (y.Height / 2.0)) - (x.Top + (x.Height / 2.0)));
    }

    private static Edge? Dominant(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return null;
        }

        return Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? Edge.Right : Edge.Left) : (dy > 0 ? Edge.Bottom : Edge.Top);
    }

    /// <summary><paramref name="b"/> stands across <paramref name="edge"/> of <paramref name="a"/>, sharing some of it.</summary>
    private static bool Meets(Rectangle a, Rectangle b, Edge edge) => edge switch
    {
        Edge.Right => Math.Abs(b.Left - a.Right) <= EdgeTolerance && Overlap(a.Top, a.Bottom, b.Top, b.Bottom) > 0,
        Edge.Left => Math.Abs(a.Left - b.Right) <= EdgeTolerance && Overlap(a.Top, a.Bottom, b.Top, b.Bottom) > 0,
        Edge.Bottom => Math.Abs(b.Top - a.Bottom) <= EdgeTolerance && Overlap(a.Left, a.Right, b.Left, b.Right) > 0,
        _ => Math.Abs(a.Top - b.Bottom) <= EdgeTolerance && Overlap(a.Left, a.Right, b.Left, b.Right) > 0,
    };

    private static int Overlap(int a0, int a1, int b0, int b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

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

    /// <summary>
    /// The grid an older device understands, worked out from the free field: a monitor's column is how many
    /// monitors stand one after another to its left, its row how many stand one above another over it. It is an
    /// approximation — the grid cannot say "only the lower half of this edge" — but it keeps every device that
    /// meets another one meeting it there too.
    /// </summary>
    public IReadOnlyList<LayoutCell> ToCells()
    {
        var columns = new Dictionary<PlacedMonitor, int>();
        var rows = new Dictionary<PlacedMonitor, int>();
        int Column(PlacedMonitor m) => columns.TryGetValue(m, out var known) ? known : columns[m] =
            _placed.Where(o => o != m && o.Bounds.Right <= m.Bounds.Left + EdgeTolerance && o.Bounds.Left < m.Bounds.Left)
                .Select(o => Column(o) + 1).DefaultIfEmpty(0).Max();
        int Row(PlacedMonitor m) => rows.TryGetValue(m, out var known) ? known : rows[m] =
            _placed.Where(o => o != m && o.Bounds.Bottom <= m.Bounds.Top + EdgeTolerance && o.Bounds.Top < m.Bounds.Top)
                .Select(o => Row(o) + 1).DefaultIfEmpty(0).Max();

        var cells = new List<LayoutCell>();
        var taken = new HashSet<(int, int)>();
        foreach (var monitor in _placed.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top))
        {
            var at = (Column(monitor), Row(monitor));
            if (taken.Add(at))
            {
                cells.Add(new LayoutCell(monitor.DeviceId, at.Item1, at.Item2, monitor.Key.Length > 0 ? monitor.Key : null));
            }
        }

        return cells;
    }

    private static IEnumerable<LayoutRect> MonitorsOf(LayoutPlacement placement, Func<string, IReadOnlyList<LayoutRect>?>? monitorsOf)
    {
        if (monitorsOf?.Invoke(placement.DeviceId) is { Count: > 0 } live)
        {
            return live;
        }

        if (placement.Monitors is { Count: > 0 } kept)
        {
            return kept;
        }

        return [new LayoutRect(string.Empty, 0, 0, DefaultScreen.Width, DefaultScreen.Height)];
    }

    private static Rectangle Union(IEnumerable<PlacedMonitor> monitors) =>
        monitors.Select(m => m.Bounds).Aggregate(Rectangle.Union);
}
