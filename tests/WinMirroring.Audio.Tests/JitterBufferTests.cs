using WinMirroring.Audio.Playback;

namespace WinMirroring.Audio.Tests;

public class JitterBufferTests
{
    private static float[] Ms(int ms, float value = 0.5f) =>
        Enumerable.Repeat(value, AudioFormat.MillisecondsToValues(ms)).ToArray();

    private static JitterBuffer Buffer(int targetMs) => new(targetMs, AudioFormat.FrameMs);

    [Fact]
    public void PlaysSilenceUntilTheTargetIsBuffered()
    {
        var buffer = Buffer(40);
        var output = new float[AudioFormat.MillisecondsToValues(10)];

        buffer.Write(Ms(30));
        Assert.Equal(output.Length, buffer.Read(output));
        Assert.All(output, v => Assert.Equal(0f, v));

        buffer.Write(Ms(10));
        buffer.Read(output);

        // Eased in from silence rather than starting at full level: the first few milliseconds ramp up.
        Assert.True(output[0] < 0.5f);
        Assert.Equal(0.5f, output[^1]);
        Assert.InRange(buffer.BufferedMs, 29.9, 30.1);
    }

    [Fact]
    public void RunningOutFadesDownInsteadOfCuttingToSilence()
    {
        var buffer = Buffer(20);
        var output = new float[AudioFormat.MillisecondsToValues(10)];
        buffer.Write(Ms(20));
        buffer.Read(output);
        buffer.Read(output);

        buffer.Read(output);

        Assert.Equal(1, buffer.Underruns);
        Assert.InRange(output[0], 0.1f, 0.5f); // on its way down from what was playing
        Assert.Equal(0f, output[^1]);

        buffer.Write(Ms(10));
        buffer.Read(output);
        Assert.All(output, v => Assert.Equal(0f, v)); // still refilling to the target
    }

    /// <summary>
    /// A backlog is taken off in small steps, and the join is eased rather than being a step change in the middle
    /// of the waveform — which is what a click is.
    /// </summary>
    [Fact]
    public void BacklogIsTrimmedInStepsAndEased()
    {
        var buffer = Buffer(30);
        var output = new float[AudioFormat.MillisecondsToValues(10)];
        buffer.Write(Ms(30));
        buffer.Read(output);

        buffer.Write(Ms(200, 0.25f)); // e.g. a burst after a network stall

        Assert.Equal(1, buffer.Trims);
        Assert.InRange(buffer.BufferedMs, 199, 201); // one step of 20 ms came off, not 170 at once

        buffer.Read(output);
        Assert.NotEqual(0.25f, output[0]); // the join is ramped, not a jump
        Assert.Equal(0.25f, output[^1]);

        // Writing on keeps taking a step off each time, so the delay does come back down.
        for (var i = 0; i < 10; i++)
        {
            buffer.Write(Ms(AudioFormat.FrameMs, 0.25f));
            buffer.Read(output);
        }

        Assert.InRange(buffer.BufferedMs, 0, 100);
    }

    /// <summary>
    /// The whole point of the adaptive target: a link that delivers unevenly gets a deeper buffer, which is what
    /// stops it running dry, and the depth comes back down slowly once it settles.
    /// </summary>
    [Fact]
    public void AimingGrowsWithJitterAndComesBackSlowly()
    {
        var buffer = Buffer(40);
        Assert.Equal(40, buffer.AimedMs);

        buffer.Aim(30);

        Assert.InRange(buffer.AimedMs, 145, 155); // 40 configured + one frame + three times 30
        var raised = buffer.AimedMs;

        buffer.Aim(0);
        Assert.InRange(buffer.AimedMs, 60, raised - 1); // down a step, not back to the floor at once
    }

    [Fact]
    public void AimingNeverGoesBelowTheConfiguredTargetOrAboveTheCeiling()
    {
        var buffer = Buffer(60);

        buffer.Aim(0);
        Assert.Equal(60 + AudioFormat.FrameMs, buffer.AimedMs); // the configured amount plus one frame in hand

        buffer.Aim(1000);
        Assert.Equal(200, buffer.AimedMs);
    }
}
