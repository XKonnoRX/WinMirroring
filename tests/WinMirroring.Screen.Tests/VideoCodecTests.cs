using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen.Tests;

public class VideoCodecTests
{
    private static byte[] Gradient(int width, int height)
    {
        var image = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[i] = (byte)(x * 255 / Math.Max(1, width - 1));      // B
                image[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1)); // G
                image[i + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2)); // R
                image[i + 3] = 255;
            }
        }

        return image;
    }

    private static double MeanColorError(byte[] a, byte[] b)
    {
        long error = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            error += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return error / (double)(a.Length / 4 * 3);
    }

    [Fact]
    public void VideoFrameMessageRoundTrips()
    {
        var access = new byte[] { 0, 0, 0, 1, 0x67, 42, 7, 9 };

        var decoded = ScreenCodec.TryDecodeVideoFrame(ScreenCodec.EncodeVideoFrame(11, 7, 1920, 1080, keyframe: true, access));

        Assert.NotNull(decoded);
        Assert.Equal((11u, 7u, 1920, 1080, true), (decoded.StreamId, decoded.FrameNo, decoded.Width, decoded.Height, decoded.Keyframe));
        Assert.Equal(access, decoded.Data);

        var delta = ScreenCodec.TryDecodeVideoFrame(ScreenCodec.EncodeVideoFrame(3, 2, 64, 48, keyframe: false, new byte[] { 9 }));
        Assert.NotNull(delta);
        Assert.False(delta.Keyframe);
        Assert.Equal(new byte[] { 9 }, delta.Data);

        Assert.True(ScreenCodec.TryReadHeader(ScreenCodec.EncodeVideoFrame(5, 1, 10, 10, false, []), out var type, out var streamId));
        Assert.Equal((ScreenMessageType.VideoFrame, 5u), (type, streamId));

        // A tile Frame is not a VideoFrame, and a truncated message is rejected.
        Assert.Null(ScreenCodec.TryDecodeVideoFrame(ScreenCodec.EncodeFrame(1, 1, 10, 10, [])));
        Assert.Null(ScreenCodec.TryDecodeVideoFrame([(byte)ScreenMessageType.VideoFrame, 0, 0, 0]));
    }

    [Fact]
    public void Nv12RoundTripIsCloseToTheOriginal()
    {
        const int width = 128;
        const int height = 96;
        var bgra = Gradient(width, height);
        var nv12 = new byte[Nv12Converter.Nv12Size(width, height)];

        Nv12Converter.BgraCropToNv12(bgra, width, new Rectangle(0, 0, width, height), nv12);
        var back = new byte[width * height * 4];
        Nv12Converter.Nv12ToBgra(nv12, width, height, back);

        for (var i = 3; i < back.Length; i += 4)
        {
            Assert.Equal(255, back[i]);
        }

        Assert.InRange(MeanColorError(bgra, back), 0, 12);
    }

    [Fact]
    public void Nv12ConvertsOnlyTheCropRegion()
    {
        const int frameWidth = 200;
        const int frameHeight = 160;
        var bgra = Gradient(frameWidth, frameHeight);
        var crop = new Rectangle(40, 20, 100, 80);
        var nv12 = new byte[Nv12Converter.Nv12Size(crop.Width, crop.Height)];

        Nv12Converter.BgraCropToNv12(bgra, frameWidth, crop, nv12);
        var back = new byte[crop.Width * crop.Height * 4];
        Nv12Converter.Nv12ToBgra(nv12, crop.Width, crop.Height, back);

        // The crop's top-left pixel must match the source pixel at (40, 20), not (0, 0).
        var source = (crop.Y * frameWidth + crop.X) * 4;
        Assert.InRange(Math.Abs(back[0] - bgra[source]), 0, 6);
        Assert.InRange(Math.Abs(back[2] - bgra[source + 2]), 0, 6);
    }

    /// <summary>
    /// A decoded frame is as tall as the coded picture, which H.264 rounds up to a multiple of 16, so for a
    /// 1080-line screen the chroma plane starts after 1088 rows. Reading it after 1080 leaves the picture grey
    /// with a shifted colour ghost over it, which is what a real 1080p stream looked like.
    /// </summary>
    [Fact]
    public void ChromaIsReadAfterTheCodedHeightNotTheVisibleOne()
    {
        const int width = 64;
        const int height = 18;
        const int coded = 32; // what a decoder would hand back for 18 visible rows
        var nv12 = new byte[width * coded + (width * coded / 2)];
        Array.Fill(nv12, (byte)128, 0, width * height);
        Array.Fill(nv12, (byte)235, width * height, width * (coded - height)); // the rows below the picture
        for (var i = width * coded; i < nv12.Length; i += 2)
        {
            nv12[i] = 90;       // U
            nv12[i + 1] = 240;  // V — mid-grey luma with this chroma is a strong red
        }

        Assert.Equal(coded, Nv12Converter.RowsIn(nv12.Length, width, height));

        var bgra = new byte[width * height * 4];
        Nv12Converter.Nv12ToBgra(nv12, width, height, bgra, width * coded);
        for (var y = 0; y < height; y++)
        {
            var i = y * width * 4;
            Assert.Equal((54, 54, 255), (bgra[i], bgra[i + 1], bgra[i + 2]));
        }

        // Read as if the frame were tightly packed, the top rows take the leftover luma for chroma: not red.
        var wrong = new byte[width * height * 4];
        Nv12Converter.Nv12ToBgra(nv12, width, height, wrong);
        Assert.NotEqual(bgra[0..4], wrong[0..4]);
    }

    /// <summary>The same, over the real encoder and decoder at a screen height that is not a multiple of 16.</summary>
    [Fact]
    public void HardwareH264KeepsColoursAt1080p()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        const int width = 1920;
        const int height = 1080;
        var source = ColourBands(width, height);
        var crop = new Rectangle(0, 0, width, height);

        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 12000, NullLogger.Instance)!;
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance)!;
        var output = Array.Empty<byte>();
        var produced = false;
        foreach (var unit in encoder.Encode(source, width, crop, forceKeyframe: true))
        {
            produced |= decoder.DecodeInto(unit.Data, ref output, out var w, out var h) && (w, h) == (width, height);
        }

        Assert.True(produced, "the 1080p frame did not come back");
        Assert.InRange(MeanColorError(source, output), 0, 24);
    }

    /// <summary>
    /// A stream changes size while it is running now — the picture is sent at the size the viewer is showing it
    /// at — and the decoder has to follow. It cannot be asked what that size is: what a decoder offers is the
    /// coded size, rounded up to whole macroblocks (1088 rows for a 1080-line picture), so the size comes from
    /// the source with every frame.
    /// </summary>
    [Fact]
    public void TheDecoderFollowsAStreamThatChangesSize()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        using var decoder = H264VideoDecoder.TryCreate(1280, 720, NullLogger.Instance)!;
        var output = Array.Empty<byte>();

        Assert.Equal((1280, 720), Through(decoder, ref output, 1280, 720));
        var big = output;

        // The source rebuilds its encoder at the new size and sends a key frame.
        Assert.Equal((640, 360), Through(decoder, ref output, 640, 360));
        Assert.InRange(MeanColorError(ColourBands(640, 360), output.AsSpan(0, 640 * 360 * 4).ToArray()), 0, 24);

        // And back up again, because a window can be made larger as easily as smaller.
        Assert.Equal((1280, 720), Through(decoder, ref output, 1280, 720));
        Assert.InRange(MeanColorError(big.AsSpan(0, 1280 * 720 * 4).ToArray(), output.AsSpan(0, 1280 * 720 * 4).ToArray()), 0, 24);
    }

    /// <summary>Encodes a picture of this size and decodes it; returns the size that came out.</summary>
    private static (int Width, int Height) Through(H264VideoDecoder decoder, ref byte[] output, int width, int height)
    {
        var source = ColourBands(width, height);
        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 8000, NullLogger.Instance)!;
        var seen = (Width: 0, Height: 0);

        // A hardware encoder may take a frame or two to hand the first one back.
        for (var attempt = 0; attempt < 5 && seen.Width == 0; attempt++)
        {
            foreach (var unit in encoder.Encode(source, width, new Rectangle(0, 0, width, height), forceKeyframe: attempt == 0))
            {
                decoder.Expect(width, height);
                if (decoder.DecodeInto(unit.Data, ref output, out var w, out var h))
                {
                    seen = (w, h);
                }
            }
        }

        return seen;
    }

    /// <summary>Wide bands of saturated colour: a chroma plane read at the wrong offset shows up immediately.</summary>
    private static byte[] ColourBands(int width, int height)
    {
        var image = new byte[width * height * 4];
        ReadOnlySpan<(byte B, byte G, byte R)> colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220)];
        for (var y = 0; y < height; y++)
        {
            var colour = colours[(y / 40) % colours.Length];
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[i] = colour.B;
                image[i + 1] = colour.G;
                image[i + 2] = colour.R;
                image[i + 3] = 255;
            }
        }

        return image;
    }

    [Fact]
    public void Even_RoundsDown()
    {
        Assert.Equal(128, Nv12Converter.Even(129));
        Assert.Equal(128, Nv12Converter.Even(128));
        Assert.Equal(0, Nv12Converter.Even(1));
    }

    [Fact]
    public void CapabilityProbeIsStableAndDoesNotThrow()
    {
        var encode = VideoCodecSupport.CanEncodeH264;
        var decode = VideoCodecSupport.CanDecodeH264;

        // Cached: a second read returns the same answer.
        Assert.Equal(encode, VideoCodecSupport.CanEncodeH264);
        Assert.Equal(decode, VideoCodecSupport.CanDecodeH264);
    }

    /// <summary>
    /// A still screen gives the source one frame and then nothing: that one frame must come out of the encoder and
    /// the decoder at once, not only when a next frame pushes it through.
    /// </summary>
    [Fact]
    public void TheFirstFrameIsAPictureWithoutASecondOne()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        const int width = 1280;
        const int height = 720;
        var crop = new Rectangle(0, 0, width, height);
        var bgra = Gradient(width, height);
        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 8000, NullLogger.Instance)!;
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance)!;
        var output = Array.Empty<byte>();

        var units = encoder.Encode(bgra, width, crop, forceKeyframe: true);

        var unit = Assert.Single(units);
        Assert.True(unit.Keyframe);
        Assert.True(decoder.DecodeInto(unit.Data, ref output, out var decodedWidth, out var decodedHeight));
        Assert.Equal((width, height), (decodedWidth, decodedHeight));
        Assert.InRange(MeanColorError(bgra, output), 0, 40);
    }

    [Fact]
    public void HardwareH264EncodeDecodeRoundTrips()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264, "No hardware H.264 encoder on this machine.");

        const int width = 256;
        const int height = 192;
        const int fps = 30;
        var crop = new Rectangle(0, 0, width, height);
        var bgra = Gradient(width, height);

        using var encoder = H264VideoEncoder.TryCreate(width, height, fps, 6000, NullLogger.Instance);
        Assert.NotNull(encoder);
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance);
        Assert.NotNull(decoder);

        var output = Array.Empty<byte>();
        var produced = false;
        var sawKeyframe = false;
        var decodedWidth = 0;
        var decodedHeight = 0;
        var totalUnits = 0;
        var totalBytes = 0;

        // The hardware pipeline primes over a few frames; feed until a picture comes back (bounded).
        for (var frame = 0; frame < 120 && !produced; frame++)
        {
            foreach (var unit in encoder.Encode(bgra, width, crop, forceKeyframe: frame == 0))
            {
                totalUnits++;
                totalBytes += unit.Data.Length;
                sawKeyframe |= unit.Keyframe;
                if (decoder.DecodeInto(unit.Data, ref output, out decodedWidth, out decodedHeight))
                {
                    produced = true;
                }
            }

            Thread.Sleep(5);
        }

        Assert.True(produced, $"No decodable frame. units={totalUnits} bytes={totalBytes} keyframe={sawKeyframe} " +
            $"mgr={encoder.DiagHasDeviceManager} events={encoder.DiagEvents} needInput={encoder.DiagNeedInput} " +
            $"fedInputs={encoder.DiagInputs} encOut={encoder.DiagOutputs} err={encoder.DiagLastError}");
        Assert.True(sawKeyframe, "No key frame was emitted.");
        Assert.Equal((width, height), (decodedWidth, decodedHeight));
        for (var i = 3; i < output.Length; i += 4)
        {
            Assert.Equal(255, output[i]);
        }

        // Compression plus 4:2:0 chroma: allow a generous error, but it must resemble the original.
        Assert.InRange(MeanColorError(bgra, output), 0, 40);
    }
}
