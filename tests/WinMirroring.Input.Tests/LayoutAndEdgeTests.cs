using System.Drawing;
using WinMirroring.Core.Settings;
using WinMirroring.Input.Layout;

namespace WinMirroring.Input.Tests;

public class LayoutAndEdgeTests
{
    private static readonly DeviceLayout Layout = DeviceLayout.FromCells(
    [
        new LayoutCell("laptop", 0, 0),
        new LayoutCell("pc", 1, 0),
        new LayoutCell("tablet", 1, 1),
    ]);

    [Theory]
    [InlineData("laptop", Edge.Right, "pc")]
    [InlineData("pc", Edge.Left, "laptop")]
    [InlineData("pc", Edge.Bottom, "tablet")]
    [InlineData("tablet", Edge.Top, "pc")]
    [InlineData("laptop", Edge.Left, null)]
    [InlineData("laptop", Edge.Bottom, null)]
    [InlineData("unknown", Edge.Right, null)]
    public void NeighborFollowsTheGrid(string device, Edge edge, string? expected)
    {
        Assert.Equal(expected, Layout.Neighbor(device, edge));
    }

    [Fact]
    public void DuplicateDevicesAndCellsAreIgnored()
    {
        var layout = DeviceLayout.FromCells(
        [
            new LayoutCell("a", 0, 0),
            new LayoutCell("a", 2, 2),
            new LayoutCell("b", 0, 0),
            new LayoutCell("", 1, 0),
        ]);

        Assert.True(layout.Contains("a"));
        Assert.False(layout.Contains("b"));
        Assert.True(layout.TryGetCell("a", out var column, out var row));
        Assert.Equal((0, 0), (column, row));
        Assert.Null(layout.Neighbor("a", Edge.Right));
    }

    [Fact]
    public void EmptyLayoutHasNoNeighbors()
    {
        Assert.True(DeviceLayout.Empty.IsEmpty);
        Assert.Null(DeviceLayout.Empty.Neighbor("x", Edge.Left));
    }

    [Theory]
    [InlineData(Edge.Left, Edge.Right)]
    [InlineData(Edge.Right, Edge.Left)]
    [InlineData(Edge.Top, Edge.Bottom)]
    [InlineData(Edge.Bottom, Edge.Top)]
    public void OppositeEdges(Edge edge, Edge opposite)
    {
        Assert.Equal(opposite, EdgeMath.Opposite(edge));
    }

    [Fact]
    public void RelativePositionAndPointAreInverse()
    {
        var screen = new Rectangle(-1920, 0, 3840, 1080);

        var relative = EdgeMath.RelativeAlong(screen, Edge.Right, new Point(1919, 540));
        var point = EdgeMath.PointOn(screen, Edge.Left, relative, inset: 1);

        Assert.InRange(relative, 0.49f, 0.51f);
        Assert.Equal(new Point(-1919, 540), point);
    }

    [Fact]
    public void PointOnStaysInsideTheScreen()
    {
        var screen = new Rectangle(0, 0, 2560, 1440);

        Assert.Equal(new Point(2, 0), EdgeMath.PointOn(screen, Edge.Left, -5f, inset: 2));
        Assert.Equal(new Point(2557, 1439), EdgeMath.PointOn(screen, Edge.Right, 7f, inset: 2));
        Assert.Equal(new Point(2559, 1), EdgeMath.PointOn(screen, Edge.Top, 1f, inset: 1));
        Assert.Equal(new Point(0, 1438), EdgeMath.PointOn(screen, Edge.Bottom, 0f, inset: 1));
    }

    [Fact]
    public void RelativeAlongClamps()
    {
        var screen = new Rectangle(0, 0, 100, 50);

        Assert.Equal(0f, EdgeMath.RelativeAlong(screen, Edge.Top, new Point(-40, 0)));
        Assert.Equal(1f, EdgeMath.RelativeAlong(screen, Edge.Bottom, new Point(400, 49)));
        Assert.Equal(1f, EdgeMath.RelativeAlong(screen, Edge.Left, new Point(0, 49)));
    }
}
