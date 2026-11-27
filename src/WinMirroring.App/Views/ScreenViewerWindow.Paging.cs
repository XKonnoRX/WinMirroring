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

    /// <summary>The fingers let go: across to the other monitor if they went far enough, back where it was if not.</summary>
    private void FinishPaging()
    {
        var shift = _pageShift;
        LetGoOfPaging();
        if (Math.Abs(shift) >= MonitorNeighbours.CommitsAfter(Surface.ActualWidth) && MonitorRevealedBy(shift) is { } target)
        {
            GoToMonitor(target.Position, target.Key);
        }
        else
        {
            ApplyView();
        }
    }

    /// <summary>The swipe was taken away before it was finished: the picture goes back, wherever it had got to.</summary>
    private void CancelPaging()
    {
        LetGoOfPaging();
        ApplyView();
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

    private void GoToMonitor(int position, string key)
    {
        _crop = new System.Drawing.RectangleF(0, 0, 1, 1);
        ResetCropButton.IsEnabled = false;
        ResetZoom();
        AskForWholeAnew();
        _viewer.SwitchMonitor(position, key);
        ShowNotice($"Монитор {position + 1}");
    }
}
