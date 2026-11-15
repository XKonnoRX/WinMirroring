using System.Drawing;
using System.Runtime.InteropServices;
using WinMirroring.Screen;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Protocol;
using Size = System.Drawing.Size;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The pointer, which now travels on its own instead of being drawn into the picture. A pointer in the picture is a
/// change in the picture, so moving the mouse was a video frame — encoded, sent, decoded and painted, arriving tens
/// of milliseconds later and only as often as frames do. On its own it is eighteen bytes at the rate a mouse moves.
/// </summary>
public class PointerTests
{
    [Fact]
    public void APointerPositionRoundTrips()
    {
        var decoded = ScreenCodec.TryDecodeCursor(ScreenCodec.EncodeCursor(7, 3, 0.25f, 0.75f, visible: true));

        Assert.NotNull(decoded);
        Assert.Equal((7u, 3u, 0.25f, 0.75f, true), (decoded.StreamId, decoded.ShapeId, decoded.X, decoded.Y, decoded.Visible));
        Assert.False(ScreenCodec.TryDecodeCursor(ScreenCodec.EncodeCursor(7, 3, 0.5f, 0.5f, visible: false))!.Visible);
    }

    /// <summary>Out of range or not a number at all becomes something a window can still draw.</summary>
    [Fact]
    public void ANonsensePositionIsNotTakenAsOne()
    {
        Assert.Null(ScreenCodec.TryDecodeCursor(ScreenCodec.EncodeCursor(7, 3, float.NaN, 0.5f, visible: true)));
        Assert.Equal(1f, ScreenCodec.TryDecodeCursor(ScreenCodec.EncodeCursor(7, 3, 4f, 0.5f, visible: true))!.X);
    }

    [Fact]
    public void APointerShapeRoundTrips()
    {
        var pixels = new byte[6 * 4 * 4];
        Random.Shared.NextBytes(pixels);

        var decoded = ScreenCodec.TryDecodeCursorShape(ScreenCodec.EncodeCursorShape(7, 9, 6, 4, 2, 3, pixels));

        Assert.NotNull(decoded);
        Assert.Equal((7u, 9u, 6, 4, 2, 3), (decoded.StreamId, decoded.ShapeId, decoded.Width, decoded.Height, decoded.HotspotX, decoded.HotspotY));
        Assert.Equal(pixels, decoded.Pixels);
    }

    /// <summary>A shape whose size does not match the pixels it carries is refused rather than read past its end.</summary>
    [Fact]
    public void AShapeThatDoesNotAddUpIsRefused()
    {
        var message = ScreenCodec.EncodeCursorShape(7, 9, 6, 4, 2, 3, new byte[6 * 4 * 4]);

        Assert.Null(ScreenCodec.TryDecodeCursorShape(message.AsSpan(0, message.Length - 8)));
        Assert.Null(ScreenCodec.TryDecodeCursorShape(ScreenCodec.EncodeCursorShape(7, 9, 0, 4, 0, 0, [])));
    }

    /// <summary>
    /// Windows' plain arrow, which is the shape that matters: it carries no transparency of its own, only a mask
    /// saying which pixels to keep and a picture saying what colour to make them. Drawn over one background that
    /// leaves nothing behind to say which was which — the arrow comes out either invisible or in a black box — so it
    /// is drawn over two and the difference between them is the transparency.
    /// </summary>
    [Fact]
    public void ThePlainArrowComesOutWhiteWithTransparencyAroundIt()
    {
        var arrow = LoadCursorW(0, IDC_ARROW);
        Assert.NotEqual(0, arrow);

        var picture = CursorReader.TryDraw(arrow);

        Assert.NotNull(picture);
        Assert.InRange(picture.Width, 8, ScreenCodec.MaxCursorSize);
        Assert.InRange(picture.Height, 8, ScreenCodec.MaxCursorSize);
        Assert.InRange(picture.HotspotX, 0, picture.Width);
        Assert.InRange(picture.HotspotY, 0, picture.Height);

        var opaque = 0;
        var clear = 0;
        var white = 0;
        for (var i = 0; i < picture.Pixels.Length; i += 4)
        {
            var alpha = picture.Pixels[i + 3];
            opaque += alpha == 255 ? 1 : 0;
            clear += alpha == 0 ? 1 : 0;
            white += alpha == 255 && picture.Pixels[i] > 200 && picture.Pixels[i + 1] > 200 && picture.Pixels[i + 2] > 200 ? 1 : 0;

            // Premultiplied, which is what a window paints with: no channel may be brighter than the pixel is opaque.
            Assert.True(picture.Pixels[i] <= alpha && picture.Pixels[i + 1] <= alpha && picture.Pixels[i + 2] <= alpha);
        }

        Assert.True(opaque > 20, $"the arrow came out with only {opaque} solid pixels");
        Assert.True(clear > opaque, $"an arrow is mostly nothing: {clear} see-through against {opaque} solid");
        Assert.True(white > 10, $"the arrow came out with only {white} white pixels, so its mask was lost");
    }

    /// <summary>
    /// Where the pointer is on the picture is the way back from where a tap on the picture is on the screen, and the
    /// two have to agree exactly or a pointer would be drawn somewhere other than where it would act.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.25f, 0.8f)]
    public void TheWayToTheScreenAndBackAgree(float x, float y)
    {
        var bounds = new Rectangle(2000, 100, 1920, 1080);
        var frame = new Size(1920, 1080);
        var crop = new RectangleF(0.25f, 0f, 0.5f, 1f);

        var screen = ScreenSource.MapToScreen(x, y, crop, frame, bounds);
        var (backX, backY) = ScreenSource.MapFromScreen(screen, crop, frame, bounds);

        Assert.Equal(x, backX, 2);
        Assert.Equal(y, backY, 2);
    }

    /// <summary>A pointer away from the streamed part is not on the picture at all, and the viewer must not draw it.</summary>
    [Fact]
    public void APointerOutsideTheStreamedPartIsOutside()
    {
        var bounds = new Rectangle(0, 0, 1920, 1080);
        var frame = new Size(1920, 1080);
        var crop = new RectangleF(0.5f, 0f, 0.5f, 1f); // only the right half is streamed

        var (x, _) = ScreenSource.MapFromScreen(new Point(100, 500), crop, frame, bounds);

        Assert.True(x < 0, $"a point on the left half came out at {x}");
    }

    private const int IDC_ARROW = 32512;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadCursorW(nint instance, int cursor);
}
