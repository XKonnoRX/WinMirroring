using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The sharp picture a source sends once its screen has stopped moving. The reason it exists is colour: video as
/// it is streamed keeps one colour for every four pixels, and thin coloured text comes out smeared. So what has to
/// hold here is that one-pixel-wide detail in pure colour comes back as it went in.
/// </summary>
public class StillCodecTests
{
    [Fact]
    public void OnePixelStripesOfPureColourComeBackAsTheyWere()
    {
        const int width = 64;
        const int height = 48;
        var picture = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var red = (x & 1) == 0;
                picture[i] = (byte)(red ? 0 : 255); // blue
                picture[i + 1] = 0;
                picture[i + 2] = (byte)(red ? 255 : 0); // red
                picture[i + 3] = 255;
            }
        }

        var jpeg = StillCodec.Encode(picture, width, height);
        var decoded = StillCodec.Decode(jpeg);

        Assert.NotNull(decoded);
        Assert.Equal((width, height), (decoded.Value.Width, decoded.Value.Height));
        var worst = 0;
        for (var i = 0; i < picture.Length; i += 4)
        {
            worst = Math.Max(worst, Math.Abs(picture[i] - decoded.Value.Bgra[i]));
            worst = Math.Max(worst, Math.Abs(picture[i + 2] - decoded.Value.Bgra[i + 2]));
        }

        // With colour kept for every pixel the stripes survive to within the rounding of a high-quality JPEG. With
        // one colour for every four pixels, red and blue side by side would both have come back purple — out by
        // more than a hundred.
        Assert.True(worst < 60, $"a stripe came back {worst} off its colour");
    }

    [Fact]
    public void WhatIsNotAPictureIsNotOpened()
    {
        Assert.Null(StillCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Null(StillCodec.Decode([]));
    }

    [Fact]
    public void TheMessageSaysWhichFrameItFollowsAndWhichPartItIsOf()
    {
        var message = ScreenCodec.EncodeStill(7, afterFrameNo: 1234, width: 1920, height: 1080, cropId: 9, picture: [10, 20, 30]);

        Assert.True(ScreenCodec.TryReadHeader(message, out var type, out var streamId));
        Assert.Equal((ScreenMessageType.Still, 7u), (type, streamId));
        Assert.True(ScreenCodec.TryDecodeStill(message, out var after, out var width, out var height, out var cropId, out var offset));
        Assert.Equal((1234u, 1920, 1080, (byte)9), (after, width, height, cropId));
        Assert.Equal(new byte[] { 10, 20, 30 }, message[offset..]);

        Assert.False(ScreenCodec.TryDecodeStill(message.AsSpan(0, 16), out _, out _, out _, out _, out _));
        Assert.False(ScreenCodec.TryDecodeStill(ScreenCodec.EncodeVideoFrame(7, 1, 16, 16, true, new byte[] { 1 }), out _, out _, out _, out _, out _));
    }
}
