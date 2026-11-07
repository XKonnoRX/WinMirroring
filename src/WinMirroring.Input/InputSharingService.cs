using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;
using WinMirroring.Input.Hooks;
using WinMirroring.Input.Injection;
using WinMirroring.Input.Layout;
using WinMirroring.Input.Protocol;
using WinMirroring.Input.Screen;
using static WinMirroring.Input.Native.NativeMethods;

namespace WinMirroring.Input;

/// <summary>
/// Mouse and keyboard sharing, Mouse Without Borders style. Every device runs this service:
/// as the <b>controller</b> it watches its own cursor hit a screen edge, then swallows local input and streams it
/// to the neighbour from the layout; as the <b>controlled</b> device it replays what arrives through SendInput and
/// reports when the cursor is pushed out across one of its edges, so control can move on or come back.
/// </summary>
public sealed class InputSharingService : IAsyncDisposable
{
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkHome = 0x24;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;

    private abstract record Command;

    private sealed record MoveCommand(int Dx, int Dy) : Command;

    private sealed record ButtonCommand(MouseButton Button, bool Down) : Command;

    private sealed record WheelCommand(int Delta, bool Horizontal) : Command;

    private sealed record KeyCommand(ushort VirtualKey, ushort ScanCode, bool Up, bool Extended) : Command;

    private sealed record CrossEdgeCommand(Edge Edge, float Relative) : Command;

    private sealed record EdgeReachedCommand(string PeerId, Edge Edge, float Relative) : Command;

    private sealed record ReturnHomeCommand : Command;

    private sealed record SessionLostCommand(string PeerId) : Command;

    /// <summary>Per-controller state on the controlled side; touched only by that session's receive loop.</summary>
    private sealed class ControlledState
    {
        public bool Active;
        public int PushLeft;
        public int PushRight;
        public int PushTop;
        public int PushBottom;

        public void ResetPush() => PushLeft = PushRight = PushTop = PushBottom = 0;
    }

    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly InputInjector _injector = new();
    private readonly ConcurrentDictionary<string, ControlledState> _controllers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    // Physical key/button state as seen by the hooks, shared between the hook thread and the worker.
    private readonly object _keyGate = new();
    private readonly HashSet<uint> _physicalDownKeys = [];
    private readonly HashSet<MouseButton> _physicalDownButtons = [];
    private HashSet<uint> _passThroughKeyUps = [];
    private HashSet<MouseButton> _passThroughButtonUps = [];

    // Edge push accumulators; written on the hook thread, reset from the worker.
    private int _pushLeft;
    private int _pushRight;
    private int _pushTop;
    private int _pushBottom;

    private InputHookThread? _hooks;
    private Task? _worker;
    private PeerSession? _target;
    private volatile bool _capturing;
    private volatile DeviceLayout _layout;

    public InputSharingService(MirroringNode node, ILogger<InputSharingService> logger)
    {
        _node = node;
        _logger = logger;
        _layout = DeviceLayout.FromCells(node.Settings.Input.Layout);
    }

    public DeviceLayout Layout => _layout;

    public bool IsEnabled => _hooks is not null;

    /// <summary>True while this device's input is being sent to another device.</summary>
    public bool IsCapturing => _capturing;

    public string? RemoteTargetId => _target?.PeerId;

    /// <summary>Raised with the peer id that now receives this device's input, or null when it is back home.</summary>
    public event Action<string?>? FocusChanged;

    public event Action? LayoutChanged;

    public void Start()
    {
        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                OnSessionReady(session);
            }
        }

        _worker = Task.Run(() => WorkerAsync(_cts.Token));

        if (_node.Settings.Input.Enabled)
        {
            StartHooks();
        }
    }

    /// <summary>Turns the hooks on or off and remembers the choice.</summary>
    public void SetEnabled(bool enabled)
    {
        _node.Settings.Input.Enabled = enabled;
        _node.SaveSettings();
        if (enabled)
        {
            StartHooks();
        }
        else
        {
            StopHooks();
        }
    }

    /// <summary>Brings input back to this device (also bound to Ctrl+Alt+Home while away).</summary>
    public void ReturnHome() => _commands.Writer.TryWrite(new ReturnHomeCommand());

    /// <summary>Stores a new grid, applies it here and pushes it to every connected peer.</summary>
    public void ApplyLayout(IEnumerable<LayoutCell> cells)
    {
        var list = cells.Where(c => !string.IsNullOrEmpty(c.DeviceId)).ToList();
        var settings = _node.Settings.Input;
        settings.Layout = list;
        settings.LayoutVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _node.SaveSettings();
        _layout = DeviceLayout.FromCells(list);
        LayoutChanged?.Invoke();

        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                _ = SendLayoutAsync(session);
            }
        }
    }

    private void StartHooks()
    {
        if (_hooks is not null)
        {
            return;
        }

        var hooks = new InputHookThread(_logger)
        {
            MouseFilter = OnMouseHook,
            KeyboardFilter = OnKeyboardHook,
            RawMouseMoved = OnRawMouse,
        };
        hooks.Start();
        _hooks = hooks;
    }

    private void StopHooks()
    {
        var hooks = _hooks;
        if (hooks is null)
        {
            return;
        }

        _hooks = null;
        _commands.Writer.TryWrite(new ReturnHomeCommand());
        hooks.Dispose();
    }

    // ---- sessions and layout sync ------------------------------------------------------------------------

    private void OnSessionReady(PeerSession session)
    {
        session.RegisterHandler(ChannelId.Input, OnInputFrameAsync);
        session.ControlMessageReceived += OnControlMessage;
        if (_node.Settings.Input.LayoutVersion > 0)
        {
            _ = SendLayoutAsync(session);
        }
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;
        if (_controllers.TryRemove(session.PeerId, out var state) && state.Active)
        {
            _injector.ReleaseAll();
        }

        _commands.Writer.TryWrite(new SessionLostCommand(session.PeerId));
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        if (message is not LayoutMessage layout)
        {
            return;
        }

        var settings = _node.Settings.Input;
        if (layout.Version <= settings.LayoutVersion)
        {
            return;
        }

        settings.Layout = layout.Cells.Where(c => !string.IsNullOrEmpty(c.DeviceId)).ToList();
        settings.LayoutVersion = layout.Version;
        _node.SaveSettings();
        _layout = DeviceLayout.FromCells(settings.Layout);
        _logger.LogInformation("Adopted layout version {Version} from {Peer}", layout.Version, session.PeerName);
        LayoutChanged?.Invoke();
    }

    private async Task SendLayoutAsync(PeerSession session)
    {
        try
        {
            var settings = _node.Settings.Input;
            await session.SendControlAsync(new LayoutMessage(settings.LayoutVersion, settings.Layout.ToList()), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Layout not sent to {Peer}", session.PeerName);
        }
    }

    // ---- hook thread: decide what to swallow, enqueue the rest --------------------------------------------

    private bool OnMouseHook(MouseHookEvent e)
    {
        if (e.IsOurInjection)
        {
            return false;
        }

        var isButton = TryMapButton(e.Message, e.HighWord, out var button, out var down);
        if (isButton)
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

        if (!_capturing)
        {
            return false;
        }

        switch (e.Message)
        {
            case WM_MOUSEMOVE:
                // Motion is taken from Raw Input; the hook only keeps the local cursor still.
                return true;

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                _commands.Writer.TryWrite(new WheelCommand(e.HighWord, e.Message == WM_MOUSEHWHEEL));
                return true;
        }

        if (!isButton)
        {
            return false;
        }

        _commands.Writer.TryWrite(new ButtonCommand(button, down));
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

        if (!_capturing)
        {
            return false;
        }

        if (!e.IsUp && e.VirtualKey == VkHome && controlAndAltDown)
        {
            _commands.Writer.TryWrite(new ReturnHomeCommand());
            return true;
        }

        _commands.Writer.TryWrite(new KeyCommand((ushort)e.VirtualKey, (ushort)e.ScanCode, e.IsUp, e.IsExtended));
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

    private void OnRawMouse(int dx, int dy)
    {
        if (_capturing)
        {
            _commands.Writer.TryWrite(new MoveCommand(dx, dy));
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

        if (dx < 0 && cursor.X <= screen.Left)
        {
            if ((_pushLeft += -dx) >= resistance)
            {
                crossed = Edge.Left;
            }
        }
        else
        {
            _pushLeft = 0;
        }

        if (dx > 0 && cursor.X >= screen.Right - 1)
        {
            if ((_pushRight += dx) >= resistance)
            {
                crossed = Edge.Right;
            }
        }
        else
        {
            _pushRight = 0;
        }

        if (dy < 0 && cursor.Y <= screen.Top)
        {
            if ((_pushTop += -dy) >= resistance)
            {
                crossed = Edge.Top;
            }
        }
        else
        {
            _pushTop = 0;
        }

        if (dy > 0 && cursor.Y >= screen.Bottom - 1)
        {
            if ((_pushBottom += dy) >= resistance)
            {
                crossed = Edge.Bottom;
            }
        }
        else
        {
            _pushBottom = 0;
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

    // ---- worker: controller-side state machine and sending -----------------------------------------------

    private async Task WorkerAsync(CancellationToken ct)
    {
        var reader = _commands.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var command))
                {
                    try
                    {
                        await HandleAsync(command, reader, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Input command {Command} failed; returning control home", command.GetType().Name);
                        await SetLocalAsync(sendLeave: false, placement: null).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(Command command, ChannelReader<Command> reader, CancellationToken ct)
    {
        switch (command)
        {
            case MoveCommand move:
            {
                if (_target is null)
                {
                    return;
                }

                long dx = move.Dx, dy = move.Dy;
                while (reader.TryPeek(out var next) && next is MoveCommand more && reader.TryRead(out _))
                {
                    dx += more.Dx;
                    dy += more.Dy;
                }

                var writer = new ArrayBufferWriter<byte>(16);
                while (dx != 0 || dy != 0)
                {
                    var stepX = (int)Math.Clamp(dx, short.MinValue, short.MaxValue);
                    var stepY = (int)Math.Clamp(dy, short.MinValue, short.MaxValue);
                    InputCodec.WriteMouseMove(writer, stepX, stepY);
                    dx -= stepX;
                    dy -= stepY;
                }

                await SendToTargetAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
                return;
            }

            case ButtonCommand button:
                await SendToTargetAsync(InputCodec.Single(w => InputCodec.WriteMouseButton(w, button.Button, button.Down)), ct).ConfigureAwait(false);
                return;

            case WheelCommand wheel:
                await SendToTargetAsync(InputCodec.Single(w => InputCodec.WriteMouseWheel(w, wheel.Delta, wheel.Horizontal)), ct).ConfigureAwait(false);
                return;

            case KeyCommand key:
                await SendToTargetAsync(InputCodec.Single(w => InputCodec.WriteKey(w, key.VirtualKey, key.ScanCode, key.Up, key.Extended)), ct).ConfigureAwait(false);
                return;

            case CrossEdgeCommand cross:
                await CrossEdgeAsync(cross.Edge, cross.Relative, ct).ConfigureAwait(false);
                return;

            case EdgeReachedCommand reached:
                await OnEdgeReachedAsync(reached, ct).ConfigureAwait(false);
                return;

            case ReturnHomeCommand:
                await SetLocalAsync(sendLeave: true, placement: null).ConfigureAwait(false);
                return;

            case SessionLostCommand lost:
                if (_target is { } target && string.Equals(target.PeerId, lost.PeerId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Lost the session to {Peer} while controlling it; input is back here", target.PeerName);
                    await SetLocalAsync(sendLeave: false, placement: null).ConfigureAwait(false);
                }

                return;
        }
    }

    private async Task SendToTargetAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var target = _target;
        if (target is null)
        {
            return;
        }

        try
        {
            await target.SendAsync(ChannelId.Input, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Sending input to {Peer} failed; input is back here", target.PeerName);
            await SetLocalAsync(sendLeave: false, placement: null).ConfigureAwait(false);
        }
    }

    private async Task CrossEdgeAsync(Edge edge, float relative, CancellationToken ct)
    {
        if (_target is not null)
        {
            return;
        }

        var neighborId = _layout.Neighbor(_node.Identity.Id, edge);
        if (neighborId is null)
        {
            return;
        }

        if (!_node.Sessions.TryGetSession(neighborId, out var session) || session.State != SessionState.Ready)
        {
            _logger.LogDebug("Edge {Edge} leads to {Peer}, which is not connected", edge, neighborId);
            return;
        }

        await EnterRemoteAsync(session, EdgeMath.Opposite(edge), relative, ct).ConfigureAwait(false);
    }

    private async Task EnterRemoteAsync(PeerSession session, Edge entryEdge, float relative, CancellationToken ct)
    {
        lock (_keyGate)
        {
            _passThroughKeyUps = new HashSet<uint>(_physicalDownKeys);
            _passThroughButtonUps = new HashSet<MouseButton>(_physicalDownButtons);
        }

        _target = session;
        _capturing = true;

        try
        {
            await session.SendAsync(ChannelId.Input, InputCodec.Single(w => InputCodec.WriteEnter(w, entryEdge, relative)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Could not hand input to {Peer}", session.PeerName);
            await SetLocalAsync(sendLeave: false, placement: null).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Input moved to {Peer}, entering at its {Edge} edge", session.PeerName, entryEdge);
        FocusChanged?.Invoke(session.PeerId);
    }

    private async Task OnEdgeReachedAsync(EdgeReachedCommand reached, CancellationToken ct)
    {
        var target = _target;
        if (target is null || !string.Equals(target.PeerId, reached.PeerId, StringComparison.Ordinal))
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
            await SetLocalAsync(sendLeave: true, placement: (entryEdge, reached.Relative)).ConfigureAwait(false);
            return;
        }

        if (!_node.Sessions.TryGetSession(nextId, out var next) || next.State != SessionState.Ready)
        {
            return;
        }

        await SendLeaveAsync(target).ConfigureAwait(false);
        _target = null;
        await EnterRemoteAsync(next, entryEdge, reached.Relative, ct).ConfigureAwait(false);
    }

    private async Task SetLocalAsync(bool sendLeave, (Edge Edge, float Relative)? placement)
    {
        var previous = _target;
        _target = null;
        _capturing = false;
        lock (_keyGate)
        {
            _passThroughKeyUps = [];
            _passThroughButtonUps = [];
        }

        if (sendLeave && previous is not null)
        {
            await SendLeaveAsync(previous).ConfigureAwait(false);
        }

        if (placement is { } at)
        {
            VirtualScreen.SetCursor(EdgeMath.PointOn(VirtualScreen.Current, at.Edge, at.Relative, inset: 2));
        }

        ResetPush();

        if (previous is not null)
        {
            _logger.LogInformation("Input is back on this device (was on {Peer})", previous.PeerName);
            FocusChanged?.Invoke(null);
        }
    }

    private async Task SendLeaveAsync(PeerSession session)
    {
        try
        {
            await session.SendAsync(ChannelId.Input, InputCodec.Single(InputCodec.WriteLeave), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Leave not delivered to {Peer}", session.PeerName);
        }
    }

    // ---- controlled side: replay what the controller sends ------------------------------------------------

    private ValueTask OnInputFrameAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var state = _controllers.GetOrAdd(session.PeerId, static _ => new ControlledState());
        var data = payload.Span;
        while (InputCodec.TryRead(data, out var evt, out var consumed))
        {
            data = data[consumed..];
            Apply(session, state, evt);
        }

        return ValueTask.CompletedTask;
    }

    private void Apply(PeerSession session, ControlledState state, in InputEvent evt)
    {
        switch (evt.Kind)
        {
            case InputEventKind.Enter:
                state.Active = true;
                state.ResetPush();
                var entry = EdgeMath.PointOn(VirtualScreen.Current, evt.Edge, evt.Relative, inset: 1);
                _injector.MoveAbsolute(entry.X, entry.Y);
                _logger.LogInformation("{Peer} took control, cursor at {Edge} edge", session.PeerName, evt.Edge);
                break;

            case InputEventKind.MouseMove when state.Active:
                HandleRemoteMove(session, state, evt.Dx, evt.Dy);
                break;

            case InputEventKind.MouseButton when state.Active:
                _injector.Button(evt.Button, evt.Down);
                break;

            case InputEventKind.MouseWheel when state.Active:
                _injector.Wheel(evt.WheelDelta, evt.Horizontal);
                break;

            case InputEventKind.Key when state.Active:
                _injector.Key(evt.VirtualKey, evt.ScanCode, evt.Up, evt.Extended);
                break;

            case InputEventKind.Leave:
                state.Active = false;
                state.ResetPush();
                _injector.ReleaseAll();
                _logger.LogInformation("{Peer} released control", session.PeerName);
                break;

            case InputEventKind.EdgeReached:
                _commands.Writer.TryWrite(new EdgeReachedCommand(session.PeerId, evt.Edge, evt.Relative));
                break;
        }
    }

    private void HandleRemoteMove(PeerSession session, ControlledState state, int dx, int dy)
    {
        if (!VirtualScreen.TryGetCursor(out var cursor))
        {
            return;
        }

        var screen = VirtualScreen.Current;
        var wantedX = cursor.X + dx;
        var wantedY = cursor.Y + dy;
        var x = Math.Clamp(wantedX, screen.Left, screen.Right - 1);
        var y = Math.Clamp(wantedY, screen.Top, screen.Bottom - 1);
        _injector.MoveAbsolute(x, y);

        var resistance = Math.Max(1, _node.Settings.Input.EdgeResistance);
        Edge? reached = null;

        if (wantedX < screen.Left)
        {
            if ((state.PushLeft += screen.Left - wantedX) >= resistance)
            {
                reached = Edge.Left;
            }
        }
        else
        {
            state.PushLeft = 0;
        }

        if (wantedX > screen.Right - 1)
        {
            if ((state.PushRight += wantedX - (screen.Right - 1)) >= resistance)
            {
                reached = Edge.Right;
            }
        }
        else
        {
            state.PushRight = 0;
        }

        if (wantedY < screen.Top)
        {
            if ((state.PushTop += screen.Top - wantedY) >= resistance)
            {
                reached = Edge.Top;
            }
        }
        else
        {
            state.PushTop = 0;
        }

        if (wantedY > screen.Bottom - 1)
        {
            if ((state.PushBottom += wantedY - (screen.Bottom - 1)) >= resistance)
            {
                reached = Edge.Bottom;
            }
        }
        else
        {
            state.PushBottom = 0;
        }

        if (reached is { } edge)
        {
            state.ResetPush();
            var relative = EdgeMath.RelativeAlong(screen, edge, new System.Drawing.Point(x, y));
            _ = SendEdgeReachedAsync(session, edge, relative);
        }
    }

    private async Task SendEdgeReachedAsync(PeerSession session, Edge edge, float relative)
    {
        try
        {
            await session.SendAsync(ChannelId.Input, InputCodec.Single(w => InputCodec.WriteEdgeReached(w, edge, relative)), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "EdgeReached not delivered to {Peer}", session.PeerName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;

        var hooks = _hooks;
        _hooks = null;
        hooks?.Dispose();

        if (_target is { } target)
        {
            await SetLocalAsync(sendLeave: true, placement: null).ConfigureAwait(false);
            _logger.LogDebug("Released {Peer} on shutdown", target.PeerName);
        }

        _injector.ReleaseAll();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
