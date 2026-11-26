using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using WinMirroring.App.Services;
using WinMirroring.Core;

namespace WinMirroring.App.Views;

/// <summary>
/// The viewer half of "the keyboard comes up when a text box is tapped" (see <see cref="RemoteKeyboardWatcher"/>):
/// the other device says a tap from here put its focus in a text box, and this device's touch keyboard comes up, as
/// it would for a text box of its own; a tap somewhere else takes down a keyboard brought up that way. A keyboard
/// opened by hand is left alone, and the whole thing can be switched off in the settings for a device that has a
/// keyboard of its own.
/// </summary>
public partial class ScreenViewerWindow
{
    private MirroringNode? _node;

    /// <summary>The keyboard on the screen now was brought up by a tap in the picture, not by hand.</summary>
    private bool _keyboardFromTap;

    private void InitializeKeyboardHints(IServiceProvider services)
    {
        _node = services.GetService<MirroringNode>();
        _viewer.KeyboardHint += (_, show) => Dispatcher.BeginInvoke(() => OnKeyboardHint(show));
    }

    private void OnKeyboardHint(bool show)
    {
        if (_node?.Settings.Screen.AutoKeyboardOnTap != true || !ControlEnabled || !IsVisible)
        {
            return;
        }

        var up = _inputPane?.Location is not null;
        if (show && !up)
        {
            // Typing goes to the picture, which is what sends it on.
            ScreenImage.Focus();
            Keyboard.Focus(ScreenImage);
            TouchKeyboard.Toggle();
            _keyboardFromTap = true;
        }
        else if (!show && up && _keyboardFromTap)
        {
            TouchKeyboard.Toggle();
            _keyboardFromTap = false;
        }
    }

    /// <summary>The keyboard went away (by its own close button, say): it is not ours to take down any more.</summary>
    private void ForgetTapKeyboard() => _keyboardFromTap = false;
}
