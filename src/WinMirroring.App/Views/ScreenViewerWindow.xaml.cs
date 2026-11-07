using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WinMirroring.App.Services;
using WinMirroring.Clipboard;
using WinMirroring.Screen;
using WinMirroring.Screen.Protocol;
using InputButton = WinMirroring.Input.Protocol.MouseButton;

namespace WinMirroring.App.Views;

/// <summary>
/// Shows a monitor or window of another device and sends mouse, touch and keyboard input back. Files dropped on the
/// picture are dropped at the same point there; a drag started in the picture and pulled out of the window comes here.
/// </summary>
public partial class ScreenViewerWindow : Window
{
    private const byte ContactDown = 1;
    private const byte ContactMove = 2;
    private const byte ContactUp = 3;
    private const byte ContactCancel = 4;
    private const int MaxContacts = 10;

    /// <summary>How far (in device-independent pixels) the pointer must leave the picture to pull a drag out.</summary>
    private const double DragOutMargin = 12;

    private static readonly HashSet<Key> ExtendedKeys =
    [
        Key.Insert, Key.Delete, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Left, Key.Right, Key.Up, Key.Down,
        Key.RightCtrl, Key.RightAlt, Key.Divide, Key.NumLock, Key.PrintScreen, Key.LWin, Key.RWin, Key.Apps,
    ];

    private readonly ScreenViewer _viewer;
    private readonly DragDropCoordinator? _dragDrop;
    private readonly ClipboardSyncService? _clipboard;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly HashSet<MouseButton> _buttonsDown = [];
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _keysDown = [];
    private readonly Dictionary<int, (byte Contact, float X, float Y)> _touches = [];
    private WriteableBitmap? _bitmap;
    private System.Drawing.RectangleF _crop = new(0, 0, 1, 1);
    private Point? _cropStart;
    private (float X, float Y) _lastPointer = (0.5f, 0.5f);
    private bool _dragOutTried;
    private bool _dragOutActive;
    private bool _sizedToContent;
    private bool _closingFromRemote;
    private int _framesSinceTick;
    private WindowState _stateBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;

    public ScreenViewerWindow(ScreenViewer viewer, IServiceProvider services)
    {
        InitializeComponent();
        _viewer = viewer;
        _dragDrop = services.GetService<DragDropCoordinator>();
        _clipboard = services.GetService<ClipboardSyncService>();
        UpdateInfo();

        _viewer.InfoChanged += _ => Dispatcher.BeginInvoke(UpdateInfo);
        _viewer.Closed += (_, reason) => Dispatcher.BeginInvoke(() => OnRemoteClosed(reason));
        _viewer.Attach(update => Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(update)));

        ScreenImage.MouseMove += OnImageMouseMove;
        ScreenImage.MouseDown += OnImageMouseDown;
        ScreenImage.MouseUp += OnImageMouseUp;
        ScreenImage.MouseWheel += OnImageMouseWheel;
        ScreenImage.TouchDown += OnImageTouchDown;
        ScreenImage.TouchMove += OnImageTouchMove;
        ScreenImage.TouchUp += OnImageTouchUp;
        ScreenImage.LostTouchCapture += OnImageLostTouchCapture;
        PreviewKeyDown += OnPreviewKey;
        PreviewKeyUp += OnPreviewKey;
        PreviewTextInput += OnPreviewTextInput;
        Deactivated += (_, _) => ReleaseRemoteInput();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            StatsText.Text = $"{_framesSinceTick} кадр/с";
            _framesSinceTick = 0;
        }, Dispatcher);
        _statsTimer.Start();

        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            NoticeText.Text = string.Empty;
            _noticeTimer!.Stop();
        }, Dispatcher);
        _noticeTimer.Stop();

        ClipboardButton.Visibility = _clipboard is null ? Visibility.Collapsed : Visibility.Visible;

        if (viewer.Info.Kind == "window")
        {
            PlaceAtCursor();
        }

        Loaded += (_, _) => ScreenImage.Focus();
    }

    private bool ControlEnabled => ControlToggle.IsChecked == true && _viewer.Info.ControlAllowed && CropToggle.IsChecked != true;

    private bool CanDropFiles => _dragDrop?.ViewerDragAvailable == true && ControlEnabled && _bitmap is not null && !_dragOutActive;

    private void UpdateInfo()
    {
        var info = _viewer.Info;
        Title = $"{info.Title} — {_viewer.PeerName}";
        TitleText.Text = Title;
        MonitorPanel.Visibility = info.Kind == "monitor" && info.MonitorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorText.Text = $"{info.MonitorIndex + 1} из {info.MonitorCount}";
        ControlToggle.IsEnabled = info.ControlAllowed;
        KeyboardButton.IsEnabled = info.ControlAllowed;
        if (!info.ControlAllowed)
        {
            ControlToggle.IsChecked = false;
            ControlToggle.ToolTip = "Управление запрещено на том устройстве";
        }
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
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

        if (WindowState != WindowState.Normal)
        {
            _sizedToContent = true;
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

    // ---- mouse -------------------------------------------------------------------------------------------

    /// <summary>Normalized position on the picture (clamped); <paramref name="outside"/> says how far outside it is.</summary>
    private bool TryNormalize(Point position, out float x, out float y, out double outside)
    {
        x = y = 0;
        outside = 0;
        var width = ScreenImage.ActualWidth;
        var height = ScreenImage.ActualHeight;
        if (_bitmap is null || width <= 0 || height <= 0)
        {
            return false;
        }

        outside = Math.Max(Math.Max(-position.X, position.X - width), Math.Max(-position.Y, position.Y - height));
        x = (float)Math.Clamp(position.X / width, 0, 1);
        y = (float)Math.Clamp(position.Y / height, 0, 1);
        return true;
    }

    private bool TryNormalize(MouseEventArgs e, out float x, out float y) => TryNormalize(e.GetPosition(ScreenImage), out x, out y, out _);

    /// <summary>Mouse events WPF made up from touch: touch has its own path.</summary>
    private static bool IsFromTouch(MouseEventArgs e) => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (CropToggle.IsChecked == true)
        {
            UpdateCropRectangle(e.GetPosition(Overlay));
            return;
        }

        if (!ControlEnabled || _dragOutActive || IsFromTouch(e) || !TryNormalize(e.GetPosition(ScreenImage), out var x, out var y, out var outside))
        {
            return;
        }

        if (outside > DragOutMargin && _buttonsDown.Contains(MouseButton.Left))
        {
            // Pulled past the edge with the button held: maybe a drag of files on the other device.
            if (!_dragOutTried && _dragDrop?.ViewerDragAvailable == true)
            {
                _dragOutTried = true;
                _ = DragOutAsync(_lastPointer.X, _lastPointer.Y);
            }

            return;
        }

        if (outside <= 0)
        {
            _dragOutTried = false;
        }

        _lastPointer = (x, y);
        _viewer.SendPointer(PointerAction.Move, InputButton.None, x, y);
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

        if (ControlEnabled && !_dragOutActive && !IsFromTouch(e) && TryNormalize(e, out var x, out var y))
        {
            ScreenImage.CaptureMouse();
            _buttonsDown.Add(e.ChangedButton);
            _dragOutTried = false;
            _lastPointer = (x, y);
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

        // Only release what we pressed: an up without a down (after a window was dragged here, say) stays here.
        if (_buttonsDown.Remove(e.ChangedButton))
        {
            if (TryNormalize(e, out var x, out var y))
            {
                _lastPointer = (x, y);
            }

            _viewer.SendPointer(PointerAction.Up, Map(e.ChangedButton), _lastPointer.X, _lastPointer.Y);
            e.Handled = true;
        }

        if (_buttonsDown.Count == 0)
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

    // ---- touch -------------------------------------------------------------------------------------------

    private void OnImageTouchDown(object? sender, TouchEventArgs e)
    {
        if (!ControlEnabled)
        {
            return; // unhandled: WPF turns it into mouse input (selecting a part, for example)
        }

        e.Handled = true;
        if (!TryNormalize(e.GetTouchPoint(ScreenImage).Position, out var x, out var y, out _) || _touches.Count >= MaxContacts)
        {
            return;
        }

        var contact = Enumerable.Range(0, MaxContacts).Select(i => (byte)i).First(i => _touches.Values.All(t => t.Contact != i));
        _touches[e.TouchDevice.Id] = (contact, x, y);
        ScreenImage.Focus();
        e.TouchDevice.Capture(ScreenImage);
        _viewer.SendTouch(ContactDown, contact, x, y);
    }

    private void OnImageTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_touches.TryGetValue(e.TouchDevice.Id, out var touch))
        {
            return;
        }

        e.Handled = true;
        if (TryNormalize(e.GetTouchPoint(ScreenImage).Position, out var x, out var y, out _) && (x != touch.X || y != touch.Y))
        {
            _touches[e.TouchDevice.Id] = (touch.Contact, x, y);
            _viewer.SendTouch(ContactMove, touch.Contact, x, y);
        }
    }

    private void OnImageTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_touches.Remove(e.TouchDevice.Id, out var touch))
        {
            return;
        }

        e.Handled = true;
        if (TryNormalize(e.GetTouchPoint(ScreenImage).Position, out var x, out var y, out _))
        {
            touch = (touch.Contact, x, y);
        }

        _viewer.SendTouch(ContactUp, touch.Contact, touch.X, touch.Y);
        ScreenImage.ReleaseTouchCapture(e.TouchDevice);
    }

    private void OnImageLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (_touches.Remove(e.TouchDevice.Id, out var touch))
        {
            _viewer.SendTouch(ContactCancel, touch.Contact, touch.X, touch.Y);
        }
    }

    /// <summary>The window lost focus: nothing may stay pressed on the other device.</summary>
    private void ReleaseRemoteInput()
    {
        if (_dragOutActive)
        {
            return;
        }

        foreach (var button in _buttonsDown)
        {
            _viewer.SendPointer(PointerAction.Up, Map(button), _lastPointer.X, _lastPointer.Y);
        }

        _buttonsDown.Clear();
        foreach (var touch in _touches.Values)
        {
            _viewer.SendTouch(ContactCancel, touch.Contact, touch.X, touch.Y);
        }

        _touches.Clear();
        foreach (var (vk, key) in _keysDown)
        {
            _viewer.SendKey(vk, key.Scan, up: true, key.Extended);
        }

        _keysDown.Clear();
    }

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

        // Keys without a virtual key (Unicode packets from the touch keyboard, IME results) arrive as text input.
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || key is Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        var scan = (ushort)MapVirtualKeyW(vk, 0);
        var extended = ExtendedKeys.Contains(key);
        if (e.IsUp)
        {
            _keysDown.Remove(vk);
        }
        else
        {
            _keysDown[vk] = (scan, extended);
        }

        _viewer.SendKey(vk, scan, up: e.IsUp, extended);
        e.Handled = true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ControlEnabled && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            _viewer.SendText(e.Text);
            e.Handled = true;
        }
    }

    private void OnToggleKeyboard(object sender, RoutedEventArgs e)
    {
        ScreenImage.Focus();
        Keyboard.Focus(ScreenImage);
        TouchKeyboard.Toggle();
    }

    // ---- files and clipboard -----------------------------------------------------------------------------

    private void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDropFiles && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanDropFiles || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        if (!TryNormalize(e.GetPosition(ScreenImage), out var x, out var y, out var outside) || outside > 0)
        {
            ShowNotice("Отпустите файлы на изображении");
            return;
        }

        try
        {
            var sent = await _dragDrop!.DropIntoViewerAsync(_viewer, paths, x, y);
            ShowNotice(sent ? $"Файлы ({paths.Length}) переданы на {_viewer.PeerName}" : "Эти файлы нельзя передать");
        }
        catch (Exception ex)
        {
            ShowNotice($"Не удалось передать файлы: {ex.Message}");
        }
    }

    private async Task DragOutAsync(float x, float y)
    {
        try
        {
            var payload = await _dragDrop!.CatchFromViewerAsync(_viewer, x, y);
            if (payload is null || !_buttonsDown.Contains(MouseButton.Left))
            {
                return; // nothing was dragged, or the button was released meanwhile (and that release already went out)
            }

            // End the drag over there (it drops on its catch surface, which accepts nothing) and continue it here.
            _dragOutActive = true;
            _buttonsDown.Remove(MouseButton.Left);
            _viewer.SendPointer(PointerAction.Up, InputButton.Left, x, y);
            ScreenImage.ReleaseMouseCapture();
            ShowNotice($"Перетаскивание с {_viewer.PeerName}: {payload.Manifest.TopLevel.Count()} шт.");
            await _dragDrop.StartViewerDragAsync(payload);
        }
        catch (Exception ex)
        {
            ShowNotice($"Перетаскивание не удалось: {ex.Message}");
        }
        finally
        {
            _dragOutActive = false;
        }
    }

    private async void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var sent = _clipboard is not null && await _clipboard.PublishNowAsync();
            ShowNotice(sent ? "Буфер обмена отправлен" : "Буфер обмена пуст или недоступен");
        }
        catch (Exception ex)
        {
            ShowNotice($"Буфер обмена не отправлен: {ex.Message}");
        }

        ScreenImage.Focus();
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
        _noticeTimer.Stop();
        ReleaseRemoteInput();
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
