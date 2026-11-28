using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Screen.Encoding;
using Rectangle = System.Drawing.Rectangle;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The last step of a picture that stays on the graphics card: WPF drawing it. Everything before this can be
/// checked by reading pixels back; this cannot, because the one failure worth fearing is the quiet one — a window
/// that is given the surface, raises nothing, and draws black. So this puts a real window on the screen for a
/// moment, takes what the screen shows where it is, and holds that to the picture that was decoded.
///
/// By hand only: it shows a window, and needs a desktop that is on.
/// </summary>
[Collection<HardwareVideoCollection>]
public class DirectPresentTests
{
    private const int Width = 640;
    private const int Height = 360;

    [Fact(Explicit = true)]
    public void APictureLeftOnTheCardIsWhatWpfPutsOnTheScreen() => DrawsTwoPictures(minimisedBetween: false);

    /// <summary>
    /// What a view does when it is put out of sight: the surface is taken back from WPF, the window is minimised
    /// with nothing of the card's in it, and when it is brought back it is given a surface again. That the second
    /// picture is then on the screen says both halves work — WPF lets go without complaint, and takes up again.
    /// </summary>
    [Fact(Explicit = true)]
    public void AViewMinimisedWithNothingOfTheCardsDrawsFromItAgainWhenBroughtBack() => DrawsTwoPictures(minimisedBetween: true);

    private static void DrawsTwoPictures(bool minimisedBetween)
    {
        Assert.SkipUnless(VideoCodecSupport.CanEncodeH264 && VideoCodecSupport.CanDecodeH264, "No hardware H.264 encoder on this machine.");

        // Two pictures, so that what is on the screen can be seen to follow the surface it was pointed at second.
        var first = Encoded(step: 0);
        var second = Encoded(step: 2);
        using var decoder = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        Assert.SkipUnless(decoder.UsesGraphicsCard, "This machine's graphics card does not decode and convert.");
        using var early = decoder.CreateSurface(Width, Height)!;
        using var late = decoder.CreateSurface(Width, Height)!;
        var memory = Array.Empty<byte>();
        foreach (var unit in first)
        {
            decoder.DecodeInto(unit, ref memory, out _, out _, onto: early);
        }

        using var again = H264VideoDecoder.TryCreate(Width, Height, NullLogger.Instance)!;
        using var lateOwn = again.CreateSurface(Width, Height)!;
        foreach (var unit in second)
        {
            again.DecodeInto(unit, ref memory, out _, out _, onto: lateOwn);
        }

        List<System.Drawing.Color>? atFirst = null;
        List<System.Drawing.Color>? atSecond = null;
        var tier = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bridge = Direct3D9Bridge.TryCreate() ?? throw new InvalidOperationException("No Direct3D 9 device.");
                tier = RenderCapability.Tier >> 16;
                var image = new D3DImage();
                Point(image, bridge.Open(early));
                var picture = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.Fill };
                var window = new Window
                {
                    Width = Width / 2.0,
                    Height = Height / 2.0,
                    Left = 60,
                    Top = 60,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = true,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Background = System.Windows.Media.Brushes.Magenta, // what shows through if the picture is not drawn
                    Content = picture,
                };
                window.Show();

                var stage = 0;
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (stage++ == 0)
                        {
                            atFirst = Sample(picture);
                            if (minimisedBetween)
                            {
                                TakeBack(image);
                                window.WindowState = WindowState.Minimized;
                                return;
                            }

                            Point(image, bridge.Open(lateOwn));
                            stage++;
                            return;
                        }

                        if (stage == 2)
                        {
                            window.WindowState = WindowState.Normal;
                            Point(image, bridge.Open(lateOwn));
                            return;
                        }

                        atSecond = Sample(picture);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    timer.Stop();
                    window.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                timer.Start();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "the window never finished");
        Assert.Null(failure);
        TestContext.Current.TestOutputHelper?.WriteLine($"WPF render tier {tier}");

        AssertShows(atFirst!, step: 0);
        AssertShows(atSecond!, step: 2);
    }

    private static void TakeBack(D3DImage image)
    {
        image.Lock();
        try
        {
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        }
        finally
        {
            image.Unlock();
        }
    }

    private static void Point(D3DImage image, nint surface)
    {
        image.Lock();
        try
        {
            // As the window does it: without the software fallback, so that WPF itself lets go of the surface when
            // it loses the card's front buffer (see ScreenViewerWindow.Surfaces).
            Assert.True(image.IsFrontBufferAvailable, "WPF should have the card's front buffer on a machine that runs this test");
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
            image.AddDirtyRect(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        finally
        {
            image.Unlock();
        }
    }

    /// <summary>The colour the screen shows at the middle of each band of the picture, top to bottom.</summary>
    private static List<System.Drawing.Color> Sample(FrameworkElement picture)
    {
        var topLeft = picture.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = picture.PointToScreen(new System.Windows.Point(picture.ActualWidth, picture.ActualHeight));
        var area = new Rectangle((int)topLeft.X, (int)topLeft.Y, (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
        using var shot = new Bitmap(area.Width, area.Height);
        using (var graphics = Graphics.FromImage(shot))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        }

        var colours = new List<System.Drawing.Color>();
        for (var band = 0; band < Height / 40; band++)
        {
            var y = (int)(((band * 40) + 20) / (double)Height * area.Height);
            colours.Add(shot.GetPixel(area.Width / 2, Math.Clamp(y, 0, area.Height - 1)));
        }

        return colours;
    }

    private static void AssertShows(List<System.Drawing.Color> seen, int step)
    {
        (byte B, byte G, byte R)[] colours = [(40, 40, 230), (40, 200, 40), (230, 60, 40), (30, 210, 220), (250, 250, 250), (10, 10, 10)];
        for (var band = 0; band < seen.Count; band++)
        {
            var expected = colours[(band + step) % colours.Length];
            var got = seen[band];
            var off = Math.Max(Math.Abs(got.R - expected.R), Math.Max(Math.Abs(got.G - expected.G), Math.Abs(got.B - expected.B)));
            Assert.True(off <= 30, $"band {band} of picture {step} is ({got.R},{got.G},{got.B}) on the screen, expected ({expected.R},{expected.G},{expected.B})");
        }
    }

    private static List<byte[]> Encoded(int step)
    {
        var units = new List<byte[]>();
        using var encoder = H264VideoEncoder.TryCreate(Width, Height, 30, 8000, NullLogger.Instance)!;
        for (var attempt = 0; attempt < 5 && units.Count == 0; attempt++)
        {
            foreach (var unit in encoder.Encode(SharedSurfaceTests.Bands(step, Width, Height), Width, new Rectangle(0, 0, Width, Height), forceKeyframe: attempt == 0))
            {
                units.Add(unit.Data);
            }
        }

        Assert.NotEmpty(units);
        return units;
    }
}
