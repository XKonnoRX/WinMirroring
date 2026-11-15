using System.Drawing;
using System.Runtime.InteropServices;

namespace WinMirroring.Screen.Capture;

/// <summary>One pointer shape as pixels, with the point in it that is actually the pointer.</summary>
internal sealed record CursorPicture(int Width, int Height, int HotspotX, int HotspotY, byte[] Pixels);

/// <summary>
/// Reads this device's pointer: where it is, which shape it wears, and that shape's pixels.
///
/// Why this exists rather than letting the capture include the pointer, which it used to: a pointer baked into the
/// picture is a change in the picture, so moving the mouse became a video frame — encoded, sent, decoded and drawn,
/// arriving tens of milliseconds later and only as often as frames do. Sent separately it is a dozen bytes at the
/// rate the mouse itself moves, and the viewer draws it; this is what RDP does, and it is most of why RDP feels
/// immediate even when its picture is not.
/// </summary>
internal static class CursorReader
{
    private const int CursorShowing = 1;

    /// <summary>
    /// The pointer right now: its shape handle, where it is on this desktop, and whether it is being shown at all
    /// (it is hidden while typing, and suppressed entirely while the device is being used by touch).
    /// </summary>
    public static bool TryRead(out nint shape, out Point position, out bool visible)
    {
        shape = 0;
        position = default;
        visible = false;

        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info))
        {
            return false;
        }

        shape = info.hCursor;
        position = new Point(info.ptScreenPos.X, info.ptScreenPos.Y);
        visible = (info.flags & CursorShowing) != 0 && info.hCursor != 0;
        return true;
    }

    /// <summary>
    /// Draws one shape into premultiplied BGRA, which is what a window paints with. Null when the shape cannot be
    /// read at all.
    ///
    /// Drawn twice, once over black and once over white, because that is the only way to recover what is see-through
    /// in it. Half of Windows' pointers — the plain arrow among them — carry no transparency of their own: they are
    /// a mask that says which pixels to keep and a picture that says what colour to make them, and drawing one over
    /// a single background leaves nothing behind that says which was which. Over black a pixel comes out as its
    /// colour times its opacity; over white, that plus what shows through. The difference between the two is what
    /// shows through, so one subtraction gives the opacity and the black drawing is already the colour premultiplied
    /// by it.
    /// </summary>
    public static CursorPicture? TryDraw(nint shape)
    {
        if (shape == 0 || !GetIconInfo(shape, out var icon))
        {
            return null;
        }

        try
        {
            if (!TrySize(icon, out var width, out var height))
            {
                return null;
            }

            var dc = CreateCompatibleDC(0);
            if (dc == 0)
            {
                return null;
            }

            nint dib = 0;
            nint previous = 0;
            try
            {
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down, so the rows come out the way everything else here holds them
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                };

                dib = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
                if (dib == 0 || bits == 0)
                {
                    return null;
                }

                previous = SelectObject(dc, dib);
                var pixels = width * height;
                var overBlack = Draw(dc, shape, bits, width, height, pixels, 0x00000000u);
                var overWhite = Draw(dc, shape, bits, width, height, pixels, 0x00FFFFFFu);
                if (overBlack is null || overWhite is null)
                {
                    return null;
                }

                return new CursorPicture(width, height, icon.xHotspot, icon.yHotspot, Combine(overBlack, overWhite, pixels));
            }
            finally
            {
                if (previous != 0)
                {
                    SelectObject(dc, previous);
                }

                if (dib != 0)
                {
                    DeleteObject(dib);
                }

                DeleteDC(dc);
            }
        }
        finally
        {
            if (icon.hbmMask != 0)
            {
                DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != 0)
            {
                DeleteObject(icon.hbmColor);
            }
        }
    }

    /// <summary>
    /// A colour pointer's bitmap is its size. A mask-only one has no colour bitmap and its mask holds both halves,
    /// one above the other, so it is half as tall as it looks.
    /// </summary>
    private static bool TrySize(ICONINFO icon, out int width, out int height)
    {
        width = height = 0;
        var bitmap = icon.hbmColor != 0 ? icon.hbmColor : icon.hbmMask;
        if (bitmap == 0 || GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var description) == 0)
        {
            return false;
        }

        width = description.bmWidth;
        height = icon.hbmColor != 0 ? description.bmHeight : description.bmHeight / 2;
        return width is > 0 and <= Protocol.ScreenCodec.MaxCursorSize && height is > 0 and <= Protocol.ScreenCodec.MaxCursorSize;
    }

    private static byte[]? Draw(nint dc, nint shape, nint bits, int width, int height, int pixels, uint background)
    {
        unsafe
        {
            var target = new Span<uint>((void*)bits, pixels);
            target.Fill(0xFF000000u | background);
        }

        if (!DrawIconEx(dc, 0, 0, shape, width, height, 0, 0, DI_NORMAL))
        {
            return null;
        }

        var copy = new byte[pixels * 4];
        Marshal.Copy(bits, copy, 0, copy.Length);
        return copy;
    }

    /// <summary>
    /// Opacity from the difference between the two drawings, colour from the one over black — which is already the
    /// colour multiplied by that opacity, which is the form a window wants it in.
    /// </summary>
    private static byte[] Combine(byte[] overBlack, byte[] overWhite, int pixels)
    {
        var result = new byte[pixels * 4];
        for (var i = 0; i < result.Length; i += 4)
        {
            var shows = ((overWhite[i] - overBlack[i]) + (overWhite[i + 1] - overBlack[i + 1]) + (overWhite[i + 2] - overBlack[i + 2])) / 3;
            var alpha = Math.Clamp(255 - shows, 0, 255);
            result[i] = (byte)Math.Min(overBlack[i], alpha);
            result[i + 1] = (byte)Math.Min(overBlack[i + 1], alpha);
            result[i + 2] = (byte)Math.Min(overBlack[i + 2], alpha);
            result[i + 3] = (byte)alpha;
        }

        return result;
    }

    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public NativePoint ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, out BITMAP bitmap);
}
