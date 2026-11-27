using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Whether this machine can take part in the hardware H.264 screen path, probed once by actually creating (and
/// disposing) the encoder and decoder transforms. The result is a property of the hardware, so it is cached for
/// the process. A device only uses H.264 when it can encode and the peer advertised it can decode.
/// </summary>
internal static class VideoCodecSupport
{
    private static readonly Lazy<bool> EncodeProbe = new(ProbeEncode);
    private static readonly Lazy<bool> DecodeProbe = new(ProbeDecode);

    /// <summary>True when a hardware H.264 encoder MFT can be configured here.</summary>
    public static bool CanEncodeH264 => EncodeProbe.Value;

    /// <summary>True when the H.264 decoder MFT can be configured here (effectively always on Windows).</summary>
    public static bool CanDecodeH264 => DecodeProbe.Value;

    private static readonly Lazy<bool> HevcEncodeProbe = new(() => Probe(() => H264VideoEncoder.TryCreate(640, 480, 30, 4000, NullLogger.Instance, VideoFormat.Hevc)));
    private static readonly Lazy<bool> HevcDecodeProbe = new(ProbeHevcDecode);

    /// <summary>
    /// On the graphics card, or not at all: decoded in memory, HEVC pictures come out wrong here (measured), so a
    /// device whose card does not take the stream says it cannot decode HEVC and is sent H.264.
    /// </summary>
    private static bool ProbeHevcDecode()
    {
        try
        {
            using var decoder = H264VideoDecoder.TryCreate(640, 480, NullLogger.Instance, format: VideoFormat.Hevc);
            return decoder is { UsesGraphicsCard: true };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A hardware HEVC encoder is here (most cards of the last several years have one).</summary>
    public static bool CanEncodeHevc => HevcEncodeProbe.Value;

    /// <summary>An HEVC decoder is here — which on Windows means the HEVC Video Extension is installed.</summary>
    public static bool CanDecodeHevc => HevcDecodeProbe.Value;

    private static bool Probe(Func<IDisposable?> create)
    {
        try
        {
            using var made = create();
            return made is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ProbeEncode()
    {
        try
        {
            using var encoder = H264VideoEncoder.TryCreate(640, 480, 30, 4000, NullLogger.Instance);
            return encoder is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ProbeDecode()
    {
        try
        {
            using var decoder = H264VideoDecoder.TryCreate(640, 480, NullLogger.Instance);
            return decoder is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
