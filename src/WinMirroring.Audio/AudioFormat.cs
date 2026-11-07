using NAudio.Wave;

namespace WinMirroring.Audio;

/// <summary>The one format on the wire and in the mixer: 48 kHz stereo float, 10 ms Opus frames.</summary>
internal static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMs = 10;
    public const int FrameSamples = SampleRate * FrameMs / 1000;     // per channel
    public const int FrameValues = FrameSamples * Channels;          // interleaved
    public const int MaxOpusPacket = 1275;
    public const byte MediaKind = 1;
    public const string Codec = "opus";

    public static WaveFormat Wave { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static int MillisecondsToValues(int ms) => SampleRate / 1000 * ms * Channels;
}
