using System.Drawing;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// A swipe sideways in a view goes to the monitor beside the one being looked at. Which one that is has to be
/// read off where the monitors stand, not off their numbers.
/// </summary>
public class MonitorNeighbourTests
{
    // A desk as it often is: the main monitor in the middle, numbered 1, with 2 to its right and 3 to its left.
    private static readonly MonitorNeighbours.Placed Middle = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 2560, 1440));
    private static readonly MonitorNeighbours.Placed Right = new(@"\\.\DISPLAY2", new Rectangle(2560, 180, 1920, 1080));
    private static readonly MonitorNeighbours.Placed Left = new(@"\\.\DISPLAY3", new Rectangle(-1920, 180, 1920, 1080));

    private static readonly MonitorNeighbours.Placed[] Desk = [Middle, Right, Left];

    [Fact]
    public void TheMonitorBesideIsTheOneThatStandsThere()
    {
        Assert.Equal(Right.Key, MonitorNeighbours.Beside(Desk, Middle.Key, +1));
        Assert.Equal(Left.Key, MonitorNeighbours.Beside(Desk, Middle.Key, -1));
    }

    [Fact]
    public void AtTheEndOfTheRowThereIsNothingFurtherThatWay()
    {
        Assert.Null(MonitorNeighbours.Beside(Desk, Right.Key, +1));
        Assert.Null(MonitorNeighbours.Beside(Desk, Left.Key, -1));
        Assert.Equal(Middle.Key, MonitorNeighbours.Beside(Desk, Right.Key, -1));
        Assert.Equal(Middle.Key, MonitorNeighbours.Beside(Desk, Left.Key, +1));
    }

    [Fact]
    public void TheNearestOnThatSideIsTakenNotTheFarthest()
    {
        // From the left-hand one, going right: the middle one, not the one beyond it.
        Assert.Equal(Middle.Key, MonitorNeighbours.Beside(Desk, Left.Key, +1));
    }

    [Fact]
    public void AMonitorAboveIsNotBeside()
    {
        MonitorNeighbours.Placed[] stacked = [Middle, new(@"\\.\DISPLAY2", new Rectangle(320, -1080, 1920, 1080))];

        Assert.Null(MonitorNeighbours.Beside(stacked, Middle.Key, +1));
        Assert.Null(MonitorNeighbours.Beside(stacked, Middle.Key, -1));
    }

    [Fact]
    public void OfTwoEquallyFarTheOneAtTheSameHeightIsTaken()
    {
        MonitorNeighbours.Placed[] corner =
        [
            Middle,
            new(@"\\.\DISPLAY2", new Rectangle(2560, -1080, 1920, 1080)), // up and to the right
            new(@"\\.\DISPLAY3", new Rectangle(2560, 0, 1920, 1080)), // level with it
        ];

        Assert.Equal(@"\\.\DISPLAY3", MonitorNeighbours.Beside(corner, Middle.Key, +1));
    }

    [Fact]
    public void WithoutKnowingWhereTheyStandThereIsNoBeside()
    {
        MonitorNeighbours.Placed[] unknown = [new("a", Rectangle.Empty), new("b", Rectangle.Empty)];

        Assert.Null(MonitorNeighbours.Beside(unknown, "a", +1));
        Assert.Null(MonitorNeighbours.Beside(Desk, "not one of them", +1));
        Assert.Null(MonitorNeighbours.Beside([Middle], Middle.Key, +1));
        Assert.Null(MonitorNeighbours.Beside(Desk, Middle.Key, 0));
    }

    [Theory]
    [InlineData(60, 5, true)]
    [InlineData(-60, 20, true)]
    [InlineData(40, 30, false)] // a scroll that drifts
    [InlineData(5, 60, false)]
    [InlineData(0, 0, false)]
    public void ASwipeSidewaysIsClearlyMoreAcrossThanUpOrDown(double dx, double dy, bool sideways) =>
        Assert.Equal(sideways, MonitorNeighbours.IsSideways(dx, dy));

    [Fact]
    public void HowFarASwipeHasToGoFollowsTheWidthOfTheViewWithinReason()
    {
        Assert.Equal(90, MonitorNeighbours.CommitsAfter(400));
        Assert.Equal(1280 * 0.12, MonitorNeighbours.CommitsAfter(1280), 3);
        Assert.Equal(260, MonitorNeighbours.CommitsAfter(5000));
    }
}
