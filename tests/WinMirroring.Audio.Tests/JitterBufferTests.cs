using WinMirroring.Audio.Playback;

namespace WinMirroring.Audio.Tests;

public class JitterBufferTests
{
    private static float[] Ms(int ms, float value = 0.5f) =>
        Enumerable.Repeat(value, AudioFormat.MillisecondsToValues(ms)).ToArray();

    [Fact]
    public void PlaysSilenceUntilTheTargetIsBuffered()
    {
        var buffer = new JitterBuffer(targetMs: 40);
        var output = new float[AudioFormat.MillisecondsToValues(10)];

        buffer.Write(Ms(30));
        Assert.Equal(output.Length, buffer.Read(output));
        Assert.All(output, v => Assert.Equal(0f, v));

        buffer.Write(Ms(10));
        buffer.Read(output);
        Assert.All(output, v => Assert.Equal(0.5f, v));
        Assert.InRange(buffer.BufferedMs, 29.9, 30.1);
    }

    [Fact]
    public void UnderrunRebuffersInsteadOfStuttering()
    {
        var buffer = new JitterBuffer(targetMs: 20);
        var output = new float[AudioFormat.MillisecondsToValues(10)];
        buffer.Write(Ms(20));
        buffer.Read(output);
        buffer.Read(output);

        buffer.Read(output);
        Assert.Equal(1, buffer.Underruns);
        Assert.All(output, v => Assert.Equal(0f, v));

        buffer.Write(Ms(10));
        buffer.Read(output);
        Assert.All(output, v => Assert.Equal(0f, v)); // still refilling to the target
    }

    [Fact]
    public void GrowingBacklogIsTrimmedBackToTheTarget()
    {
        var buffer = new JitterBuffer(targetMs: 30);
        var output = new float[AudioFormat.MillisecondsToValues(10)];
        buffer.Write(Ms(30));
        buffer.Read(output);

        buffer.Write(Ms(200)); // e.g. a burst after a network stall

        Assert.Equal(1, buffer.Trims);
        Assert.InRange(buffer.BufferedMs, 29.9, 30.1);
    }
}
