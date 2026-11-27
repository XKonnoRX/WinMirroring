using System.Windows;
using System.Windows.Input;
using WinMirroring.App.Services;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// From one monitor of the other device to the one beside it, with a swipe of three fingers.
///
/// Three fingers put down together and moved sideways slide the picture with them, and letting go far enough over
/// shows the monitor that stands on that side — the picture moves left, so what comes is what was to its right, as
/// when turning a page. Which monitor is on which side is taken from where they stand on that device's desktop.
///
/// It was two fingers at first, and that did not hold: two fingers on the picture already zoom it and move it, and
/// the start of a swipe is not told from the start of a pinch until the fingers have moved — by which time the
/// picture has moved with them, the wrong way as often as not. Three fingers are nothing else here, so there is
/// nothing to tell apart: the picture is not touched until they have said which way they go, and it is the same
/// gesture whether the picture is zoomed or not.
///
/// What is given up is small. Three fingers moving sideways towards a side that has a monitor no longer reach the
/// other device; any other three fingers — up, down, a tap, towards a side with nothing on it, or on a device with
/// one monitor — still do, as before.
///
/// One thing is not this program's to decide. Windows 11 has gestures of three and four fingers of its own for a
/// touch screen, on unless switched off, and it takes them before any program sees them: with those on, a swipe of
/// three fingers changes the program on this device instead. See <see cref="SystemTouchGestures"/>.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>Three fingers are sliding the picture sideways towards another monitor.</summary>
    private bool _paging;

    /// <summary>Three fingers are down and have not said yet which way they go.</summary>
    private bool _swiping;

    /// <summary>How far the picture is slid from its place while they do.</summary>
    private double _pageShift;

    /// <summary>How far the fingers had already gone when the picture started to follow them.</summary>
    private double _pageFrom;

    /// <summary>When the second finger of the gesture came down.</summary>
    private long _twoSince;

    /// <summary>
    /// A third finger belongs with the two only if it comes this soon after the second: three put down together.
    /// One that lands later — the side of a hand, mostly, while two fingers are moving a zoomed picture — is not a
    /// swipe starting.
    /// </summary>
    private const int ThirdFingerWithinMs = 400;

    /// <summary>Whether a finger coming down now makes the two already down into three that may swipe.</summary>
    private bool JoinsAsThird() =>
        _gesture.Count == 2 && !_paging && !_swiping && SwipeBetweenMonitors
        && Environment.TickCount64 - _twoSince <= ThirdFingerWithinMs;

    /// <summary>Said once, in the first window where it matters: Windows will take these three fingers itself.</summary>
    private static bool _saidWindowsTakesThem;

    private void WarnIfWindowsTakesTheSwipe()
    {
        if (_saidWindowsTakesThem || !SwipeBetweenMonitors || !SystemTouchGestures.TakeThreeFingers)
        {
            return;
        }

        _saidWindowsTakesThem = true;
        ShowNotice("Свайп тремя пальцами между мониторами перехватывает Windows. Выключите «Жесты тремя и четырьмя пальцами»: Параметры → Bluetooth и устройства → Сенсорный ввод", seconds: 14);
    }

    private bool SwipeBetweenMonitors =>
        _node?.Settings.Screen.SwipeMonitors != false && _viewer.Info.Kind == "monitor" && _viewer.Info.MonitorCount > 1;

    /// <summary>
    /// The monitor on the side a movement of <paramref name="dx"/> reveals — fingers moving left bring what is to
    /// the right — as its place in the device's list and its name, or null when there is none there.
    /// </summary>
    private (int Position, string Key)? MonitorRevealedBy(double dx)
    {
        if (!SwipeBetweenMonitors || dx == 0 || _input is null)
        {
            return null;
        }

        var monitors = _input.PeerMonitors(_viewer.Session.PeerId);
        var placed = monitors
            .Select(m => new MonitorNeighbours.Placed(m.Key, new System.Drawing.Rectangle(m.Left, m.Top, m.Width, m.Height)))
            .ToList();
        var beside = MonitorNeighbours.Beside(placed, _viewer.Info.MonitorKey, dx < 0 ? +1 : -1);
        if (beside is null)
        {
            return null;
        }

        var position = monitors.ToList().FindIndex(m => m.Key == beside);
        return position < 0 ? null : (position, beside);
    }

    /// <summary>Whether three fingers that have moved this far together are a swipe to another monitor.</summary>
    private bool StartsPaging(System.Windows.Vector slide) =>
        MonitorNeighbours.IsSideways(slide.X, slide.Y) && MonitorRevealedBy(slide.X) is not null;

    // ---- the page ------------------------------------------------------------------------------------------
    //
    // A swipe that only slid the picture aside and then, on letting go, put another in its place was a jump at both
    // ends: nothing came in where the picture went out, and the change itself was a cut. It is a page now. Beside
    // the picture, on the side it is leaving, comes a page the size of the view carrying the last small picture of
    // the monitor on that side; letting go carries the movement through — the page slides the rest of the way in,
    // or the picture slides back — and the page stays where it has come to rest until that monitor's own stream
    // has a frame to put in its place.

    /// <summary>Between the picture and the page beside it, so that the two read as two.</summary>
    private const double PageGap = 24;

    /// <summary>How wide the small pictures are asked for: to fill the view for a moment, not to be read from.</summary>
    private const int PagePictureWidth = 640;

    /// <summary>A pull towards a side with no monitor on it gives this much, to show there is nothing there.</summary>
    private const double NothingThereGives = 0.22;

    /// <summary>The page is being carried the rest of the way after the fingers let go.</summary>
    private bool _pageSliding;

    /// <summary>The monitor whose page is resting over the picture, waiting for its first frame; null when none is.</summary>
    private string? _pageHeld;

    /// <summary>The monitor the page that is up shows, so that it is only filled in when that changes.</summary>
    private string? _pageShows;

    private System.Windows.Threading.DispatcherTimer? _pageHeldTimer;
    private readonly Dictionary<string, (System.Windows.Media.Imaging.BitmapSource Picture, long Ticks)> _pagePictures = new(StringComparer.OrdinalIgnoreCase);
    private bool _pagePicturesWatched;

    /// <summary>
    /// Asks the other device for small pictures of its other monitors, so that there is one to show the moment a
    /// swipe starts. Asked when a view opens, when it changes monitor, and when two fingers come down (a third
    /// may follow); the other device sends them for a few seconds and stops, and what arrived is kept here.
    /// </summary>
    private void HavePagePicturesAtHand()
    {
        if (!SwipeBetweenMonitors)
        {
            return;
        }

        if (!_pagePicturesWatched)
        {
            _pagePicturesWatched = true;
            _viewer.MonitorPicturesChanged += _ => Dispatcher.BeginInvoke(TakePagePictures);
        }

        _viewer.AskForMonitorPictures(PagePictureWidth);
    }

    private void TakePagePictures()
    {
        foreach (var (key, picture) in _viewer.MonitorPictures)
        {
            if (_pagePictures.TryGetValue(key, out var have) && have.Ticks >= picture.ReceivedTicks)
            {
                continue;
            }

            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(picture.Jpeg);
                bitmap.EndInit();
                bitmap.Freeze();
                _pagePictures[key] = (bitmap, picture.ReceivedTicks);
                if (string.Equals(_pageShows, key, StringComparison.OrdinalIgnoreCase))
                {
                    PageImage.Source = bitmap; // a fresher one of the page that is up
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.IO.IOException or ArgumentException)
            {
            }
        }
    }

    /// <summary>The fingers have moved: the picture goes with them, less willingly towards a side with nothing on it.</summary>
    private void SlidePageTo(double shift)
    {
        _pageShift = MonitorRevealedBy(shift) is null ? shift * NothingThereGives : shift;
        ApplyView();
    }

    /// <summary>Puts the page where the picture's movement says it is, or takes it away. Called whenever the view moves.</summary>
    private void PlacePage()
    {
        var width = Surface.ActualWidth;
        var height = Surface.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_pageHeld is not null)
        {
            ShowPageAt(0, width, height);
            return;
        }

        if ((!_paging && !_pageSliding) || _pageShift == 0 || MonitorRevealedBy(_pageShift) is not { } target)
        {
            if (PageCover.Visibility == Visibility.Visible)
            {
                PageCover.Visibility = Visibility.Collapsed;
                _pageShows = null;
            }

            return;
        }

        if (!string.Equals(_pageShows, target.Key, StringComparison.OrdinalIgnoreCase))
        {
            _pageShows = target.Key;
            PageImage.Source = _pagePictures.TryGetValue(target.Key, out var have) ? have.Picture : null;
            PageLabel.Text = $"Монитор {target.Position + 1}";
        }

        // The picture moving left leaves room on its right, and the other way round.
        ShowPageAt(_pageShift + ((_pageShift < 0 ? 1 : -1) * (width + PageGap)), width, height);
    }

    private void ShowPageAt(double left, double width, double height)
    {
        PageCover.Width = width;
        PageCover.Height = height;
        System.Windows.Controls.Canvas.SetLeft(PageCover, left);
        System.Windows.Controls.Canvas.SetTop(PageCover, 0);
        PageCover.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The fingers let go: the movement is carried through. Far enough over, the page comes the rest of the way in
    /// and its monitor is asked for; otherwise the picture goes back where it was.
    /// </summary>
    private void FinishPaging()
    {
        var shift = _pageShift;
        var width = Surface.ActualWidth;
        LetGoOfPaging();
        if (Math.Abs(shift) >= MonitorNeighbours.CommitsAfter(width) && MonitorRevealedBy(shift) is { } target)
        {
            SlidePage(shift, (shift < 0 ? -1 : 1) * (width + PageGap), () =>
            {
                // The page has arrived and stays: the stream it stands in for has not started yet.
                _pageHeld = target.Key;
                _pageHeldTimer ??= new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(2.5), System.Windows.Threading.DispatcherPriority.Background, (_, _) => LetThePageGo(), Dispatcher);
                _pageHeldTimer.Stop();
                _pageHeldTimer.Interval = TimeSpan.FromSeconds(2.5); // if the stream never says so: not for ever
                _pageHeldTimer.Start();
                GoToMonitor(target.Position, target.Key);
            });
        }
        else
        {
            SlidePage(shift, 0, ApplyView);
        }
    }

    /// <summary>Moves the picture (and the page with it) from one shift to another over a moment, easing out.</summary>
    private void SlidePage(double from, double to, Action then)
    {
        var distance = Math.Abs(to - from);
        if (distance < 1)
        {
            _pageShift = 0;
            then();
            return;
        }

        // Longer for further, within what still feels like one movement.
        var lasts = TimeSpan.FromMilliseconds(Math.Clamp(120 + (distance * 0.12), 140, 300));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _pageSliding = true;
        _pageShift = from;
        EventHandler? step = null;
        step = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / lasts.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3);
            _pageShift = from + ((to - from) * eased);
            if (t < 1)
            {
                ApplyView();
                return;
            }

            System.Windows.Media.CompositionTarget.Rendering -= step;
            _pageSliding = false;
            _pageShift = 0; // whatever follows starts from the picture in its own place
            then();
        };
        System.Windows.Media.CompositionTarget.Rendering += step;
    }

    /// <summary>A frame arrived. If it is of the monitor whose page is resting over the picture, the page has done its work.</summary>
    private void PageSawAFrame()
    {
        if (_pageHeld is { } key && string.Equals(_viewer.Info.MonitorKey, key, StringComparison.OrdinalIgnoreCase))
        {
            LetThePageGo();
        }
    }

    /// <summary>
    /// The stream says it is of the monitor whose page is resting over the picture. Its first frame is here or
    /// about to be — and a screen on which nothing moves sends no second one to wait for — so the page goes in a
    /// moment either way.
    /// </summary>
    private void PageHeardOfItsMonitor()
    {
        if (_pageHeld is { } key && _pageHeldTimer is { } timer && string.Equals(_viewer.Info.MonitorKey, key, StringComparison.OrdinalIgnoreCase))
        {
            timer.Stop();
            timer.Interval = TimeSpan.FromMilliseconds(300);
            timer.Start();
        }
    }

    /// <summary>Takes the resting page away, fading: what is under it is the same picture, sharper.</summary>
    private void LetThePageGo()
    {
        _pageHeldTimer?.Stop();
        if (_pageHeld is null)
        {
            return;
        }

        _pageHeld = null;
        _pageShows = null;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            PageCover.BeginAnimation(OpacityProperty, null);
            PageCover.Opacity = 1;
            if (_pageHeld is null && !_paging && !_pageSliding)
            {
                PageCover.Visibility = Visibility.Collapsed;
            }
        };
        PageCover.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The swipe was taken away before it was finished: the picture goes back, wherever it had got to.</summary>
    private void CancelPaging()
    {
        var shift = _pageShift;
        LetGoOfPaging();
        SlidePage(shift, 0, ApplyView);
    }

    private void LetGoOfPaging()
    {
        _paging = false;
        _swiping = false;
        _pageShift = 0;
        _pageFrom = 0;
        var held = _gesture.Values.ToList();
        _gesture.Clear(); // first: letting go of a finger says so at once, and by then it must be nobody's
        _gestureStart = null;
        foreach (var device in held)
        {
            ScreenImage.ReleaseTouchCapture(device);
        }
    }

    private void GoToMonitor(int position, string? key)
    {
        _crop = new System.Drawing.RectangleF(0, 0, 1, 1);
        ResetCropButton.IsEnabled = false;
        ResetZoom();
        AskForWholeAnew();
        _viewer.SwitchMonitor(position, key);
        ShowNotice($"Монитор {position + 1}");
    }
}
