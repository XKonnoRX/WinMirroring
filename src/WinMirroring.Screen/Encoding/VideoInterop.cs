using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Shared Media Foundation glue for the H.264 screen path: the subtype GUIDs Vortice does not predefine, the
/// one-time platform start-up, small attribute/keyframe helpers, and the <c>ICodecAPI</c> interop used to tune the
/// hardware encoder. Everything here is best-effort: a failure means the caller falls back to the JPEG tile path.
/// </summary>
internal static class VideoInterop
{
    /// <summary>MFVideoFormat_NV12 (FourCC 'NV12').</summary>
    public static readonly Guid Nv12 = new(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>MFVideoFormat_H264 (FourCC 'H264'): the Annex-B elementary stream with inline SPS/PPS on IDR frames.</summary>
    public static readonly Guid H264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>MF_LOW_LATENCY, identical GUID to CODECAPI_AVLowLatencyMode.</summary>
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04bab53a");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const int MF_E_NO_EVENTS = unchecked((int)0xC00D3E85);
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT_OR_NOTACCEPTING = unchecked((int)0xC00D36B5);

    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    public const int MF_EVENT_FLAG_NO_WAIT = 0x1;

    /// <summary>eAVEncH264VProfile_Main.</summary>
    public const uint H264ProfileMain = 77;

    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const uint InterlaceProgressive = 2;

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;

    private static readonly object StartupGate = new();
    private static bool _started;

    /// <summary>Starts Media Foundation once per process (full, not the light version); left running for its lifetime.</summary>
    public static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (_started)
            {
                return;
            }

            MediaFactory.MFStartup(false).CheckError();
            _started = true;
        }
    }

    /// <summary>Packs two 32-bit values into one attribute value (width/height, numerator/denominator).</summary>
    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>
    /// True when the Annex-B access unit carries a key frame: it contains a NAL unit of type 5 (IDR slice) or
    /// type 7 (sequence parameter set). Scans for 3-byte start codes, which the 4-byte form also ends with.
    /// </summary>
    public static bool ContainsKeyframe(ReadOnlySpan<byte> annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                var nalType = annexB[i + 3] & 0x1F;
                if (nalType is 5 or 7)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>A small subset of PROPVARIANT: only the unsigned-int and boolean values the encoder tuning needs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CodecPropVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public long Value;

    private const ushort VtI4 = 3;
    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;

    public static CodecPropVariant UInt32(uint value) => new() { Vt = VtUi4, Value = value };

    public static CodecPropVariant Int32(int value) => new() { Vt = VtI4, Value = (uint)value };

    public static CodecPropVariant Bool(bool value) => new() { Vt = VtBool, Value = value ? -1 : 0 };
}

/// <summary>
/// Classic COM declaration of <c>ICodecAPI</c> (not covered by Vortice). Methods are declared in vtable order up to
/// <see cref="SetValue"/>, which is all that is used; later methods are omitted on purpose.
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig]
    int IsSupported(ref Guid api);

    [PreserveSig]
    int IsModifiable(ref Guid api);

    [PreserveSig]
    int GetParameterRange(ref Guid api, out CodecPropVariant min, out CodecPropVariant max, out CodecPropVariant delta);

    [PreserveSig]
    int GetParameterValues(ref Guid api, out nint values, out uint count);

    [PreserveSig]
    int GetDefaultValue(ref Guid api, out CodecPropVariant value);

    [PreserveSig]
    int GetValue(ref Guid api, out CodecPropVariant value);

    [PreserveSig]
    int SetValue(ref Guid api, ref CodecPropVariant value);
}
