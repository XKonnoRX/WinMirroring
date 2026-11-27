using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// HEVC through the same encoder and decoder as H.264. The two formats differ in three places — the name, how a
/// key picture is recognised, what marks the end of a picture — and each of them is a way for a stream to produce
/// nothing at all, so what is checked is the whole way round: frames in, the same pictures out.
/// </summary>
[Collection<HardwareVideoCollection>]
public class HevcTests
{
    private const int Width = 640;
    private const int Height = 368;

    [Fact]
    public void AKeyPictureIsRecognisedInEitherFormat()
    {
        // H.264: an IDR slice (5) and a parameter set (7) are; an ordinary slice (1) is not.
        Assert.True(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x65, 0x88], VideoFormat.H264));
        Assert.True(VideoInterop.ContainsKeyframe([0, 0, 1, 0x67, 0x42], VideoFormat.H264));
        Assert.False(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x41, 0x9A], VideoFormat.H264));

        // HEVC: type in bits 1..6 of the first byte. IDR_W_RADL is 19 (0x26), a parameter set 32 (0x40), an
        // ordinary trailing picture 1 (0x02).
        Assert.True(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x26, 0x01, 0xAF], VideoFormat.Hevc));
        Assert.True(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x40, 0x01, 0x0C], VideoFormat.Hevc));
        Assert.False(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x02, 0x01, 0xD0], VideoFormat.Hevc));

        // And the same bytes are not read the same way: 0x26 is H.264's type 6, which is not a key picture.
        Assert.False(VideoInterop.ContainsKeyframe([0, 0, 0, 1, 0x26, 0x01, 0xAF], VideoFormat.H264));
    }

    [Fact]
    public void AStreamsFormatIsReadOffItsFirstBytes()
    {
        // What each encoder really puts first: a parameter set, or a delimiter before it.
        Assert.Equal(VideoFormat.Hevc, VideoInterop.Sniff([0, 0, 0, 1, 0x40, 0x01, 0x0C, 0x01]));
        Assert.Equal(VideoFormat.Hevc, VideoInterop.Sniff([0, 0, 1, 0x46, 0x01, 0x50]));
        Assert.Equal(VideoFormat.H264, VideoInterop.Sniff([0, 0, 0, 1, 0x67, 0x4D, 0x40, 0x28]));
        Assert.Equal(VideoFormat.H264, VideoInterop.Sniff([0, 0, 0, 1, 0x09, 0x10, 0, 0, 0, 1, 0x67]));
        Assert.Equal(VideoFormat.H264, VideoInterop.Sniff([]));

        Assert.Equal("hevc", VideoInterop.NameOf(VideoInterop.FormatNamed("HEVC")));
        Assert.Equal("h264", VideoInterop.NameOf(VideoInterop.FormatNamed("something else")));
    }

    [Fact]
    public void WhatARealEncoderProducesIsReadAsItsOwnFormat()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanEncodeHevc, "No hardware encoder for both formats on this machine.");

        foreach (var format in new[] { VideoFormat.H264, VideoFormat.Hevc })
        {
            using var encoder = H264VideoEncoder.TryCreate(Width, Height, 30, 6000, NullLogger.Instance, format)!;
            var first = encoder.Encode(SharedSurfaceTests.Bands(0, Width, Height), Width, new Rectangle(0, 0, Width, Height), forceKeyframe: true);
            Assert.NotEmpty(first);
            Assert.True(first[0].Keyframe);
            Assert.Equal(format, VideoInterop.Sniff(first[0].Data));
        }
    }

    [Fact]
    public void FramesGoRoundThroughHevcAndComeBackTheSamePictures()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeHevc && VideoCodecSupport.CanDecodeHevc, "No hardware HEVC encoder, or no HEVC decoder, on this machine.");

        var units = new List<VideoAccessUnit>();
        using (var encoder = H264VideoEncoder.TryCreate(Width, Height, 30, 6000, NullLogger.Instance, VideoFormat.Hevc)!)
        {
            Assert.Equal(VideoFormat.Hevc, encoder.Format);
            for (var step = 0; step < 6; step++)
            {
                units.AddRange(encoder.Encode(SharedSurfaceTests.Bands(step, Width, Height), Width, new Rectangle(0, 0, Width, Height), forceKeyframe: step == 0));
            }
        }

        Assert.True(units.Count >= 3, $"the encoder produced {units.Count} pictures out of six frames");
        Assert.True(units[0].Keyframe, "the first picture of a stream was not recognised as a key picture");
        Assert.Contains(units.Skip(1), u => !u.Keyframe);

        // On the graphics card only: decoded in memory HEVC pictures come out wrong here, which is why a device
        // whose card does not take the stream says it cannot decode HEVC at all (see VideoCodecSupport).
        foreach (var onCard in new[] { true })
        {
            using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance, useGraphicsCard: onCard, VideoFormat.Hevc);
            Assert.NotNull(decoder);
            Assert.True(decoder!.UsesGraphicsCard);
            var picture = Array.Empty<byte>();
            var produced = 0;
            foreach (var unit in units)
            {
                if (decoder!.DecodeInto(unit.Data, ref picture, out var width, out var height))
                {
                    Assert.Equal((Width, Height), (width, height));
                    produced++;
                }
            }

            // One picture out for each one in: a decoder that held one back would show every frame a frame late.
            Assert.Equal(units.Count, produced);

            // The last one in is the last one out, and it is the picture that was encoded.
            var expected = SharedSurfaceTests.Bands(5, Width, Height);
            long error = 0;
            for (var i = 0; i < expected.Length; i += 4)
            {
                error += Math.Abs(expected[i] - picture[i]) + Math.Abs(expected[i + 1] - picture[i + 1]) + Math.Abs(expected[i + 2] - picture[i + 2]);
            }

            var mean = error / (expected.Length / 4 * 3.0);
            Assert.True(mean < 6, $"decoded {(onCard ? "on the card" : "in memory")}, the picture differs from what was encoded by {mean:F1} a channel");
        }
    }
}
