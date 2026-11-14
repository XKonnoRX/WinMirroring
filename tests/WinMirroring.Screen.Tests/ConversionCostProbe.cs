using System.Diagnostics;
using System.Drawing;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// What one frame of colour conversion costs on the machine it is run on. Explicit, because it is a measurement
/// rather than a test — but worth keeping: this loop was the reason a view ran at about ten frames a second, and
/// the only way to know whether a given device can keep up is to run it there. Writes to %TEMP%.
/// </summary>
public class ConversionCostProbe
{
    private static readonly string Report = Path.Combine(Path.GetTempPath(), "conversion-cost.txt");

    [Fact(Explicit = true)]
    public void HowLongAFrameTakes()
    {
        File.WriteAllText(Report, $"cores {Environment.ProcessorCount}{Environment.NewLine}");
        Sized(1920, 1080);

        // The same screen sent at the size a smaller window shows it: a quarter of the pixels, and every step
        // from the decoder to the glass costs by the pixel.
        Sized(960, 540);
        Assert.True(File.Exists(Report));
    }

    private static void Sized(int width, int height)
    {
        // What a stream really hands the viewer: the coded picture is a multiple of 16 rows tall.
        var Width = width;
        var Height = height;
        var CodedHeight = (height + 15) / 16 * 16;
        File.AppendAllText(Report, $"-- {Width}×{Height} --{Environment.NewLine}");

        var nv12 = new byte[Nv12Converter.Nv12Size(Width, CodedHeight)];
        Random.Shared.NextBytes(nv12);
        var bgra = new byte[Width * Height * 4];
        var frame = new byte[Width * Height * 4];
        Random.Shared.NextBytes(frame);
        var nv12Out = new byte[Nv12Converter.Nv12Size(Width, Height)];

        Measure("Nv12ToBgra (viewer, every frame)", () => Nv12Converter.Nv12ToBgra(nv12, Width, Height, bgra, Width * CodedHeight));
        Measure("BgraCropToNv12 (source, only without the graphics card)", () => Nv12Converter.BgraCropToNv12(frame, Width, new Rectangle(0, 0, Width, Height), nv12Out));
    }

    private static void Measure(string what, Action once)
    {
        for (var i = 0; i < 20; i++)
        {
            once();
        }

        const int Runs = 60;
        var ticks = new List<long>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var started = Stopwatch.GetTimestamp();
            once();
            ticks.Add(Stopwatch.GetTimestamp() - started);
        }

        ticks.Sort();
        double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        var mean = Ms(ticks.Sum() / ticks.Count);
        File.AppendAllText(
            Report,
            $"{what}: mean {mean:F2} ms, median {Ms(ticks[Runs / 2]):F2} ms, worst {Ms(ticks[^1]):F2} ms, " +
            $"{1000 / mean:F0} frames/s if nothing else happened{Environment.NewLine}");
    }
}
