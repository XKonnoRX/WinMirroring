using System.Diagnostics;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The two pieces that decide how fast a screen stream goes out: the window of frames on the wire waiting to be
/// acknowledged, which is what actually sets the rate, and the controller that moves the bitrate to fit the link.
/// Both used to be a constant, which is how a viewer that paused for a moment could freeze the picture at its
/// source; then both moved far too readily, which is how it came to stutter once a second instead.
/// </summary>
public class FlowControlTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Late = TimeSpan.FromMilliseconds(40);

    private static LinkReading Reading(long acks, long late, long givenUp = 0) => new(70, 30, acks, late, givenUp);

    [Fact]
    public async Task AWindowHandsOutItsSlotsAndTakesThemBackOnAcknowledgement()
    {
        using var window = new SendWindow(2, Grace, Late);
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
        using var window = new SendWindow(1, Grace, Late);
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
        using var window = new SendWindow(1, TimeSpan.FromSeconds(10), Late);
        var ct = TestContext.Current.CancellationToken;
        await window.TakeAsync(ct);

        var waiting = window.TakeAsync(ct);
        await Task.Delay(20, ct);
        Assert.False(waiting.IsCompleted);

        window.OnAck();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(0, window.GivenUp);
    }

    /// <summary>
    /// The source measures a round trip and cannot otherwise tell how much of it was the link and how much was the
    /// far device, so the viewer says how long it held the frame and that travels in the acknowledgement.
    /// </summary>
    [Fact]
    public async Task TheWindowMeasuresTheRoundTripAndHowMuchOfItWasTheViewer()
    {
        using var window = new SendWindow(4, Grace, Late);
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(ct);
        await Task.Delay(60, ct);
        window.OnAck(viewerMs: 25);

        var reading = window.Reading;
        Assert.InRange(reading.DelayMs, 30, 400);
        Assert.Equal(25, reading.ViewerMs);
        Assert.Equal(1, reading.Acks);
        Assert.Equal(1, reading.LateAcks); // sixty milliseconds against a late threshold of forty
    }

    /// <summary>A peer on an older build sends no time at all; everything else about the measurement still works.</summary>
    [Fact]
    public async Task AViewerThatDoesNotSayHowLongItHeldAFrameLeavesThatNumberEmpty()
    {
        using var window = new SendWindow(4, Grace, Late);
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(ct);
        window.OnAck();

        Assert.Equal(0, window.Reading.ViewerMs);
        Assert.Equal(1, window.Reading.Acks);
    }

    /// <summary>Inside the band there is nothing to decide, and deciding anyway is what made the picture lurch.</summary>
    [Fact]
    public void TheBitrateHoldsStillWhileMostFramesComeBackInTime()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        long acks = 0, late = 0;
        pacing.Observe(Reading(acks, late), 30, now);

        // A tenth of the frames late is neither trouble nor room to ask for more.
        for (var i = 0; i < 20; i++)
        {
            pacing.Observe(Reading(acks += 90, late += 9), 30, now += 3100);
        }

        Assert.Equal(1, pacing.Share);
        Assert.False(pacing.HeldBack);
        Assert.Equal(0, pacing.Changes);
    }

    [Fact]
    public void TheBitrateComesDownWhenMostFramesAreLateAndClimbsBackWhenTheyStop()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        long acks = 0, late = 0;
        pacing.Observe(Reading(acks, late), 30, now);

        for (var i = 0; i < 6; i++)
        {
            pacing.Observe(Reading(acks += 30, late += 20), 30, now += 3100);
        }

        // Never below a third of what was configured: a picture nobody can read is not the better outcome.
        Assert.Equal(1.0 / 3, pacing.Share, 3);
        Assert.True(pacing.HeldBack);

        for (var i = 0; i < 20; i++)
        {
            pacing.Observe(Reading(acks += 30, late), 30, now += 3100);
        }

        Assert.Equal(1, pacing.Share);
        Assert.False(pacing.HeldBack);
    }

    /// <summary>
    /// The regression this is all about. A single late frame — a hiccup that happens a few times a minute on
    /// Wi-Fi — used to be read as trouble, so the target moved about once a second for hours on end; every move
    /// changed the encoder's bitrate, every bitrate change made it emit a big frame, and a big frame on a loaded
    /// link is the hitch that was reported. One frame in ninety is not an answer to anything.
    /// </summary>
    [Fact]
    public void OneLateFrameAmongManyIsNotTrouble()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        long acks = 0, late = 0, stalls = 0;
        pacing.Observe(Reading(acks, late), 30, now);

        // Down a few steps first, so there is somewhere to go in either direction.
        for (var i = 0; i < 3; i++)
        {
            pacing.Observe(Reading(acks += 30, late += 20), 30, now += 3100);
        }

        var lowered = pacing.Share;
        Assert.True(lowered < 1);

        // Now one late frame, and one the window gave up on, among ninety.
        for (var i = 0; i < 2; i++)
        {
            pacing.Observe(Reading(acks += 90, late += 1, givenUp: ++stalls), 30, now += 3100);
        }

        Assert.True(pacing.Share > lowered);
    }

    /// <summary>
    /// It does not touch the frame rate at all any more. The window is the rate controller — a stream can only go
    /// as fast as its frames are acknowledged — and a second limiter on top of that could only take away rate the
    /// link was willing to carry. The two used to argue: the window would find thirty frames a second while this
    /// was asking for fourteen.
    /// </summary>
    [Fact]
    public void TheFrameRateIsTheConfiguredOneWhateverTheLinkDoes()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        long acks = 0, late = 0;
        pacing.Observe(Reading(acks, late), 30, now);

        for (var i = 0; i < 10; i++)
        {
            pacing.Observe(Reading(acks += 30, late += 30), 30, now += 3100);
        }

        Assert.Equal(30, pacing.Fps);
        Assert.Equal(1.0 / 3, pacing.Share, 3);

        // ...and it follows the setting when that changes under a running stream.
        pacing.Observe(Reading(acks += 30, late += 30), 15, now += 3100);
        Assert.Equal(15, pacing.Fps);
    }

    /// <summary>A handful of frames says nothing about a link; a view that has just opened has only a handful.</summary>
    [Fact]
    public void ItWaitsForEnoughFramesBeforeJudgingTheLink()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        pacing.Observe(Reading(0, 0), 30, now);

        pacing.Observe(Reading(3, 3), 30, now += 10_000);
        Assert.Equal(1, pacing.Share);
        Assert.Equal(0, pacing.Changes);

        pacing.Observe(Reading(9, 9), 30, now += 100);
        Assert.True(pacing.Share < 1);
        Assert.Equal(1, pacing.Changes);
    }

    /// <summary>
    /// What this release is about, in numbers. A stream can only run at its window divided by the round trip. Take
    /// a slot before the encoder runs — which is what the source used to do — and the encoder's time is charged to
    /// the window as well, so the rate becomes the window divided by the encoder <em>plus</em> the round trip. On
    /// the link this was found on that was the difference between nineteen frames a second and thirty: the slot was
    /// standing for work that was not on the wire at all.
    ///
    /// Both runs here do exactly the same work with exactly the same delays, in the same order; only the moment the
    /// slot is taken differs.
    /// </summary>
    [Fact]
    public async Task TakingASlotBeforeTheEncoderRunsCostsRealFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var beforeEncoding = await FramesPerSecondAsync(slotBeforeEncoding: true, ct);
        var afterEncoding = await FramesPerSecondAsync(slotBeforeEncoding: false, ct);

        Assert.True(
            afterEncoding > beforeEncoding * 1.2,
            $"{afterEncoding:F1} frames a second taking the slot at the send, {beforeEncoding:F1} taking it before the encoder");
    }

    /// <summary>
    /// Drives a window the way the stream's loop does: encode, take a slot, send, and the acknowledgement comes
    /// back a round trip later. The shape is a real session's — encoding takes about half a round trip, through a
    /// window of two — but both are stretched well past the scheduler's own slop, because the arithmetic allows at
    /// most a half as much again however the two are arranged and a test of that cannot afford to be noisy.
    /// </summary>
    private static async Task<double> FramesPerSecondAsync(bool slotBeforeEncoding, CancellationToken ct)
    {
        const int Frames = 10;
        var encoding = TimeSpan.FromMilliseconds(100);
        var roundTrip = TimeSpan.FromMilliseconds(200);
        using var window = new SendWindow(2, TimeSpan.FromSeconds(30), Late);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            if (slotBeforeEncoding)
            {
                await window.TakeAsync(ct);
                await Task.Delay(encoding, ct);
            }
            else
            {
                await Task.Delay(encoding, ct);
                await window.TakeAsync(ct);
            }

            _ = Task.Run(
                async () =>
                {
                    await Task.Delay(roundTrip, CancellationToken.None).ConfigureAwait(false);
                    window.OnAck();
                },
                CancellationToken.None);
        }

        var rate = Frames / clock.Elapsed.TotalSeconds;
        await Task.Delay(roundTrip * 2, ct); // let the acknowledgements land before the window goes away
        return rate;
    }

    /// <summary>
    /// A link that is clearly overloaded is not a close call, and walking down a fifth at a time would spend
    /// fifteen seconds reaching a bitrate it could have had in six. A link that is merely marginal still gets the
    /// small step.
    /// </summary>
    [Fact]
    public void AHopelessLinkComesDownFasterThanAMarginalOne()
    {
        Assert.True(AfterOneStep(late: 30, of: 30) < AfterOneStep(late: 10, of: 30));

        static double AfterOneStep(long late, long of)
        {
            var pacing = new StreamPacing(30);
            pacing.Observe(Reading(0, 0), 30, 0);
            pacing.Observe(Reading(of, late), 30, 3100);
            return pacing.Share;
        }
    }

    /// <summary>Steps are rare on purpose: the encoder is retuned on each one, and that is not free.</summary>
    [Fact]
    public void TheBitrateMovesAtMostOnceEveryFewSeconds()
    {
        var pacing = new StreamPacing(30);
        var now = 0L;
        long acks = 0, late = 0;
        pacing.Observe(Reading(acks, late), 30, now);

        // Ten seconds' worth of thoroughly late frames, offered a hundred times as the loop would offer them.
        for (var i = 0; i < 100; i++)
        {
            pacing.Observe(Reading(acks += 30, late += 30), 30, now += 100);
        }

        Assert.InRange(pacing.Changes, 1, 4);
    }
}
