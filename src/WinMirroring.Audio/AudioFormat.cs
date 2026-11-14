using NAudio.Wave;

namespace WinMirroring.Audio;

/// <summary>
/// The one format on the wire and in the mixer: 48 kHz stereo float, 20 ms Opus frames.
///
/// Twenty milliseconds rather than ten: it halves the packet rate (fifty a second instead of a hundred) and with it
/// both the fixed cost per packet — thirty bytes of seal, four of timestamp, twenty-eight of UDP and IP, which at
/// ten milliseconds came to about a quarter of the sound's traffic — and the work the capture thread hands on. On
/// Wi-Fi the number of packets is what costs, not their size, because each one takes its own turn in the air. The
/// ten extra milliseconds of delay are invisible behind a playout buffer of forty and more.
/// </summary>
internal static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000;     // per channel
    public const int FrameValues = FrameSamples * Channels;          // interleaved
    public const int MaxOpusPacket = 1275;
    public const byte MediaKind = 1;
    public const string Codec = "opus";

    public static WaveFormat Wave { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static int MillisecondsToValues(int ms) => SampleRate / 1000 * ms * Channels;
}
