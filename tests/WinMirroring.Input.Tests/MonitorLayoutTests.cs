using System.Drawing;
using WinMirroring.Core.Settings;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Screen;

namespace WinMirroring.Input.Tests;

/// <summary>Devices with several monitors: one cell per monitor, crossings from a given monitor's edge.</summary>
public class MonitorLayoutTests
{
    private const string Laptop = "laptop";
    private const string Tablet = "tablet";
    private const string Pc = "pc";
    private const string Built = @"\\.\DISPLAY1";
    private const string Lg = @"\\.\DISPLAY2";

    // The laptop's built-in screen at (0,0), its LG monitor to the right of it; the tablet right of the LG, the PC
    // (whole) below the built-in screen.
    private static readonly DeviceLayout Layout = DeviceLayout.FromCells(
    [
        new LayoutCell(Laptop, 0, 0, Built),
        new LayoutCell(Laptop, 1, 0, Lg),
        new LayoutCell(Tablet, 2, 0),
        new LayoutCell(Pc, 0, 1),
    ]);

    [Fact]
    public void EachMonitorHasItsOwnNeighbours()
    {
        var built = Layout.CellOf(Laptop, Built)!;
        var lg = Layout.CellOf(Laptop, Lg)!;

        Assert.Equal(Tablet, Layout.Neighbor(lg, Edge.Right)!.DeviceId);
        Assert.Equal(Pc, Layout.Neighbor(built, Edge.Bottom)!.DeviceId);
        Assert.Null(Layout.Neighbor(lg, Edge.Bottom));

        // Next to the built-in screen on the right is the laptop's own LG monitor: not a crossing to another device.
        Assert.Equal(Laptop, Layout.Neighbor(built, Edge.Right)!.DeviceId);
    }

    [Fact]
    public void CrossingsComeBackOntoTheMonitorTheyLeft()
    {
        var tablet = Layout.CellOf(Tablet, null)!;
        var pc = Layout.CellOf(Pc, "anything")!;

        Assert.Equal(Lg, Layout.Neighbor(tablet, Edge.Left)!.Monitor);
        Assert.Equal(Built, Layout.Neighbor(pc, Edge.Top)!.Monitor);
    }

    [Fact]
    public void AMonitorWithoutACellFallsBackToTheWholeDeviceOrHasNoCrossing()
    {
        var mixed = DeviceLayout.FromCells(
        [
            new LayoutCell(Laptop, 0, 0, Built),
            new LayoutCell(Laptop, 1, 0),
        ]);

        Assert.Equal(Built, mixed.CellOf(Laptop, Built)!.Monitor);
        Assert.Null(mixed.CellOf(Laptop, Lg)!.Monitor);           // the whole-device cell takes the rest
        Assert.Null(Layout.CellOf(Laptop, @"\\.\DISPLAY9"));        // only monitor cells: an unplaced monitor stays put
        Assert.Equal(Built, Layout.CellOf(Laptop, null)!.Monitor);  // an older device does not say: its first cell
        Assert.Null(Layout.CellOf("unknown", null));
    }

    [Fact]
    public void DeviceLevelQueriesSeeEveryCellOfTheDevice()
    {
        Assert.True(Layout.Contains(Laptop));
        Assert.True(Layout.HasMonitorCells);
        Assert.Equal(Tablet, Layout.Neighbor(Laptop, Edge.Right));
        Assert.Equal(Pc, Layout.Neighbor(Laptop, Edge.Bottom));
        Assert.Equal(Laptop, Layout.Neighbor(Tablet, Edge.Left));
    }

    [Fact]
    public void TheSameMonitorTwiceOrTwoThingsInACellAreIgnored()
    {
        var layout = DeviceLayout.FromCells(
        [
            new LayoutCell(Laptop, 0, 0, Built),
            new LayoutCell(Laptop, 1, 0, Built),
            new LayoutCell(Tablet, 0, 0),
        ]);

        Assert.Equal(0, layout.CellOf(Laptop, Built)!.Column);
        Assert.False(layout.Contains(Tablet));
    }

    // ---- geometry: a 1920×1080 primary with a taller 1920×1200 monitor to its right, 60 px higher ----

    private static readonly MonitorSet Monitors = new(
    [
        new MonitorInfo(Lg, new Rectangle(1920, -60, 1920, 1200), Primary: false),
        new MonitorInfo(Built, new Rectangle(0, 0, 1920, 1080), Primary: true),
        new MonitorInfo(@"\\.\DISPLAY3", new Rectangle(3840, 0, 1920, 1080), Primary: false, IsVirtual: true),
    ]);

    [Fact]
    public void MonitorsAreNumberedPrimaryFirstAndTheVirtualOneIsLeftOut()
    {
        Assert.Equal([Built, Lg], Monitors.ForLayout.Select(m => m.Key));
        Assert.Equal("Монитор 1 · 1920×1080 · основной", Monitors.Describe(Monitors.ForLayout[0]));
        Assert.Equal("Монитор 2 · 1920×1200 · справа", Monitors.Describe(Monitors.ForLayout[1]));
    }

    [Fact]
    public void AnEdgeIsOpenOnlyWhereNoOtherMonitorContinues()
    {
        var built = Monitors.Find(Built)!;
        var lg = Monitors.Find(Lg)!;

        Assert.True(Monitors.ContinuesBeyond(built, Edge.Right, new Point(1919, 500)));   // onto the LG
        Assert.False(Monitors.ContinuesBeyond(built, Edge.Top, new Point(1000, 0)));      // nothing above
        Assert.False(Monitors.ContinuesBeyond(lg, Edge.Left, new Point(1920, 1100)));     // below the built-in screen's bottom
        Assert.True(Monitors.ContinuesBeyond(lg, Edge.Left, new Point(1920, 500)));
        Assert.True(Monitors.ContinuesBeyond(lg, Edge.Right, new Point(3839, 500)));      // the virtual monitor is still a monitor here
    }

    /// <summary>The real monitors of the machine running the tests (on request only: build agents may have none).</summary>
    [Fact(Explicit = true)]
    public void TheCatalogSeesThisMachinesMonitors()
    {
        var set = MonitorCatalog.Refresh();
        foreach (var monitor in set.All)
        {
            TestContext.Current.SendDiagnosticMessage($"{monitor.Key} {monitor.Bounds} primary={monitor.Primary} virtual={monitor.IsVirtual}: {set.Describe(monitor)}");
        }

        Assert.Single(set.All, m => m.Primary);
        Assert.All(set.All, m => Assert.StartsWith(@"\\.\", m.Key));
        Assert.Equal(Point.Empty, set.All.Single(m => m.Primary).Bounds.Location);
    }

    [Fact]
    public void PointsOffEveryMonitorSnapOntoTheNearestOne()
    {
        // The bounding box's corner under the built-in screen, where no monitor is.
        Assert.Null(Monitors.At(new Point(500, 1130)));
        Assert.Equal(new Point(500, 1079), Monitors.Snap(new Point(500, 1130)));
        Assert.Equal(Built, Monitors.Nearest(new Point(500, 1130)).Key);
        Assert.Equal(new Point(100, 100), Monitors.Snap(new Point(100, 100)));
    }
}
