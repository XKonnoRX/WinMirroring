using WinMirroring.Input.Timing;

namespace WinMirroring.Input.Tests;

public class PlayoutSchedulerTests
{
    private const long Start = 5_000_000_000;

    [Fact]
    public void SteadyLinkAddsOnlyTheMargin()
    {
        var scheduler = new PlayoutScheduler();
        long last = 0;
        for (var i = 0; i < 200; i++)
        {
            var remote = (uint)(1_000_000 + i * 1000);
            var arrival = Start + i * 1000 + 4000; // constant 4 ms one-way
            last = scheduler.Schedule(remote, arrival);
            Assert.Equal(arrival + PlayoutScheduler.MarginMicroseconds, last);
        }

        Assert.Equal(PlayoutScheduler.MarginMicroseconds, scheduler.CurrentDelayMicroseconds);
    }

    [Fact]
    public void BurstyArrivalsAreReplayedWithOriginalSpacing()
    {
        // The controller samples every 1 ms; the network delivers 8 samples at once every 8 ms (Wi-Fi style).
        var scheduler = new PlayoutScheduler(maxDelayMicroseconds: 30_000);
        var playTimes = new List<long>();
        for (var burst = 0; burst < 40; burst++)
        {
            var arrival = Start + (burst + 1) * 8000 + 3000;
            for (var i = 0; i < 8; i++)
            {
                var sample = burst * 8 + i;
                playTimes.Add(scheduler.Schedule((uint)(sample * 1000), arrival));
            }
        }

        Assert.InRange(scheduler.CurrentDelayMicroseconds, 7000, 9500);
        var spacing = playTimes.Skip(40).Zip(playTimes.Skip(41), (a, b) => b - a).ToList();
        Assert.All(spacing, gap => Assert.InRange(gap, 950, 1050));
    }

    [Fact]
    public void DelayIsCappedAndPlayTimesNeverGoBackwards()
    {
        var scheduler = new PlayoutScheduler(maxDelayMicroseconds: 20_000);
        long previous = long.MinValue;
        for (var i = 0; i < 100; i++)
        {
            var spike = i % 10 == 0 ? 150_000 : 0;
            var playAt = scheduler.Schedule((uint)(i * 1000), Start + i * 1000 + 2000 + spike);
            Assert.True(playAt >= previous, "play-out time went backwards");
            previous = playAt;
        }

        Assert.True(scheduler.CurrentDelayMicroseconds <= 20_000);
    }

    [Fact]
    public void RemoteClockWrapAroundIsHandled()
    {
        var scheduler = new PlayoutScheduler();
        var first = scheduler.Schedule(uint.MaxValue - 500, Start);
        var second = scheduler.Schedule(500, Start + 1001);

        Assert.Equal(1001, second - first);
    }

    [Fact]
    public void DisabledSchedulerPlaysOnArrival()
    {
        var scheduler = new PlayoutScheduler(enabled: false);

        Assert.Equal(Start + 7, scheduler.Schedule(123, Start + 7));
        Assert.Equal(0, scheduler.CurrentDelayMicroseconds);
    }

    [Fact]
    public void LongIdleResynchronises()
    {
        var scheduler = new PlayoutScheduler();
        scheduler.Schedule(0, Start);
        scheduler.Schedule(1000, Start + 50_000); // 49 ms of jitter

        var afterIdle = Start + 20_000_000;
        var playAt = scheduler.Schedule(2000, afterIdle);

        Assert.Equal(afterIdle + PlayoutScheduler.MarginMicroseconds, playAt);
    }
}
