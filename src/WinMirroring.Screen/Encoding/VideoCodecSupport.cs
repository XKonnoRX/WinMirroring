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
