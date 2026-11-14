using System.Diagnostics;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The two pieces that decide how fast a screen stream goes out: the window of unacknowledged frames and the
/// controller that moves the frame rate and bitrate. Both used to be a constant, which is how a viewer that paused
/// for a moment could freeze the picture at its source.
/// </summary>
public class FlowControlTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task AWindowHandsOutItsSlotsAndTakesThemBackOnAcknowledgement()
    {
        using var window = new SendWindow(2, Grace);
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(ct);
        await window.TakeAsync(ct);
        Assert.Equal(2, window.InFlight);

        window.OnAck();
        Assert.Equal(1, window.InFlight);
        await window.TakeAsync(ct);
        Assert.Equal(2, window.InFlight);
        Assert.Equal(0, window.GivenUp);
    }

    /// <summary>The whole point: an acknowledgement that never comes must not stop the stream for good.</summary>
    [Fact]
    public async Task AFrameGoesOutAnywayWhenNoAcknowledgementArrives()
    {
        using var window = new SendWindow(1, Grace);
        var ct = TestContext.Current.CancellationToken;
        await window.TakeAsync(ct);

        var waited = Stopwatch.StartNew();
        await window.TakeAsync(ct);
        waited.Stop();

        Assert.Equal(1, window.GivenUp);
        Assert.InRange(waited.Elapsed, Grace * 0.5, Grace * 6);
    }

    [Fact]
    public async Task AnAcknowledgementReleasesAWaitingFrameEarly()
    {
        using var window = new SendWindow(1, TimeSpan.FromSeconds(10));
        var ct = TestContext.Current.CancellationToken;
        await window.TakeAsync(ct);

        var waiting = window.TakeAsync(ct);
        await Task.Delay(20, ct);
        Assert.False(waiting.IsCompleted);

        window.OnAck();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(0, window.GivenUp);
    }

    [Fact]
    public async Task TheWindowMeasuresHowLongAcknowledgementsTake()
    {
        using var window = new SendWindow(4, Grace);
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(ct);
        await Task.Delay(60, ct);
        window.OnAck();

        Assert.InRange(window.DelayMs, 30, 400);
    }

    [Fact]
    public void PacingHoldsStillWhileAcknowledgementsArePrompt()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;

        pacing.Observe(0, 0, 30, now);
        for (var i = 0; i < 20; i++)
        {
            pacing.Observe(40, 0, 30, now += 1000);
        }

        Assert.Equal(30, pacing.Fps);
        Assert.Equal(1, pacing.Share);
        Assert.False(pacing.HeldBack);
    }

    [Fact]
    public void PacingBacksOffWhenAcknowledgementsLagAndRecoversSlowly()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        pacing.Observe(0, 0, 30, now);

        for (var i = 0; i < 5; i++)
        {
            pacing.Observe(800, 0, 30, now += 1100);
        }

        Assert.InRange(pacing.Fps, 8, 12);
        Assert.InRange(pacing.Share, 0.15, 0.25);
        Assert.True(pacing.HeldBack);

        for (var i = 0; i < 20; i++)
        {
            pacing.Observe(30, 0, 30, now += 3100);
        }

        Assert.Equal(30, pacing.Fps);
        Assert.Equal(1, pacing.Share);
        Assert.False(pacing.HeldBack);
    }

    /// <summary>A frame the window stopped waiting for is a stall even if the measured delay still looks fine.</summary>
    [Fact]
    public void PacingBacksOffWhenTheWindowStopsWaiting()
    {
        var pacing = new StreamPacing(60);
        var now = 0L;
        pacing.Observe(0, 0, 60, now);

        pacing.Observe(10, 1, 60, now += 1100);

        Assert.True(pacing.HeldBack);
        Assert.InRange(pacing.Fps, 8, 45);
    }

    [Fact]
    public void PacingNeverGoesAboveTheConfiguredFrameRate()
    {
        var pacing = new StreamPacing(60);
        var now = 0L;
        pacing.Observe(0, 0, 60, now);

        for (var i = 0; i < 50; i++)
        {
            pacing.Observe(10, 0, 15, now += 3100);
        }

        Assert.Equal(15, pacing.Fps);
    }
}
