using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using WinMirroring.Screen;

namespace WinMirroring.App.Services;

/// <summary>
/// The source half of "the keyboard comes up when a text box is tapped". On a tablet, touching a text box brings
/// the touch keyboard up by itself; touching one in the picture of another device did nothing, because the tap
/// lands on that device and the keyboard that would come up is that device's — if it comes up at all, and on a PC
/// with a keyboard plugged in it does not. So this device looks, a moment after a finger from a viewer leaves the
/// screen, at what has the focus now (through UI Automation, the same thing screen readers use): a text box that
/// can be typed in, or something else. The viewer is told, and brings its own keyboard up or down.
///
/// Only touches count, as with Windows' own keyboard: a click with a mouse means a keyboard is at hand.
/// </summary>
public sealed class RemoteKeyboardWatcher : IDisposable
{
    /// <summary>Long enough for the tapped control to take the focus, short enough to feel like the tap did it.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly ScreenStreamingService _screen;
    private readonly ILogger<RemoteKeyboardWatcher> _logger;

    /// <summary>What each viewer (peer, stream) was told last: "down" is only worth saying after "up".</summary>
    private readonly ConcurrentDictionary<(string Peer, uint Stream), bool> _told = new();
    private int _generation;

    public RemoteKeyboardWatcher(ScreenStreamingService screen, ILogger<RemoteKeyboardWatcher> logger)
    {
        _screen = screen;
        _logger = logger;
    }

    public void Start() => RemoteInputTracker.TouchEnded += OnTouchEnded;

    private void OnTouchEnded(string peerId, uint streamId)
    {
        // Several fingers, or taps in quick succession: only the last one is looked at.
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Settle).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || FocusIsEditable() is not { } editable)
            {
                return;
            }

            var key = (peerId, streamId);
            var before = _told.TryGetValue(key, out var told) && told;
            if (!editable && !before)
            {
                return; // nothing was brought up for this viewer, so there is nothing to take down
            }

            if (await _screen.SendKeyboardHintAsync(peerId, streamId, editable).ConfigureAwait(false))
            {
                _told[key] = editable;
            }
        });
    }

    /// <summary>
    /// Whether the focused element takes typing: an edit box, or a document whose text is not read-only (Word,
    /// Notepad). A browser page is a document too, but a read-only one, so tapping a link does not bring a keyboard
    /// up. Null when it cannot be told (the element went away, or it is in a process that does not answer).
    /// </summary>
    private bool? FocusIsEditable()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null)
            {
                return null;
            }

            var current = element.Current;
            if (current.ProcessId == Environment.ProcessId || !current.IsEnabled || !current.IsKeyboardFocusable)
            {
                return current.ProcessId == Environment.ProcessId ? null : false;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern values)
            {
                if (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document || current.ControlType == ControlType.ComboBox)
                {
                    return !values.Current.IsReadOnly;
                }
            }

            if (current.ControlType == ControlType.Edit)
            {
                return true;
            }

            if (current.ControlType == ControlType.Document
                && element.TryGetCurrentPattern(TextPattern.Pattern, out var text) && text is TextPattern document)
            {
                return document.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
            }

            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            _logger.LogDebug("Could not tell what has the focus: {Message}", ex.Message);
            return null;
        }
    }

    public void Dispose() => RemoteInputTracker.TouchEnded -= OnTouchEnded;
}
