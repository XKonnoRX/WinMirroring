using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>A collection of one: nothing else may allocate while the process's memory is the measurement.</summary>
[CollectionDefinition(DisableParallelization = true)]
public class ProcessMemoryCollection;

/// <summary>
/// Watching a screen must not cost memory that is never given back. Media Foundation and WIC objects are not the
/// garbage collector's business: a wrapper dropped instead of disposed keeps whatever it points at for as long as
/// the process runs, and one decoded 1080p frame is four megabytes of it. That is how a tablet watching a screen
/// filled its memory in a few minutes, after which the decoder's own allocation began to fail and, later, the
/// window manager could not allocate either. So these run the per-frame paths thousands of times over and insist
/// the process stays where it was.
///
/// What they measure belongs to the whole process, so they cannot share it: another test encoding a 1080p frame
/// alongside them looks exactly like a leak. Hence a collection of their own, which xunit runs by itself.
/// </summary>
[Collection<ProcessMemoryCollection>]
public class MemoryTests
{
    /// <summary>What the process may grow by over a measured run: far under any real leak, well over the noise.</summary>
    private const long AllowedGrowthMb = 64;

    private static long PrivateMb()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }

    /// <summary>
    /// Runs one frame's work over and over and reports the most the process grew once it had settled. The largest
    /// reading counts rather than the last, so that a collection landing at the end cannot hide a leak.
    /// </summary>
    private static long GrowthMb(int warmUp, int runs, Action<int> frame)
    {
        for (var i = 0; i < warmUp; i++)
        {
            frame(i);
        }

        var start = PrivateMb();
        var worst = 0L;
        for (var i = 0; i < runs; i++)
        {
            frame(warmUp + i);
            if (i % 25 == 24)
            {
                worst = Math.Max(worst, PrivateMb() - start);
            }
        }

        return Math.Max(worst, PrivateMb() - start);
    }

    /// <summary>
    /// Frames that decode to nothing — the decoder priming itself, or a stream whose key frame was lost — take
    /// exactly the same output buffer as frames that decode to a picture. No encoder is needed for this, so it
    /// runs on any machine, and it is the cheapest guard against the output buffer being allocated per frame again.
    /// </summary>
    [Fact]
    public void FramesThatDecodeToNothingCostNoMemory()
    {
        Assert.SkipUnless(VideoCodecSupport.CanDecodeH264, "No H.264 decoder on this machine.");

        using var decoder = H264VideoDecoder.TryCreate(1280, 720, NullLogger.Instance);
        Assert.NotNull(decoder);

        // A slice that refers to a picture the decoder has never seen: accepted, and nothing comes out.
        var orphan = new byte[] { 0, 0, 0, 1, 0x61, 0x9A, 0x42, 0x17, 0x5B, 0xE0 };
        var picture = Array.Empty<byte>();

        var growth = GrowthMb(20, 400, _ => decoder.DecodeInto(orphan, ref picture, out _, out _));

        Assert.True(growth <= AllowedGrowthMb, $"the process grew by {growth} MB over 400 frames that produced nothing");
    }

    /// <summary>The whole way round, at the size a screen is actually watched at, with real pictures coming back.</summary>
    [Fact]
    public void WatchingAScreenDoesNotGrowTheProcess()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        const int width = 1280;
        const int height = 720;
        var crop = new Rectangle(0, 0, width, height);
        var frame = Bands(width, height);
        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 8000, NullLogger.Instance);
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance);
        Assert.NotNull(encoder);
        Assert.NotNull(decoder);
        var picture = Array.Empty<byte>();
        var pictures = 0;

        var growth = GrowthMb(10, 300, i =>
        {
            Scroll(frame, width, height);
            foreach (var unit in encoder.Encode(frame, width, crop, forceKeyframe: i == 0))
            {
                if (decoder.DecodeInto(unit.Data, ref picture, out _, out _))
                {
                    pictures++;
                }
            }
        });

        Assert.True(pictures > 100, $"only {pictures} pictures came back, so little was measured");
        Assert.True(growth <= AllowedGrowthMb, $"the process grew by {growth} MB over 300 frames");
    }

    /// <summary>
    /// The JPEG tile path too: it is where a view ends up when H.264 fails, which on the tablet was exactly when
    /// memory had already run short.
    /// </summary>
    [Fact]
    public void DecodingTilesCostsNoMemory()
    {
        const int width = TileEncoder.TileSize * 6;
        const int height = TileEncoder.TileSize * 4;
        using var encoder = new TileEncoder();
        var tiles = encoder.Encode(Bands(width, height), width, height, new Rectangle(0, 0, width, height), forceAll: true);
        Assert.NotEmpty(tiles);

        using var decoder = new TileDecoder();
        var image = new byte[width * height * 4];

        var growth = GrowthMb(50, 3000, i => decoder.DecodeInto(tiles[i % tiles.Count], image, width, height));

        Assert.True(growth <= AllowedGrowthMb, $"the process grew by {growth} MB over 3000 tiles");
    }

    /// <summary>Wide colour bands: something an encoder has real work to do on, unlike a flat picture.</summary>
    private static byte[] Bands(int width, int height)
    {
        var image = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[i] = (byte)(x * 7 % 251);
                image[i + 1] = (byte)(y * 13 % 241);
                image[i + 2] = (byte)((x ^ y) % 239);
                image[i + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>Moves the picture by one row, so every frame really differs from the one before it.</summary>
    private static void Scroll(byte[] image, int width, int height)
    {
        var row = width * 4;
        var first = image[..row];
        Array.Copy(image, row, image, 0, (height - 1) * row);
        first.CopyTo(image.AsSpan((height - 1) * row));
    }
}
