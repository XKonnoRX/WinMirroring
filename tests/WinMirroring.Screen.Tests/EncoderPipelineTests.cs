using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Feeding the encoder and collecting from it are two steps, not one. That is what lets a stream capture, encode
/// and send at the same time instead of one after another: while a frame is on the wire the next is being encoded
/// and the one after that captured, so the stream's period is the longest of the three rather than their sum.
/// Waiting inside the capture loop for the frame just handed over — which is what it used to do — also made every
/// slow frame of the encoder's into a gap in the capture, and an uneven stream is what a stutter is.
/// </summary>
public class EncoderPipelineTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Frames = 6;

    /// <summary>
    /// The premise of the whole thing: handing a frame over costs far less than encoding it. If the two cost the
    /// same there would be nothing to overlap, and splitting the loop would have bought nothing.
    /// </summary>
    [Fact]
    public void HandingFramesOverCostsFarLessThanEncodingThem()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264, "No hardware H.264 encoder on this machine.");

        var picture = ColourBands(Width, Height);
        var crop = new Rectangle(0, 0, Width, Height);

        using var waited = H264VideoEncoder.TryCreate(Width, Height, 60, 8000, NullLogger.Instance)!;
        var encoding = Stopwatch.StartNew();
        var produced = 0;
        for (var i = 0; i < Frames; i++)
        {
            produced += waited.Encode(picture, Width, crop, forceKeyframe: i == 0).Count;
        }

        encoding.Stop();

        using var fed = H264VideoEncoder.TryCreate(Width, Height, 60, 8000, NullLogger.Instance)!;
        var feeding = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            Assert.True(fed.Feed(picture, Width, crop, forceKeyframe: i == 0));
        }

        feeding.Stop();

        // And nothing is lost by not waiting: the same frames come out, a moment later, on the encoder's own thread.
        var collected = 0;
        var deadline = Stopwatch.StartNew();
        while (collected < produced && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            collected += fed.TakePending().Count;
            Thread.Sleep(5);
        }

        Assert.True(produced > 0, "the encoder waited on produced nothing to compare against");
        Assert.True(
            collected >= produced,
            $"{collected} frames came out of the encoder that was only fed, against {produced} from the one that was waited on");
        Assert.True(
            feeding.Elapsed < encoding.Elapsed * 0.75,
            $"feeding {Frames} frames took {feeding.Elapsed.TotalMilliseconds:F0} ms, encoding them one at a time {encoding.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// What the capture loop watches so that an encoder which has fallen behind does not become the place a stall
    /// piles up: each frame waiting there holds a picture in memory or a texture on the card that cannot be written
    /// again while it sits there.
    /// </summary>
    [Fact]
    public void TheEncoderSaysHowManyFramesItHasNotTakenIn()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264, "No hardware H.264 encoder on this machine.");

        using var encoder = H264VideoEncoder.TryCreate(Width, Height, 60, 8000, NullLogger.Instance)!;
        var picture = ColourBands(Width, Height);
        var crop = new Rectangle(0, 0, Width, Height);

        Assert.Equal(0, encoder.Waiting);
        for (var i = 0; i < Frames; i++)
        {
            encoder.Feed(picture, Width, crop, forceKeyframe: i == 0);
        }

        // It takes them in on its own thread, so this cannot be held to a number straight after feeding — only to
        // never being more than what was handed over, and to coming back down once it has caught up.
        Assert.InRange(encoder.Waiting, 0, Frames);

        var deadline = Stopwatch.StartNew();
        while (encoder.Waiting > 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Assert.Equal(0, encoder.Waiting);
    }

    /// <summary>A frame that is the wrong size is refused rather than fed, so the caller knows to rebuild.</summary>
    [Fact]
    public void AFrameOfTheWrongSizeIsRefused()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264, "No hardware H.264 encoder on this machine.");

        using var encoder = H264VideoEncoder.TryCreate(Width, Height, 60, 8000, NullLogger.Instance)!;

        Assert.False(encoder.Feed(ColourBands(640, 360), 640, new Rectangle(0, 0, 640, 360), forceKeyframe: true));
        Assert.Equal(0, encoder.Waiting);
    }

    private static byte[] ColourBands(int width, int height)
    {
        var image = new byte[width * height * 4];
        ReadOnlySpan<(byte B, byte G, byte R)> colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220)];
        for (var y = 0; y < height; y++)
        {
            var colour = colours[(y / 40) % colours.Length];
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
