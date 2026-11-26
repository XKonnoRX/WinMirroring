using System.Drawing;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// Makes a picture smaller in memory, for the stream whose graphics card will not do it.
///
/// A picture is sent at the size the viewer shows it at (see ScreenSource.FitOutput): a tablet showing a 2560×1440
/// screen on its own smaller one otherwise decodes, reads back and scales down every one of the pixels it then
/// throws away, and that — not the link — was most of the delay between the two. The card does the scaling when it
/// can, as part of turning the frame into NV12. But a card that does not produce the colours the two ends agreed on
/// is not used for that at all (see <see cref="GpuNv12Converter"/>), and then nothing scaled: the frame was
/// converted by the processor at its full size and sent at its full size. This is the missing half of that path.
///
/// Two ways, by how much smaller. Down to half size each pixel of the result is a blend of the four it falls
/// between, which is what the card would do. Below half that would skip pixels of the original altogether — thin
/// lines and small text would flicker in and out as they moved — so there every pixel of the result is the average
/// of all the pixels it covers.
/// </summary>
internal static class BgraScaler
{
    /// <summary>Rows below which starting threads costs more than the work.</summary>
    private const int ParallelFromRows = 64;

    /// <summary>
    /// Scales the <paramref name="crop"/> part of <paramref name="source"/> (rows of <paramref name="sourceWidth"/>
    /// pixels, four bytes each) into <paramref name="target"/> as a tightly packed
    /// <paramref name="width"/>×<paramref name="height"/> picture.
    /// </summary>
    public static unsafe void Scale(byte[] source, int sourceWidth, Rectangle crop, byte[] target, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (width < 1 || height < 1 || crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0
            || crop.Right > sourceWidth
            || (long)crop.Bottom * sourceWidth * 4 > source.Length
            || (long)width * height * 4 > target.Length)
        {
            throw new ArgumentException("The picture to scale does not fit the buffers it was given.");
        }

        var averaging = crop.Width > width * 2 || crop.Height > height * 2;
        var columns = averaging ? Spans(crop.Width, width) : Blends(crop.Width, width);
        var rows = averaging ? Spans(crop.Height, height) : Blends(crop.Height, height);

        fixed (byte* src = source)
        fixed (byte* dst = target)
        fixed (int* cols = columns)
        fixed (int* rws = rows)
        {
            // The pinning lasts for the whole of Parallel.For, which does not return until every row is done.
            var origin = (nint)(src + (((long)crop.Y * sourceWidth) + crop.X) * 4);
            var output = (nint)dst;
            var colTable = (nint)cols;
            var rowTable = (nint)rws;
            var stride = sourceWidth * 4;

            if (height < ParallelFromRows)
            {
                for (var y = 0; y < height; y++)
                {
                    Row(averaging, (byte*)origin, stride, (byte*)output + ((long)y * width * 4), width, (int*)colTable, (int*)rowTable + (y * 3));
                }

                return;
            }

            Parallel.For(0, height, y =>
                Row(averaging, (byte*)origin, stride, (byte*)output + ((long)y * width * 4), width, (int*)colTable, (int*)rowTable + (y * 3)));
        }
    }

    private static unsafe void Row(bool averaging, byte* origin, int stride, byte* output, int width, int* columns, int* row)
    {
        if (averaging)
        {
            AverageRow(origin, stride, output, width, columns, row);
        }
        else
        {
            BlendRow(origin, stride, output, width, columns, row);
        }
    }

    /// <summary>
    /// For each pixel of the result along one axis: the first of the two source pixels it falls between, the
    /// second, and how far towards the second it is, out of 256. Pixel centres are what is lined up, so the result
    /// neither shifts nor loses half a pixel at the edges.
    /// </summary>
    private static int[] Blends(int from, int to)
    {
        var table = new int[to * 3];
        var ratio = from / (double)to;
        for (var i = 0; i < to; i++)
        {
            var at = Math.Clamp(((i + 0.5) * ratio) - 0.5, 0, from - 1);
            var first = (int)at;
            table[i * 3] = first;
            table[(i * 3) + 1] = Math.Min(first + 1, from - 1);
            table[(i * 3) + 2] = (int)Math.Round((at - first) * 256);
        }

        return table;
    }

    /// <summary>For each pixel of the result along one axis: the first source pixel it covers and one past the last.</summary>
    private static int[] Spans(int from, int to)
    {
        var table = new int[to * 3];
        for (var i = 0; i < to; i++)
        {
            var first = (int)((long)i * from / to);
            var end = Math.Max(first + 1, (int)((long)(i + 1) * from / to));
            table[i * 3] = first;
            table[(i * 3) + 1] = Math.Min(end, from);
            table[(i * 3) + 2] = 0;
        }

        return table;
    }

    private static unsafe void BlendRow(byte* origin, int stride, byte* output, int width, int* columns, int* row)
    {
        var upper = origin + ((long)row[0] * stride);
        var lower = origin + ((long)row[1] * stride);
        var down = row[2];
        var up = 256 - down;
        for (var x = 0; x < width; x++)
        {
            var left = columns[0] * 4;
            var right = columns[1] * 4;
            var toRight = columns[2];
            var toLeft = 256 - toRight;
            columns += 3;

            var a = upper + left;
            var b = upper + right;
            var c = lower + left;
            var d = lower + right;
            output[0] = (byte)(((((a[0] * toLeft) + (b[0] * toRight)) * up) + (((c[0] * toLeft) + (d[0] * toRight)) * down) + 32768) >> 16);
            output[1] = (byte)(((((a[1] * toLeft) + (b[1] * toRight)) * up) + (((c[1] * toLeft) + (d[1] * toRight)) * down) + 32768) >> 16);
            output[2] = (byte)(((((a[2] * toLeft) + (b[2] * toRight)) * up) + (((c[2] * toLeft) + (d[2] * toRight)) * down) + 32768) >> 16);
            output[3] = 255;
            output += 4;
        }
    }

    private static unsafe void AverageRow(byte* origin, int stride, byte* output, int width, int* columns, int* row)
    {
        var top = row[0];
        var bottom = row[1];
        for (var x = 0; x < width; x++)
        {
            var left = columns[0];
            var right = columns[1];
            columns += 3;

            uint blue = 0, green = 0, red = 0;
            for (var y = top; y < bottom; y++)
            {
                var pixel = origin + ((long)y * stride) + (left * 4);
                for (var i = left; i < right; i++)
                {
                    blue += pixel[0];
                    green += pixel[1];
                    red += pixel[2];
                    pixel += 4;
                }
            }

            var count = (uint)((right - left) * (bottom - top));
            var half = count / 2;
            output[0] = (byte)((blue + half) / count);
            output[1] = (byte)((green + half) / count);
            output[2] = (byte)((red + half) / count);
            output[3] = 255;
            output += 4;
        }
    }
}
