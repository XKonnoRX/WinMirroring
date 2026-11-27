using System.Windows.Input;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// From one monitor of the other device to the one beside it, with a swipe.
///
/// Two fingers moved sideways together slide the picture with them, and letting go far enough over shows the
/// monitor that stands on that side — the picture moves left, so what comes is what was to its right, as when
/// turning a page. Which monitor is on which side is taken from where they stand on that device's desktop.
///
/// Two fingers on the picture already meant something: a pinch zooms here, and two fingers moving together are
/// handed to the other device, where they mostly scroll. A swipe sideways is taken from that second case, and only
/// where it leads somewhere — on a device with one monitor, or towards a side with no monitor on it, two fingers
/// moving sideways still go to the other device as they did. What is given up is scrolling sideways with two
/// fingers towards a side that has a monitor; it can be switched off for whoever needs that more.
///
/// Zoomed in, two fingers move the picture, so there the same thing is done by pulling on past its edge.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>Two fingers are sliding the picture sideways towards another monitor.</summary>
    private bool _paging;

    /// <summary>How far the picture is slid from its place while they do, or pulled past its edge when zoomed in.</summary>
    private double _pageShift;

    /// <summary>How far a zoomed picture has been pulled past its edge by the fingers on it now.</summary>
    private double _overpull;

    /// <summary>How much of a pull past the edge is shown: enough to see it give, not so much that it comes away.</summary>
    private const double OverpullShown = 0.35;

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

    /// <summary>Whether two fingers that have moved this far together are a swipe to another monitor.</summary>
    private bool StartsPaging(System.Windows.Vector slide) =>
        MonitorNeighbours.IsSideways(slide.X, slide.Y) && MonitorRevealedBy(slide.X) is not null;

    /// <summary>The fingers let go: across to the other monitor if they went far enough, back where it was if not.</summary>
    private void FinishPaging()
    {
        var shift = _pageShift;
        _paging = false;
        _pageShift = 0;
        foreach (var device in _gesture.Values.ToList())
        {
            ScreenImage.ReleaseTouchCapture(device);
        }

        _gesture.Clear();
        _gestureStart = null;
        if (Math.Abs(shift) >= MonitorNeighbours.CommitsAfter(Surface.ActualWidth) && MonitorRevealedBy(shift) is { } target)
        {
            GoToMonitor(target.Position, target.Key);
        }
        else
        {
            ApplyView();
        }
    }

    /// <summary>
    /// Zoomed in, the fingers let go after pulling the picture past its edge: the same, if they pulled far enough.
    /// True when it went to another monitor.
    /// </summary>
    private bool FinishOverpull()
    {
        var pull = _overpull;
        _overpull = 0;
        _pageShift = 0;
        if (Math.Abs(pull) >= MonitorNeighbours.CommitsAfter(Surface.ActualWidth) * 1.4 && MonitorRevealedBy(pull) is { } target)
        {
            GoToMonitor(target.Position, target.Key);
            return true;
        }

        return false;
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
