using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// Zoomed in, only the part of the screen that is on this screen is asked for.
///
/// Zooming used to enlarge a picture of the whole screen: the source went on encoding all of it, at its full size,
/// and this device decoded all of it to show a quarter. Every bit and every millisecond spent on the three quarters
/// outside the window was spent on nothing — and on a link that could not carry the full bitrate, what was zoomed
/// into was the same blur, larger. Now the part in view is what the source is asked for (with a margin, so a
/// small move does not run off the edge of it), and all of the stream goes into that.
///
/// The picture of the whole stays where it was, underneath, as it last looked: the window is laid out around it
/// and positions are measured on it, exactly as before, so nothing about pointing changes. The part asked for is
/// drawn over it, in the place it belongs. Moving past the edge of that part therefore shows the old, coarser
/// picture for a moment rather than nothing, and a new part is asked for as soon as the view stops moving — the
/// way a map fills in.
///
/// Every picture says which request it was made for (<see cref="FrameUpdate.CropId"/>), because pictures of the
/// last part are still arriving when the next is asked for, and each has to be drawn where it belongs.
/// </summary>
public partial class ScreenViewerWindow
{
    private static readonly Rect WholePicture = new(0, 0, 1, 1);

    /// <summary>
    /// The most the whole picture is asked to grow by when zoomed, once parts can be asked for instead. Past this,
    /// a part takes over; up to it the whole picture at a little more than its shown size is the cheaper thing.
    /// </summary>
    private const double WholeZoomLimit = 1.45;

    /// <summary>How long the view has to hold still before a part is asked for, and the longest a moving one waits.</summary>
    private static readonly TimeSpan PartAfterStill = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan PartAtLatest = TimeSpan.FromMilliseconds(350);

    /// <summary>The requests made lately, by the name each was given: which part of the whole picture it was for.</summary>
    private readonly Dictionary<byte, Rect> _parts = [];
    private byte _partId;

    /// <summary>What the source was last asked for, and the zoom it was asked at.</summary>
    private Rect _askedPart = WholePicture;
    private double _askedZoom = 1;

    /// <summary>The part the picture drawn over the whole is of, or null while none is drawn.</summary>
    private Rect? _shownPart;
    private WriteableBitmap? _detail;
    private DispatcherTimer? _partTimer;
    private long _viewMovedTicks;
    private long _partDueSince;

    /// <summary>Whether this stream's source says which part each picture is of; without that, zoom stays as it was.</summary>
    private bool PartsPossible => !_partsFailed && _viewer.Info.CropTags && _viewer.Info.Codec == "h264";

    /// <summary>
    /// Something in here threw. Asking for parts is a way of seeing better, not something a view cannot do without,
    /// so it is given up for this window — which then zooms the way it always did — rather than left to throw
    /// again on the next picture, sixty times a second.
    /// </summary>
    private bool _partsFailed;

    private void GiveUpOnParts(Exception ex)
    {
        if (_partsFailed)
        {
            return;
        }

        _partsFailed = true;
        Log.Warning(ex, "Asking for the part of the screen in view failed; this view goes back to enlarging the whole picture");
        try
        {
            AskForWholeAnew();
        }
        catch (Exception again)
        {
            Log.Debug(again, "...and so did asking for the whole again");
        }
    }

    /// <summary>The view moved or changed size: once it settles, see whether a different part should be asked for.</summary>
    private void ViewMoved()
    {
        try
        {
            ViewMovedCore();
        }
        catch (Exception ex)
        {
            GiveUpOnParts(ex);
        }
    }

    private void ViewMovedCore()
    {
        PlaceDetail();
        PlaceStill();
        if (!PartsPossible && _askedPart == WholePicture)
        {
            return;
        }

        var now = Environment.TickCount64;
        _viewMovedTicks = now;
        _partTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => PartTimerTick(), Dispatcher);
        if (!_partTimer.IsEnabled)
        {
            _partDueSince = now;
            _partTimer.Start();
        }
    }

    private void PartTimerTick()
    {
        var now = Environment.TickCount64;
        if (now - _viewMovedTicks < PartAfterStill.TotalMilliseconds && now - _partDueSince < PartAtLatest.TotalMilliseconds)
        {
            return; // still moving, and not for so long that what is in view has to be caught up with anyway
        }

        _partTimer!.Stop();
        try
        {
            ChoosePart();
        }
        catch (Exception ex)
        {
            GiveUpOnParts(ex);
        }
    }

    private void ChoosePart()
    {
        var visible = VisiblePart();
        var wanted = WantedPart(visible);
        if (wanted == WholePicture)
        {
            if (_askedPart != WholePicture)
            {
                AskFor(WholePicture);
            }

            return;
        }

        if (_askedPart != WholePicture && visible is { } inView && StillServes(_askedPart, inView))
        {
            return;
        }

        AskFor(wanted);
    }

    /// <summary>What part of the whole picture is inside the window now, or null while there is no picture laid out.</summary>
    private Rect? VisiblePart()
    {
        var width = ScreenImage.ActualWidth;
        var height = ScreenImage.ActualHeight;
        if (!HasPicture || width <= 0 || height <= 0 || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            return null;
        }

        // Through the zoom and the pan, backwards: the corners of the window as points of the picture.
        var topLeft = Surface.TranslatePoint(new Point(0, 0), ScreenImage);
        var bottomRight = Surface.TranslatePoint(new Point(Surface.ActualWidth, Surface.ActualHeight), ScreenImage);
        var left = Math.Clamp(topLeft.X / width, 0, 1);
        var top = Math.Clamp(topLeft.Y / height, 0, 1);
        var right = Math.Clamp(bottomRight.X / width, 0, 1);
        var bottom = Math.Clamp(bottomRight.Y / height, 0, 1);
        return right - left < 0.01 || bottom - top < 0.01 ? null : new Rect(left, top, right - left, bottom - top);
    }

    // The arithmetic — how much around what is in view to ask for, and when what was asked for no longer does —
    // is ViewParts', where it can be tested without a window.

    private static System.Drawing.RectangleF Shares(Rect part) => new((float)part.X, (float)part.Y, (float)part.Width, (float)part.Height);

    private Rect WantedPart(Rect? visible)
    {
        if (!PartsPossible || _zoom <= 1.02 || visible is not { } inView)
        {
            return WholePicture;
        }

        var part = ViewParts.For(Shares(inView));
        return part == ViewParts.Whole ? WholePicture : new Rect(part.X, part.Y, part.Width, part.Height);
    }

    private bool StillServes(Rect asked, Rect visible) => ViewParts.StillServes(Shares(asked), Shares(visible), _askedZoom, _zoom);

    /// <summary>Asks the source for <paramref name="part"/> of the picture this window lays out (or all of it).</summary>
    private void AskFor(Rect part)
    {
        if (_parts.Count > 24)
        {
            _parts.Clear(); // names come round again long before pictures this old could still be arriving
        }

        _partId = (byte)(_partId == byte.MaxValue ? 1 : _partId + 1);
        _parts[_partId] = part;
        _askedPart = part;
        _askedZoom = _zoom;

        // The size first: the source then builds its encoder once, for the part at the size it will be shown at.
        ReportShownSize();
        var whole = part == WholePicture;
        _viewer.SetCrop(whole ? _crop : ViewParts.Within(_crop, Shares(part)), _partId, whole ? null : Shares(part));
    }

    /// <summary>
    /// Asks for the whole picture again and forgets every part: what the whole picture <em>is</em> has just changed
    /// (another crop, another monitor), so a part of the old one belongs nowhere on the new.
    /// </summary>
    private void AskForWholeAnew()
    {
        _partTimer?.Stop();
        HideStill();

        // Pictures of those parts may still be on their way. They are not forgotten — a picture under a name
        // nobody remembers would be taken for one of the whole and drawn as it — but marked as belonging nowhere.
        foreach (var id in _parts.Keys.ToList())
        {
            _parts[id] = Rect.Empty;
        }

        HideDetail();
        AskFor(WholePicture);
    }

    /// <summary>
    /// Where this frame belongs: null for a picture of the whole, the part it is of for a picture of a part, and
    /// an empty rectangle for one that belongs nowhere any more and is not to be drawn at all.
    /// </summary>
    private Rect? PartOf(FrameUpdate update) =>
        _parts.TryGetValue(update.CropId, out var part) && part != WholePicture ? part : null;

    private void PresentDetail(FrameUpdate update, Rect part)
    {
        if (update.Surface is { } onCard)
        {
            if (!ShowPartSurface(onCard))
            {
                return; // could not be drawn from the card; the next one arrives in memory
            }
        }
        else
        {
            if (_detail is null || _detail.PixelWidth != update.Width || _detail.PixelHeight != update.Height
                || !ReferenceEquals(DetailImage.Source, _detail))
            {
                _detail = new WriteableBitmap(update.Width, update.Height, 96, 96, PixelFormats.Bgr32, null);
                DetailImage.Source = _detail;
            }

            _detail.WritePixels(new Int32Rect(0, 0, update.Width, update.Height), update.Image, update.Width * 4, 0);
        }

        _shownPart = part;
        PlaceDetail();
        DetailImage.Visibility = Visibility.Visible;
    }

    private void HideDetail()
    {
        if (_shownPart is null)
        {
            return;
        }

        _shownPart = null;
        DetailImage.Visibility = Visibility.Collapsed;
    }

    /// <summary>Puts the picture of a part over the place it is a part of, through the zoom and the pan.</summary>
    private void PlaceDetail()
    {
        var width = ScreenImage.ActualWidth;
        var height = ScreenImage.ActualHeight;
        if (_shownPart is not { } part || width <= 0 || height <= 0)
        {
            return;
        }

        var topLeft = ScreenImage.TranslatePoint(new Point(part.Left * width, part.Top * height), Overlay);
        var bottomRight = ScreenImage.TranslatePoint(new Point(part.Right * width, part.Bottom * height), Overlay);
        Canvas.SetLeft(DetailImage, topLeft.X);
        Canvas.SetTop(DetailImage, topLeft.Y);
        DetailImage.Width = Math.Max(0, bottomRight.X - topLeft.X);
        DetailImage.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
    }

    /// <summary>The size to tell the source its picture is shown at, in this screen's real pixels.</summary>
    private (double Width, double Height) ShownSizeFor(double width, double height)
    {
        if (_askedPart != WholePicture)
        {
            // A part, at the zoom it was asked at — not the zoom of this instant, which during a pinch is a
            // different number every frame and would have the source rebuild its encoder for each of them.
            return (width * _askedZoom * _askedPart.Width, height * _askedZoom * _askedPart.Height);
        }

        if (!PartsPossible)
        {
            return (width * _zoom, height * _zoom);
        }

        // Zoomed in further than the whole is worth enlarging for, a part is about to be asked for: the whole
        // stays the size it last was, rather than have the source build an encoder for a size nobody will see.
        if (_zoom <= WholeZoomLimit)
        {
            _wholeZoom = _zoom;
        }

        return (width * _wholeZoom, height * _wholeZoom);
    }

    /// <summary>The zoom the whole picture was last sized for (see <see cref="ShownSizeFor"/>).</summary>
    private double _wholeZoom = 1;
}
