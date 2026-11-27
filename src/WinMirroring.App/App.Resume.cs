using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using WinMirroring.App.Views;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Screen;

namespace WinMirroring.App;

/// <summary>
/// A view that lost its connection is opened again when the connection is back.
///
/// The window is not closed in the meantime: it stays where it was with the last picture and a line saying what is
/// happening, so what the person sees is a picture that stopped for a few seconds and went on — not their own
/// desktop appearing under their hands and a view to find and open again. When the other device is reachable, the
/// same monitor is asked for; when its first picture is on its way, the new window takes the old one's place.
/// Nothing is waited for for ever: after two minutes the old window closes, as it used to at once.
/// </summary>
public partial class App
{
    /// <summary>How long a view waits for its device to come back before it closes after all.</summary>
    private static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(2);

    /// <summary>How long after asking for the screen again it is asked for once more, and how many times in all.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(3);
    private const int MostAsks = 8;

    private sealed class WaitingView
    {
        public required ScreenViewerWindow Window { get; init; }
        public required string PeerId { get; init; }
        public required string PeerName { get; init; }
        public required int MonitorIndex { get; init; }
        public required string MonitorKey { get; init; }
        public required bool InputOn { get; init; }
        public required long Until { get; init; }
        public long NextAsk { get; set; }
        public int Asks { get; set; }
    }

    private readonly Dictionary<string, WaitingView> _waitingViews = [];
    private readonly Dictionary<string, List<long>> _resumedAt = [];
    private DispatcherTimer? _resumeTimer;

    /// <summary>
    /// A window's stream ended without anybody ending it. True when it will be opened again and the window should
    /// wait; false and the window closes, as it always did.
    /// </summary>
    private bool WaitToResume(ScreenViewerWindow window)
    {
        var viewer = window.Viewer;
        var peerId = viewer.Session.PeerId;
        var now = Environment.TickCount64;
        if (_exiting || viewer.Info.Kind != "monitor" || _services?.GetService<ScreenStreamingService>() is null)
        {
            return false;
        }

        if (!_resumedAt.TryGetValue(peerId, out var earlier))
        {
            _resumedAt[peerId] = earlier = [];
        }

        if (!ViewEndings.AllowAnother(earlier, now))
        {
            Log.Information("The view of {Peer} keeps ending as soon as it is reopened; it is left closed", viewer.PeerName);
            return false;
        }

        // One waiting view per device: an older one still waiting has nothing left to wait for.
        if (_waitingViews.Remove(peerId, out var older) && !ReferenceEquals(older.Window, window))
        {
            older.Window.Close();
        }

        _waitingViews[peerId] = new WaitingView
        {
            Window = window,
            PeerId = peerId,
            PeerName = viewer.PeerName,
            MonitorIndex = viewer.Info.MonitorIndex,
            MonitorKey = viewer.Info.MonitorKey,
            InputOn = window.InputOn,
            Until = now + (long)ResumeWithin.TotalMilliseconds,

            // Not at once: a session is announced as ready a moment before the other side is listening for
            // requests on it, and a request sent into that moment is simply not heard.
            NextAsk = now + 700,
        };

        Log.Information("The view of {Peer} lost its connection; it will be reopened when the device is back", viewer.PeerName);
        _resumeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => ResumeTick(), Dispatcher);
        _resumeTimer.Start();
        return true;
    }

    private void ResumeTick()
    {
        var now = Environment.TickCount64;
        var screen = _services?.GetService<ScreenStreamingService>();
        var node = _services?.GetService<MirroringNode>();
        foreach (var waiting in _waitingViews.Values.ToList())
        {
            if (now > waiting.Until || waiting.Asks >= MostAsks || screen is null || node is null)
            {
                Log.Information("The view of {Peer} did not come back; closing it", waiting.PeerName);
                _waitingViews.Remove(waiting.PeerId);
                waiting.Window.Close();
                continue;
            }

            if (now < waiting.NextAsk
                || !node.Sessions.TryGetSession(waiting.PeerId, out var session)
                || session.State != SessionState.Ready)
            {
                continue;
            }

            waiting.Asks++;
            waiting.NextAsk = now + (long)AskAgainAfter.TotalMilliseconds;
            _ = screen.RequestScreenAsync(waiting.PeerId, waiting.MonitorIndex, waiting.MonitorKey.Length > 0 ? waiting.MonitorKey : null);
        }

        if (_waitingViews.Count == 0)
        {
            _resumeTimer?.Stop();
        }
    }

    /// <summary>
    /// A view has just opened. If it is the one a waiting window was waiting for, the new window takes over what
    /// the old one was doing and the old one goes. Returns false when the view is one too many — a second answer
    /// to a request that was repeated — and must not get a window at all.
    /// </summary>
    private bool TakeOverFromWaiting(ScreenViewer viewer, out WaitingView? waited)
    {
        waited = null;
        if (viewer.Info.Kind != "monitor")
        {
            return true;
        }

        var peerId = viewer.Session.PeerId;
        if (_waitingViews.Remove(peerId, out waited))
        {
            return true;
        }

        // Nothing is waiting, yet a view of this device has only just been reopened and is there: this is the
        // answer to the request that was sent twice.
        var reopenedLately = _resumedAt.TryGetValue(peerId, out var times)
            && times.Count > 0
            && Environment.TickCount64 - times[^1] < 15_000;
        if (reopenedLately && FindScreenView(peerId) is not null)
        {
            viewer.Close();
            return false;
        }

        return true;
    }

    /// <summary>A window closed: if it was waiting to be reopened, somebody closed it, and that is an answer.</summary>
    private void ForgetWaiting(ScreenViewerWindow window)
    {
        foreach (var waiting in _waitingViews.Values.Where(w => ReferenceEquals(w.Window, window)).ToList())
        {
            _waitingViews.Remove(waiting.PeerId);
        }
    }
}
