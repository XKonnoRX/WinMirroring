using WinMirroring.Core.Power;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Tests;

/// <summary>
/// Spending less on a battery. Everything in this mode is a trade — fewer frames, a slower beacon, a longer pulse
/// — which is why it is a mode at all: what costs nothing was simply done and is not in here.
/// </summary>
public class PowerModeTests
{
    private static readonly TimeSpan Usually = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WhileSaving = TimeSpan.FromSeconds(4);

    [Fact]
    public void SavingFollowsTheSettingAndWhatTheDeviceIsPluggedInto()
    {
        var settings = new AppSettings();
        var onBattery = true;
        var power = new PowerMode(settings, () => onBattery);

        settings.Power.Saving = PowerSaving.Never;
        Assert.False(power.Saving);

        settings.Power.Saving = PowerSaving.Always;
        Assert.True(power.Saving);

        settings.Power.Saving = PowerSaving.OnBattery;
        Assert.True(power.Saving);

        onBattery = false;
        Assert.False(power.Saving);

        // And plugged in, "always" still means always.
        settings.Power.Saving = PowerSaving.Always;
        Assert.True(power.Saving);
    }

    /// <summary>
    /// The frame rate is lowered while saving and never raised: a device set to twenty-four frames does not start
    /// sending thirty because it is on a battery.
    /// </summary>
    [Fact]
    public void TheFrameRateIsOnlyEverLowered()
    {
        var settings = new AppSettings { Power = { Saving = PowerSaving.Always, Fps = 30 } };
        var power = new PowerMode(settings, () => false);

        Assert.Equal(30, power.Fps(60));
        Assert.Equal(24, power.Fps(24));

        settings.Power.Saving = PowerSaving.Never;
        Assert.Equal(60, power.Fps(60));
    }

    /// <summary>A frame rate of nothing is not a frame rate; a setting that says so is read as one frame.</summary>
    [Fact]
    public void AFrameRateOfNothingIsRefused()
    {
        var settings = new AppSettings { Power = { Saving = PowerSaving.Always, Fps = 0 } };
        var power = new PowerMode(settings, () => true);

        Assert.Equal(1, power.Fps(60));
    }

    [Fact]
    public void TheLongerIntervalIsUsedOnlyWhileSaving()
    {
        var settings = new AppSettings { Power = { Saving = PowerSaving.OnBattery } };
        var onBattery = false;
        var power = new PowerMode(settings, () => onBattery);

        Assert.Equal(Usually, power.Every(Usually, WhileSaving));

        onBattery = true;
        Assert.Equal(WhileSaving, power.Every(Usually, WhileSaving));
    }

    /// <summary>
    /// On a battery by default, because the device this is for is the one least likely to be asked about it —
    /// and on a machine with no battery that setting never comes on at all.
    /// </summary>
    [Fact]
    public void ADeviceSavesOnItsBatteryWithoutBeingTold()
    {
        var settings = new AppSettings();

        Assert.Equal(PowerSaving.OnBattery, settings.Power.Saving);
        Assert.True(new PowerMode(settings, () => true).Saving);
        Assert.False(new PowerMode(settings, () => false).Saving);
    }
}
