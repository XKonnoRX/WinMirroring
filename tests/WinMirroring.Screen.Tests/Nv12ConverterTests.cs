using System.Drawing;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The conversion was rewritten to work four pixels at a time across every core, because one pixel at a time on
/// one thread cost nearly twenty-three milliseconds a frame and held a view to about ten frames a second. The point
/// of these tests is that it changed only in speed: the straightforward arithmetic is written out again here and
/// the fast version has to match it byte for byte, including at the edges where a vector no longer fits.
/// </summary>
public class Nv12ConverterTests
{
    public static TheoryData<int, int> Sizes => new()
    {
        { 1920, 1080 },
        { 1280, 720 },
        { 64, 64 },
        { 20, 16 },  // narrower than two vectors: the tail does all of it
        { 18, 4 },   // too small to share out between threads
        { 2, 2 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Nv12ToBgraMatchesTheStraightforwardVersion(int width, int height)
    {
        var codedHeight = (height + 15) / 16 * 16;
        var nv12 = new byte[Nv12Converter.Nv12Size(width, codedHeight)];
        new Random(width * height).NextBytes(nv12);

        var fast = new byte[width * height * 4];
        var reference = new byte[width * height * 4];
        Nv12Converter.Nv12ToBgra(nv12, width, height, fast, width * codedHeight);
        ReferenceNv12ToBgra(nv12, width, height, reference, width * codedHeight);

        Assert.Equal(reference, fast);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void BgraToNv12MatchesTheStraightforwardVersion(int width, int height)
    {
        var frameWidth = width + 8;
        var frame = new byte[frameWidth * (height + 4) * 4];
        new Random(width + height).NextBytes(frame);
        var crop = new Rectangle(4, 2, width, height);

        var fast = new byte[Nv12Converter.Nv12Size(width, height)];
        var reference = new byte[fast.Length];
        Nv12Converter.BgraCropToNv12(frame, frameWidth, crop, fast);
        ReferenceBgraCropToNv12(frame, frameWidth, crop, reference);

        Assert.Equal(reference, fast);
    }

    /// <summary>A grey ramp has to come back as the same grey: a wrong matrix or wrong levels show up at once.</summary>
    [Fact]
    public void GreyRoundTripsThroughBothDirections()
    {
        const int Size = 32;
        var frame = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            var level = (byte)(i % 256);
            frame[(i * 4) + 0] = level;
            frame[(i * 4) + 1] = level;
            frame[(i * 4) + 2] = level;
            frame[(i * 4) + 3] = 255;
        }

        var nv12 = new byte[Nv12Converter.Nv12Size(Size, Size)];
        Nv12Converter.BgraCropToNv12(frame, Size, new Rectangle(0, 0, Size, Size), nv12);
        var back = new byte[frame.Length];
        Nv12Converter.Nv12ToBgra(nv12, Size, Size, back);

        for (var i = 0; i < Size * Size; i++)
        {
            Assert.InRange(back[(i * 4) + 0], Math.Max(0, frame[(i * 4) + 0] - 3), Math.Min(255, frame[(i * 4) + 0] + 3));
            Assert.InRange(back[(i * 4) + 1], Math.Max(0, frame[(i * 4) + 1] - 3), Math.Min(255, frame[(i * 4) + 1] + 3));
            Assert.InRange(back[(i * 4) + 2], Math.Max(0, frame[(i * 4) + 2] - 3), Math.Min(255, frame[(i * 4) + 2] + 3));
            Assert.Equal(255, back[(i * 4) + 3]);
        }
    }

    private static void ReferenceNv12ToBgra(ReadOnlySpan<byte> nv12, int width, int height, byte[] bgra, int uvOffset)
    {
        var uvPlane = uvOffset > 0 ? uvOffset : width * height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = nv12[(y * width) + x] - 16;
                var uv = uvPlane + (y / 2 * width) + (x & ~1);
                var d = nv12[uv] - 128;
                var e = nv12[uv + 1] - 128;
                var c298 = 298 * c;
                var r = (c298 + (409 * e) + 128) >> 8;
                var g = (c298 - (100 * d) - (208 * e) + 128) >> 8;
                var b = (c298 + (516 * d) + 128) >> 8;
                var o = ((y * width) + x) * 4;
                bgra[o] = Clamp(b);
                bgra[o + 1] = Clamp(g);
                bgra[o + 2] = Clamp(r);
                bgra[o + 3] = 255;
            }
        }
    }

    private static void ReferenceBgraCropToNv12(byte[] frame, int frameWidth, Rectangle crop, byte[] nv12)
    {
        var width = crop.Width;
        var height = crop.Height;
        var uvPlane = width * height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = (((crop.Y + y) * frameWidth) + crop.X + x) * 4;
                int b = frame[p], g = frame[p + 1], r = frame[p + 2];
                nv12[(y * width) + x] = (byte)((((66 * r) + (129 * g) + (25 * b) + 128) >> 8) + 16);
            }
        }

        for (var cy = 0; cy < height; cy += 2)
        {
            for (var cx = 0; cx < width; cx += 2)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var p = (((crop.Y + cy + dy) * frameWidth) + crop.X + cx + dx) * 4;
                        sumB += frame[p];
                        sumG += frame[p + 1];
                        sumR += frame[p + 2];
                    }
                }

                var b = sumB >> 2;
                var g = sumG >> 2;
                var r = sumR >> 2;
                var uv = uvPlane + (cy / 2 * width) + cx;
                nv12[uv] = (byte)((((-38 * r) - (74 * g) + (112 * b) + 128) >> 8) + 128);
                nv12[uv + 1] = (byte)((((112 * r) - (94 * g) - (18 * b) + 128) >> 8) + 128);
            }
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
