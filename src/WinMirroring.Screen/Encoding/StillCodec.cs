using Vortice.Mathematics;
using Vortice.WIC;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// One whole picture of a screen that has stopped moving, as sharp as it is on the screen it came from.
///
/// Video is the right thing for a picture that moves, and the wrong thing to read from. H.264 as it is used here
/// keeps colour at half the resolution of brightness in each direction, which is invisible in a film and is
/// exactly what makes coloured text on a dark background — an editor, a terminal — look smeared; and at a
/// streaming bitrate it rounds off fine detail besides. So once the screen has stood still for a moment, it is
/// sent once more as a single image with every pixel's own colour, and the viewer lays that over the video until
/// something moves again.
///
/// JPEG at high quality with no chroma subsampling: nearly indistinguishable from the original for this purpose,
/// a few hundred kilobytes for a desktop, and quick to make and to open. (PNG would be exact, at three times the
/// size and several times the time.)
/// </summary>
internal static class StillCodec
{
    private const float Quality = 0.93f;

    /// <summary>WICJpegYCrCbSubsampling444: every pixel keeps its own colour.</summary>
    private const byte FullColour = 3;

    /// <summary>Encodes a tightly packed BGRA picture.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        var bgr = new byte[width * height * 3];
        for (int from = 0, to = 0; to < bgr.Length; from += 4, to += 3)
        {
            bgr[to] = bgra[from];
            bgr[to + 1] = bgra[from + 1];
            bgr[to + 2] = bgra[from + 2];
        }

        using var wic = new IWICImagingFactory();
        using var output = new MemoryStream(256 * 1024);
        using (var encoder = wic.CreateEncoder(ContainerFormat.Jpeg, output))
        {
            using var frame = encoder.CreateNewFrame(out var options);
            using (options)
            {
                options.Set("ImageQuality", Quality);
                options.Set("JpegYCrCbSubsampling", FullColour);
                frame.Initialize(options);
            }

            frame.SetSize((uint)width, (uint)height);
            var format = PixelFormat.Format24bppBGR;
            frame.SetPixelFormat(ref format);
            frame.WritePixels((uint)height, (uint)(width * 3), new ReadOnlySpan<byte>(bgr));
            frame.Commit();
            encoder.Commit();
        }

        return output.ToArray();
    }

    /// <summary>Decodes into a tightly packed BGRA picture, or null when the bytes are not a picture.</summary>
    public static unsafe (byte[] Bgra, int Width, int Height)? Decode(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            using var wic = new IWICImagingFactory();
            using var input = new MemoryStream(jpeg.ToArray(), writable: false);
            using var decoder = wic.CreateDecoderFromStream(input, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            using var converter = wic.CreateFormatConverter();
            converter.Initialize(frame, PixelFormat.Format32bppBGRA);

            var size = converter.Size;
            if (size.Width is < 1 or > 16384 || size.Height is < 1 or > 16384)
            {
                return null;
            }

            var bgra = new byte[size.Width * size.Height * 4];
            fixed (byte* pixels = bgra)
            {
                converter.CopyPixels(new RectI(0, 0, size.Width, size.Height), (uint)(size.Width * 4), (uint)bgra.Length, (nint)pixels);
            }

            return (bgra, size.Width, size.Height);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }
}
