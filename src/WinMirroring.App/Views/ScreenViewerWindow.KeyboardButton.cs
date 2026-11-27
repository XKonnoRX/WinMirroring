using System.Windows;
using System.Windows.Input;
using WinMirroring.App.Services;

namespace WinMirroring.App.Views;

/// <summary>
/// The round keyboard button over the picture. A press opens or closes the touch keyboard; a drag moves the button,
/// and where it was left is remembered (as shares of the room it has, so it stays in the same corner of a window of
/// any size). It is shown while this window can type into the other device at all, and not over a single window's
/// copy, which is too small to put anything over.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>How far a press has to travel to be a drag: further for a finger, which wobbles.</summary>
    private const double MouseDragAfter = 4;
    private const double TouchDragAfter = 10;

    private Point? _handleDown;
    private Point _handleGrab;
    private bool _handleDragged;

    private void InitializeKeyboardButton()
    {
        KeyboardHandle.MouseEnter += (_, _) => KeyboardHandle.Opacity = 1;
        KeyboardHandle.MouseLeave += (_, _) => KeyboardHandle.Opacity = _handleDown is null ? 0.6 : 1;

        KeyboardHandle.MouseLeftButtonDown += (_, e) =>
        {
            if (IsFromTouch(e))
            {
                return; // the finger has its own path below
            }

            HandleDown(e.GetPosition(Surface), e.GetPosition(KeyboardHandle));
            KeyboardHandle.CaptureMouse();
            e.Handled = true;
        };
        KeyboardHandle.MouseMove += (_, e) =>
        {
            if (!IsFromTouch(e) && _handleDown is not null)
            {
                HandleMove(e.GetPosition(Surface), MouseDragAfter);
            }
        };
        KeyboardHandle.MouseLeftButtonUp += (_, e) =>
        {
            if (IsFromTouch(e) || _handleDown is null)
            {
                return;
            }

            KeyboardHandle.ReleaseMouseCapture();
            HandleUp();
            e.Handled = true;
        };

        KeyboardHandle.TouchDown += (_, e) =>
        {
            HandleDown(e.GetTouchPoint(Surface).Position, e.GetTouchPoint(KeyboardHandle).Position);
            e.TouchDevice.Capture(KeyboardHandle);
            e.Handled = true;
        };
        KeyboardHandle.TouchMove += (_, e) =>
        {
            if (_handleDown is not null)
            {
                HandleMove(e.GetTouchPoint(Surface).Position, TouchDragAfter);
            }

            e.Handled = true;
        };
        KeyboardHandle.TouchUp += (_, e) =>
        {
            KeyboardHandle.ReleaseTouchCapture(e.TouchDevice);
            if (_handleDown is not null)
            {
                HandleUp();
            }

            e.Handled = true;
        };

        Surface.SizeChanged += (_, _) => PlaceKeyboardHandle();
        Loaded += (_, _) => UpdateKeyboardHandle();
    }

    private void HandleDown(Point onSurface, Point onHandle)
    {
        _handleDown = onSurface;
        _handleGrab = onHandle;
        _handleDragged = false;
        KeyboardHandle.Opacity = 1;
    }

    private void HandleMove(Point onSurface, double dragAfter)
    {
        if (!_handleDragged && (onSurface - _handleDown!.Value).Length <= dragAfter)
        {
            return;
        }

        _handleDragged = true;
        var (roomX, roomY) = HandleRoom();
        var screen = _node?.Settings.Screen;
        if (screen is null)
        {
            return;
        }

        screen.KeyboardButtonX = roomX > 0 ? Math.Clamp((onSurface.X - _handleGrab.X) / roomX, 0, 1) : 0;
        screen.KeyboardButtonY = roomY > 0 ? Math.Clamp((onSurface.Y - _handleGrab.Y) / roomY, 0, 1) : 0;
        PlaceKeyboardHandle();
    }

    private void HandleUp()
    {
        _handleDown = null;
        KeyboardHandle.Opacity = KeyboardHandle.IsMouseOver ? 1 : 0.6;
        if (_handleDragged)
        {
            _node?.SaveSettings();
            return;
        }

        // A press, not a drag: the keyboard, with the typing going to the picture.
        CloseQuickPanel();
        ScreenImage.Focus();
        Keyboard.Focus(ScreenImage);
        TouchKeyboard.Toggle();
    }

    /// <summary>The room the button can move in: the picture's area less the button itself.</summary>
    private (double X, double Y) HandleRoom() =>
        (Math.Max(0, Surface.ActualWidth - KeyboardHandle.Width), Math.Max(0, Surface.ActualHeight - KeyboardHandle.Height));

    private void PlaceKeyboardHandle()
    {
        var screen = _node?.Settings.Screen;
        var (roomX, roomY) = HandleRoom();
        var x = Math.Clamp(screen?.KeyboardButtonX ?? 0.97, 0, 1) * roomX;
        var y = Math.Clamp(screen?.KeyboardButtonY ?? 0.62, 0, 1) * roomY;
        KeyboardHandle.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>Shown while this window can type into the other device; asked again whenever that may have changed.</summary>
    private void UpdateKeyboardHandle()
    {
        var show = _node?.Settings.Screen.KeyboardButton != false && !_windowMode && ControlEnabled && HasPicture;
        KeyboardHandle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            PlaceKeyboardHandle();
        }
    }
}
