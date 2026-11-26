using System.Diagnostics;
using System.Drawing;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Making a frame smaller in memory, for a stream whose graphics card is not used for conversion. The point of it
/// is the viewer: a picture sent at the size it is shown at is half the decoding and half the memory traffic of one
/// sent at the size of the screen it came from.
/// </summary>
public class BgraScalerTests
{
    [Fact]
    public void AFlatColourStaysThatColour()
    {
        var source = Filled(200, 120, (30, 140, 220));
        foreach (var (width, height) in new[] { (150, 90), (100, 60), (64, 36), (17, 11) })
        {
            var target = new byte[width * height * 4];
            BgraScaler.Scale(source, 200, new Rectangle(0, 0, 200, 120), target, width, height);

            for (var i = 0; i < target.Length; i += 4)
            {
                Assert.Equal((30, 140, 220, 255), (target[i], target[i + 1], target[i + 2], target[i + 3]));
            }
        }
    }

    [Fact]
    public void TheCropIsWhatIsScaledAndNothingAroundIt()
    {
        // Red everywhere, a green block in the middle; only the block is asked for.
        var source = Filled(160, 100, (0, 0, 255));
        var crop = new Rectangle(40, 20, 80, 60);
        for (var y = crop.Top; y < crop.Bottom; y++)
        {
            for (var x = crop.Left; x < crop.Right; x++)
            {
                var i = ((y * 160) + x) * 4;
                source[i] = 0;
                source[i + 1] = 255;
                source[i + 2] = 0;
            }
        }

        foreach (var (width, height) in new[] { (60, 44), (20, 14) })
        {
            var target = new byte[width * height * 4];
            BgraScaler.Scale(source, 160, crop, target, width, height);

            for (var i = 0; i < target.Length; i += 4)
            {
                Assert.Equal((0, 255, 0), (target[i], target[i + 1], target[i + 2]));
            }
        }
    }

    [Fact]
    public void AGradientKeepsItsEndsAndItsOrder()
    {
        const int width = 256;
        const int height = 64;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                source[i] = source[i + 1] = source[i + 2] = (byte)x;
                source[i + 3] = 255;
            }
        }

        var target = new byte[192 * 48 * 4];
        BgraScaler.Scale(source, width, new Rectangle(0, 0, width, height), target, 192, 48);

        Assert.InRange(target[0], 0, 2);
        Assert.InRange(target[(191 * 4)], 253, 255);
        for (var x = 1; x < 192; x++)
        {
            Assert.True(target[x * 4] >= target[(x - 1) * 4], $"the gradient goes backwards at {x}");
        }
    }

    /// <summary>
    /// Far smaller than the original, every pixel that is covered has to count: a fine pattern must come out as
    /// its average, not as whichever of its pixels happened to be landed on.
    /// </summary>
    [Fact]
    public void AFinePatternMadeMuchSmallerComesOutAsItsAverage()
    {
        const int width = 400;
        const int height = 400;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                source[i] = source[i + 1] = source[i + 2] = (byte)(((x + y) & 1) == 0 ? 255 : 0);
                source[i + 3] = 255;
            }
        }

        var target = new byte[100 * 100 * 4];
        BgraScaler.Scale(source, width, new Rectangle(0, 0, width, height), target, 100, 100);

        for (var i = 0; i < target.Length; i += 4)
        {
            Assert.InRange(target[i], 126, 129);
        }
    }

    [Fact]
    public void APictureThatDoesNotFitItsBuffersIsRefused()
    {
        var source = new byte[100 * 100 * 4];
        Assert.Throws<ArgumentException>(() => BgraScaler.Scale(source, 100, new Rectangle(50, 50, 80, 20), new byte[40 * 10 * 4], 40, 10));
        Assert.Throws<ArgumentException>(() => BgraScaler.Scale(source, 100, new Rectangle(0, 0, 100, 100), new byte[10], 50, 50));
    }

    /// <summary>What it costs on this machine, for the record: a 2560×1440 screen made the size of a 1920×1080 one.</summary>
    [Fact(Explicit = true)]
    public void HowLongAFullScreenTakesToMakeSmaller()
    {
        var source = new byte[2560 * 1440 * 4];
        new Random(7).NextBytes(source);
        var target = new byte[1920 * 1080 * 4];
        var crop = new Rectangle(0, 0, 2560, 1440);
        BgraScaler.Scale(source, 2560, crop, target, 1920, 1080); // warm up

        var best = double.MaxValue;
        for (var run = 0; run < 10; run++)
        {
            var started = Stopwatch.GetTimestamp();
            BgraScaler.Scale(source, 2560, crop, target, 1920, 1080);
            best = Math.Min(best, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        TestContext.Current.SendDiagnosticMessage($"2560x1440 -> 1920x1080 in {best:F1} ms");
        Assert.True(best < 12, $"scaling a full screen took {best:F1} ms at best");
    }

    private static byte[] Filled(int width, int height, (byte B, byte G, byte R) colour)
    {
        var image = new byte[width * height * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            image[i] = colour.B;
            image[i + 1] = colour.G;
            image[i + 2] = colour.R;
            image[i + 3] = 255;
        }

        return image;
    }
}
