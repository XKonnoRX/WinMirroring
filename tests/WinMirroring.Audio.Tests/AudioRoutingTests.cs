namespace WinMirroring.Audio.Tests;

public class AudioRoutingTests
{
    private const string Laptop = "a-laptop";
    private const string Tablet = "b-tablet";

    [Fact]
    public void SoundFollowsTheDeviceThatWatchesTheScreen()
    {
        var route = AudioRouting.Choose(new AudioRouteInputs { Viewers = [Tablet] });

        Assert.Equal(new AudioRoute(Tablet, AudioRouteReason.Watches), route);
        Assert.True(route!.Value.Automatic);
    }

    [Fact]
    public void ControlComesBeforeWatchingAndChoicesComeBeforeBoth()
    {
        var other = "c-pc";
        Assert.Equal(AudioRouteReason.Controls, AudioRouting.Choose(new AudioRouteInputs { Focus = other, Viewers = [Tablet] })!.Value.Reason);
        Assert.Equal(new AudioRoute(Tablet, AudioRouteReason.Asked), AudioRouting.Choose(new AudioRouteInputs { Focus = other, Listeners = [Tablet] }));
        Assert.Equal(new AudioRoute(other, AudioRouteReason.Pinned), AudioRouting.Choose(new AudioRouteInputs { Pinned = other, Listeners = [Tablet] }));
    }

    [Fact]
    public void NothingGoesOutWhenSendingIsOff() =>
        Assert.Null(AudioRouting.Choose(new AudioRouteInputs { SendEnabled = false, Pinned = Tablet, Listeners = [Tablet], Viewers = [Tablet] }));

    [Fact]
    public void ARefusalBeatsEveryReason() =>
        Assert.Null(AudioRouting.Choose(new AudioRouteInputs { Pinned = Tablet, Listeners = [Tablet], Focus = Tablet, Viewers = [Tablet], RefusedBy = [Tablet] }));

    [Fact]
    public void NoAutomaticSoundBackToADeviceThatSendsHereByChoiceOrWasGivenWay()
    {
        Assert.Null(AudioRouting.Choose(new AudioRouteInputs { Viewers = [Tablet], SendsHereByChoice = p => p == Tablet }));
        Assert.Null(AudioRouting.Choose(new AudioRouteInputs { Focus = Tablet, Viewers = [Tablet], IsYielded = p => p == Tablet }));

        // A choice made here still counts: then both devices play each other's sound, and neither mutes.
        Assert.Equal(AudioRouteReason.Pinned, AudioRouting.Choose(new AudioRouteInputs { Pinned = Tablet, SendsHereByChoice = _ => true, IsYielded = _ => true })!.Value.Reason);
    }

    [Fact]
    public void DisconnectedDevicesAreSkipped() =>
        Assert.Equal(new AudioRoute("c-pc", AudioRouteReason.Watches),
            AudioRouting.Choose(new AudioRouteInputs { Pinned = Tablet, Viewers = [Tablet, "c-pc"], IsReady = p => p != Tablet }));

    [Fact]
    public void TheNewerAutomaticStreamWins()
    {
        var old = TimeSpan.FromMinutes(3);
        var fresh = TimeSpan.FromMilliseconds(10);

        // The laptop has sent to the tablet for minutes; now the tablet sends its sound to the laptop.
        Assert.True(AudioRouting.ShouldYield(true, true, old, fresh, Laptop, Tablet));
        Assert.False(AudioRouting.ShouldYield(true, true, fresh, old, Tablet, Laptop));
    }

    [Fact]
    public void ForStreamsStartedTogetherExactlyOneSideGivesWay()
    {
        var a = TimeSpan.FromMilliseconds(300);
        var b = TimeSpan.FromMilliseconds(100);

        var laptopYields = AudioRouting.ShouldYield(true, true, a, b, Laptop, Tablet);
        var tabletYields = AudioRouting.ShouldYield(true, true, b, a, Tablet, Laptop);

        Assert.NotEqual(laptopYields, tabletYields);
    }

    [Fact]
    public void AChoiceIsNeverGivenUpAndAlwaysWinsOverAnAutomaticStream()
    {
        var any = TimeSpan.FromSeconds(1);
        Assert.False(AudioRouting.ShouldYield(oursAutomatic: false, theirsAutomatic: true, any, any, Laptop, Tablet));
        Assert.False(AudioRouting.ShouldYield(oursAutomatic: false, theirsAutomatic: false, any, any, Laptop, Tablet));
        Assert.True(AudioRouting.ShouldYield(oursAutomatic: true, theirsAutomatic: false, TimeSpan.Zero, TimeSpan.FromHours(1), Laptop, Tablet));
    }
}
