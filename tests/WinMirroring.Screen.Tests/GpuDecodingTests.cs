using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The viewer's side of a stream, where the picture is turned into pixels. It used to be decoded by the processor
/// and then walked pixel by pixel to convert NV12 into BGRA — about twenty-two megabytes of memory traffic for
/// every 1080p frame, which measured at forty-two milliseconds on a tablet and was ninety-two per cent of the round
/// trip the source was waiting on. The card can do all of it, and only the finished picture has to cross into
/// memory, but the two have to agree on colour to the eye or a stream would change its look the moment one of them
/// took over from the other.
/// </summary>
[Collection<HardwareVideoCollection>]
public class GpuDecodingTests
{
    private static bool CanUseCard => GpuFrameReader.TryCreate(NullLogger.Instance) is { } reader && Dispose(reader);

    private static bool Dispose(GpuFrameReader reader)
    {
        reader.Dispose();
        return true;
    }

    /// <summary>Given a card that will convert, the decoder is supposed to be using it and not the processor.</summary>
    [Fact]
    public void TheCardTakesTheStreamWhenItCan()
    {
        Assert.SkipUnless(VideoCodecSupport.CanDecodeH264, "No H.264 decoder on this machine.");
        Assert.SkipUnless(CanUseCard, "This machine's graphics card will not do the conversion.");

        using var decoder = H264VideoDecoder.TryCreate(1280, 720, NullLogger.Instance)!;

        Assert.True(decoder.UsesGraphicsCard, "the decoder fell back to the processor on a machine whose card can do it");
        Assert.False(decoder.GraphicsCardFailed);
    }

    /// <summary>And when it is told not to, it does not — which is the path a card that lets a stream down leaves.</summary>
    [Fact]
    public void TheProcessorTakesTheStreamWhenItIsToldTo()
    {
        Assert.SkipUnless(VideoCodecSupport.CanDecodeH264, "No H.264 decoder on this machine.");

        using var decoder = H264VideoDecoder.TryCreate(1280, 720, NullLogger.Instance, useGraphicsCard: false)!;

        Assert.False(decoder.UsesGraphicsCard);
    }

    /// <summary>
    /// The same access units through both paths. Not pixel for pixel — the card filters chroma its own way — but
    /// close enough that nobody could tell which one they were looking at, which is the whole requirement.
    /// </summary>
    [Fact]
    public void TheCardAndTheProcessorProduceTheSamePicture()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");
        Assert.SkipUnless(CanUseCard, "This machine's graphics card will not do the conversion.");

        const int width = 1920;
        const int height = 1080;
        var units = Encoded(width, height);
        Assert.NotEmpty(units);

        var onCard = Decoded(units, width, height, useGraphicsCard: true);
        var inMemory = Decoded(units, width, height, useGraphicsCard: false);
        Assert.NotNull(onCard);
        Assert.NotNull(inMemory);

        Assert.InRange(MeanColourError(onCard, inMemory), 0, 12);
    }

    /// <summary>
    /// What this machine's card makes of a decoded picture, every way of asking it, against what the processor
    /// makes of the same one. Explicit: it is how a card that gets refused is looked into, here or on whichever
    /// device is showing a stream badly. Writes to %TEMP%.
    /// </summary>
    [Fact(Explicit = true)]
    public void WhatTheCardMakesOfADecodedPicture()
    {
        var report = Path.Combine(Path.GetTempPath(), "decode-colour.txt");
        var log = new CollectingLogger();

        using var reader = GpuFrameReader.TryCreate(log);

        File.WriteAllText(report, string.Join(Environment.NewLine, log.Lines) + Environment.NewLine);
        Assert.True(File.Exists(report));
    }

    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    /// <summary>
    /// What a frame costs each way on the machine it is run on. Explicit, because it is a measurement rather than a
    /// test — but the measurement this release is about, and the only way to know whether a given device can keep
    /// up with thirty frames a second is to run it there. Writes to %TEMP%.
    /// </summary>
    [Fact(Explicit = true)]
    public void HowLongAFrameTakesEachWay()
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        var report = Path.Combine(Path.GetTempPath(), "decode-cost.txt");
        File.WriteAllText(report, $"cores {Environment.ProcessorCount}, card {(CanUseCard ? "yes" : "no")}{Environment.NewLine}");

        foreach (var (width, height) in new[] { (1920, 1080), (1280, 720), (960, 540) })
        {
            var units = Encoded(width, height);
            File.AppendAllText(report, $"-- {width}×{height}, {units.Count} access unit(s) --{Environment.NewLine}");
            Measure(report, "on the card", units, width, height, useGraphicsCard: true);
            Measure(report, "in memory  ", units, width, height, useGraphicsCard: false);
        }

        Assert.True(File.Exists(report));
    }

    private static void Measure(string report, string what, List<byte[]> units, int width, int height, bool useGraphicsCard)
    {
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance, useGraphicsCard);
        if (decoder is null || (useGraphicsCard && !decoder.UsesGraphicsCard))
        {
            File.AppendAllText(report, $"{what}: not available here{Environment.NewLine}");
            return;
        }

        var output = Array.Empty<byte>();
        const int Runs = 40;
        var ticks = new List<long>(Runs);
        for (var i = 0; i < Runs + 5; i++)
        {
            var started = Stopwatch.GetTimestamp();
            foreach (var unit in units)
            {
                decoder.DecodeInto(unit, ref output, out _, out _);
            }

            if (i >= 5)
            {
                ticks.Add(Stopwatch.GetTimestamp() - started);
            }
        }

        ticks.Sort();
        double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        var mean = ticks.Sum() / (double)ticks.Count * 1000.0 / Stopwatch.Frequency;
        File.AppendAllText(
            report,
            $"{what}: mean {mean:F2} ms, median {Ms(ticks[ticks.Count / 2]):F2} ms, worst {Ms(ticks[^1]):F2} ms, " +
            $"{1000 / Math.Max(mean, 0.01):F0} frames/s if nothing else happened{Environment.NewLine}");
    }

    /// <summary>A key frame of colour bands, as the source would send it.</summary>
    private static List<byte[]> Encoded(int width, int height)
    {
        using var encoder = H264VideoEncoder.TryCreate(width, height, 30, 12000, NullLogger.Instance)!;
        var units = new List<byte[]>();
        for (var attempt = 0; attempt < 5 && units.Count == 0; attempt++)
        {
            foreach (var unit in encoder.Encode(ColourBands(width, height), width, new Rectangle(0, 0, width, height), forceKeyframe: attempt == 0))
            {
                units.Add(unit.Data);
            }
        }

        return units;
    }

    private static byte[]? Decoded(List<byte[]> units, int width, int height, bool useGraphicsCard)
    {
        using var decoder = H264VideoDecoder.TryCreate(width, height, NullLogger.Instance, useGraphicsCard);
        if (decoder is null)
        {
            return null;
        }

        var output = Array.Empty<byte>();
        var produced = false;
        foreach (var unit in units)
        {
            produced |= decoder.DecodeInto(unit, ref output, out var w, out var h) && (w, h) == (width, height);
        }

        return produced ? output : null;
    }

    private static byte[] ColourBands(int width, int height)
    {
        var image = new byte[width * height * 4];
        ReadOnlySpan<(byte B, byte G, byte R)> colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220), (250, 250, 250), (10, 10, 10)];
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

    private static double MeanColourError(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        long error = 0;
        for (var i = 0; i < length; i += 4)
        {
            error += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return error / (double)(length / 4 * 3);
    }
}
