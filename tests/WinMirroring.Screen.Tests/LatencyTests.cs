using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// What an hour of working through a tablet turned up, read back out of the log afterwards.
///
/// Every view lost its UDP path within a minute or two and never got it back: a screen that stood still for a
/// second was taken for a path that had gone quiet. On the connection it was left with, a lost segment froze the
/// picture about once every five seconds. Acknowledgements were matched to frames by their order, so one picture
/// lost whole put every measurement after it one frame out. And a device that took a hair longer over a frame than
/// frames took to arrive showed every picture two or three frames late for as long as the screen kept moving.
/// </summary>
public class LatencyTests
{
    private static readonly byte[] SmallPicture = new byte[900];

    // ---- the UDP path --------------------------------------------------------------------------------------

    private sealed class Clock
    {
        public long Now { get; set; } = 10_000;
    }

    private static VideoLink Confirmed(Clock clock)
    {
        var link = VideoLink.ForTest(NullLogger.Instance, () => clock.Now);
        link.ProbeIfDue();
        Assert.True(link.Confirm());
        return link;
    }

    [Fact]
    public void AScreenThatStoodStillIsNotAPathThatWentQuiet()
    {
        var clock = new Clock();
        using var link = Confirmed(clock);

        Assert.True(link.TrySend(1, 1280, 720, true, SmallPicture));
        link.Acknowledged();

        // Nothing on the screen changes for a while, so nothing is sent and nothing comes back.
        clock.Now += 30_000;

        Assert.True(link.TrySend(2, 1280, 720, false, SmallPicture));
        Assert.True(link.Carries);
    }

    [Fact]
    public void OnePictureLostAtTheEndOfABurstDoesNotCondemnThePath()
    {
        var clock = new Clock();
        using var link = Confirmed(clock);

        Assert.True(link.TrySend(1, 1280, 720, true, SmallPicture)); // lost whole: never answered

        clock.Now += 5_000;

        Assert.True(link.TrySend(2, 1280, 720, true, SmallPicture));
        link.Acknowledged();
        Assert.True(link.Carries);
    }

    [Fact]
    public void APathThatStopsAnsweringIsLeftAndThenTriedAgain()
    {
        var clock = new Clock();
        using var link = Confirmed(clock);

        for (uint frame = 1; frame <= VideoLink.UnansweredBeforeGivingUp; frame++)
        {
            Assert.True(link.TrySend(frame, 1280, 720, false, SmallPicture));
            clock.Now += 400;
        }

        // More than a second since the first of them, and not one answered.
        Assert.False(link.TrySend(9, 1280, 720, false, SmallPicture));
        Assert.False(link.Carries);

        // While it rests nothing is probed, and an answer that turns up late changes nothing.
        link.ProbeIfDue();
        Assert.False(link.Confirm());

        clock.Now += (long)VideoLink.FirstRest.TotalMilliseconds + 1;
        link.ProbeIfDue();
        Assert.True(link.Confirm());
        Assert.True(link.Carries);
        Assert.True(link.TrySend(10, 1280, 720, true, SmallPicture));
    }

    [Fact]
    public void ProbesNobodyAnswersAreGivenARestAndThenSentAgain()
    {
        var clock = new Clock();
        using var link = VideoLink.ForTest(NullLogger.Instance, () => clock.Now);

        for (var probe = 0; probe <= VideoLink.ProbesBeforeResting; probe++)
        {
            link.ProbeIfDue();
            clock.Now += (long)VideoLink.ProbeEvery.TotalMilliseconds;
        }

        Assert.False(link.Confirm()); // resting: this answer is to probes it has stopped waiting on

        clock.Now += (long)VideoLink.FirstRest.TotalMilliseconds;
        link.ProbeIfDue();
        Assert.True(link.Confirm());
    }

    // ---- acknowledgements by number ------------------------------------------------------------------------

    [Fact]
    public async Task AnAcknowledgementAnswersItsOwnFrameAndFreesTheOnesBeforeIt()
    {
        using var window = new SendWindow(3, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(40));
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(1, ct); // lost whole on the way: never answered
        await Task.Delay(120, ct);
        await window.TakeAsync(2, ct);
        window.OnAck(2, viewerMs: 5, paintMs: 1);

        // Frame 2 came back at once. Matched by order it would have been read as frame 1 coming back after 120 ms
        // — late — and frame 1 would still be holding a slot.
        Assert.Equal(0, window.InFlight);
        Assert.Equal(0, window.Reading.LateAcks);
        Assert.True(window.DelayMs < 60, $"the round trip was read as {window.DelayMs:F0} ms");
    }

    [Fact]
    public async Task AnAcknowledgementForAFrameAlreadyGivenUpOnChangesNothing()
    {
        using var window = new SendWindow(2, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(40));
        var ct = TestContext.Current.CancellationToken;

        await window.TakeAsync(5, ct);
        await window.TakeAsync(6, ct);
        window.OnAck(6, viewerMs: -1, paintMs: -1);
        window.OnAck(5, viewerMs: -1, paintMs: -1);

        Assert.Equal(0, window.InFlight);
        Assert.Equal(1, window.Reading.Acks);
    }

    [Fact]
    public async Task TheOldestUnansweredFrameSaysHowLongItHasWaited()
    {
        using var window = new SendWindow(3, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(40));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, window.OldestAgeMs);
        await window.TakeAsync(1, ct);
        await Task.Delay(60, ct);
        Assert.True(window.OldestAgeMs >= 50, $"read as {window.OldestAgeMs:F0} ms");

        window.OnAck(1, viewerMs: -1, paintMs: -1);
        Assert.Equal(0, window.OldestAgeMs);
    }

    // ---- pictures that came the other way ------------------------------------------------------------------

    [Fact]
    public void PicturesThatCameOnTheConnectionAreNotWaitedForAsDatagrams()
    {
        var delivered = new List<uint>();
        var givenUp = new List<uint>();
        long clock = 0;
        var reassembly = new VideoReassembly((frameNo, _, _, _, _, _) => delivered.Add(frameNo), givenUp.Add, () => clock);

        Feed(reassembly, 10);
        Assert.Equal([10u], delivered);

        // The stream goes back to the connection for a while: 11 to 500 arrive there.
        for (uint frame = 11; frame <= 500; frame++)
        {
            reassembly.Passed(frame);
        }

        // ...and comes back. The first datagram picture is simply the next one.
        Feed(reassembly, 501);

        Assert.Equal([10u, 501u], delivered);
        Assert.Empty(givenUp);
    }

    [Fact]
    public void APictureHalfArrivedIsDroppedWhenALaterOneComesOnTheConnection()
    {
        var delivered = new List<uint>();
        var givenUp = new List<uint>();
        long clock = 0;
        var reassembly = new VideoReassembly((frameNo, _, _, _, _, _) => delivered.Add(frameNo), givenUp.Add, () => clock);

        Feed(reassembly, 1);
        var plain = new byte[VideoDatagram.SafeDatagramBytes];
        var big = new byte[VideoDatagram.PayloadSize * 3];
        var length = VideoDatagram.Write(plain, 2, 1280, 720, false, big, 0);
        Assert.True(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out var header));
        reassembly.Add(header, plain.AsSpan(VideoDatagram.HeaderSize, length - VideoDatagram.HeaderSize));
        Assert.True(reassembly.Waiting);

        reassembly.Passed(3);

        Assert.False(reassembly.Waiting);
        Assert.Empty(givenUp);
        Feed(reassembly, 4);
        Assert.Equal([1u, 4u], delivered);
    }

    private static void Feed(VideoReassembly reassembly, uint frameNo)
    {
        var plain = new byte[VideoDatagram.SafeDatagramBytes];
        var pieces = VideoDatagram.Pieces(SmallPicture.Length);
        for (var piece = 0; piece < pieces; piece++)
        {
            var length = VideoDatagram.Write(plain, frameNo, 1280, 720, false, SmallPicture, piece);
            Assert.True(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out var header));
            reassembly.Add(header, plain.AsSpan(VideoDatagram.HeaderSize, length - VideoDatagram.HeaderSize));
        }
    }
}

/// <summary>
/// Catching up: a picture decoded without being turned into pixels, because a newer one is already waiting. The
/// decoder still has to have seen it — the next picture is read against it — so what must hold is that the picture
/// after a skipped one comes out exactly as it would have.
/// </summary>
[Collection<HardwareVideoCollection>]
public class CatchUpDecodingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APictureDecodedWithoutBeingReadStillCarriesTheNextOne(bool useGraphicsCard)
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        const int width = 640;
        const int height = 368;

        // A few pictures that differ, so the later ones really are read against the earlier ones.
        var units = new List<byte[]>();
        using (var encoder = H264VideoEncoder.TryCreate(width, height, 30, 8000, NullLogger.Instance)!)
        {
            for (var step = 0; step < 6; step++)
            {
                foreach (var unit in encoder.Encode(Bands(width, height, step), width, new Rectangle(0, 0, width, height), forceKeyframe: step == 0))
                {
                    units.Add(unit.Data);
                }
            }
        }

        Assert.True(units.Count >= 3, $"the encoder produced {units.Count} pictures out of six frames");

        var every = Decode(units, width, height, useGraphicsCard, skip: _ => false, out _);
        var last = Decode(units, width, height, useGraphicsCard, skip: i => i > 0 && i < units.Count - 1, out var untouched);
        Assert.SkipWhen(every is null || last is null, "This machine's decoder produced no picture.");

        Assert.True(untouched, "a picture that was only to be decoded was written into the image all the same");
        Assert.Equal(every!.Length, last!.Length);
        long error = 0;
        for (var i = 0; i < every.Length; i++)
        {
            error += Math.Abs(every[i] - last[i]);
        }

        Assert.True(error / (double)every.Length < 0.5, $"the picture after the skipped ones differs by {error / (double)every.Length:F2} a channel");
    }

    private static byte[]? Decode(List<byte[]> units, int width, int height, bool useGraphicsCard, Func<int, bool> skip, out bool untouched)
    {
        untouched = true;
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance, useGraphicsCard);
        if (decoder is null)
        {
            return null;
        }

        var output = Array.Empty<byte>();
        var produced = false;
        for (var i = 0; i < units.Count; i++)
        {
            if (skip(i))
            {
                var before = output.Length == 0 ? null : (byte[])output.Clone();
                var kept = output;
                decoder.DecodeInto(units[i], ref output, out _, out _, read: false);
                untouched &= ReferenceEquals(kept, output) && (before is null || before.AsSpan().SequenceEqual(output));
                continue;
            }

            produced |= decoder.DecodeInto(units[i], ref output, out _, out _);
        }

        return produced ? output : null;
    }

    private static byte[] Bands(int width, int height, int step)
    {
        var image = new byte[width * height * 4];
        ReadOnlySpan<(byte B, byte G, byte R)> colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220), (250, 250, 250), (10, 10, 10)];
        for (var y = 0; y < height; y++)
        {
            var colour = colours[((y / 40) + step) % colours.Length];
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                image[i] = colour.B;
                image[i + 1] = colour.G;
                image[i + 2] = colour.R;
                image[i + 3] = 255;
            }
        }

        return image;
    }
}
