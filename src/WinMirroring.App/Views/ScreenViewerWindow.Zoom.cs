using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinMirroring.App.Services;
using WinMirroring.Screen;
using WinMirroring.Screen.Protocol;
using InputButton = WinMirroring.Input.Protocol.MouseButton;

namespace WinMirroring.App.Views;

/// <summary>
/// Looking closer, and the touch keyboard.
///
/// The picture is zoomed by a transform on the image, not by asking for a different picture: every position this
/// window sends is measured on the image itself (<c>e.GetPosition(ScreenImage)</c>), and WPF measures through the
/// transform, so clicks and touches land on what is under them at any zoom without anything else changing. What
/// does change is how much picture is asked for: the stream is requested at the size it is drawn at, so a zoomed
/// view gets the other device's real pixels (up to its own resolution) instead of a stretched copy.
///
/// Moving around a zoomed picture: with the mouse, holding the pointer near an edge of the window scrolls that way
/// (the way Windows' Magnifier follows the pointer), so the mouse itself keeps doing what it did — clicking there.
/// With fingers, two of them pan and pinch here, while one goes on being a touch there. Ctrl+Alt+wheel zooms at the
/// pointer (and plain Ctrl+wheel while control is off), Ctrl+Alt+plus/minus/0 from the keyboard, and buttons in
/// the toolbar and the menu.
///
/// The touch keyboard used to shrink the picture: Windows makes room for a docked keyboard by shrinking the work
/// area, and a maximized window — which is what full screen is here — shrinks along with it. The window now keeps
/// its size and the keyboard lies over it; instead, the picture is lifted just enough that the place last tapped
/// (where the text box is, almost always) stays above the keyboard, and it comes back down when the keyboard goes.
/// </summary>
public partial class ScreenViewerWindow
{
    private const double MaxZoom = 4;
    private const double ZoomStep = 1.25;

    /// <summary>How close to an edge of the window the pointer has to be to scroll a zoomed picture, and how fast at the very edge.</summary>
    private const double EdgeBand = 40;
    private const double EdgeSpeed = 22;

    /// <summary>Room left between what was tapped and the top of the keyboard.</summary>
    private const double KeyboardMargin = 72;

    private readonly ScaleTransform _zoomScale = new();
    private readonly TranslateTransform _zoomShift = new();
    private readonly Dictionary<int, TouchDevice> _fingers = [];
    private readonly Dictionary<int, TouchDevice> _gesture = [];
    private double _zoom = 1;

    /// <summary>Where the scaled picture is moved from its place in the layout (its top-left), in this window's units.</summary>
    private Vector _pan;

    /// <summary>How far the picture is lifted out of the touch keyboard's way.</summary>
    private double _lift;

    private Point? _mouseOnSurface;

    /// <summary>The last place pressed or tapped, as a point of the picture (so it stays right when the picture moves).</summary>
    private Point? _interest;

    private DispatcherTimer? _edgeTimer;
    private (double Distance, Point Middle, double Zoom, Vector Pan)? _gestureStart;
    private InputPaneWatcher? _inputPane;
    private Int32Rect? _maximizedRect;

    private void InitializeZoom()
    {
        ScreenImage.RenderTransform = new TransformGroup { Children = { _zoomScale, _zoomShift } };
        Surface.SizeChanged += (_, _) => ApplyView();
        ScreenImage.SizeChanged += (_, _) => ApplyView();
        Surface.PreviewMouseMove += OnSurfacePreviewMouseMove;
        Surface.MouseLeave += (_, _) =>
        {
            _mouseOnSurface = null;
            _edgeTimer?.Stop();
        };

        _edgeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => ScrollAtEdge(), Dispatcher);
        _edgeTimer.Stop();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(KeepSizeUnderKeyboard);
            _inputPane = new InputPaneWatcher(handle);
            _inputPane.Showing += rect => Dispatcher.BeginInvoke(() => OnKeyboardShowing(rect));
            _inputPane.Hiding += () => Dispatcher.BeginInvoke(OnKeyboardHiding);
        };
        Closed += (_, _) => _inputPane?.Dispose();
        UpdateZoomText();
    }

    // ---- the view -----------------------------------------------------------------------------------------

    /// <summary>Where the image is laid out inside <c>Surface</c>, before any zoom.</summary>
    private Vector LayoutOrigin => VisualTreeHelper.GetOffset(ScreenImage);

    /// <summary>Keeps the picture covering the window where it can, centred where it cannot, and draws it.</summary>
    private void ApplyView()
    {
        var origin = LayoutOrigin;
        _pan = new Vector(
            Clamp(_pan.X, origin.X, ScreenImage.ActualWidth * _zoom, Surface.ActualWidth),
            Clamp(_pan.Y, origin.Y, ScreenImage.ActualHeight * _zoom, Surface.ActualHeight));

        _zoomScale.ScaleX = _zoomScale.ScaleY = _zoom;
        _zoomShift.X = _pan.X;
        _zoomShift.Y = _pan.Y - _lift;
        PlacePointer();
    }

    private static double Clamp(double pan, double origin, double size, double view)
    {
        if (size <= view + 0.5)
        {
            return ((view - size) / 2) - origin;
        }

        return Math.Clamp(pan, view - origin - size, -origin);
    }

    /// <summary>Zooms to <paramref name="zoom"/>, keeping the point of the picture under <paramref name="anchor"/> (in Surface units) where it is.</summary>
    private void ZoomTo(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        if (zoom < 1.03)
        {
            zoom = 1;
        }

        var origin = LayoutOrigin;
        var local = new Point((anchor.X - origin.X - _pan.X) / _zoom, (anchor.Y - origin.Y - _pan.Y + _lift) / _zoom);
        _zoom = zoom;
        _pan = new Vector(anchor.X - origin.X - (local.X * zoom), anchor.Y - origin.Y + _lift - (local.Y * zoom));
        ApplyView();
        UpdateZoomText();
        ReportShownSize();
    }

    private Point SurfaceCentre => new(Surface.ActualWidth / 2, Surface.ActualHeight / 2);

    private void ZoomBy(double factor) => ZoomTo(_zoom * factor, _mouseOnSurface ?? SurfaceCentre);

    /// <summary>Back to the whole picture.</summary>
    private void ResetZoom()
    {
        _zoom = 1;
        _pan = default;
        _gesture.Clear();
        _gestureStart = null;
        _undecided = false;
        _edgeTimer?.Stop();
        ApplyView();
        UpdateZoomText();
        ReportShownSize();
    }

    private void UpdateZoomText()
    {
        var text = $"{Math.Round(_zoom * 100)}%";
        ZoomText.Content = text;
        QuickZoomText.Text = text;
        ZoomOutButton.IsEnabled = QuickZoomOutButton.IsEnabled = _zoom > 1;
        ZoomInButton.IsEnabled = QuickZoomInButton.IsEnabled = _zoom < MaxZoom;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomTo(_zoom * ZoomStep, SurfaceCentre);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomTo(_zoom / ZoomStep, SurfaceCentre);

    private void OnZoomReset(object sender, RoutedEventArgs e) => ResetZoom();

    /// <summary>How far the picture is zoomed in (1: whole), for the control panel.</summary>
    public double ZoomLevel => _zoom;

    /// <summary>From the control panel, which is where these buttons are while the view is full screen.</summary>
    public void ZoomFromPanel(int direction)
    {
        if (direction > 0)
        {
            ZoomTo(_zoom * ZoomStep, SurfaceCentre);
        }
        else if (direction < 0)
        {
            ZoomTo(_zoom / ZoomStep, SurfaceCentre);
        }
        else
        {
            ResetZoom();
        }
    }

    /// <summary>Ctrl+Alt+wheel zooms at the pointer; Ctrl+wheel too while this window only watches. True when it did.</summary>
    private bool TryZoomWithWheel(MouseWheelEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Control) || !(modifiers.HasFlag(ModifierKeys.Alt) || !ControlEnabled))
        {
            return false;
        }

        ZoomTo(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), e.GetPosition(Surface));
        e.Handled = true;
        return true;
    }

    /// <summary>Ctrl+Alt with plus, minus or zero: zoom in, out, or back. True when the key was one of them.</summary>
    private bool TryZoomWithKey(Key key, bool down)
    {
        var modifiers = Keyboard.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Control) || !modifiers.HasFlag(ModifierKeys.Alt))
        {
            return false;
        }

        Action? act = key switch
        {
            Key.OemPlus or Key.Add => () => ZoomBy(ZoomStep),
            Key.OemMinus or Key.Subtract => () => ZoomBy(1 / ZoomStep),
            Key.D0 or Key.NumPad0 => ResetZoom,
            _ => null,
        };

        if (act is null)
        {
            return false;
        }

        if (down)
        {
            act();
        }

        return true;
    }

    // ---- moving around with the mouse ---------------------------------------------------------------------

    private void OnSurfacePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsFromTouch(e))
        {
            return;
        }

        _mouseOnSurface = e.GetPosition(Surface);
        if (_zoom > 1 && CropToggle.IsChecked != true && EdgePush(_mouseOnSurface.Value) != default)
        {
            _edgeTimer?.Start();
        }
    }

    /// <summary>How far to move the picture this tick for a pointer at <paramref name="at"/>: towards whichever edge it is near.</summary>
    private Vector EdgePush(Point at)
    {
        static double Axis(double p, double size) =>
            p < EdgeBand ? EdgeSpeed * (1 - (Math.Max(0, p) / EdgeBand))
            : p > size - EdgeBand ? -EdgeSpeed * (1 - (Math.Max(0, size - p) / EdgeBand))
            : 0;

        return new Vector(Axis(at.X, Surface.ActualWidth), Axis(at.Y, Surface.ActualHeight));
    }

    private void ScrollAtEdge()
    {
        if (_zoom <= 1 || _mouseOnSurface is not { } at || CropToggle.IsChecked == true || !IsActive)
        {
            _edgeTimer?.Stop();
            return;
        }

        var push = EdgePush(at);
        if (push == default)
        {
            _edgeTimer?.Stop();
            return;
        }

        var before = _pan;
        _pan += push;
        ApplyView();
        if (_pan == before)
        {
            return; // already at that edge of the picture
        }

        // The picture moved under a pointer that did not: what it points at there is somewhere else now.
        if (ControlEnabled && !_dragOutActive && TryNormalize(Mouse.GetPosition(ScreenImage), out var x, out var y, out var outside) && outside <= 0)
        {
            _pointerHere = (x, y);
            _lastPointer = (x, y);
            PlacePointer();
            _viewer.SendPointer(PointerAction.Move, InputButton.None, x, y);
        }
    }

    // ---- moving around with fingers -----------------------------------------------------------------------

    /// <summary>Remembers where something was pressed, as a point of the picture.</summary>
    private void NoteInterest(Point onImage) => _interest = onImage;

    /// <summary>
    /// Two fingers on a whole picture have not said yet what they are: a pinch, which zooms here, or two fingers
    /// moving together (scrolling, mostly), which belong to the other device. The first movement decides.
    /// </summary>
    private bool _undecided;

    /// <summary>A pinch must change the distance between the fingers by this much to be a pinch...</summary>
    private const double PinchDecides = 0.12;

    /// <summary>...before the fingers together have moved this far, which makes it a gesture for the other device.</summary>
    private const double SlideDecides = 28;

    /// <summary>A finger came down on the picture. True when it belongs to a gesture here rather than a touch there.</summary>
    private bool GestureTouchDown(TouchEventArgs e)
    {
        var id = e.TouchDevice.Id;
        if (_gesture.Count > 0)
        {
            if (_undecided)
            {
                // A third finger before the two decided: a gesture of more fingers, for the other device.
                HandOverToRemote();
                return false;
            }

            if (_gesture.Count < 2)
            {
                _gesture[id] = e.TouchDevice;
                e.TouchDevice.Capture(ScreenImage);
                StartGesture();
            }

            e.Handled = true;
            return true;
        }

        // A second finger while the first is a touch there: it is taken back, and the two are a gesture here — on a
        // zoomed picture for certain, on a whole one only if they turn out to pinch.
        if (ControlEnabled && _touches.Count == 1 && _fingers.Count == 1)
        {
            var (firstId, first) = _fingers.First();
            if (_touches.Remove(firstId, out var touch))
            {
                _viewer.SendTouch(ContactCancel, touch.Contact, touch.X, touch.Y);
            }

            _fingers.Clear();
            _gesture[firstId] = first;
            _undecided = _zoom <= 1;
        }
        else if (ControlEnabled || _zoom <= 1)
        {
            return false;
        }

        // (Watching only, on a zoomed picture: one finger already moves it.)
        _gesture[id] = e.TouchDevice;
        e.TouchDevice.Capture(ScreenImage);
        StartGesture();
        e.Handled = true;
        return true;
    }

    private bool GestureTouchMove(TouchEventArgs e)
    {
        if (!_gesture.ContainsKey(e.TouchDevice.Id) || _gestureStart is not { } start)
        {
            return false;
        }

        e.Handled = true;
        var (distance, middle) = Measure();
        if (_undecided)
        {
            var pinched = start.Distance > 0 && Math.Abs((distance / start.Distance) - 1) >= PinchDecides;
            if (!pinched)
            {
                if ((middle - start.Middle).Length >= SlideDecides)
                {
                    HandOverToRemote();
                }

                return true;
            }

            _undecided = false; // a pinch: zooming here, from where the fingers started
        }

        var origin = LayoutOrigin;
        if (_gesture.Count >= 2 && start.Distance > 0)
        {
            var zoom = Math.Clamp(start.Zoom * distance / start.Distance, 1, MaxZoom);
            var local = new Point(
                (start.Middle.X - origin.X - start.Pan.X) / start.Zoom,
                (start.Middle.Y - origin.Y - start.Pan.Y + _lift) / start.Zoom);
            _zoom = zoom;
            _pan = new Vector(middle.X - origin.X - (local.X * zoom), middle.Y - origin.Y + _lift - (local.Y * zoom));
            UpdateZoomText();
        }
        else
        {
            _pan = start.Pan + (middle - start.Middle);
        }

        ApplyView();
        return true;
    }

    private bool GestureTouchUp(TouchEventArgs e)
    {
        if (_undecided && _gesture.ContainsKey(e.TouchDevice.Id))
        {
            // Lifted before deciding — a two-finger tap: it is the other device's, and the lift goes there too.
            HandOverToRemote();
            return false;
        }

        if (!_gesture.Remove(e.TouchDevice.Id))
        {
            _fingers.Remove(e.TouchDevice.Id);
            return false;
        }

        e.Handled = true;
        ScreenImage.ReleaseTouchCapture(e.TouchDevice);
        if (_gesture.Count > 0)
        {
            StartGesture();
        }
        else
        {
            _gestureStart = null;
            if (_zoom < 1.03)
            {
                ResetZoom();
            }
            else
            {
                ReportShownSize();
            }
        }

        return true;
    }

    /// <summary>
    /// The fingers held back as a possible pinch were not one: they become touches on the other device, from
    /// where they are now, and go on as any other touches do.
    /// </summary>
    private void HandOverToRemote()
    {
        _undecided = false;
        foreach (var (id, device) in _gesture.ToList())
        {
            if (_touches.Count >= MaxContacts
                || !TryNormalize(device.GetTouchPoint(ScreenImage).Position, out var x, out var y, out _))
            {
                continue;
            }

            var contact = Enumerable.Range(0, MaxContacts).Select(i => (byte)i).First(i => _touches.Values.All(t => t.Contact != i));
            _touches[id] = (contact, x, y);
            _fingers[id] = device;
            _viewer.SendTouch(ContactDown, contact, x, y);
        }

        _gesture.Clear();
        _gestureStart = null;
    }

    private void StartGesture()
    {
        var (distance, middle) = Measure();
        _gestureStart = (distance, middle, _zoom, _pan);
    }

    private (double Distance, Point Middle) Measure()
    {
        var points = _gesture.Values.Take(2).Select(d => d.GetTouchPoint(Surface).Position).ToList();
        if (points.Count == 1)
        {
            return (0, points[0]);
        }

        return ((points[0] - points[1]).Length, new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2));
    }

    // ---- the touch keyboard -----------------------------------------------------------------------------

    private void OnKeyboardShowing(InputPaneRect rect)
    {
        if (!IsVisible || PresentationSource.FromVisual(Surface) is null)
        {
            return;
        }

        var top = Surface.PointFromScreen(new Point(rect.Left, rect.Top));
        var bottom = Surface.PointFromScreen(new Point(rect.Right, rect.Bottom));
        if (top.Y >= Surface.ActualHeight || bottom.Y <= 0)
        {
            SetLift(0); // not over this window
            return;
        }

        // What should stay in sight: where the picture was last tapped, or else where its pointer is.
        double? focus = null;
        if (_interest is { } point)
        {
            focus = ScreenImage.TranslatePoint(point, Surface).Y + _lift;
        }
        else if (_viewer.Pointer is { Visible: true } pointer)
        {
            focus = ScreenImage.TranslatePoint(new Point(pointer.X * ScreenImage.ActualWidth, pointer.Y * ScreenImage.ActualHeight), Surface).Y + _lift;
        }

        var wanted = focus is { } y ? y - (top.Y - KeyboardMargin) : Surface.ActualHeight - top.Y;
        SetLift(Math.Clamp(wanted, 0, Math.Max(0, Surface.ActualHeight - top.Y)));
    }

    private void OnKeyboardHiding()
    {
        SetLift(0);
        ForgetTapKeyboard();
    }

    private void SetLift(double lift)
    {
        if (Math.Abs(lift - _lift) < 0.5)
        {
            return;
        }

        _lift = lift;
        ApplyView();
    }

    /// <summary>
    /// Keeps a maximized (or full-screen) viewer its size while the touch keyboard is up. Windows makes room for a
    /// docked keyboard by shrinking the work area and resizing maximized windows to it; this one would rather have
    /// the keyboard over it and lift the picture itself.
    /// </summary>
    private nint KeepSizeUnderKeyboard(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmWindowPosChanging = 0x0046;
        const uint SwpNoSize = 0x0001;
        const uint SwpNoMove = 0x0002;
        if (message != WmWindowPosChanging || _windowMode || WindowState != WindowState.Maximized || lParam == 0)
        {
            return 0;
        }

        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((position.flags & SwpNoSize) != 0)
        {
            return 0;
        }

        // Full screen covers the whole monitor whatever the work area says, always — so it does not matter whether
        // Windows tells about the keyboard before or after it shrinks the work area. A maximized window with its
        // frame keeps the size it had before the keyboard came up.
        Int32Rect? keep;
        if (IsFullScreen)
        {
            keep = MonitorRect(hwnd);
        }
        else if (_inputPane?.Location is null)
        {
            if ((position.flags & SwpNoMove) == 0)
            {
                _maximizedRect = new Int32Rect(position.x, position.y, position.cx, position.cy);
            }

            return 0;
        }
        else
        {
            keep = _maximizedRect;
        }

        if (keep is { } rect && (position.cx < rect.Width || position.cy < rect.Height))
        {
            position.x = rect.X;
            position.y = rect.Y;
            position.cx = rect.Width;
            position.cy = rect.Height;
            position.flags &= ~SwpNoMove;
            Marshal.StructureToPtr(position, lParam, false);
        }

        return 0;
    }

    private static Int32Rect? MonitorRect(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var r = info.rcMonitor;
        return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public InputPaneRect rcMonitor;
        public InputPaneRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
}
