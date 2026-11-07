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

namespace WinMirroring.Input;

/// <summary>
/// Mouse and keyboard sharing, Mouse Without Borders style. Every device runs this service:
/// as the <b>controller</b> it watches its own cursor hit a screen edge, then hides the local cursor, swallows local
/// input and streams it to the neighbour from the layout; as the <b>controlled</b> device it replays what arrives
/// and reports when the cursor is pushed out across one of its edges, so control can move on or come back.
/// <para>
/// Local input is never held hostage by the network: a capture is a lease that the controlled side renews with
/// keepalives. If nothing arrives for <see cref="CaptureLeaseMs"/>, the very next local input event ends the capture.
/// </para>
/// </summary>
public sealed partial class InputSharingService : IAsyncDisposable
{
    /// <summary>Controller side: silence from the controlled device after which local input is taken back.</summary>
    private const long CaptureLeaseMs = 1500;

    /// <summary>Controlled side: silence from the controller after which pressed keys and buttons are released.</summary>
    private const long ControlledSilenceMs = 2000;

    /// <summary>A session is offered as a target only if it heard from the peer this recently (heartbeat is 5 s).</summary>
    private const long UsableSilenceMs = 8000;

    private static readonly TimeSpan AliveInterval = TimeSpan.FromMilliseconds(250);

    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly InputInjector _injector = new();
    private readonly RemoteCursorPlayer _player;
    private readonly ConcurrentDictionary<string, ControlledState> _controllers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private InputHookThread? _hooks;
    private Task? _worker;
    private volatile DeviceLayout _layout;

    public InputSharingService(MirroringNode node, ILogger<InputSharingService> logger)
    {
        _node = node;
        _logger = logger;
        _layout = DeviceLayout.FromCells(node.Settings.Input.Layout);
        _player = new RemoteCursorPlayer(_injector, () => _node.Settings.Input.EdgeResistance, logger);
        _player.EdgeReached += OnPlayerEdgeReached;
        ApplyInputSettings();
    }

    public DeviceLayout Layout => _layout;

    public bool IsEnabled => _hooks is not null;

    /// <summary>True while this device's input is being sent to another device.</summary>
    public bool IsCapturing => Volatile.Read(ref _capture) is not null;

    public string? RemoteTargetId => Volatile.Read(ref _capture)?.Session.PeerId;

    /// <summary>True while another device drives this device's cursor.</summary>
    public bool IsBeingControlled => _player.HasOwner;

    /// <summary>Current jitter-buffer delay while being controlled.</summary>
    public double PlayoutDelayMilliseconds => _player.CurrentDelayMilliseconds;

    /// <summary>Raised with the peer id that now receives this device's input, or null when it is back home.</summary>
    public event Action<string?>? FocusChanged;

    public event Action? LayoutChanged;

    /// <summary>Raised with the peer id that starts controlling this device, or null when it stops.</summary>
    public event Action<string?>? ControlledByChanged;

    // ---- drag and drop support ---------------------------------------------------------------------------

    /// <summary>Controlled side: raised before keys and buttons are released because control ends (peer id).</summary>
    public event Action<string>? ControlEnding;

    /// <summary>
    /// Controlled side: awaited (at most a second) before telling the controller that the cursor leaves this device
    /// while it holds the left button, so a drag in progress can be caught and handed over first.
    /// </summary>
    public Func<PeerSession, System.Drawing.Point, Task>? BeforeEdgeHandOff { get; set; }

    /// <summary>Peer id of the device currently controlling this one, if any.</summary>
    public string? ControllerId => (_player.Owner as PeerSession)?.PeerId;

    /// <summary>Where the local cursor is parked (and hidden) while input goes to another device.</summary>
    public System.Drawing.Point CursorParkPoint => Screen.VirtualScreen.PrimaryCenter;

    /// <summary>Physical state of a local mouse button as seen by the hooks, even while input is captured.</summary>
    public bool IsLocalButtonPhysicallyDown(Protocol.MouseButton button)
    {
        lock (_keyGate)
        {
            return _physicalDownButtons.Contains(button);
        }
    }

    /// <summary>
    /// Injects a button here. Tracked presses are released automatically when a controller leaves; untracked ones
    /// are expected to be released by the user's own physical button.
    /// </summary>
    public void InjectLocalButton(Protocol.MouseButton button, bool down, bool track)
    {
        if (track)
        {
            _injector.Button(button, down);
        }
        else
        {
            _injector.ButtonUntracked(button, down);
        }
    }

    public void NudgeCursor() => _injector.Nudge();

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

    /// <summary>Re-reads smoothing settings.</summary>
    public void ApplyInputSettings()
    {
        var settings = _node.Settings.Input;
        _player.Configure(settings.SmoothingEnabled, settings.MaxSmoothingDelayMs);
    }

    /// <summary>Brings input back to this device (also bound to Ctrl+Alt+Home while away).</summary>
    public void ReturnHome() => _commands.Writer.TryWrite(new ReturnHomeCommand());

    /// <summary>Sends this device's input to <paramref name="peerId"/> right away, cursor in the middle of its screen.</summary>
    public void ControlPeer(string peerId) => _commands.Writer.TryWrite(new ControlPeerCommand(peerId));

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
        if (_controllers.TryRemove(session.PeerId, out var state))
        {
            ReleaseControl(session, state, "session closed");
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

    private bool IsSessionUsable(PeerSession session) =>
        session.State == SessionState.Ready
        && Environment.TickCount64 - session.LastReceivedTicks < UsableSilenceMs;

    public async ValueTask DisposeAsync()
    {
        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;

        _commands.Writer.TryComplete();
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

        if (Volatile.Read(ref _capture) is { } capture)
        {
            EndCapture(capture, sendLeave: true, placement: null, "shutdown");
        }

        var hooks = _hooks;
        _hooks = null;
        hooks?.Dispose();

        foreach (var (peerId, state) in _controllers)
        {
            if (_node.Sessions.TryGetSession(peerId, out var session))
            {
                ReleaseControl(session, state, "shutdown");
            }
        }

        _player.Dispose();
        _injector.ReleaseAll();
        _cts.Dispose();
    }
}
