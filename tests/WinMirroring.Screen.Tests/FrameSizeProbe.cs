using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// What sizes the encoder here actually produces, key frames against the rest. Explicit, because it is a
/// measurement rather than a test — but the one that decides what is worth doing about big frames: a key frame
/// several times the size of the others is a cliff that no amount of pacing or parity hides, while one that the
/// rate control has already flattened is not a problem to solve. Writes to %TEMP%.
/// </summary>
public class FrameSizeProbe
{
    [Fact(Explicit = true)]
    public void HowBigTheFramesAre()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264, "No hardware H.264 encoder on this machine.");

        var report = Path.Combine(Path.GetTempPath(), "frame-sizes.txt");
        File.WriteAllText(report, $"datagram payload {VideoDatagram.PayloadSize} bytes{Environment.NewLine}");

        foreach (var (width, height, bitrate, fps) in new[] { (1920, 1080, 12_000, 60), (1920, 1080, 12_000, 30), (1138, 640, 8_000, 60) })
        {
            Measure(report, width, height, bitrate, fps);
        }

        Assert.True(File.Exists(report));
    }

    private static void Measure(string report, int width, int height, int bitrateKbps, int fps)
    {
        using var encoder = H264VideoEncoder.TryCreate(width, height, fps, bitrateKbps, NullLogger.Instance)!;
        var crop = new Rectangle(0, 0, width, height);
        var keyframes = new List<int>();
        var others = new List<int>();

        // A moving picture, because a still one costs a codec almost nothing and would say nothing about the worst
        // case: a band of noise that slides across the frame, which is roughly what a window being dragged is.
        var frame = new byte[width * height * 4];
        for (var i = 0; i < 180; i++)
        {
            Paint(frame, width, height, i);
            encoder.Feed(frame, width, crop, forceKeyframe: i % (fps * 2) == 0);
            foreach (var unit in encoder.TakePending())
            {
                (unit.Keyframe ? keyframes : others).Add(unit.Data.Length);
            }

            Thread.Sleep(1000 / fps);
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            foreach (var unit in encoder.TakePending())
            {
                (unit.Keyframe ? keyframes : others).Add(unit.Data.Length);
            }

            Thread.Sleep(10);
        }

        var budget = bitrateKbps * 1000 / 8 / fps;
        File.AppendAllText(
            report,
            $"-- {width}×{height} at {fps} fps, {bitrateKbps} kbit/s (a frame's budget is {budget} bytes) --{Environment.NewLine}"
            + Describe("key frames", keyframes)
            + Describe("the rest", others));
    }

    private static string Describe(string what, List<int> sizes)
    {
        if (sizes.Count == 0)
        {
            return $"{what}: none{Environment.NewLine}";
        }

        sizes.Sort();
        var mean = sizes.Sum() / (double)sizes.Count;
        return $"{what}: {sizes.Count} of them, mean {mean:F0} bytes ({mean / VideoDatagram.PayloadSize:F1} datagrams), "
            + $"median {sizes[sizes.Count / 2]}, worst {sizes[^1]} ({sizes[^1] / (double)VideoDatagram.PayloadSize:F0} datagrams)"
            + Environment.NewLine;
    }

    /// <summary>A sliding band of noise over a flat ground: something an encoder cannot predict away.</summary>
    private static void Paint(byte[] frame, int width, int height, int step)
    {
        var random = new Random(step);
        var band = height / 4;
        var top = (step * 23) % Math.Max(1, height - band);
        Array.Clear(frame);
        for (var y = top; y < top + band; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width * 4; x += 4)
            {
                frame[row + x] = (byte)random.Next(256);
                frame[row + x + 1] = (byte)random.Next(256);
                frame[row + x + 2] = (byte)random.Next(256);
                frame[row + x + 3] = 255;
            }
        }
    }
}
