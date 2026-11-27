using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// The sharp picture of a screen that has stopped moving, laid over the video.
///
/// The source sends it a little after its screen stands still (see StillCodec in the screen library for why video
/// alone is not good enough to read from). Here it is put exactly where the video picture it is of is drawn — over
/// the whole picture, or over the part that is zoomed into — and taken away again the moment a video frame
/// arrives, since that is the screen moving. Nothing else knows it is there: it takes no input and has no part in
/// the layout.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>The part of the whole picture the sharp picture on the screen is of, or null while none is shown.</summary>
    private Rect? _stillPart;

    private void ShowStill(ScreenViewer.StillPicture still)
    {
        try
        {
            // Between its being decoded and this running, the screen may have moved after all.
            if (!HasPicture || _viewer.LastFrameNo > still.AfterFrameNo || IsWaitingToResume)
            {
                return;
            }

            Rect part;
            if (_parts.TryGetValue(still.CropId, out var known))
            {
                if (known.IsEmpty)
                {
                    return; // of a part that belongs nowhere any more
                }

                part = known;
            }
            else
            {
                part = WholePicture;
            }

            // It must lie over a picture of the same thing: over the part that is drawn, or over the whole when no
            // part is. Anything else is a picture from either side of a change of view, and is let go.
            if (part == WholePicture ? _shownPart is not null : _shownPart != part)
            {
                return;
            }

            var bitmap = BitmapSource.Create(still.Width, still.Height, 96, 96, PixelFormats.Bgr32, null, still.Bgra, still.Width * 4);
            bitmap.Freeze();
            StillImage.Source = bitmap;
            _stillPart = part;
            PlaceStill();
            StillImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            HideStill();
        }
    }

    private void HideStill()
    {
        if (_stillPart is null)
        {
            return;
        }

        _stillPart = null;
        StillImage.Visibility = Visibility.Collapsed;
        StillImage.Source = null;
    }

    /// <summary>Keeps the sharp picture over what it is a picture of as the view is zoomed and moved.</summary>
    private void PlaceStill()
    {
        var width = ScreenImage.ActualWidth;
        var height = ScreenImage.ActualHeight;
        if (_stillPart is not { } part || width <= 0 || height <= 0)
        {
            return;
        }

        var topLeft = ScreenImage.TranslatePoint(new Point(part.Left * width, part.Top * height), Overlay);
        var bottomRight = ScreenImage.TranslatePoint(new Point(part.Right * width, part.Bottom * height), Overlay);
        Canvas.SetLeft(StillImage, topLeft.X);
        Canvas.SetTop(StillImage, topLeft.Y);
        StillImage.Width = Math.Max(0, bottomRight.X - topLeft.X);
        StillImage.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
    }
}
