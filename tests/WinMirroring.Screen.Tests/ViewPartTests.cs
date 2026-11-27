using System.Drawing;
using WinMirroring.Screen.Protocol;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// Zoomed in, a viewer asks for the part of the screen it has in view instead of all of it. Three things have to
/// hold for that to work: the part asked for is the right one, every picture says which request it was made for
/// (so pictures of the last part, still arriving, are not drawn in the place of the next), and a position outside
/// the part being sent is still a position on the screen.
/// </summary>
public class ViewPartTests
{
    [Fact]
    public void WhatIsAskedForIsWhatIsInViewAndAMarginAroundIt()
    {
        // Zoomed to four times, looking at the middle.
        var visible = new RectangleF(0.375f, 0.375f, 0.25f, 0.25f);

        var part = ViewParts.For(visible);

        Assert.Equal(0.375 - (0.25 * ViewParts.Margin), part.Left, 4);
        Assert.Equal(0.625 + (0.25 * ViewParts.Margin), part.Right, 4);
        Assert.True(part.Width * part.Height < 0.15, "a sixteenth of the screen in view should not ask for much more than that");
    }

    [Fact]
    public void TheMarginStopsAtTheEdgeOfThePicture()
    {
        var part = ViewParts.For(new RectangleF(0, 0.7f, 0.3f, 0.3f));

        Assert.Equal(0, part.Left, 4);
        Assert.Equal(1, part.Bottom, 4);
        Assert.True(part.Right > 0.3f && part.Top < 0.7f);
    }

    [Fact]
    public void BarelyZoomedInTheWholeIsAskedFor()
    {
        Assert.Equal(ViewParts.Whole, ViewParts.For(new RectangleF(0.05f, 0.05f, 0.9f, 0.9f)));
        Assert.Equal(ViewParts.Whole, ViewParts.For(ViewParts.Whole));
    }

    [Fact]
    public void ASmallMoveIsServedByWhatWasAlreadyAskedFor()
    {
        var visible = new RectangleF(0.4f, 0.4f, 0.2f, 0.2f);
        var asked = ViewParts.For(visible);

        Assert.True(ViewParts.StillServes(asked, visible, 2, 2));
        Assert.True(ViewParts.StillServes(asked, new RectangleF(0.41f, 0.405f, 0.2f, 0.2f), 2, 2));
    }

    [Fact]
    public void AMoveToTheEdgeOfItOrAChangeOfZoomAsksAgain()
    {
        var visible = new RectangleF(0.4f, 0.4f, 0.2f, 0.2f);
        var asked = ViewParts.For(visible);

        // Nearly all the margin on one side used up.
        Assert.False(ViewParts.StillServes(asked, new RectangleF(0.428f, 0.4f, 0.2f, 0.2f), 2, 2));

        // Past it altogether.
        Assert.False(ViewParts.StillServes(asked, new RectangleF(0.5f, 0.4f, 0.2f, 0.2f), 2, 2));

        // The same place at another zoom: the picture would be the wrong size for it.
        Assert.False(ViewParts.StillServes(asked, visible, 2, 2.5));
    }

    [Fact]
    public void AgainstTheEdgeOfThePictureThereIsNoMarginToRunOutOf()
    {
        var visible = new RectangleF(0, 0, 0.25f, 0.25f);
        var asked = ViewParts.For(visible);

        Assert.True(ViewParts.StillServes(asked, visible, 4, 4));
    }

    [Fact]
    public void APartOfACropIsThatPartOfTheSourcesScreen()
    {
        var crop = new RectangleF(0.5f, 0.5f, 0.5f, 0.5f);

        var within = ViewParts.Within(crop, new RectangleF(0.5f, 0, 0.5f, 0.5f));

        Assert.Equal(new RectangleF(0.75f, 0.5f, 0.25f, 0.25f), within);
    }

    // ---- which request a picture was made for ----------------------------------------------------------

    [Fact]
    public void ACropRequestCarriesItsNameAndAnOlderOneCarriesNone()
    {
        var message = ScreenCodec.EncodeCrop(3, new RectangleF(0.1f, 0.2f, 0.5f, 0.3f), id: 17);

        Assert.True(ScreenCodec.TryDecodeCrop(message, out var crop, out var id));
        Assert.Equal(17, id);
        Assert.Equal(0.5f, crop.Width, 3);

        // What a viewer from before this sends: the same message, one byte shorter.
        Assert.True(ScreenCodec.TryDecodeCrop(message.AsSpan(0, 21), out _, out var unnamed));
        Assert.Equal(0, unnamed);
    }

    [Fact]
    public void AVideoFrameSaysWhichRequestItWasMadeFor()
    {
        var message = ScreenCodec.EncodeVideoFrame(11, 7, 1280, 720, keyframe: true, new byte[] { 1, 2, 3 }, cropId: 42);

        Assert.Equal(42, ScreenCodec.VideoCropId(message));
        var frame = ScreenCodec.TryDecodeVideoFrame(message)!;
        Assert.Equal(42, frame.CropId);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Data);
        Assert.True(frame.Keyframe);

        Assert.Equal(0, ScreenCodec.TryDecodeVideoFrame(ScreenCodec.EncodeVideoFrame(11, 8, 1280, 720, false, new byte[] { 9 }))!.CropId);
    }

    [Fact]
    public void ThatSurvivesBeingCutIntoDatagramsAndALostOne()
    {
        var picture = new byte[VideoDatagram.PayloadSize * 5];
        new Random(3).NextBytes(picture);
        var tags = new List<byte>();
        var whole = new List<byte[]>();
        var reassembly = new VideoReassembly(
            (_, _, _, _, data, tag) =>
            {
                tags.Add(tag);
                whole.Add(data.ToArray());
            },
            _ => { },
            () => 0);

        var plain = new byte[VideoDatagram.SafeDatagramBytes];
        var pieces = VideoDatagram.Pieces(picture.Length);
        for (var piece = 0; piece < pieces; piece++)
        {
            if (piece == 2)
            {
                continue; // lost on the way; the parity brings it back
            }

            var length = VideoDatagram.Write(plain, 1, 1280, 720, false, picture, piece, tag: 9);
            Assert.True(VideoDatagram.TryReadHeader(plain.AsSpan(0, length), out var header));
            Assert.Equal(9, header.Tag);
            reassembly.Add(header, plain.AsSpan(VideoDatagram.HeaderSize, length - VideoDatagram.HeaderSize));
        }

        Assert.Equal([(byte)9], tags);
        Assert.Equal(picture, Assert.Single(whole));
    }

    // ---- positions outside the part being sent ---------------------------------------------------------

    [Fact]
    public void APositionOutsideThePartBeingSentIsStillAPositionOnTheScreen()
    {
        Assert.True(ScreenCodec.TryDecodePointer(
            ScreenCodec.EncodePointer(3, PointerAction.Down, Input.Protocol.MouseButton.Left, 1.2f, -0.3f, 0),
            out _, out _, out var x, out var y, out _));
        Assert.Equal((1.2f, -0.3f), (x, y));

        // The source sends the right half of a 1000-pixel-wide screen; a fifth of a part-width past its right edge
        // would be off the screen, so it is held there — while a fifth before its left edge is a real place.
        var crop = new RectangleF(0.5f, 0, 0.5f, 1);
        var frame = new Size(1000, 500);
        var bounds = new Rectangle(0, 0, 1000, 500);
        Assert.Equal(999, ScreenSource.MapToScreen(1.2f, 0.5f, crop, frame, bounds).X);
        Assert.Equal(400, ScreenSource.MapToScreen(-0.2f, 0.5f, crop, frame, bounds).X);

        // And a number that is not a position at all does not become a point a long way off.
        Assert.True(ScreenCodec.TryDecodeTouch(ScreenCodec.EncodeTouch(3, 1, 0, 500f, 0.5f), out _, out _, out var far, out _));
        Assert.True(far < 10);
    }
}
