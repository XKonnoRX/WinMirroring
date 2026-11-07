using Microsoft.Extensions.Logging;
using WinMirroring.Core.Peers;
using WinMirroring.Input.Hooks;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;
using WinMirroring.Input.Timing;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input;

/// <summary>Controller side: edge detection, capture of local input and its forwarding.</summary>
public sealed partial class InputSharingService
{
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkHome = 0x24;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;

    private abstract record Command;

    private sealed record CrossEdgeCommand(Edge Edge, float Relative) : Command;

    private sealed record EdgeReachedCommand(string PeerId, Edge Edge, float Relative) : Command;

    private sealed record ReturnHomeCommand : Command;

    private sealed record ReleaseRequestedCommand(string PeerId) : Command;

    private sealed record ControlPeerCommand(string PeerId) : Command;

    private sealed record SessionLostCommand(string PeerId) : Command;

    private sealed record CaptureLostCommand(Capture Capture, string Reason) : Command;

    /// <summary>One period of sending local input to one peer.</summary>
    private sealed class Capture(PeerSession session, InputSender sender)
    {
        public PeerSession Session { get; } = session;

        public InputSender Sender { get; } = sender;

        public long StartedTicks { get; } = Environment.TickCount64;
    }

    // Physical key/button state as seen by the hooks, shared between the hook thread and the worker.
    private readonly object _keyGate = new();
    private readonly HashSet<uint> _physicalDownKeys = [];
    private readonly HashSet<MouseButton> _physicalDownButtons = [];
    private HashSet<uint> _passThroughKeyUps = [];
    private HashSet<MouseButton> _passThroughButtonUps = [];

    // Edge push accumulators, hook thread only (reset from the worker).
    private int _pushLeft;
    private int _pushRight;
    private int _pushTop;
    private int _pushBottom;

    /// <summary>The active capture; read lock-free by the hook thread. Changed only with Volatile/Interlocked.</summary>
    private Capture? _capture;

    // ---- hook thread -------------------------------------------------------------------------------------

    private bool IsCaptureHealthy(Capture capture)
    {
        if (capture.Sender.IsFaulted)
        {
            return false;
        }

        var lastHeard = Math.Max(capture.Session.LastReceivedTicks, capture.StartedTicks);
        return Environment.TickCount64 - lastHeard <= CaptureLeaseMs;
    }

    /// <summary>Ends a capture from the hook thread: local input flows again immediately, the worker cleans up.</summary>
    private void DropCapture(Capture capture, string reason)
    {
        if (Interlocked.CompareExchange(ref _capture, null, capture) == capture)
        {
            _commands.Writer.TryWrite(new CaptureLostCommand(capture, reason));
        }
    }

    private bool OnMouseHook(MouseHookEvent e)
    {
        if (e.IsOurInjection)
        {
            return false;
        }

        var isButton = TryMapButton(e.Message, e.HighWord, out var button, out var down);
        if (isButton && !e.IsInjected)
        {
            lock (_keyGate)
            {
                if (down)
                {
                    _physicalDownButtons.Add(button);
                }
                else
                {
                    _physicalDownButtons.Remove(button);
                }
            }
        }

        var capture = Volatile.Read(ref _capture);
        if (capture is null)
        {
            return false;
        }

        if (!IsCaptureHealthy(capture))
        {
            DropCapture(capture, "the other device stopped responding");
            return false;
        }

        bool posted;
        switch (e.Message)
        {
            case WM_MOUSEMOVE:
                if (e.IsInjected)
                {
                    // Someone else's synthetic motion: neither forward nor block it.
                    return false;
                }

                // pt is where the system would put the cursor after its own speed and acceleration, so the
                // difference to the (parked) cursor is exactly the motion the user is used to on this device.
                if (!VirtualScreen.TryGetCursor(out var cursor))
                {
                    return true;
                }

                var dx = e.X - cursor.X;
                var dy = e.Y - cursor.Y;
                if (dx == 0 && dy == 0)
                {
                    return true;
                }

                posted = capture.Sender.PostMove(MonotonicClock.NowWire(), dx, dy);
                break;

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                posted = capture.Sender.PostWheel(e.HighWord, e.Message == WM_MOUSEHWHEEL);
                break;

            default:
                if (!isButton)
                {
                    return false;
                }

                posted = capture.Sender.PostButton(button, down);
                if (!down)
                {
                    lock (_keyGate)
                    {
                        // A button held since before capture started must still be released locally.
                        if (_passThroughButtonUps.Remove(button))
                        {
                            return false;
                        }
                    }
                }

                break;
        }

        if (!posted)
        {
            DropCapture(capture, "send queue overflow");
            return false;
        }

        return true;
    }

    private bool OnKeyboardHook(KeyboardHookEvent e)
    {
        if (e.IsOurInjection)
        {
            return false;
        }

        bool controlAndAltDown;
        lock (_keyGate)
        {
            if (e.IsUp)
            {
                _physicalDownKeys.Remove(e.VirtualKey);
            }
            else
            {
                _physicalDownKeys.Add(e.VirtualKey);
            }

            controlAndAltDown = AnyDown(VkControl, VkLControl, VkRControl) && AnyDown(VkMenu, VkLMenu, VkRMenu);
        }

        var capture = Volatile.Read(ref _capture);
        if (capture is null)
        {
            return false;
        }

        if (!IsCaptureHealthy(capture))
        {
            DropCapture(capture, "the other device stopped responding");
            return false;
        }

        if (!e.IsUp && e.VirtualKey == VkHome && controlAndAltDown)
        {
            _commands.Writer.TryWrite(new ReturnHomeCommand());
            return true;
        }

        if (!capture.Sender.PostKey((ushort)e.VirtualKey, (ushort)e.ScanCode, e.IsUp, e.IsExtended))
        {
            DropCapture(capture, "send queue overflow");
            return false;
        }

        if (e.IsUp)
        {
            lock (_keyGate)
            {
                if (_passThroughKeyUps.Remove(e.VirtualKey))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool AnyDown(params uint[] keys)
    {
        foreach (var key in keys)
        {
            if (_physicalDownKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Raw device motion, used only to notice the cursor being pushed against an edge.</summary>
    private void OnRawMouse(int dx, int dy)
    {
        if (Volatile.Read(ref _capture) is not null || _player.HasOwner)
        {
            return;
        }

        var layout = _layout;
        if (layout.IsEmpty || !layout.Contains(_node.Identity.Id) || !VirtualScreen.TryGetCursor(out var cursor))
        {
            return;
        }

        var screen = VirtualScreen.Current;
        var resistance = Math.Max(1, _node.Settings.Input.EdgeResistance);
        Edge? crossed = null;

        _pushLeft = dx < 0 && cursor.X <= screen.Left ? _pushLeft - dx : 0;
        _pushRight = dx > 0 && cursor.X >= screen.Right - 1 ? _pushRight + dx : 0;
        _pushTop = dy < 0 && cursor.Y <= screen.Top ? _pushTop - dy : 0;
        _pushBottom = dy > 0 && cursor.Y >= screen.Bottom - 1 ? _pushBottom + dy : 0;

        if (_pushLeft >= resistance)
        {
            crossed = Edge.Left;
        }
        else if (_pushRight >= resistance)
        {
            crossed = Edge.Right;
        }
        else if (_pushTop >= resistance)
        {
            crossed = Edge.Top;
        }
        else if (_pushBottom >= resistance)
        {
            crossed = Edge.Bottom;
        }

        if (crossed is { } edge)
        {
            ResetPush();
            _commands.Writer.TryWrite(new CrossEdgeCommand(edge, EdgeMath.RelativeAlong(screen, edge, cursor)));
        }
    }

    private void ResetPush()
    {
        Volatile.Write(ref _pushLeft, 0);
        Volatile.Write(ref _pushRight, 0);
        Volatile.Write(ref _pushTop, 0);
        Volatile.Write(ref _pushBottom, 0);
    }

    private static bool TryMapButton(uint message, short highWord, out MouseButton button, out bool down)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN: button = MouseButton.Left; down = true; return true;
            case WM_LBUTTONUP: button = MouseButton.Left; down = false; return true;
            case WM_RBUTTONDOWN: button = MouseButton.Right; down = true; return true;
            case WM_RBUTTONUP: button = MouseButton.Right; down = false; return true;
            case WM_MBUTTONDOWN: button = MouseButton.Middle; down = true; return true;
            case WM_MBUTTONUP: button = MouseButton.Middle; down = false; return true;
            case WM_XBUTTONDOWN: button = highWord == 2 ? MouseButton.X2 : MouseButton.X1; down = true; return true;
            case WM_XBUTTONUP: button = highWord == 2 ? MouseButton.X2 : MouseButton.X1; down = false; return true;
            default: button = MouseButton.None; down = false; return false;
        }
    }

    // ---- worker: capture state machine. Never waits for the network. -------------------------------------

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Handle(command);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Input command {Command} failed", command.GetType().Name);
                    if (Volatile.Read(ref _capture) is { } capture)
                    {
                        EndCapture(capture, sendLeave: false, placement: null, "internal error");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(Command command)
    {
        var capture = Volatile.Read(ref _capture);
        switch (command)
        {
            case CrossEdgeCommand cross when capture is null:
                var neighborId = _layout.Neighbor(_node.Identity.Id, cross.Edge);
                if (neighborId is not null && IsCrossingSuppressed?.Invoke(neighborId) == true)
                {
                    break; // that device shows our desktop as a second monitor: it is not a separate computer now
                }

                if (neighborId is not null && TryGetUsableSession(neighborId, out var neighbor))
                {
                    BeginCapture(neighbor, atCenter: false, EdgeMath.Opposite(cross.Edge), cross.Relative);
                }

                break;

            case EdgeReachedCommand reached:
                OnEdgeReached(capture, reached);
                break;

            case ReturnHomeCommand when capture is not null:
                EndCapture(capture, sendLeave: true, placement: null, "returned by the user");
                break;

            case ReleaseRequestedCommand released when capture is not null && capture.Session.PeerId == released.PeerId:
                EndCapture(capture, sendLeave: true, placement: null, "returned from the controlled device");
                break;

            case ControlPeerCommand control:
                if (_hooks is null)
                {
                    _logger.LogInformation("Input sharing is off; cannot control {Peer}", control.PeerId);
                }
                else if (capture?.Session.PeerId != control.PeerId && TryGetUsableSession(control.PeerId, out var target))
                {
                    if (capture is null)
                    {
                        BeginCapture(target, atCenter: true, Edge.Left, 0.5f);
                    }
                    else
                    {
                        SwitchCapture(capture, target, atCenter: true, Edge.Left, 0.5f);
                    }
                }

                break;

            case SessionLostCommand lost when capture is not null && capture.Session.PeerId == lost.PeerId:
                EndCapture(capture, sendLeave: false, placement: null, "session closed");
                break;

            case CaptureLostCommand dropped:
                EndCapture(dropped.Capture, sendLeave: false, placement: null, dropped.Reason);
                break;
        }
    }

    private bool TryGetUsableSession(string peerId, out PeerSession session)
    {
        if (_node.Sessions.TryGetSession(peerId, out session) && IsSessionUsable(session))
        {
            return true;
        }

        _logger.LogDebug("{Peer} is not connected or not responding", peerId);
        return false;
    }

    private void OnEdgeReached(Capture? capture, EdgeReachedCommand reached)
    {
        if (capture is null || !string.Equals(capture.Session.PeerId, reached.PeerId, StringComparison.Ordinal))
        {
            return;
        }

        var nextId = _layout.Neighbor(reached.PeerId, reached.Edge);
        if (nextId is null)
        {
            return;
        }

        var entryEdge = EdgeMath.Opposite(reached.Edge);
        if (string.Equals(nextId, _node.Identity.Id, StringComparison.Ordinal))
        {
            EndCapture(capture, sendLeave: true, placement: (entryEdge, reached.Relative), "cursor came back");
        }
        else if (TryGetUsableSession(nextId, out var next))
        {
            SwitchCapture(capture, next, atCenter: false, entryEdge, reached.Relative);
        }
    }

    private void BeginCapture(PeerSession session, bool atCenter, Edge entryEdge, float relative)
    {
        var sender = new InputSender(session, _logger);
        if (!sender.PostEnter(atCenter, entryEdge, relative))
        {
            sender.Abort();
            return;
        }

        lock (_keyGate)
        {
            _passThroughKeyUps = new HashSet<uint>(_physicalDownKeys);
            _passThroughButtonUps = new HashSet<MouseButton>(_physicalDownButtons);
        }

        Volatile.Write(ref _capture, new Capture(session, sender));
        _hooks?.ShowCursorShield(VirtualScreen.PrimaryCenter);
        _logger.LogInformation("Input moved to {Peer}", session.PeerName);
        FocusChanged?.Invoke(session.PeerId);
    }

    /// <summary>Hands input straight from one peer to another without coming back here.</summary>
    private void SwitchCapture(Capture capture, PeerSession next, bool atCenter, Edge entryEdge, float relative)
    {
        var sender = new InputSender(next, _logger);
        if (!sender.PostEnter(atCenter, entryEdge, relative))
        {
            sender.Abort();
            return;
        }

        var replacement = new Capture(next, sender);
        if (Interlocked.CompareExchange(ref _capture, replacement, capture) != capture)
        {
            sender.Abort();
            return;
        }

        capture.Sender.CompleteWithLeave();
        _logger.LogInformation("Input moved from {From} to {To}", capture.Session.PeerName, next.PeerName);
        FocusChanged?.Invoke(next.PeerId);
    }

    private void EndCapture(Capture capture, bool sendLeave, (Edge Edge, float Relative)? placement, string reason)
    {
        Interlocked.CompareExchange(ref _capture, null, capture);

        if (sendLeave && !capture.Sender.IsFaulted)
        {
            capture.Sender.CompleteWithLeave();
        }
        else
        {
            capture.Sender.Abort();
        }

        if (Volatile.Read(ref _capture) is not null)
        {
            // Another capture already took over; leave the shield and focus to it.
            return;
        }

        lock (_keyGate)
        {
            _passThroughKeyUps = [];
            _passThroughButtonUps = [];
        }

        _hooks?.HideCursorShield();
        if (placement is { } at)
        {
            VirtualScreen.SetCursor(EdgeMath.PointOn(VirtualScreen.Current, at.Edge, at.Relative, inset: 2));
        }

        ResetPush();
        _logger.LogInformation("Input is back on this device (was on {Peer}): {Reason}", capture.Session.PeerName, reason);
        FocusChanged?.Invoke(null);
    }
}
