using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Audio.Capture;

namespace WinMirroring.Audio.Tests;

/// <summary>Needs a real audio endpoint, so it runs only on request (--explicit only).</summary>
public class AudioCaptureSmokeTests
{
    [Fact(Explicit = true)]
    public async Task CaptureStartsAndDeliversWholeFrames()
    {
        await using var capture = await AudioCapture.CreateAsync(NullLogger.Instance);
        var frames = 0;
        var wrongSize = 0;
        capture.FrameReady += frame =>
        {
            if (frame.Length == AudioFormat.FrameValues)
            {
                Interlocked.Increment(ref frames);
            }
            else
            {
                Interlocked.Increment(ref wrongSize);
            }
        };

        capture.Start();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        var report = $"{capture.Description}: {frames} frames in 1.5 s";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "winmirroring-capture-smoke.txt"), report, TestContext.Current.CancellationToken);
        Assert.Equal(0, wrongSize);
        Assert.False(capture.IncludesOwnPlayback, "process loopback should be available on Windows 11");
    }
}
