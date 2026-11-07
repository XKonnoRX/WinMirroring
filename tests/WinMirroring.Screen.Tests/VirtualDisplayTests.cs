using System.Drawing;
using System.Xml.Linq;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Display;

namespace WinMirroring.Screen.Tests;

public sealed class VirtualDisplayTests
{
    private static readonly DisplayMode[] Offered =
    [
        new(1280, 720, 60), new(1920, 1080, 60), new(2560, 1440, 60), new(3840, 2160, 60), new(1920, 1280, 60), new(800, 600, 60),
    ];

    [Fact]
    public void ExactSizeWins()
    {
        Assert.Equal(new DisplayMode(1920, 1280, 60), VirtualDisplay.PickMode(Offered, 1920, 1280, out var exact));
        Assert.True(exact);
    }

    [Fact]
    public void SameShapeNoLargerThanTheScreenIsNext()
    {
        // A 16:9 tablet at 2000x1125 gets the largest 16:9 mode that fits, not a bigger one.
        Assert.Equal(new DisplayMode(1920, 1080, 60), VirtualDisplay.PickMode(Offered, 2000, 1125, out var exact));
        Assert.False(exact);
    }

    [Fact]
    public void OtherwiseTheClosestShape()
    {
        // 2736x1824 is 3:2; the only 3:2 mode is smaller, so it is the same-shape pick.
        Assert.Equal(new DisplayMode(1920, 1280, 60), VirtualDisplay.PickMode(Offered, 2736, 1824, out _));

        // 4:3 has no same-shape mode that fits at 1024x768 except 800x600.
        Assert.Equal(new DisplayMode(800, 600, 60), VirtualDisplay.PickMode(Offered, 1024, 768, out _));

        Assert.Null(VirtualDisplay.PickMode([], 1920, 1080, out _));
    }

    [Theory]
    [InlineData("right", 1920, 0)]
    [InlineData("left", -1280, 0)]
    [InlineData("top", 0, -800)]
    [InlineData("bottom", 0, 1080)]
    [InlineData("anything", 1920, 0)]
    public void NewMonitorGoesBesideTheOthers(string side, int x, int y)
    {
        var monitors = new[] { new MonitorDescription(0, 1, @"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), true) };

        Assert.Equal(new Point(x, y), VirtualDisplay.PlaceBeside(monitors, new Size(1280, 800), side));
    }

    [Fact]
    public void RightOfTwoMonitorsMeansRightOfBoth()
    {
        var monitors = new[]
        {
            new MonitorDescription(0, 1, @"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), true),
            new MonitorDescription(1, 2, @"\\.\DISPLAY2", new Rectangle(1920, -200, 2560, 1440), false),
        };

        Assert.Equal(new Point(4480, 0), VirtualDisplay.PlaceBeside(monitors, new Size(1280, 800), "right"));
    }

    [Fact]
    public void ResolutionIsAddedOnceToTheDriverSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vdd_settings-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <vdd_settings>
              <monitors><count>1</count></monitors>
              <resolutions>
                <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
              </resolutions>
            </vdd_settings>
            """);
        try
        {
            Assert.True(VirtualDisplaySetup.AddResolution(path, 1920, 1280));
            Assert.False(VirtualDisplaySetup.AddResolution(path, 1920, 1280));
            Assert.False(VirtualDisplaySetup.AddResolution(path, 1920, 1080));

            var resolutions = XDocument.Load(path).Descendants("resolution").ToList();
            Assert.Equal(2, resolutions.Count);
            Assert.Equal("1280", resolutions[1].Element("height")?.Value);
            Assert.Equal("60", resolutions[1].Element("refresh_rate")?.Value);
            Assert.Equal("1", XDocument.Load(path).Descendants("count").Single().Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("100x100")]
    [InlineData("axb")]
    [InlineData("99999x1080")]
    public void ElevatedEntryRejectsBadSizes(string argument) => Assert.Equal(2, VirtualDisplaySetup.Run(argument));
}
