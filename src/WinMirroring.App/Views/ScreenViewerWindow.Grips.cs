using System.Windows;
using System.Windows.Input;
using WinMirroring.App.Services;
using WinMirroring.Screen;

namespace WinMirroring.App.Views;

/// <summary>
/// From one monitor of the other device to the one beside it, by a grip at the edge of the picture.
///
/// Two gestures were tried for this first, and both were somebody else's already. Two fingers sideways are also
/// how a zoomed picture is moved, and the start of one is not told from the start of the other. Three fingers are
/// nothing else in this window, but Windows 11 keeps them for itself on a touch screen unless that is switched off
/// in its settings, and three fingers put down together within a moment is not something a hand does without
/// thinking about it. A gesture that has to be explained and then set up is not a gesture.
///
/// So there is something to take hold of instead. At the left and the right edge of the picture, where the other
/// device has a monitor on that side and only there, stands a small grip. Pulled inwards it brings that monitor's
/// page with it, under the finger, exactly as the swipe did (the page, its small picture and the way it is carried
/// through on letting go are the ones in ScreenViewerWindow.Paging). Touched and let go, it turns the page by
/// itself. One finger, starting on a thing that is nothing else: there is no other meaning to tell it from, here
/// or in Windows, and that there is a monitor that way is seen without trying.
///
/// What it costs is the strip of picture under it. For that, a grip pulled along the edge instead of inwards
/// moves — both of them, so that they stay a pair — and stays where it was left.
///
/// The grips are for fingers: they are shown on a device with a touch screen. With a mouse there are the buttons
/// in the toolbar and the menu, and a grip over the edge of the picture would only be in the way of what is
/// there — a scroll bar, most often.
/// </summary>
public partial class ScreenViewerWindow
{
    private const double GripRestOpacity = 0.6;

    /// <summary>Which grip is held: −1 the left one, +1 the right one, 0 neither.</summary>
    private int _gripSide;

    /// <summary>The finger that holds it; null when it is the mouse.</summary>
    private TouchDevice? _gripFinger;

    /// <summary>Where it was taken, in the picture's area, and how far down the grip itself.</summary>
    private Point _gripStart;
    private double _gripGrabY;

    /// <summary>What the hold has turned out to be since.</summary>
    private MonitorNeighbours.GripDoes _gripDoes;

    /// <summary>How far the finger had already gone inwards when the picture started to follow it.</summary>
    private double _gripFrom;

    /// <summary>A grip is being pulled inwards and the picture is going with it.</summary>
    private bool GripPulling => _gripSide != 0 && _gripDoes == MonitorNeighbours.GripDoes.Pull;

    private bool GripsWanted =>
        _node?.Settings.Screen.PageGrips != false && SeveralMonitors && SystemTouchGestures.HasTouchScreen;

    private void InitializeGrips()
    {
        foreach (var (grip, side) in new[] { (PageGripLeft, -1), (PageGripRight, +1) })
        {
            grip.TouchDown += (_, e) =>
            {
                e.Handled = true; // never a touch on the picture underneath, whatever comes of it here
                if (GripTaken(side, e.TouchDevice, e.GetTouchPoint(Surface).Position, e.GetTouchPoint(grip).Position.Y))
                {
                    e.TouchDevice.Capture(grip);
                }
            };
            grip.TouchMove += (_, e) =>
            {
                e.Handled = true;
                if (_gripSide == side && ReferenceEquals(_gripFinger, e.TouchDevice))
                {
                    GripMoved(e.GetTouchPoint(Surface).Position, TouchDragAfter);
                }
            };
            grip.TouchUp += (_, e) =>
            {
                e.Handled = true;
                if (_gripSide == side && ReferenceEquals(_gripFinger, e.TouchDevice))
                {
                    GripLetGo(); // first: giving the finger back is told as losing it, and by then it is nobody's
                    grip.ReleaseTouchCapture(e.TouchDevice);
                }
            };
            grip.LostTouchCapture += (_, e) =>
            {
                if (_gripSide == side && ReferenceEquals(_gripFinger, e.TouchDevice))
                {
                    GripLost();
                }
            };

            grip.MouseLeftButtonDown += (_, e) =>
            {
                if (IsFromTouch(e))
                {
                    return; // the finger has its own path above
                }

                e.Handled = true;
                if (GripTaken(side, null, e.GetPosition(Surface), e.GetPosition(grip).Y))
                {
                    grip.CaptureMouse();
                }
            };
            grip.MouseMove += (_, e) =>
            {
                if (!IsFromTouch(e) && _gripSide == side && _gripFinger is null && grip.IsMouseCaptured)
                {
                    GripMoved(e.GetPosition(Surface), MouseDragAfter);
                }
            };
            grip.MouseLeftButtonUp += (_, e) =>
            {
                if (IsFromTouch(e) || _gripSide != side || _gripFinger is not null || !grip.IsMouseCaptured)
                {
                    return;
                }

                e.Handled = true;
                GripLetGo();
                grip.ReleaseMouseCapture();
            };
            grip.LostMouseCapture += (_, _) =>
            {
                if (_gripSide == side && _gripFinger is null)
                {
                    GripLost();
                }
            };
            grip.MouseEnter += (_, _) => DimGrips();
            grip.MouseLeave += (_, _) => DimGrips();
        }

        Surface.SizeChanged += (_, _) => PlaceGrips();
        Loaded += (_, _) => UpdateGrips();
    }

    /// <summary>A finger or the mouse came down on a grip. False when it is not taken: one is held already, or a page is on its way.</summary>
    private bool GripTaken(int side, TouchDevice? finger, Point onSurface, double grabY)
    {
        if (_gripSide != 0 || _paging || _swiping || _pageSliding || _pageHeld is not null)
        {
            return false;
        }

        _gripSide = side;
        _gripFinger = finger;
        _gripStart = onSurface;
        _gripGrabY = grabY;
        _gripDoes = MonitorNeighbours.GripDoes.Nothing;
        _gripFrom = 0;
        CloseQuickPanel();
        HavePagePicturesAtHand(); // a fresher picture of what is about to be pulled in
        DimGrips();
        return true;
    }

    private void GripMoved(Point onSurface, double decidesAfter)
    {
        var moved = onSurface - _gripStart;
        if (_gripDoes == MonitorNeighbours.GripDoes.Nothing)
        {
            _gripDoes = MonitorNeighbours.WhatGripDoes(moved.X, moved.Y, decidesAfter);
            if (_gripDoes == MonitorNeighbours.GripDoes.Nothing)
            {
                return;
            }

            // The picture goes with the finger from where it stands: what the finger moved before saying which
            // way it goes is not made up in a jump.
            _gripFrom = moved.X;
        }

        if (_gripDoes == MonitorNeighbours.GripDoes.Pull)
        {
            _pageShift = MonitorNeighbours.GripPull(_gripSide, moved.X - _gripFrom);
            ApplyView();
            return;
        }

        // Along the edge: the grips are being moved out of the way of something.
        var room = Surface.ActualHeight - PageGripLeft.Height;
        if (room > 0 && _node?.Settings.Screen is { } screen)
        {
            screen.PageGripsY = Math.Clamp((onSurface.Y - _gripGrabY) / room, 0, 1);
            PlaceGrips();
        }
    }

    private void GripLetGo()
    {
        var side = _gripSide;
        var did = _gripDoes;
        var shift = _pageShift;
        ForgetGrip();
        switch (did)
        {
            case MonitorNeighbours.GripDoes.Nothing:
                TurnPage(side); // touched and let go: the page turns by itself
                break;
            case MonitorNeighbours.GripDoes.Pull:
                CarryPageThrough(shift);
                break;
            default:
                _node?.SaveSettings(); // where the grips were left
                break;
        }

        DimGrips();
    }

    /// <summary>The finger was taken away before it let go (by the system, mostly): nothing is turned.</summary>
    private void GripLost()
    {
        var did = _gripDoes;
        var shift = _pageShift;
        ForgetGrip();
        if (did == MonitorNeighbours.GripDoes.Pull)
        {
            SlidePage(shift, 0, ApplyView);
        }
        else if (did == MonitorNeighbours.GripDoes.Move)
        {
            _node?.SaveSettings();
        }

        DimGrips();
    }

    private void ForgetGrip()
    {
        _gripSide = 0;
        _gripFinger = null;
        _gripDoes = MonitorNeighbours.GripDoes.Nothing;
        _gripFrom = 0;
    }

    /// <summary>Turns to the monitor on the side of the grip that was touched, the page sliding in from there.</summary>
    private void TurnPage(int side)
    {
        // The left grip brings what is to the left, so the picture leaves to the right; and the other way round.
        double way = -side;
        if (MonitorRevealedBy(way) is not { } target)
        {
            return;
        }

        SlidePage(way, way * (Surface.ActualWidth + PageGap), () => RestPageOn(target));
    }

    /// <summary>
    /// Shows each grip where there is a monitor on its side to go to, and nowhere else. Asked whenever that may
    /// have changed: the stream says something new about itself, a page has come to rest, and once a second for
    /// the rest — the first picture, the setting, a frame being drawn.
    /// </summary>
    private void UpdateGrips()
    {
        var wanted = GripsWanted && HasPicture && CropToggle.IsChecked != true;
        ShowGrip(PageGripLeft, -1, wanted && MonitorRevealedBy(+1) is not null);
        ShowGrip(PageGripRight, +1, wanted && MonitorRevealedBy(-1) is not null);
        PlaceGrips();
        DimGrips();
    }

    private void ShowGrip(FrameworkElement grip, int side, bool show)
    {
        if (_gripSide == side)
        {
            return; // held: it is not taken from under the finger
        }

        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (grip.Visibility != visibility)
        {
            grip.Visibility = visibility;
        }
    }

    private void PlaceGrips()
    {
        var share = Math.Clamp(_node?.Settings.Screen.PageGripsY ?? 0.5, 0, 1);
        var top = share * Math.Max(0, Surface.ActualHeight - PageGripLeft.Height);
        PageGripLeft.Margin = new Thickness(0, top, 0, 0);
        PageGripRight.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>
    /// The grips stand back while a page is moving or resting — they would lie on top of it — and come forward
    /// under a hand. Not shown is not the same as gone: a held grip keeps the finger it has.
    /// </summary>
    private void DimGrips()
    {
        var away = _paging || GripPulling || _pageSliding || _pageHeld is not null;
        Dim(PageGripLeft, -1);
        Dim(PageGripRight, +1);

        void Dim(FrameworkElement grip, int side)
        {
            var opacity = away ? 0 : _gripSide == side || grip.IsMouseOver ? 1 : GripRestOpacity;
            if (grip.Opacity != opacity)
            {
                grip.Opacity = opacity;
            }
        }
    }
}
