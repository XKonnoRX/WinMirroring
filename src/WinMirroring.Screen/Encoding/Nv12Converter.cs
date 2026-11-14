using System.Drawing;
using System.Runtime.Intrinsics;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// CPU conversion between the captured BGRA pixels and the planar NV12 the H.264 encoder/decoder use (BT.601
/// studio-swing, the same matrix both ways so the round-trip only loses what the codec loses).
///
/// <see cref="Nv12ToBgra"/> is the hottest loop in the whole program: the viewer runs it on every frame it
/// decodes, two million pixels of it, and until it was measured it ran one pixel at a time on one thread — nearly
/// twenty-three milliseconds for one 1080p frame on a fast machine, which is three or four times that on a tablet.
/// That single loop was what held a view to about ten frames a second and, with frames waiting their turn behind
/// it, was most of the delay between moving something on one screen and seeing it on the other. It now does four
/// pixels at a time across every core, with exactly the same integer arithmetic, so the result is byte for byte
/// what it was.
/// </summary>
internal static class Nv12Converter
{
    /// <summary>Below this a picture is small enough that handing rows to other threads costs more than it saves.</summary>
    private const int ParallelFromHeight = 64;
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
        var height = crop.Height;
        fixed (byte* src = frame)
        fixed (byte* dst = nv12)
        {
            var source = (nint)src;
            var target = (nint)dst;
            var pairs = height / 2;
            if (height < ParallelFromHeight || pairs < 2)
            {
                for (var pair = 0; pair < pairs; pair++)
                {
                    BgraPairToNv12(src, dst, frameWidth, crop, pair);
                }

                return;
            }

            Parallel.For(0, pairs, pair => BgraPairToNv12((byte*)source, (byte*)target, frameWidth, crop, pair));
        }
    }

    /// <summary>Two rows of luma and the one row of chroma they share: the smallest independent piece of a frame.</summary>
    private static unsafe void BgraPairToNv12(byte* src, byte* dst, int frameWidth, Rectangle crop, int pair)
    {
        var width = crop.Width;
        var uvPlane = width * crop.Height;
        var cy = pair * 2;

        for (var y = cy; y <= cy + 1; y++)
        {
            var srcRow = src + ((((long)(crop.Y + y) * frameWidth) + crop.X) * 4);
            var yRow = dst + ((long)y * width);
            for (var x = 0; x < width; x++)
            {
                var p = srcRow + (x * 4);
                int b = p[0];
                int g = p[1];
                int r = p[2];
                yRow[x] = (byte)((((66 * r) + (129 * g) + (25 * b) + 128) >> 8) + 16);
            }
        }

        // Chroma: one U/V pair per 2×2 block, averaged from the four pixels for less blockiness.
        var uvRow = dst + uvPlane + ((long)pair * width);
        for (var cx = 0; cx < width; cx += 2)
        {
            int sumB = 0, sumG = 0, sumR = 0;
            for (var dy = 0; dy < 2; dy++)
            {
                var srcRow = src + ((((long)(crop.Y + cy + dy) * frameWidth) + crop.X + cx) * 4);
                for (var dx = 0; dx < 2; dx++)
                {
                    var p = srcRow + (dx * 4);
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                }
            }

            var b = sumB >> 2;
            var g = sumG >> 2;
            var r = sumR >> 2;
            var u = (((-38 * r) - (74 * g) + (112 * b) + 128) >> 8) + 128;
            var v = (((112 * r) - (94 * g) - (18 * b) + 128) >> 8) + 128;
            var uv = uvRow + cx;
            uv[0] = (byte)u;
            uv[1] = (byte)v;
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
            // Pointers, not spans, because the rows are shared out between threads and a span cannot be captured.
            // The pinning above lasts for the whole of Parallel.For, which does not return until every row is done.
            var source = (nint)src;
            var target = (nint)dst;
            var pairs = height / 2;
            if (height < ParallelFromHeight || pairs < 2)
            {
                for (var y = 0; y < height; y++)
                {
                    Nv12RowToBgra(src, dst, uvPlane, width, y);
                }

                return;
            }

            // Two rows at a time: both read the same row of chroma, so a pair is the smallest independent piece.
            Parallel.For(0, pairs, pair =>
            {
                var s = (byte*)source;
                var d = (byte*)target;
                Nv12RowToBgra(s, d, uvPlane, width, pair * 2);
                Nv12RowToBgra(s, d, uvPlane, width, (pair * 2) + 1);
            });

            if ((height & 1) != 0)
            {
                Nv12RowToBgra(src, dst, uvPlane, width, height - 1);
            }
        }
    }

    /// <summary>
    /// One row, four pixels at a time. Each byte is shuffled straight into its own 32-bit lane (an index past the
    /// end of the vector reads as zero, which is what clears the other three bytes of the lane), so there is no
    /// widening to do; the arithmetic is the same as the scalar version below it, and the three results are packed
    /// back into one lane each as the B, G, R and A bytes of a pixel.
    /// </summary>
    private static unsafe void Nv12RowToBgra(byte* src, byte* dst, int uvPlane, int width, int y)
    {
        var yRow = src + ((long)y * width);
        var uvRow = src + uvPlane + ((long)(y / 2) * width);
        var outRow = dst + ((long)y * width * 4);
        var x = 0;

        if (Vector128.IsHardwareAccelerated && width >= Vector128<byte>.Count)
        {
            var lumaLanes = Vector128.Create((byte)0, 255, 255, 255, 1, 255, 255, 255, 2, 255, 255, 255, 3, 255, 255, 255);
            var uLanes = Vector128.Create((byte)0, 255, 255, 255, 0, 255, 255, 255, 2, 255, 255, 255, 2, 255, 255, 255);
            var vLanes = Vector128.Create((byte)1, 255, 255, 255, 1, 255, 255, 255, 3, 255, 255, 255, 3, 255, 255, 255);
            var sixteen = Vector128.Create(16);
            var half = Vector128.Create(128);
            var top = Vector128.Create(255);
            var alpha = Vector128.Create(unchecked((int)0xFF000000));

            // Each step reads sixteen bytes to use four of them, so it stops while a whole vector is still inside
            // the row; the few pixels left over are done one at a time.
            for (; x + Vector128<byte>.Count <= width; x += 4)
            {
                var luma = Vector128.Shuffle(Vector128.Load(yRow + x), lumaLanes).AsInt32();
                var chroma = Vector128.Load(uvRow + x);
                var c = luma - sixteen;
                var d = Vector128.Shuffle(chroma, uLanes).AsInt32() - half;
                var e = Vector128.Shuffle(chroma, vLanes).AsInt32() - half;

                var c298 = c * Vector128.Create(298);
                var r = (c298 + (e * Vector128.Create(409)) + half) >> 8;
                var g = (c298 - (d * Vector128.Create(100)) - (e * Vector128.Create(208)) + half) >> 8;
                var b = (c298 + (d * Vector128.Create(516)) + half) >> 8;

                r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), top);
                g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), top);
                b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), top);

                // A pixel in memory is B, G, R, A, which little-endian makes one integer of b | g<<8 | r<<16 | a<<24.
                (b | (g << 8) | (r << 16) | alpha).AsByte().Store(outRow + (x * 4));
            }
        }

        for (; x < width; x++)
        {
            var c = yRow[x] - 16;
            var uv = uvRow + (x & ~1);
            var d = uv[0] - 128;
            var e = uv[1] - 128;
            var c298 = 298 * c;
            var r = ((c298 + (409 * e) + 128) >> 8);
            var g = ((c298 - (100 * d) - (208 * e) + 128) >> 8);
            var b = ((c298 + (516 * d) + 128) >> 8);
            var o = outRow + (x * 4);
            o[0] = Clamp(b);
            o[1] = Clamp(g);
            o[2] = Clamp(r);
            o[3] = 255;
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
