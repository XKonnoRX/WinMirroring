using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinMirroring.Screen;
using WinMirroring.Screen.Protocol;
using InputButton = WinMirroring.Input.Protocol.MouseButton;

namespace WinMirroring.App.Views;

/// <summary>Shows a monitor or window of another device and sends pointer, touch and keyboard input back.</summary>
public partial class ScreenViewerWindow : Window
{
    private static readonly HashSet<Key> ExtendedKeys =
    [
        Key.Insert, Key.Delete, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Left, Key.Right, Key.Up, Key.Down,
        Key.RightCtrl, Key.RightAlt, Key.Divide, Key.NumLock, Key.PrintScreen, Key.LWin, Key.RWin, Key.Apps,
    ];

    private readonly ScreenViewer _viewer;
    private readonly DispatcherTimer _statsTimer;
    private WriteableBitmap? _bitmap;
    private System.Drawing.RectangleF _crop = new(0, 0, 1, 1);
    private Point? _cropStart;
    private bool _sizedToContent;
    private bool _closingFromRemote;
    private int _framesSinceTick;
    private WindowState _stateBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;

    public ScreenViewerWindow(ScreenViewer viewer)
    {
        InitializeComponent();
        _viewer = viewer;
        UpdateInfo();

        _viewer.InfoChanged += _ => Dispatcher.BeginInvoke(UpdateInfo);
        _viewer.Closed += (_, reason) => Dispatcher.BeginInvoke(() => OnRemoteClosed(reason));
        _viewer.Attach(update => Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(update)));

        ScreenImage.MouseMove += OnImageMouseMove;
        ScreenImage.MouseDown += OnImageMouseDown;
        ScreenImage.MouseUp += OnImageMouseUp;
        ScreenImage.MouseWheel += OnImageMouseWheel;
        PreviewKeyDown += OnPreviewKey;
        PreviewKeyUp += OnPreviewKey;

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            StatsText.Text = $"{_framesSinceTick} кадр/с";
            _framesSinceTick = 0;
        }, Dispatcher);
        _statsTimer.Start();

        if (viewer.Info.Kind == "window")
        {
            PlaceAtCursor();
        }
    }

    private bool ControlEnabled => ControlToggle.IsChecked == true && _viewer.Info.ControlAllowed && CropToggle.IsChecked != true;

    private void UpdateInfo()
    {
        var info = _viewer.Info;
        Title = $"{info.Title} — {_viewer.PeerName}";
        TitleText.Text = Title;
        MonitorPanel.Visibility = info.Kind == "monitor" && info.MonitorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorText.Text = $"{info.MonitorIndex + 1} из {info.MonitorCount}";
        ControlToggle.IsEnabled = info.ControlAllowed;
        if (!info.ControlAllowed)
        {
            ControlToggle.IsChecked = false;
            ControlToggle.ToolTip = "Управление запрещено на том устройстве";
        }
    }

    private void Present(FrameUpdate update)
    {
        try
        {
            if (_bitmap is null || update.Resized || _bitmap.PixelWidth != update.Width || _bitmap.PixelHeight != update.Height)
            {
                _bitmap = new WriteableBitmap(update.Width, update.Height, 96, 96, PixelFormats.Bgr32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, update.Width, update.Height), update.Image, update.Width * 4, 0);
                ScreenImage.Source = _bitmap;
                WaitingText.Visibility = Visibility.Collapsed;
                SizeToStream(update.Width, update.Height);
            }
            else
            {
                var stride = update.Width * 4;
                foreach (var rect in update.Dirty)
                {
                    _bitmap.WritePixels(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height), update.Image, stride, (rect.Y * update.Width + rect.X) * 4);
                }
            }

            _framesSinceTick++;
        }
        finally
        {
            _viewer.FramePresented();
        }
    }

    /// <summary>First frame of a window stream (and after a crop): size the viewer to the content, as large as fits.</summary>
    private void SizeToStream(int width, int height)
    {
        if (_viewer.Info.Kind != "window" && _sizedToContent)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea;
        var chrome = ActualHeight - Surface.ActualHeight;
        if (double.IsNaN(chrome) || chrome <= 0)
        {
            chrome = Toolbar.ActualHeight + SystemParameters.WindowCaptionHeight + 16;
        }

        var contentWidth = width / dpi.DpiScaleX;
        var contentHeight = height / dpi.DpiScaleY;
        var scale = Math.Min(1.0, Math.Min(work.Width * 0.92 / contentWidth, (work.Height * 0.92 - chrome) / contentHeight));
        Width = Math.Max(MinWidth, contentWidth * scale + 16);
        Height = Math.Max(MinHeight, contentHeight * scale + chrome);
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        _sizedToContent = true;
    }

    private void PlaceAtCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        var source = PresentationSource.FromVisual(Application.Current.MainWindow ?? this);
        var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        Left = cursor.X * scale - 40;
        Top = cursor.Y * scale - 20;
    }

    // ---- pointer -----------------------------------------------------------------------------------------

    private bool TryNormalize(MouseEventArgs e, out float x, out float y)
    {
        x = y = 0;
        if (_bitmap is null || ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0)
        {
            return false;
        }

        var position = e.GetPosition(ScreenImage);
        x = (float)Math.Clamp(position.X / ScreenImage.ActualWidth, 0, 1);
        y = (float)Math.Clamp(position.Y / ScreenImage.ActualHeight, 0, 1);
        return true;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (CropToggle.IsChecked == true)
        {
            UpdateCropRectangle(e.GetPosition(Overlay));
            return;
        }

        if (ControlEnabled && TryNormalize(e, out var x, out var y))
        {
            _viewer.SendPointer(PointerAction.Move, InputButton.None, x, y);
        }
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenImage.Focus();
        if (CropToggle.IsChecked == true && e.ChangedButton == MouseButton.Left)
        {
            _cropStart = e.GetPosition(Overlay);
            ScreenImage.CaptureMouse();
            UpdateCropRectangle(_cropStart.Value);
            CropRectangle.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }

        if (ControlEnabled && TryNormalize(e, out var x, out var y))
        {
            ScreenImage.CaptureMouse();
            _viewer.SendPointer(PointerAction.Down, Map(e.ChangedButton), x, y);
            e.Handled = true;
        }
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropStart is { } start && e.ChangedButton == MouseButton.Left)
        {
            ScreenImage.ReleaseMouseCapture();
            FinishCrop(start, e.GetPosition(Overlay));
            e.Handled = true;
            return;
        }

        if (ControlEnabled && TryNormalize(e, out var x, out var y))
        {
            _viewer.SendPointer(PointerAction.Up, Map(e.ChangedButton), x, y);
            e.Handled = true;
        }

        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released)
        {
            ScreenImage.ReleaseMouseCapture();
        }
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ControlEnabled && TryNormalize(e, out var x, out var y))
        {
            var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            _viewer.SendPointer(horizontal ? PointerAction.HorizontalWheel : PointerAction.Wheel, InputButton.None, x, y, e.Delta);
            e.Handled = true;
        }
    }

    private static InputButton Map(MouseButton button) => button switch
    {
        MouseButton.Left => InputButton.Left,
        MouseButton.Right => InputButton.Right,
        MouseButton.Middle => InputButton.Middle,
        MouseButton.XButton1 => InputButton.X1,
        MouseButton.XButton2 => InputButton.X2,
        _ => InputButton.None,
    };

    // ---- keyboard ----------------------------------------------------------------------------------------

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            if (e.IsDown)
            {
                ToggleFullScreen();
            }

            e.Handled = true;
            return;
        }

        if (!ControlEnabled)
        {
            return;
        }

        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return;
        }

        var scan = (ushort)MapVirtualKeyW(vk, 0);
        _viewer.SendKey(vk, scan, up: e.IsUp, extended: ExtendedKeys.Contains(key));
        e.Handled = true;
    }

    // ---- crop --------------------------------------------------------------------------------------------

    private void UpdateCropRectangle(Point current)
    {
        if (_cropStart is not { } start)
        {
            return;
        }

        Canvas.SetLeft(CropRectangle, Math.Min(start.X, current.X));
        Canvas.SetTop(CropRectangle, Math.Min(start.Y, current.Y));
        CropRectangle.Width = Math.Abs(current.X - start.X);
        CropRectangle.Height = Math.Abs(current.Y - start.Y);
    }

    private void FinishCrop(Point start, Point end)
    {
        _cropStart = null;
        CropRectangle.Visibility = Visibility.Collapsed;
        CropToggle.IsChecked = false;

        var origin = ScreenImage.TranslatePoint(new Point(0, 0), Overlay);
        var width = ScreenImage.ActualWidth;
        var height = ScreenImage.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var left = Math.Clamp((Math.Min(start.X, end.X) - origin.X) / width, 0, 1);
        var top = Math.Clamp((Math.Min(start.Y, end.Y) - origin.Y) / height, 0, 1);
        var right = Math.Clamp((Math.Max(start.X, end.X) - origin.X) / width, 0, 1);
        var bottom = Math.Clamp((Math.Max(start.Y, end.Y) - origin.Y) / height, 0, 1);
        if (right - left < 0.03 || bottom - top < 0.03)
        {
            return;
        }

        // The selection is relative to what is shown now, which may already be a crop of the source image.
        _crop = new System.Drawing.RectangleF(
            _crop.X + (float)left * _crop.Width,
            _crop.Y + (float)top * _crop.Height,
            (float)(right - left) * _crop.Width,
            (float)(bottom - top) * _crop.Height);
        _sizedToContent = false;
        _viewer.SetCrop(_crop);
        ResetCropButton.IsEnabled = true;
    }

    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        _crop = new System.Drawing.RectangleF(0, 0, 1, 1);
        _sizedToContent = false;
        _viewer.SetCrop(_crop);
        ResetCropButton.IsEnabled = false;
    }

    // ---- window ------------------------------------------------------------------------------------------

    private void OnPreviousMonitor(object sender, RoutedEventArgs e) => SwitchMonitor(-1);

    private void OnNextMonitor(object sender, RoutedEventArgs e) => SwitchMonitor(+1);

    private void SwitchMonitor(int step)
    {
        var info = _viewer.Info;
        if (info.MonitorCount > 1)
        {
            _crop = new System.Drawing.RectangleF(0, 0, 1, 1);
            ResetCropButton.IsEnabled = false;
            _viewer.SwitchMonitor((info.MonitorIndex + step + info.MonitorCount) % info.MonitorCount);
        }
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = _styleBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
            Toolbar.Visibility = Visibility.Visible;
            Topmost = false;
        }
        else
        {
            _styleBeforeFullScreen = WindowStyle;
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            Topmost = true;
        }
    }

    private void OnRemoteClosed(string? reason)
    {
        if (_closingFromRemote)
        {
            return;
        }

        _closingFromRemote = true;
        if (!string.IsNullOrEmpty(reason) && reason != "closed by viewer")
        {
            Debug.WriteLine($"Screen stream ended: {reason}");
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statsTimer.Stop();
        if (!_closingFromRemote)
        {
            _viewer.Close();
        }

        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
