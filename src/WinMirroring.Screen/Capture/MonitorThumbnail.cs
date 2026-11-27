using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.WIC;

namespace WinMirroring.Screen.Capture;

/// <summary>
/// A small picture of a monitor, for showing which is which: the monitors of a device that are not the one being
/// watched, as buttons one can recognise instead of "Монитор 2".
///
/// Taken with plain GDI rather than the capture the stream uses. That one is a session with the compositor that
/// runs for as long as it is open and delivers every frame; this is one shrunken copy of the screen every second
/// or two, for as long as somebody has the menu open, and a copy is all it needs to be.
/// </summary>
internal static class MonitorThumbnail
{
    /// <summary>How wide a thumbnail is. Enough to recognise a desktop by; a dozen kilobytes as a JPEG.</summary>
    public const int Width = 256;

    private const float Quality = 0.6f;

    /// <summary>
    /// A JPEG of what is on the screen in <paramref name="bounds"/> (in the desktop's own pixels), scaled to
    /// <see cref="Width"/> across. Null when it could not be taken.
    /// </summary>
    public static byte[]? Capture(Rectangle bounds, out int width, out int height) => Capture(bounds, Width, out width, out height);

    /// <summary>The widest such a picture is made: for standing in for the whole screen a moment, not for watching.</summary>
    public const int WidestAsked = 960;

    /// <summary>The same, <paramref name="across"/> pixels wide (kept within what such a picture is for).</summary>
    public static byte[]? Capture(Rectangle bounds, int across, out int width, out int height)
    {
        width = Math.Clamp(across <= 0 ? Width : across, 128, WidestAsked);
        height = 0;
        if (bounds.Width < 16 || bounds.Height < 16)
        {
            return null;
        }

        height = Math.Clamp((int)Math.Round(width * (double)bounds.Height / bounds.Width), 16, width * 4);
        var bgr = Shrink(bounds, width, height);
        return bgr is null ? null : EncodeJpeg(bgr, width, height);
    }

    /// <summary>The screen area shrunk to <paramref name="width"/>×<paramref name="height"/>, three bytes a pixel, top row first.</summary>
    private static unsafe byte[]? Shrink(Rectangle bounds, int width, int height)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            bitmap = CreateDIBSection(memory, ref header, 0, out var bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0)
            {
                return null;
            }

            previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, Halftone);
            if (!StretchBlt(memory, 0, 0, width, height, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height, SourceCopy | CaptureLayered))
            {
                return null;
            }

            GdiFlush();
            var bgr = new byte[width * height * 3];
            var source = new ReadOnlySpan<byte>((void*)bits, width * height * 4);
            for (int from = 0, to = 0; from < source.Length; from += 4, to += 3)
            {
                bgr[to] = source[from];
                bgr[to + 1] = source[from + 1];
                bgr[to + 2] = source[from + 2];
            }

            return bgr;
        }
        finally
        {
            if (previous != 0)
            {
                SelectObject(memory, previous);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                DeleteDC(memory);
            }

            ReleaseDC(0, screen);
        }
    }

    internal static byte[] EncodeJpeg(byte[] bgr, int width, int height)
    {
        using var wic = new IWICImagingFactory();
        using var output = new MemoryStream(16 * 1024);
        using (var encoder = wic.CreateEncoder(ContainerFormat.Jpeg, output))
        {
            using var frame = encoder.CreateNewFrame(out var options);
            using (options)
            {
                options.Set("ImageQuality", Quality);
                frame.Initialize(options);
            }

            frame.SetSize((uint)width, (uint)height);
            var format = PixelFormat.Format24bppBGR;
            frame.SetPixelFormat(ref format);
            frame.WritePixels((uint)height, (uint)(width * 3), new ReadOnlySpan<byte>(bgr, 0, width * height * 3));
            frame.Commit();
            encoder.Commit();
        }

        return output.ToArray();
    }

    private const int Halftone = 4;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayered = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint item);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
}
