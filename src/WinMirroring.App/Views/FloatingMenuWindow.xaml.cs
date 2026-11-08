using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinMirroring.App.Views;

/// <summary>
/// A small always-on-top button that floats over every window. A click opens the main menu; a drag moves it; it can
/// be hidden (and brought back from the tray). Its position is reported so the app can remember it.
/// </summary>
public partial class FloatingMenuWindow : Window
{
    private bool _pressed;
    private bool _moved;
    private Point _down;

    public FloatingMenuWindow()
    {
        InitializeComponent();
        Services.CaptureExclusion.Apply(this);
    }

    /// <summary>The button was clicked (not dragged); the app opens the control panel.</summary>
    public event Action? Clicked;

    /// <summary>The user finished dragging the button; carries the new top-left for persistence.</summary>
    public event Action<double, double>? MovedTo;

    private void OnButtonDown(object sender, MouseButtonEventArgs e)
    {
        var onScreen = PointToScreen(e.GetPosition(this));
        if (WinMirroring.Screen.RemoteInputTracker.IsRemote(onScreen.X, onScreen.Y))
        {
            // A press from a device viewing this screen: it cannot see this button (kept out of capture), so ignore it.
            _pressed = false;
            e.Handled = true;
            return;
        }

        _pressed = true;
        _moved = false;
        _down = e.GetPosition(this);
    }

    private void OnButtonMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _moved || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if ((e.GetPosition(this) - _down).Length <= 4)
        {
            return;
        }

        _moved = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _pressed = false;
            MovedTo?.Invoke(Left, Top);
        }
    }

    private void OnButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed && !_moved)
        {
            Clicked?.Invoke();
        }

        _pressed = false;
    }

    private void OnHoverIn(object sender, MouseEventArgs e) => Handle.Opacity = 1.0;

    private void OnHoverOut(object sender, MouseEventArgs e) => Handle.Opacity = 0.7;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // A tool window stays out of Alt+Tab and the taskbar switcher.
        const int GwlExStyle = -20;
        const long WsExToolWindow = 0x00000080;
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)(style | WsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
