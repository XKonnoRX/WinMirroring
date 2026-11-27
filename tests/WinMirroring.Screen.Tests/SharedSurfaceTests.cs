using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// A decoded picture left on the graphics card for the window to draw from, instead of being read back into memory
/// and uploaded again. What has to hold is that the two names for the texture — the one the decoder's device writes
/// under and the one WPF's draws from — are one picture: the same pixels, opaque, and the newest ones.
/// </summary>
[Collection<HardwareVideoCollection>]
public class SharedSurfaceTests
{
    private const int Width = 640;
    private const int Height = 368;

    [Fact]
    public void APictureLeftOnTheCardIsTheSamePictureSeenThroughDirect3D9()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        var units = Encoded(steps: 4);

        using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        Assert.SkipUnless(decoder.UsesGraphicsCard, "This machine's graphics card does not decode and convert.");
        using var bridge = Direct3D9Bridge.TryCreate();
        Assert.SkipWhen(bridge is null, "No Direct3D 9 device on this machine.");
        using var surface = decoder.CreateSurface(Width, Height);
        Assert.NotNull(surface);

        var memory = Array.Empty<byte>();
        var left = false;
        foreach (var unit in units)
        {
            if (decoder.DecodeInto(unit, ref memory, out _, out _, read: true, onto: surface))
            {
                left |= decoder.LastWasLeftOnCard;
            }
        }

        Assert.True(left, "no picture was left on the card");
        Assert.Empty(memory); // nothing crossed into memory on the way

        var seen = bridge!.Read(surface!);
        var expected = DecodedInMemory(units);
        Assert.SkipWhen(expected is null, "The decoder produced no picture in memory to compare with.");

        long error = 0;
        for (var i = 0; i < seen.Length; i += 4)
        {
            error += Math.Abs(seen[i] - expected![i]) + Math.Abs(seen[i + 1] - expected[i + 1]) + Math.Abs(seen[i + 2] - expected[i + 2]);
            Assert.Equal(255, seen[i + 3]); // opaque: WPF would draw anything less as see-through
        }

        var mean = error / (seen.Length / 4 * 3.0);
        Assert.True(mean < 1.5, $"the picture on the card differs from the one read into memory by {mean:F2} a channel");
    }

    [Fact]
    public void WhatIsWrittenLaterIsWhatIsSeenLater()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        var units = Encoded(steps: 6);

        using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        Assert.SkipUnless(decoder.UsesGraphicsCard, "This machine's graphics card does not decode and convert.");
        using var bridge = Direct3D9Bridge.TryCreate();
        Assert.SkipWhen(bridge is null, "No Direct3D 9 device on this machine.");
        using var surface = decoder.CreateSurface(Width, Height)!;

        // Opened before anything is in it, as a window would: what it shows has to follow what is written.
        var memory = Array.Empty<byte>();
        Assert.True(decoder.DecodeInto(units[0], ref memory, out _, out _, onto: surface));
        var first = bridge!.Read(surface);

        for (var i = 1; i < units.Count; i++)
        {
            decoder.DecodeInto(units[i], ref memory, out _, out _, onto: surface);
        }

        var last = bridge.Read(surface);
        long difference = 0;
        for (var i = 0; i < first.Length; i += 4)
        {
            difference += Math.Abs(first[i] - last[i]) + Math.Abs(first[i + 1] - last[i + 1]) + Math.Abs(first[i + 2] - last[i + 2]);
        }

        Assert.True(difference / (first.Length / 4 * 3.0) > 10, "the surface still shows the first picture after later ones were written");
    }

    [Fact]
    public void TwoSurfacesAreTwoPictures()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        var units = Encoded(steps: 4);

        using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        Assert.SkipUnless(decoder.UsesGraphicsCard, "This machine's graphics card does not decode and convert.");
        using var bridge = Direct3D9Bridge.TryCreate();
        Assert.SkipWhen(bridge is null, "No Direct3D 9 device on this machine.");
        using var early = decoder.CreateSurface(Width, Height)!;
        using var late = decoder.CreateSurface(Width, Height)!;

        var memory = Array.Empty<byte>();
        Assert.True(decoder.DecodeInto(units[0], ref memory, out _, out _, onto: early));
        var kept = bridge!.Read(early);
        for (var i = 1; i < units.Count; i++)
        {
            decoder.DecodeInto(units[i], ref memory, out _, out _, onto: late);
        }

        // Writing the later pictures somewhere else must leave the first where it was: that is what lets the
        // window go on drawing one picture while the next is being made.
        Assert.Equal(kept, bridge.Read(early));
        Assert.NotEqual(kept, bridge.Read(late));
        Assert.NotEqual(bridge.Open(early), bridge.Open(late));
        Assert.Equal(bridge.Open(early), bridge.Open(early));
    }

    private static List<byte[]> Encoded(int steps)
    {
        var units = new List<byte[]>();
        using var encoder = H264VideoEncoder.TryCreate(Width, Height, 30, 8000, NullLogger.Instance)!;
        for (var step = 0; step < steps; step++)
        {
            foreach (var unit in encoder.Encode(Bands(step), Width, new Rectangle(0, 0, Width, Height), forceKeyframe: step == 0))
            {
                units.Add(unit.Data);
            }
        }

        Assert.True(units.Count >= 2, $"the encoder produced {units.Count} pictures out of {steps} frames");
        return units;
    }

    private static byte[]? DecodedInMemory(List<byte[]> units)
    {
        using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        var output = Array.Empty<byte>();
        var produced = false;
        foreach (var unit in units)
        {
            produced |= decoder.DecodeInto(unit, ref output, out _, out _);
        }

        return produced ? output : null;
    }

    internal static byte[] Bands(int step, int width = Width, int height = Height)
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
