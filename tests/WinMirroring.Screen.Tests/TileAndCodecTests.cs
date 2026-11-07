using System.Drawing;
using WinMirroring.Input.Protocol;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen.Tests;

public class TileAndCodecTests
{
    private static byte[] Gradient(int width, int height, int seed = 0)
    {
        var image = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image[i] = (byte)((x + seed) & 0xFF);
                image[i + 1] = (byte)(y & 0xFF);
                image[i + 2] = (byte)((x + y) / 4 & 0xFF);
                image[i + 3] = 255;
            }
        }

        return image;
    }

    [Fact]
    public void FirstFrameSendsEveryTileThenOnlyChangedOnes()
    {
        using var encoder = new TileEncoder();
        var frame = Gradient(300, 200);

        var first = encoder.Encode(frame, 300, 200, new Rectangle(0, 0, 300, 200), forceAll: false);
        Assert.Equal(3 * 2, first.Count);

        Assert.Empty(encoder.Encode(frame, 300, 200, new Rectangle(0, 0, 300, 200), forceAll: false));

        frame[(150 * 300 + 260) * 4] ^= 0xFF; // one pixel in the bottom-right tile
        var changed = Assert.Single(encoder.Encode(frame, 300, 200, new Rectangle(0, 0, 300, 200), forceAll: false));
        Assert.Equal((256, 128, 44, 72), (changed.X, changed.Y, changed.Width, changed.Height));
    }

    [Fact]
    public void EncodedTilesDecodeCloseToTheOriginal()
    {
        using var encoder = new TileEncoder { Quality = 0.9f };
        using var decoder = new TileDecoder();
        var frame = Gradient(260, 140);
        var tiles = encoder.Encode(frame, 260, 140, new Rectangle(0, 0, 260, 140), forceAll: true);

        var image = new byte[260 * 140 * 4];
        foreach (var tile in tiles)
        {
            decoder.DecodeInto(tile, image, 260, 140);
        }

        long error = 0;
        for (var i = 0; i < image.Length; i += 4)
        {
            error += Math.Abs(image[i] - frame[i]) + Math.Abs(image[i + 1] - frame[i + 1]) + Math.Abs(image[i + 2] - frame[i + 2]);
            Assert.Equal(255, image[i + 3]);
        }

        var meanError = error / (double)(260 * 140 * 3);
        Assert.InRange(meanError, 0, 6);
    }

    [Fact]
    public void CropStreamsOnlyThatPart()
    {
        using var encoder = new TileEncoder();
        var frame = Gradient(400, 300);

        var tiles = encoder.Encode(frame, 400, 300, new Rectangle(100, 50, 150, 100), forceAll: false);

        Assert.Equal((150, 100), (encoder.Width, encoder.Height));
        Assert.Equal(2, tiles.Count);
        Assert.All(tiles, t => Assert.True(t.X + t.Width <= 150 && t.Y + t.Height <= 100));
    }

    [Fact]
    public void FrameMessageRoundTrips()
    {
        var tiles = new List<EncodedTile> { new(0, 0, 128, 128, [1, 2, 3]), new(128, 0, 72, 128, [4, 5]) };

        var decoded = ScreenCodec.TryDecodeFrame(ScreenCodec.EncodeFrame(7, 42, 200, 128, tiles));

        Assert.NotNull(decoded);
        Assert.Equal((7u, 42u, 200, 128), (decoded.StreamId, decoded.FrameNo, decoded.Width, decoded.Height));
        Assert.Equal(2, decoded.Tiles.Count);
        Assert.Equal(new byte[] { 4, 5 }, decoded.Tiles[1].Jpeg);
        Assert.Null(ScreenCodec.TryDecodeFrame(ScreenCodec.EncodeFrame(7, 1, 100, 100, [new EncodedTile(90, 0, 20, 20, [1])])));
    }

    [Fact]
    public void InputMessagesRoundTrip()
    {
        Assert.True(ScreenCodec.TryDecodePointer(ScreenCodec.EncodePointer(3, PointerAction.Down, MouseButton.Right, 0.25f, 0.75f, -120), out var action, out var button, out var x, out var y, out var wheel));
        Assert.Equal((PointerAction.Down, MouseButton.Right, 0.25f, 0.75f, -120), (action, button, x, y, wheel));

        Assert.True(ScreenCodec.TryDecodeKey(ScreenCodec.EncodeKey(3, 0x41, 0x1E, true, false), out var vk, out var scan, out var up, out var extended));
        Assert.Equal(((ushort)0x41, (ushort)0x1E, true, false), (vk, scan, up, extended));

        Assert.True(ScreenCodec.TryDecodeCrop(ScreenCodec.EncodeCrop(3, new RectangleF(0.1f, 0.2f, 0.5f, 0.3f)), out var crop));
        Assert.Equal(0.1f, crop.X, 4);
        Assert.Equal(0.3f, crop.Height, 4);

        Assert.True(ScreenCodec.TryDecodeAck(ScreenCodec.EncodeAck(3, 99), out var frameNo));
        Assert.Equal(99u, frameNo);
    }

    [Fact]
    public void TouchAndTextMessagesRoundTrip()
    {
        Assert.True(ScreenCodec.TryDecodeTouch(ScreenCodec.EncodeTouch(3, 2, 7, 0.5f, 1.5f), out var action, out var contact, out var x, out var y));
        Assert.Equal(((byte)2, (byte)7, 0.5f, 1f), (action, contact, x, y));
        Assert.False(ScreenCodec.TryDecodeTouch(ScreenCodec.EncodeTouch(3, 9, 0, 0.5f, 0.5f), out _, out _, out _, out _));
        Assert.False(ScreenCodec.TryDecodeTouch(ScreenCodec.EncodeTouch(3, 1, 0, float.NaN, 0.5f), out _, out _, out _, out _));

        Assert.True(ScreenCodec.TryDecodeText(ScreenCodec.EncodeText(3, "Привет 👋"), out var text));
        Assert.Equal("Привет 👋", text);
        Assert.True(ScreenCodec.TryReadHeader(ScreenCodec.EncodeText(5, "a"), out var type, out var streamId));
        Assert.Equal((ScreenMessageType.Text, 5u), (type, streamId));
        Assert.True(ScreenCodec.TryDecodeText(ScreenCodec.EncodeText(3, new string('x', 10_000)), out var clipped));
        Assert.Equal(ScreenCodec.MaxTextLength, clipped.Length);
        Assert.False(ScreenCodec.TryDecodeText([(byte)ScreenMessageType.Text, 3, 0, 0, 0, 1], out _));
    }

    [Theory]
    [InlineData(-1f, -1f, 5f, 5f, 0f, 0f, 1f, 1f)]
    [InlineData(0.5f, 0.5f, 0.001f, 0.5f, 0f, 0f, 1f, 1f)]
    [InlineData(0.8f, 0.8f, 0.5f, 0.5f, 0.8f, 0.8f, 0.2f, 0.2f)]
    [InlineData(float.NaN, 0f, 1f, 1f, 0f, 0f, 1f, 1f)]
    public void CropIsNormalized(float x, float y, float w, float h, float ex, float ey, float ew, float eh)
    {
        var crop = ScreenCodec.Normalize(new RectangleF(x, y, w, h));

        Assert.Equal(ex, crop.X, 4);
        Assert.Equal(ey, crop.Y, 4);
        Assert.Equal(ew, crop.Width, 4);
        Assert.Equal(eh, crop.Height, 4);
    }

    [Fact]
    public void PointerMapsThroughCropToTheScreen()
    {
        // A 1000x500 window captured at the same size, placed at (2000, 100) on a second monitor,
        // streaming only its right half.
        var bounds = new Rectangle(2000, 100, 1000, 500);
        var crop = new RectangleF(0.5f, 0f, 0.5f, 1f);

        Assert.Equal(new Point(2500, 100), ScreenSource.MapToScreen(0f, 0f, crop, new Size(1000, 500), bounds));
        Assert.Equal(new Point(2750, 350), ScreenSource.MapToScreen(0.5f, 0.5f, crop, new Size(1000, 500), bounds));
        Assert.Equal(new Point(2999, 599), ScreenSource.MapToScreen(1f, 1f, crop, new Size(1000, 500), bounds));

        // Capture at a different scale than the window bounds (DPI virtualization).
        Assert.Equal(new Point(2500, 350), ScreenSource.MapToScreen(0f, 0.5f, crop, new Size(2000, 1000), bounds));
    }
}
