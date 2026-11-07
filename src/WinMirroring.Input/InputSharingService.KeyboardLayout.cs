using Microsoft.Extensions.Logging;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Input.Keyboard;

namespace WinMirroring.Input;

/// <summary>
/// The keyboard layout follows the user. Keys travel as scan codes and mean whatever the receiving device's layout
/// says, so when input moves to a device, that device switches to the controller's layout. Switching the layout
/// there (the hotkey goes there with every other key, or a click on its language bar) is reported back, and the
/// controller takes the same layout when input returns. A screen viewer pushes its layout the same way
/// (<see cref="PushKeyboardLayout"/>).
/// </summary>
public sealed partial class InputSharingService
{
    private static readonly TimeSpan LayoutPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly long MissingLayoutNoticeMs = (long)TimeSpan.FromMinutes(30).TotalMilliseconds;

    private readonly object _layoutGate = new();
    private readonly KeyboardLayoutFilter _layoutFilter = new();
    private readonly Dictionary<int, long> _missingLayoutNotices = [];
    private Task? _layoutWatcher;

    /// <summary>Controller side: the layout the user types with while input is away (theirs, then what the other device reports).</summary>
    private int _captureLayout;

    /// <summary>A device asked for a layout (its language name) that is not installed here.</summary>
    public event Action<string>? KeyboardLayoutMissing;

    public bool SyncKeyboardLayout => _node.Settings.Input.SyncKeyboardLayout;

    public void SetSyncKeyboardLayout(bool enabled)
    {
        _node.Settings.Input.SyncKeyboardLayout = enabled;
        _node.SaveSettings();
    }

    /// <summary>
    /// Asks <paramref name="peerId"/> to type with <paramref name="layout"/>: a viewer window of ours in front controls
    /// that device's screen.
    /// </summary>
    public void PushKeyboardLayout(string peerId, int layout)
    {
        if (SyncKeyboardLayout && layout != 0 && _node.Sessions.TryGetSession(peerId, out var session))
        {
            _ = SendKeyboardLayoutAsync(session, layout);
        }
    }

    private void StartKeyboardLayoutWatcher() => _layoutWatcher = Task.Run(() => WatchKeyboardLayoutAsync(_cts.Token));

    // ---- controller side ---------------------------------------------------------------------------------

    /// <summary>Input moved to <paramref name="session"/>: it switches to the layout the user types with.</summary>
    private void OnCaptureStartedForLayout(PeerSession session, bool fromHere)
    {
        if (!SyncKeyboardLayout)
        {
            return;
        }

        // Straight from one device to the next, the layout is the one reported by the previous device.
        if (fromHere || Volatile.Read(ref _captureLayout) == 0)
        {
            Volatile.Write(ref _captureLayout, KeyboardLayouts.TryGetForeground(out _, out var local) ? local : 0);
        }

        var layout = Volatile.Read(ref _captureLayout);
        if (layout != 0)
        {
            _ = SendKeyboardLayoutAsync(session, layout);
        }
    }

    /// <summary>Input is back: take the layout the user ended up with over there.</summary>
    private void OnCaptureEndedForLayout()
    {
        var layout = Interlocked.Exchange(ref _captureLayout, 0);
        if (SyncKeyboardLayout && layout != 0)
        {
            ApplyKeyboardLayout(layout, from: "the other device");
        }
    }

    // ---- both sides --------------------------------------------------------------------------------------

    private void OnKeyboardLayoutMessage(PeerSession session, KeyboardLayoutMessage message)
    {
        if (!SyncKeyboardLayout || message.Layout == 0)
        {
            return;
        }

        if (Volatile.Read(ref _capture) is { } capture && capture.Session.PeerId == session.PeerId)
        {
            // Our input is over there and the user switched the layout there; this device follows once input is back.
            Volatile.Write(ref _captureLayout, message.Layout);
            _logger.LogDebug("Layout on {Peer} is now {Layout:X8}", session.PeerName, message.Layout);
            return;
        }

        ApplyKeyboardLayout(message.Layout, session.PeerName);
    }

    private void ApplyKeyboardLayout(int wanted, string from)
    {
        if (KeyboardLayouts.Match(wanted, KeyboardLayouts.Installed()) is not { } layout)
        {
            NoticeMissingLayout(wanted);
            return;
        }

        lock (_layoutGate)
        {
            _layoutFilter.Expect(layout, Environment.TickCount64);
        }

        var result = KeyboardLayouts.Activate(layout);
        _logger.LogDebug("Layout {Layout:X8} from {From}: {Result}", layout, from, result);
    }

    private void NoticeMissingLayout(int layout)
    {
        var language = layout & 0xFFFF;
        var now = Environment.TickCount64;
        lock (_layoutGate)
        {
            if (_missingLayoutNotices.TryGetValue(language, out var last) && now - last < MissingLayoutNoticeMs)
            {
                return;
            }

            _missingLayoutNotices[language] = now;
        }

        var name = KeyboardLayouts.DisplayName(layout);
        _logger.LogInformation("Asked for keyboard layout {Layout:X8} ({Name}), which is not installed here", layout, name);
        KeyboardLayoutMissing?.Invoke(name);
    }

    // ---- controlled side ---------------------------------------------------------------------------------

    /// <summary>While another device controls this one, reports the user's layout switches here back to it.</summary>
    private async Task WatchKeyboardLayoutAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LayoutPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_player.Owner is not PeerSession controller)
                {
                    lock (_layoutGate)
                    {
                        _layoutFilter.Reset();
                    }

                    continue;
                }

                if (!SyncKeyboardLayout || !KeyboardLayouts.TryGetForeground(out var window, out var layout))
                {
                    continue;
                }

                int? switched;
                lock (_layoutGate)
                {
                    switched = _layoutFilter.Observe(window, layout, Environment.TickCount64);
                }

                if (switched is { } value)
                {
                    _logger.LogDebug("Layout switched to {Layout:X8} while {Peer} is in control", value, controller.PeerName);
                    _ = SendKeyboardLayoutAsync(controller, value);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendKeyboardLayoutAsync(PeerSession session, int layout)
    {
        try
        {
            await session.SendControlAsync(new KeyboardLayoutMessage(layout), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Keyboard layout not sent to {Peer}: {Message}", session.PeerName, ex.Message);
        }
    }
}
