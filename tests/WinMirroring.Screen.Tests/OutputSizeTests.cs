using System.Drawing;
using WinMirroring.Screen;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// A viewer showing a full screen in a smaller window pays for every pixel it never shows, twice over: once in
/// the decoder and once turning the result into pixels, which on a tablet are the two most expensive things it
/// does per frame. So it says how large it is drawing, and the source encodes that size. These are the rules of
/// what it gets.
/// </summary>
public class OutputSizeTests
{
    private static readonly Rectangle FullScreen = new(0, 0, 1920, 1080);

    [Fact]
    public void AViewerThatHasNotSaidGetsThePictureAsItIs()
    {
        Assert.Equal(new Size(1920, 1080), ScreenSource.FitOutput(FullScreen, default, canScale: true));
    }

    [Fact]
    public void AWindowShowingItSmallerGetsItSmaller()
    {
        var output = ScreenSource.FitOutput(FullScreen, new Size(1440, 900), canScale: true);

        // Fitted to the shape of the crop, so it is not distorted: the width is what limits it here.
        Assert.Equal(new Size(1440, 810), output);
    }

    [Fact]
    public void TheShapeOfTheCropIsKeptWhateverShapeTheWindowIs()
    {
        var crop = new Rectangle(100, 100, 800, 600);

        var output = ScreenSource.FitOutput(crop, new Size(1000, 300), canScale: true);

        Assert.Equal(300 / 600.0, output.Height / 600.0, 2);
        Assert.Equal(800 / 600.0, output.Width / (double)output.Height, 2);
    }

    [Fact]
    public void ItIsNeverMadeLargerThanItWasCaptured()
    {
        Assert.Equal(new Size(1920, 1080), ScreenSource.FitOutput(FullScreen, new Size(3840, 2160), canScale: true));
    }

    /// <summary>Within a few percent, scaling costs more in sharpness than it saves in work.</summary>
    [Fact]
    public void NearlyTheSameSizeIsLeftAlone()
    {
        Assert.Equal(new Size(1920, 1080), ScreenSource.FitOutput(FullScreen, new Size(1880, 1060), canScale: true));
    }

    [Fact]
    public void WithoutTheGraphicsCardNothingIsScaled()
    {
        Assert.Equal(new Size(1920, 1080), ScreenSource.FitOutput(FullScreen, new Size(640, 360), canScale: false));
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    [InlineData(500, 500)]
    public void EverySizeItProducesCanBeEncoded(int wantedWidth, int wantedHeight)
    {
        var output = ScreenSource.FitOutput(FullScreen, new Size(wantedWidth, wantedHeight), canScale: true);

        // H.264 here is fed NV12, which has no odd sizes, and nothing below sixteen is worth encoding at all.
        Assert.True(output.Width >= 16 && output.Height >= 16);
        Assert.Equal(0, output.Width % 2);
        Assert.Equal(0, output.Height % 2);
    }

    [Fact]
    public void TheSizeMessageRoundTrips()
    {
        var message = ScreenCodec.EncodeSize(4242, 1440, 810);

        Assert.True(ScreenCodec.TryReadHeader(message, out var type, out var streamId));
        Assert.Equal(ScreenMessageType.Size, type);
        Assert.Equal(4242u, streamId);
        Assert.True(ScreenCodec.TryDecodeSize(message, out var size));
        Assert.Equal(new Size(1440, 810), size);

        Assert.False(ScreenCodec.TryDecodeSize(message.AsSpan(0, 6), out _));
        Assert.False(ScreenCodec.TryDecodeSize(ScreenCodec.EncodeAck(1, 1), out _));
    }
}
