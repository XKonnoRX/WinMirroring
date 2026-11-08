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
using WinMirroring.Input;
using WinMirroring.Screen;
using WinMirroring.Screen.Protocol;
using InputButton = WinMirroring.Input.Protocol.MouseButton;
using KeyboardLayouts = WinMirroring.Input.Keyboard.KeyboardLayouts;

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

    /// <summary>Modifier keys we keep in sync with the other device: (WPF key, virtual-key, extended-key flag).</summary>
    private static readonly (Key Key, ushort Vk, bool Extended)[] TrackedModifiers =
    [
        (Key.LeftShift, 0xA0, false), (Key.RightShift, 0xA1, false),
        (Key.LeftCtrl, 0xA2, false), (Key.RightCtrl, 0xA3, true),
        (Key.LeftAlt, 0xA4, false), (Key.RightAlt, 0xA5, true),
        (Key.LWin, 0x5B, true), (Key.RWin, 0x5C, true),
    ];

    private readonly ScreenViewer _viewer;
    private readonly DragDropCoordinator? _dragDrop;
    private readonly ClipboardSyncService? _clipboard;
    private readonly ViewModels.MainViewModel? _quick;
    private readonly InputSharingService? _input;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _layoutTimer;
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
    private bool _windowMode;
    private bool _externalControl;
    private bool _stripPressed;
    private bool _stripMoved;
    private Point _stripDownInWindow;
    private Point _stripDownOnImage;
    private bool _followingRemoteMinimize;

    public ScreenViewerWindow(ScreenViewer viewer, IServiceProvider services)
    {
        InitializeComponent();
        _viewer = viewer;
        _dragDrop = services.GetService<DragDropCoordinator>();
        _clipboard = services.GetService<ClipboardSyncService>();
        _quick = services.GetService<ViewModels.MainViewModel>();
        _input = services.GetService<InputSharingService>();
        UpdateInfo();

        // A tap on the picture while the quick panel is open just closes the panel.
        Surface.PreviewMouseDown += OnSurfacePreviewDown;
        Surface.PreviewTouchDown += OnSurfacePreviewDown;

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
        Activated += (_, _) => ScheduleLayoutPush();
        StateChanged += OnViewerStateChanged;
        InputLanguageManager.Current.InputLanguageChanged += OnInputLanguageChanged;

        _layoutTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _layoutTimer!.Stop();
            PushKeyboardLayout();
        }, Dispatcher);
        _layoutTimer.Stop();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            StatsText.Text = $"{_framesSinceTick} кадр/с";
            _framesSinceTick = 0;
        }, Dispatcher);
        _statsTimer.Start();

        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            NoticeText.Text = string.Empty;
            NoticeBorder.Visibility = Visibility.Collapsed;
            _noticeTimer!.Stop();
        }, Dispatcher);
        _noticeTimer.Stop();

        ClipboardButton.Visibility = _clipboard is null ? Visibility.Collapsed : Visibility.Visible;

        if (viewer.Info.Kind == "window")
        {
            PlaceAtCursor();
            PrepareWindowMode();
        }

        if (IsDisplayMode)
        {
            PrepareDisplayMode();
        }

        Loaded += (_, _) =>
        {
            if (IsDisplayMode && !IsFullScreen)
            {
                ToggleFullScreen();
            }

            ScreenImage.Focus();
        };
    }

    /// <summary>This screen is the other device's second monitor (its desktop extends onto us).</summary>
    private bool IsDisplayMode => _viewer.Info.Kind == "display";

    /// <summary>
    /// Second monitor: the whole primary screen, no frame, nothing to crop or switch; closing it ends the extension.
    /// </summary>
    private void PrepareDisplayMode()
    {
        _sizedToContent = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var work = SystemParameters.WorkArea;
        Left = work.Left;
        Top = work.Top;
        ImageHeader.Visibility = Visibility.Collapsed;
        QuickMonitorPanel.Visibility = Visibility.Collapsed;
        CropToggle.Visibility = Visibility.Collapsed;
        ResetCropButton.Visibility = Visibility.Collapsed;
        CloseViewerButton.Content = "Отключить второй монитор";
        ControlToggle.IsChecked = true;
    }

    /// <summary>
    /// A streamed window already carries its own frame, so the viewer drops its chrome and toolbar and shows only the
    /// picture. An invisible strip over the top drags the viewer; the small button opens the menu.
    /// </summary>
    private void PrepareWindowMode()
    {
        _windowMode = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 100;
        MinHeight = 80;
        Toolbar.Visibility = Visibility.Collapsed;
        DragStrip.Visibility = Visibility.Visible;
        WindowMenuButton.Visibility = Visibility.Visible;
        FullScreenToggle.Visibility = Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        CloseQuickPanel();
        ReleaseRemoteInput();
        WindowState = WindowState.Minimized;
    }

    // ---- borderless window: drag strip -------------------------------------------------------------------

    private void OnDragStripDown(object sender, MouseButtonEventArgs e)
    {
        CloseQuickPanel();
        _stripPressed = true;
        _stripMoved = false;
        _stripDownInWindow = e.GetPosition(this);
        _stripDownOnImage = e.GetPosition(ScreenImage);
    }

    private void OnDragStripMove(object sender, MouseEventArgs e)
    {
        if (!_stripPressed || _stripMoved || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if ((e.GetPosition(this) - _stripDownInWindow).Length <= 4)
        {
            return;
        }

        _stripMoved = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _stripPressed = false;
        }
    }

    private void OnDragStripUp(object sender, MouseButtonEventArgs e)
    {
        if (_stripPressed && !_stripMoved && ControlEnabled && TryNormalize(_stripDownOnImage, out var x, out var y, out _))
        {
            // A click on the strip (not a drag) goes through to the window's own title bar — its close/minimise/
            // maximise buttons work as usual.
            _viewer.SendPointer(PointerAction.Down, InputButton.Left, x, y);
            _viewer.SendPointer(PointerAction.Up, InputButton.Left, x, y);
        }

        _stripPressed = false;
    }

    private bool ControlEnabled => ControlToggle.IsChecked == true && _viewer.Info.ControlAllowed && CropToggle.IsChecked != true;

    private bool CanDropFiles => _dragDrop?.ViewerDragAvailable == true && ControlEnabled && _bitmap is not null && !_dragOutActive;

    private void UpdateInfo()
    {
        var info = _viewer.Info;
        Title = $"{info.Title} — {_viewer.PeerName}";
        TitleText.Text = Title;
        QuickTitle.Text = Title;
        var monitors = info.Kind == "monitor" && info.MonitorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorPanel.Visibility = monitors;
        QuickMonitorPanel.Visibility = monitors;
        MonitorText.Text = $"{info.MonitorIndex + 1} из {info.MonitorCount}";
        QuickMonitorText.Text = $"Монитор {info.MonitorIndex + 1} из {info.MonitorCount}";
        ControlToggle.IsEnabled = info.ControlAllowed;
        KeyboardButton.IsEnabled = info.ControlAllowed;
        QuickKeyboardButton.IsEnabled = info.ControlAllowed;
        if (!info.ControlAllowed)
        {
            ControlToggle.IsChecked = false;
            ControlToggle.ToolTip = "Управление запрещено на том устройстве";
        }

        FollowRemoteWindowState();
    }

    /// <summary>Borderless window view: minimise the viewer when the source window is minimised, restore it back.</summary>
    private void FollowRemoteWindowState()
    {
        if (!_windowMode)
        {
            return;
        }

        if (_viewer.Info.Minimized && WindowState != WindowState.Minimized)
        {
            _followingRemoteMinimize = true;
            WindowState = WindowState.Minimized;
        }
        else if (!_viewer.Info.Minimized && _followingRemoteMinimize && WindowState == WindowState.Minimized)
        {
            _followingRemoteMinimize = false;
            WindowState = WindowState.Normal;
        }
    }

    private void OnViewerStateChanged(object? sender, EventArgs e)
    {
        // The user brought the viewer back from the taskbar while the source window is still minimised: restore it too.
        if (_windowMode && WindowState == WindowState.Normal && _followingRemoteMinimize)
        {
            _followingRemoteMinimize = false;
            _viewer.SendWindowCommand(WindowCommand.Restore);
        }
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBorder.Visibility = Visibility.Visible;
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

        // Borderless window view has no chrome, so it sizes exactly to the picture (the window brings its own frame).
        var chrome = _windowMode ? 0 : ActualHeight - Surface.ActualHeight;
        if (!_windowMode && (double.IsNaN(chrome) || chrome <= 0))
        {
            chrome = Toolbar.ActualHeight + SystemParameters.WindowCaptionHeight + 16;
        }

        var pad = _windowMode ? 0.0 : 16.0;

        // A window stream carries its source's scale, so show it at the logical size it has there (125% laptop vs a
        // 100% tablet would otherwise differ by 1.25). Monitor streams keep sizing by the viewer's own DPI.
        var divX = _windowMode ? Math.Max(0.5, _viewer.Info.ScalePercent / 100.0) : dpi.DpiScaleX;
        var divY = _windowMode ? Math.Max(0.5, _viewer.Info.ScalePercent / 100.0) : dpi.DpiScaleY;
        var contentWidth = width / divX;
        var contentHeight = height / divY;
        var scale = Math.Min(1.0, Math.Min(work.Width * 0.92 / contentWidth, (work.Height * 0.92 - chrome) / contentHeight));
        Width = Math.Max(MinWidth, contentWidth * scale + pad);
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
            if (e.IsDown && !_windowMode)
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

        // Keep the other device's modifier state matching this window's. Without this, a combination like
        // Shift+Enter degrades to a bare Enter (which a chat then sends instead of adding a line break) whenever
        // the modifier's own key-down never reached us — e.g. Shift was already held when the window got focus.
        // Modifiers are driven entirely from here, so the key event for a modifier itself is not forwarded again.
        SyncModifiers();

        // Keys without a virtual key (Unicode packets from the touch keyboard, IME results) arrive as text input.
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || key is Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        if (IsModifier(vk))
        {
            e.Handled = true;
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

    private static bool IsModifier(ushort vk)
    {
        foreach (var (_, modVk, _) in TrackedModifiers)
        {
            if (modVk == vk)
            {
                return true;
            }
        }

        return vk is 0x10 or 0x11 or 0x12; // generic Shift/Ctrl/Alt, just in case
    }

    /// <summary>Sends the modifier down/up events needed to make the other device match this window's live state.</summary>
    private void SyncModifiers()
    {
        foreach (var (modKey, vk, extended) in TrackedModifiers)
        {
            var down = Keyboard.IsKeyDown(modKey);
            var tracked = _keysDown.ContainsKey(vk);
            if (down == tracked)
            {
                continue;
            }

            if (down)
            {
                var scan = (ushort)MapVirtualKeyW(vk, 0);
                _keysDown[vk] = (scan, extended);
                _viewer.SendKey(vk, scan, up: false, extended);
            }
            else
            {
                var (scan, ext) = _keysDown[vk];
                _keysDown.Remove(vk);
                _viewer.SendKey(vk, scan, up: true, ext);
            }
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ControlEnabled && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            _viewer.SendText(e.Text);
            e.Handled = true;
        }
    }

    // ---- keyboard layout ---------------------------------------------------------------------------------

    /// <summary>
    /// While this window controls the other device, that device types with this one's layout. Sent a moment later:
    /// a layout hotkey pressed here also goes there as keys and switches it on its own, and this has to come after.
    /// </summary>
    private void ScheduleLayoutPush()
    {
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void OnInputLanguageChanged(object sender, InputLanguageEventArgs e)
    {
        if (IsActive)
        {
            ScheduleLayoutPush();
        }
    }

    private void PushKeyboardLayout()
    {
        // A second monitor is this device's own desktop: the touch keyboard there types text, not keys.
        if (_input is not null && IsActive && ControlEnabled && !IsDisplayMode)
        {
            _input.PushKeyboardLayout(_viewer.Session.PeerId, KeyboardLayouts.OfCurrentThread());
        }
    }

    private void OnToggleKeyboard(object sender, RoutedEventArgs e)
    {
        QuickPanel.Visibility = Visibility.Collapsed;
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

    private bool IsFullScreen => WindowStyle == WindowStyle.None;

    private void ToggleFullScreen()
    {
        if (IsFullScreen)
        {
            WindowStyle = _styleBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
            Toolbar.Visibility = Visibility.Visible;
            FullScreenHandle.Visibility = Visibility.Collapsed;
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
            FullScreenHandle.Visibility = Visibility.Visible;
            Topmost = true;
        }

        FullScreenToggle.IsChecked = IsFullScreen;
        QuickPanel.Visibility = Visibility.Collapsed;
        if (_externalControl)
        {
            FullScreenHandle.Visibility = Visibility.Collapsed;
        }
    }

    // ---- driven from the main control panel --------------------------------------------------------------

    /// <summary>The stream this window shows (so the panel can list and switch between open views).</summary>
    public ScreenViewer Viewer => _viewer;

    /// <summary>
    /// Shows the view full screen and hands all control to the main panel: no toolbar, no in-view menu. The window
    /// is not topmost, so the floating button and the panel stay reachable above it.
    /// </summary>
    public void OpenFullScreenExternal()
    {
        _externalControl = true;
        Toolbar.Visibility = Visibility.Collapsed;
        if (!IsFullScreen && !_windowMode)
        {
            ToggleFullScreen();
        }

        FullScreenHandle.Visibility = Visibility.Collapsed;
        Topmost = false;
    }

    /// <summary>Brings this view to the front (switching to it from the panel).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _externalControl && !_windowMode ? WindowState.Maximized : WindowState.Normal;
        }

        Show();
        Activate();
    }

    public void CloseView() => Close();

    /// <summary>Gets the view out of the way (back to this device's own desktop) without closing the stream.</summary>
    public void MinimizeView() => WindowState = WindowState.Minimized;

    public void SwitchToMonitor(int index) => _viewer.SwitchMonitor(index);

    public void ToggleOnScreenKeyboard() => OnToggleKeyboard(this, new RoutedEventArgs());

    // ---- quick panel -------------------------------------------------------------------------------------

    private void OnToggleQuickPanel(object sender, RoutedEventArgs e)
    {
        if (QuickPanel.Visibility == Visibility.Visible)
        {
            QuickPanel.Visibility = Visibility.Collapsed;
            ScreenImage.Focus();
            return;
        }

        RefreshQuickPanel();
        QuickPanel.Visibility = Visibility.Visible;
    }

    private void CloseQuickPanel()
    {
        QuickPanel.Visibility = Visibility.Collapsed;
        ScreenImage.Focus();
    }

    private void OnSurfacePreviewDown(object? sender, InputEventArgs e)
    {
        if (QuickPanel.Visibility == Visibility.Visible)
        {
            CloseQuickPanel();
            e.Handled = true;
        }
    }

    private void RefreshQuickPanel()
    {
        FullScreenToggle.IsChecked = IsFullScreen;
        var peerId = _viewer.Session.PeerId;
        var peerName = _viewer.PeerName;
        var hasAudio = _quick is not null;
        ListenToggle.Visibility = SendSoundToggle.Visibility = KeepSoundToggle.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        if (_quick is null)
        {
            return;
        }

        ListenToggle.Content = $"Звук {peerName} играет здесь";
        ListenToggle.IsChecked = _quick.IsListeningTo(peerId);
        SendSoundToggle.Content = $"Звук этого устройства — на {peerName}";
        SendSoundToggle.IsChecked = _quick.IsSoundSentTo(peerId);
        KeepSoundToggle.IsChecked = _quick.SoundKeptHere;
    }

    private void OnFullScreenToggleClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnCloseViewer(object sender, RoutedEventArgs e) => Close();

    private void OnControlToggleClick(object sender, RoutedEventArgs e)
    {
        if (ControlToggle.IsChecked != true)
        {
            ReleaseRemoteInput();
        }
        else
        {
            ScheduleLayoutPush();
        }
    }

    private void OnCropToggleClick(object sender, RoutedEventArgs e)
    {
        if (CropToggle.IsChecked == true)
        {
            ReleaseRemoteInput();
            CloseQuickPanel();
            ShowNotice("Обведите рамкой нужную часть изображения");
        }
    }

    private void OnReleaseRemoteInput(object sender, RoutedEventArgs e)
    {
        ReleaseRemoteInput();
        ShowNotice($"Кнопки и клавиши на {_viewer.PeerName} отпущены");
    }

    private void OnAltTab(object sender, RoutedEventArgs e)
    {
        if (!ControlEnabled)
        {
            ShowNotice("Включите управление, чтобы отправлять Alt+Tab");
            return;
        }

        // Alt+Tab pressed here is caught by this device's Windows, so send it to the other device instead: hold Alt,
        // tap Tab, release Alt. Scan codes (Alt 0x38, Tab 0x0F) are layout-independent. Repeated taps keep switching.
        const ushort altVk = 0xA4, altScan = 0x38, tabVk = 0x09, tabScan = 0x0F;
        _viewer.SendKey(altVk, altScan, up: false, extended: false);
        _viewer.SendKey(tabVk, tabScan, up: false, extended: false);
        _viewer.SendKey(tabVk, tabScan, up: true, extended: false);
        _viewer.SendKey(altVk, altScan, up: true, extended: false);
    }

    private void OnReturnCursor(object sender, RoutedEventArgs e)
    {
        if (_quick is not null)
        {
            ShowNotice(_quick.ReturnCursor());
        }

        CloseQuickPanel();
    }

    private async void OnListenToggleClick(object sender, RoutedEventArgs e)
    {
        if (_quick is null)
        {
            return;
        }

        var listen = ListenToggle.IsChecked == true;
        var ok = await _quick.SetListenAsync(_viewer.Session.PeerId, _viewer.PeerName, listen);
        ShowNotice(!ok ? $"Нет связи с {_viewer.PeerName}" : listen ? $"Звук {_viewer.PeerName} играет здесь" : $"Звук {_viewer.PeerName} больше не запрашивается");
        RefreshQuickPanel();
    }

    private void OnSendSoundToggleClick(object sender, RoutedEventArgs e)
    {
        if (_quick is null)
        {
            return;
        }

        if (SendSoundToggle.IsChecked == true)
        {
            _quick.SendSoundTo(_viewer.Session.PeerId, _viewer.PeerName);
            ShowNotice($"Звук этого устройства играет на {_viewer.PeerName}");
        }
        else
        {
            _quick.SendSoundTo(null);
            ShowNotice("Звук этого устройства снова следует за вводом и просмотром");
        }

        RefreshQuickPanel();
    }

    private void OnKeepSoundToggleClick(object sender, RoutedEventArgs e)
    {
        if (_quick is null)
        {
            return;
        }

        if (KeepSoundToggle.IsChecked == true)
        {
            _quick.KeepSoundHere();
            ShowNotice("Звук этого устройства играет только здесь");
        }
        else
        {
            _quick.SendSoundTo(null);
            ShowNotice("Звук этого устройства снова следует за вводом и просмотром");
        }

        RefreshQuickPanel();
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
        _layoutTimer.Stop();
        InputLanguageManager.Current.InputLanguageChanged -= OnInputLanguageChanged;
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
