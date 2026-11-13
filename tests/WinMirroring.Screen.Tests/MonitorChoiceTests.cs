using WinMirroring.Screen.Capture;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Which monitor a viewer gets. An index is only a position in a list, and the two sides build their lists
/// separately — filtering the virtual display out of one of them is enough to make the second monitor open the
/// first. The display device name names it exactly, so it wins.
/// </summary>
public class MonitorChoiceTests
{
    [Fact]
    public void AMonitorIsChosenByItsNameWhateverIndexComesWithIt()
    {
        var monitors = DisplayCatalog.Monitors();
        Assert.SkipWhen(monitors.Count < 2, "Needs two monitors on this machine.");

        var wanted = monitors[^1];

        // The index says the first monitor and the name says the last: the name decides.
        var chosen = ScreenSource.PickMonitor(0, wanted.DeviceName);

        Assert.Equal(wanted.DeviceName, chosen.DeviceName);
        Assert.Equal(wanted.Handle, chosen.Handle);
    }

    [Fact]
    public void WithoutANameTheIndexIsTheCatalogIndexNotAPositionInSomeOtherList()
    {
        var monitors = DisplayCatalog.Monitors();
        Assert.SkipWhen(monitors.Count == 0, "No monitors on this machine.");

        foreach (var monitor in monitors)
        {
            Assert.Equal(monitor.DeviceName, ScreenSource.PickMonitor(monitor.Index).DeviceName);
        }

        // An index nobody has: the nearest one, never an exception.
        Assert.Equal(monitors[^1].DeviceName, ScreenSource.PickMonitor(99).DeviceName);
        Assert.Equal(monitors[0].DeviceName, ScreenSource.PickMonitor(-5).DeviceName);
    }

    [Fact]
    public void AnUnknownNameFallsBackToTheIndex()
    {
        var monitors = DisplayCatalog.Monitors();
        Assert.SkipWhen(monitors.Count == 0, "No monitors on this machine.");

        Assert.Equal(monitors[0].DeviceName, ScreenSource.PickMonitor(monitors[0].Index, @"\\.\DISPLAY_GONE").DeviceName);
    }
}
