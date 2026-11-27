using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinMirroring.Input.Injection;

namespace WinMirroring.App.Views;

/// <summary>
/// Speaking to the other device: this device's voice typing, with what it hears sent on as text.
///
/// Windows' voice typing writes into a text box, and the picture of another device is not one. So there is a text
/// box here for it to write into — shown over the picture while it is in use — and what lands in it is sent to
/// the other device as typed text, a moment after the speaking pauses. The same box takes text typed by hand,
/// which is then sent when Enter is pressed: a way to write a line with this device's own keyboard, corrections
/// and all, before any of it arrives over there.
///
/// While the box is open, the keys pressed here are for the box and not for the other device.
/// </summary>
public partial class ScreenViewerWindow
{
    /// <summary>How long after the last word arrives what has been said is sent on.</summary>
    private static readonly TimeSpan SendAfterPause = TimeSpan.FromMilliseconds(1300);

    private const ushort VkLeftWin = 0x5B;
    private const ushort VkH = 0x48;

    private bool _composing;
    private bool _composeByVoice;
    private DispatcherTimer? _composeTimer;
    private InputInjector? _localKeys;

    private void InitializeCompose()
    {
        DictationHandle.MouseLeftButtonUp += (_, e) =>
        {
            if (!IsFromTouch(e))
            {
                ToggleDictation();
                e.Handled = true;
            }
        };
        DictationHandle.TouchUp += (_, e) =>
        {
            ToggleDictation();
            e.Handled = true;
        };
        DictationHandle.TouchDown += (_, e) => e.Handled = true;
        DictationHandle.MouseLeftButtonDown += (_, e) => e.Handled = true;
        ComposeBox.TextChanged += (_, _) => OnComposeChanged();

        // The focus going to something else in this window — a tap on the picture — ends it: what was written is
        // sent, and the keys go back to the other device. The focus leaving the window altogether (the voice
        // typing bar taking it, another program) is not that, and changes nothing.
        ComposeBox.LostKeyboardFocus += (_, e) =>
        {
            if (_composing && e.NewFocus is not null && !ReferenceEquals(e.NewFocus, ComposeSend) && !ReferenceEquals(e.NewFocus, ComposeClose))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_composing && !ComposeBorder.IsKeyboardFocusWithin)
                    {
                        SendComposed(andClose: true);
                    }
                });
            }
        };
        ComposeSend.Click += (_, _) => SendComposed(andClose: !_composeByVoice);
        ComposeClose.Click += (_, _) => CloseCompose();
    }

    /// <summary>The microphone button: starts taking dictation, or — while it is — sends what there is and stops.</summary>
    public void ToggleDictation()
    {
        if (_composing)
        {
            SendComposed(andClose: true);
            return;
        }

        if (!ControlEnabled)
        {
            ShowNotice("Только просмотр: включите управление в меню, чтобы диктовать на то устройство");
            return;
        }

        OpenCompose(byVoice: true);
    }

    private void OpenCompose(bool byVoice)
    {
        ReleaseRemoteInput(); // nothing held down over there while the keys here go to the box
        _composing = true;
        _composeByVoice = byVoice;
        ComposeBox.Text = string.Empty;
        ComposeHint.Text = byVoice
            ? "Говорите — текст уйдёт на то устройство после паузы. Enter отправляет сразу, Esc закрывает."
            : "Enter отправляет текст на то устройство, Esc закрывает.";
        ComposeBorder.Visibility = Visibility.Visible;
        CloseQuickPanel();
        ComposeBox.Focus();
        Keyboard.Focus(ComposeBox);
        if (byVoice)
        {
            // After the box has the focus, which is what voice typing looks for when it opens.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, StartVoiceTyping);
        }
    }

    /// <summary>Opens Windows' voice typing, the way a person would: Win+H, pressed on this device.</summary>
    private void StartVoiceTyping()
    {
        try
        {
            _localKeys ??= new InputInjector();
            _localKeys.Key(VkLeftWin, 0, up: false, extended: true);
            _localKeys.Key(VkH, 0, up: false, extended: false);
            _localKeys.Key(VkH, 0, up: true, extended: false);
            _localKeys.Key(VkLeftWin, 0, up: true, extended: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ComposeHint.Text = "Не удалось открыть голосовой ввод Windows (Win+H). Текст можно набрать здесь и отправить Enter.";
        }
    }

    private void OnComposeChanged()
    {
        if (!_composing || !_composeByVoice)
        {
            return;
        }

        _composeTimer ??= new DispatcherTimer(SendAfterPause, DispatcherPriority.Background, (_, _) =>
        {
            _composeTimer!.Stop();
            if (_composing && _composeByVoice)
            {
                SendComposed(andClose: false);
            }
        }, Dispatcher);
        _composeTimer.Stop();
        _composeTimer.Start();
    }

    /// <summary>Sends what is in the box to the other device as typed text and empties it.</summary>
    private void SendComposed(bool andClose)
    {
        _composeTimer?.Stop();
        var text = ComposeBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            // What is said next begins a new box, so the space that would have joined the two is put in here.
            if (_composeByVoice && !andClose && !char.IsWhiteSpace(text[^1]))
            {
                text += " ";
            }

            _viewer.SendText(text);
            ComposeBox.Text = string.Empty;
        }

        if (andClose)
        {
            CloseCompose();
        }
    }

    private void CloseCompose()
    {
        if (!_composing)
        {
            return;
        }

        _composeTimer?.Stop();
        _composing = false;
        ComposeBorder.Visibility = Visibility.Collapsed;
        ComposeBox.Text = string.Empty;
        ScreenImage.Focus();
        Keyboard.Focus(ScreenImage);
    }

    /// <summary>
    /// A key while the box is open. True when it was one of the box's own (Enter, Escape) and has been dealt
    /// with; every other key is left for the box to take, and none goes to the other device.
    /// </summary>
    private bool ComposeTakesKey(Key key, bool down, KeyEventArgs e)
    {
        if (!_composing)
        {
            return false;
        }

        if (down && key == Key.Escape)
        {
            CloseCompose();
            e.Handled = true;
        }
        else if (down && key is Key.Enter or Key.Return)
        {
            SendComposed(andClose: !_composeByVoice);
            e.Handled = true;
        }

        return true;
    }

    /// <summary>The microphone button stands just under the keyboard button, or just over it where there is no room under.</summary>
    private void PlaceDictationHandle()
    {
        var show = KeyboardHandle.Visibility == Visibility.Visible && _node?.Settings.Screen.DictationButton != false;
        DictationHandle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var keyboard = KeyboardHandle.Margin;
        var gap = KeyboardHandle.Height + 8;
        var below = keyboard.Top + gap;
        var top = below + DictationHandle.Height <= Surface.ActualHeight ? below : keyboard.Top - DictationHandle.Height - 8;
        DictationHandle.Margin = new Thickness(keyboard.Left + ((KeyboardHandle.Width - DictationHandle.Width) / 2), Math.Max(0, top), 0, 0);
    }
}
