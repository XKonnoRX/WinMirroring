using System.Drawing;
using WinMirroring.Screen.Capture;

namespace WinMirroring.Screen.Tests;

public sealed class WindowPlacerTests
{
    [Fact]
    public void KeepsSizeAndCentresOnTheMonitor()
    {
        var target = WindowPlacer.PlaceInside(new Rectangle(10, 20, 800, 600), new Rectangle(1920, 0, 1920, 1280));

        // Same size, centred: x = 1920 + (1920-800)/2, y = (1280-600)/2.
        Assert.Equal(new Rectangle(2480, 340, 800, 600), target);
    }

    [Fact]
    public void ShrinksAWindowLargerThanTheMonitor()
    {
        var target = WindowPlacer.PlaceInside(new Rectangle(100, 100, 4000, 3000), new Rectangle(0, 0, 1920, 1080));

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), target);
    }

    [Fact]
    public void AZeroSizedWindowFillsTheMonitor()
    {
        var target = WindowPlacer.PlaceInside(Rectangle.Empty, new Rectangle(100, 100, 1920, 1080));

        Assert.Equal(new Rectangle(100, 100, 1920, 1080), target);
    }

    [Fact]
    public void WorksWithAMonitorLeftOfThePrimary()
    {
        var target = WindowPlacer.PlaceInside(new Rectangle(0, 0, 1280, 720), new Rectangle(-1920, 0, 1920, 1080));

        Assert.Equal(new Rectangle(-1600, 180, 1280, 720), target);
    }
}
