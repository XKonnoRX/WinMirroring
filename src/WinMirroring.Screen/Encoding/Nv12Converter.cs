using System.Drawing;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// CPU conversion between the captured BGRA pixels and the planar NV12 the H.264 encoder/decoder use (BT.601
/// studio-swing, the same matrix both ways so the round-trip only loses what the codec loses). This is the "first
/// cut" path the design calls for; a GPU shader or Video Processor MFT is the later zero-copy optimisation.
/// </summary>
internal static class Nv12Converter
{
    /// <summary>NV12 needs even dimensions; this rounds a length down to the nearest even number.</summary>
    public static int Even(int value) => value & ~1;

    /// <summary>Bytes a tightly packed NV12 image of <paramref name="width"/>×<paramref name="height"/> occupies.</summary>
    public static int Nv12Size(int width, int height) => width * height + width * height / 2;

    /// <summary>
    /// How many luma rows an NV12 buffer of <paramref name="length"/> bytes really holds. A decoder's frame is as
    /// tall as the coded picture (a multiple of 16), so this is where its chroma plane begins.
    /// </summary>
    public static int RowsIn(int length, int width, int fallbackHeight)
    {
        if (width <= 0 || length <= 0)
        {
            return fallbackHeight;
        }

        var rows = (int)(2L * length / (3L * width));
        return rows >= fallbackHeight ? rows & ~1 : fallbackHeight;
    }

    /// <summary>
    /// Converts the <paramref name="crop"/> region of a BGRA frame (<paramref name="frameWidth"/> pixels per row)
    /// into a tightly packed NV12 buffer. The crop width and height must be even.
    /// </summary>
    public static unsafe void BgraCropToNv12(byte[] frame, int frameWidth, Rectangle crop, byte[] nv12)
    {
        var width = crop.Width;
        var height = crop.Height;
        var uvPlane = width * height;

        fixed (byte* src = frame)
        fixed (byte* dst = nv12)
        {
            for (var y = 0; y < height; y++)
            {
                var srcRow = src + ((long)(crop.Y + y) * frameWidth + crop.X) * 4;
                var yRow = dst + (long)y * width;
                for (var x = 0; x < width; x++)
                {
                    var p = srcRow + x * 4;
                    int b = p[0];
                    int g = p[1];
                    int r = p[2];
                    yRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
            }

            // Chroma: one U/V pair per 2×2 block, averaged from the four pixels for less blockiness.
            for (var cy = 0; cy < height; cy += 2)
            {
                var uvRow = dst + uvPlane + (long)(cy / 2) * width;
                for (var cx = 0; cx < width; cx += 2)
                {
                    int sumB = 0, sumG = 0, sumR = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var srcRow = src + ((long)(crop.Y + cy + dy) * frameWidth + crop.X + cx) * 4;
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var p = srcRow + dx * 4;
                            sumB += p[0];
                            sumG += p[1];
                            sumR += p[2];
                        }
                    }

                    var b = sumB >> 2;
                    var g = sumG >> 2;
                    var r = sumR >> 2;
                    var u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    var v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    var uv = uvRow + cx;
                    uv[0] = (byte)u;
                    uv[1] = (byte)v;
                }
            }
        }
    }

    /// <summary>
    /// Converts an NV12 image into BGRA (alpha 255) in <paramref name="bgra"/>, which must hold
    /// <paramref name="width"/>×<paramref name="height"/>×4 bytes.
    /// </summary>
    /// <param name="uvOffset">
    /// Where the chroma plane starts. A decoder hands back a frame whose luma plane is as tall as the coded
    /// picture, which H.264 rounds up to a multiple of 16: for a 1080-line screen the chroma begins after 1088
    /// rows, not 1080. Reading it from the wrong place leaves the picture grey with a shifted colour ghost over
    /// it. Zero means a tightly packed image, where it follows <paramref name="height"/> rows.
    /// </param>
    public static unsafe void Nv12ToBgra(ReadOnlySpan<byte> nv12, int width, int height, byte[] bgra, int uvOffset = 0)
    {
        var uvPlane = uvOffset > 0 ? uvOffset : width * height;
        fixed (byte* src = nv12)
        fixed (byte* dst = bgra)
        {
            for (var y = 0; y < height; y++)
            {
                var yRow = src + (long)y * width;
                var uvRow = src + uvPlane + (long)(y / 2) * width;
                var outRow = dst + (long)y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var c = yRow[x] - 16;
                    var uv = uvRow + (x & ~1);
                    var d = uv[0] - 128;
                    var e = uv[1] - 128;
                    var c298 = 298 * c;
                    var r = (c298 + 409 * e + 128) >> 8;
                    var g = (c298 - 100 * d - 208 * e + 128) >> 8;
                    var b = (c298 + 516 * d + 128) >> 8;
                    var o = outRow + x * 4;
                    o[0] = Clamp(b);
                    o[1] = Clamp(g);
                    o[2] = Clamp(r);
                    o[3] = 255;
                }
            }
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
