using WinMirroring.Core.Settings;
using WinMirroring.Input.Layout;

namespace WinMirroring.Input.Tests;

/// <summary>
/// Devices standing freely on the field: the cursor crosses only where monitors of two devices meet, at the same
/// height, and a device moved on the field moves that border with it.
/// </summary>
public class FreeLayoutTests
{
    // The desk this was written at: a portrait monitor on the left reaching above and below the primary, the
    // primary in the middle, a third monitor to the right a little lower — and a tablet put against that one.
    private static readonly List<LayoutRect> Desk =
    [
        new(@"\\.\DISPLAY3", -1080, -394, 1080, 1920),
        new(@"\\.\DISPLAY2", 0, 0, 1920, 1080),
        new(@"\\.\DISPLAY1", 1920, 104, 1920, 1080),
    ];

    private static readonly List<LayoutRect> Tablet = [new(@"\\.\DISPLAY1", 0, 0, 1920, 1280)];

    private static DeviceLayout Standing(int tabletY) => DeviceLayout.From(
        [],
        [new LayoutPlacement("pc", 0, 0, Desk), new LayoutPlacement("tablet", 3840, tabletY, Tablet)],
        null);

    [Fact]
    public void TheCursorComesOutAtTheSameHeightItLeftAt()
    {
        var layout = Standing(tabletY: 104);

        // Halfway down monitor 1 (y 104 + 540): the tablet starts at 104 too, so the same 540 pixels down.
        var crossing = layout.Across("pc", @"\\.\DISPLAY1", Edge.Right, relativeOnMonitor: 540 / 1079f, relativeOnScreen: 0.5f);

        Assert.NotNull(crossing);
        Assert.Equal("tablet", crossing.Value.DeviceId);
        Assert.Equal(Edge.Left, crossing.Value.Entry);
        Assert.Equal(540 / 1279f, crossing.Value.Relative, 2);
    }

    [Fact]
    public void OnlyThePartOfTheEdgeTheyShareLeadsAcross()
    {
        // The tablet moved 700 pixels down: only the lower part of monitor 1's right edge meets it.
        var layout = Standing(tabletY: 804);

        Assert.Null(layout.Across("pc", @"\\.\DISPLAY1", Edge.Right, relativeOnMonitor: 0.2f, relativeOnScreen: 0));
        var low = layout.Across("pc", @"\\.\DISPLAY1", Edge.Right, relativeOnMonitor: 0.9f, relativeOnScreen: 0);
        Assert.Equal("tablet", low?.DeviceId);

        // And back: the top of the tablet's left edge is next to monitor 1, its bottom is next to nothing.
        var back = layout.Across("tablet", @"\\.\DISPLAY1", Edge.Left, relativeOnMonitor: 0.1f, relativeOnScreen: 0.1f);
        Assert.Equal(("pc", @"\\.\DISPLAY1", Edge.Right), (back?.DeviceId, back?.Monitor, back?.Entry));
        Assert.Null(layout.Across("tablet", @"\\.\DISPLAY1", Edge.Left, relativeOnMonitor: 0.95f, relativeOnScreen: 0.95f));
    }

    [Fact]
    public void ADeviceLeftAGapAwayStillMeetsButOneFarAwayDoesNot()
    {
        var near = DeviceLayout.From([], [new LayoutPlacement("pc", 0, 0, Desk), new LayoutPlacement("tablet", 3840 + 30, 104, Tablet)], null);
        var far = DeviceLayout.From([], [new LayoutPlacement("pc", 0, 0, Desk), new LayoutPlacement("tablet", 3840 + 400, 104, Tablet)], null);

        Assert.Equal("tablet", near.Across("pc", @"\\.\DISPLAY1", Edge.Right, 0.5f, 0.5f)?.DeviceId);
        Assert.Null(far.Across("pc", @"\\.\DISPLAY1", Edge.Right, 0.5f, 0.5f));
        Assert.Equal(Edge.Right, far.DirectionOf("pc", "tablet"));
    }

    [Fact]
    public void LiveMonitorsWinOverTheOnesTheLayoutRemembers()
    {
        // Saved when the tablet was 1280 tall; it now reports a smaller screen, and the border follows.
        var layout = DeviceLayout.From(
            [],
            [new LayoutPlacement("pc", 0, 0, Desk), new LayoutPlacement("tablet", 3840, 104, Tablet)],
            id => id == "tablet" ? [new LayoutRect("t", 0, 0, 1280, 800)] : null);

        Assert.Null(layout.Across("pc", @"\\.\DISPLAY1", Edge.Right, relativeOnMonitor: 0.95f, relativeOnScreen: 0));
        Assert.Equal("t", layout.Across("pc", @"\\.\DISPLAY1", Edge.Right, relativeOnMonitor: 0.5f, relativeOnScreen: 0)?.Monitor);
    }

    [Fact]
    public void AnOlderDeviceGetsAGridThatKeepsTheSameNeighbours()
    {
        var cells = Standing(tabletY: 104).ToCells();
        var grid = DeviceLayout.FromCells(cells);

        Assert.Equal("tablet", grid.Neighbor("pc", Edge.Right));
        Assert.Equal("pc", grid.Neighbor("tablet", Edge.Left));
        Assert.Equal(4, cells.Count);
    }

    [Fact]
    public void AGridLayoutIsStillFollowedAsAGrid()
    {
        var layout = DeviceLayout.From([new LayoutCell("a", 0, 0), new LayoutCell("b", 1, 0)], [], null);

        Assert.False(layout.IsFree);
        var crossing = layout.Across("a", null, Edge.Right, 0.3f, 0.7f);
        Assert.Equal(("b", Edge.Left, 0.7f), (crossing?.DeviceId, crossing?.Entry, crossing?.Relative));
    }
}
