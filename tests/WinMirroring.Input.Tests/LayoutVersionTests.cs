using WinMirroring.Input.Layout;

namespace WinMirroring.Input.Tests;

public class LayoutVersionTests
{
    [Fact]
    public void AChangeOnADeviceWhoseClockIsBehindStillWins()
    {
        // The layout came from a device whose clock is an hour ahead of this one.
        var seen = 1_800_000_000_000 + 3_600_000;
        var now = 1_800_000_000_000;

        var mine = LayoutVersion.Next(seen, now);

        Assert.True(LayoutVersion.Compare(mine, "laptop", seen, "pc") > 0);
    }

    [Fact]
    public void WithAClockThatIsRightTheVersionIsTheTime()
    {
        Assert.Equal(1_800_000_000_500, LayoutVersion.Next(1_800_000_000_000, 1_800_000_000_500));
    }

    [Fact]
    public void EqualVersionsAreOrderedByTheDeviceTheSameWayEverywhere()
    {
        Assert.True(LayoutVersion.Compare(42, "b", 42, "a") > 0);
        Assert.True(LayoutVersion.Compare(42, "a", 42, "b") < 0);
        Assert.Equal(0, LayoutVersion.Compare(42, "a", 42, "a"));
    }

    [Fact]
    public void AnOlderDeviceSendsNoOriginAndLosesATie()
    {
        Assert.True(LayoutVersion.Compare(42, null, 42, "a") < 0);
        Assert.Equal(0, LayoutVersion.Compare(42, null, 42, string.Empty));
        Assert.True(LayoutVersion.Compare(43, null, 42, "zzz") > 0);
    }
}
