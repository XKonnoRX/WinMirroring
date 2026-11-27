using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Media;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Core.Transport;
using WinMirroring.Input;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Encoding;
using WinMirroring.Screen.Protocol;
using WinMirroring.Screen.Transport;

namespace WinMirroring.Screen;

public sealed record ActiveScreenSource(uint StreamId, string PeerName, string Title);

/// <summary>
/// Screen and window streaming between paired devices. A viewer asks a device for one of its monitors; a device
/// pushes one of its windows to another device (from a menu, or by dragging the window across a screen edge).
/// Frames, acknowledgements and remote input travel on the Screen channel of the session.
/// </summary>
public sealed partial class ScreenStreamingService : IAsyncDisposable
{
    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, ScreenSource> _sources = new();
    private int _disposed;
    private readonly ConcurrentDictionary<uint, ScreenViewer> _viewers = new();
    private readonly ConcurrentDictionary<uint, string> _requested = new();
    private readonly ConcurrentDictionary<(nint Window, string PeerId), byte> _windowsStarting = new();
    private readonly ConcurrentDictionary<string, DeviceCapabilitiesMessage> _peerCaps = new(StringComparer.Ordinal);
    private WindowDragWatcher? _watcher;
    private InputSharingService? _input;

    public ScreenStreamingService(MirroringNode node, ILogger<ScreenStreamingService> logger)
    {
        _node = node;
        _logger = logger;
    }

    /// <summary>A stream arrived here; the UI should show it. Raised on a worker thread.</summary>
    public event Action<ScreenViewer>? ViewerOpened;

    public event Action? SourcesChanged;

    /// <summary>Peer ids currently watching one of this device's monitors (sound can follow them).</summary>
    public event Action<IReadOnlyCollection<string>>? ViewersChanged;

    /// <summary>True while a window is being moved by its title bar on this device.</summary>
    public bool IsMovingWindow => _watcher is { } watcher && watcher.Moving != 0;

    /// <summary>True while this device is streaming to, or viewing, any other device (used to defer auto-updates).</summary>
    public bool HasActivity => !_sources.IsEmpty || !_viewers.IsEmpty;

    /// <summary>True while another device shows one of this device's screens (not just a window).</summary>
    public bool IsStreamingScreen => _sources.Values.Any(s => s.Kind is StreamKind.Monitor or StreamKind.Display);

    private void RaiseSourcesChanged()
    {
        SourcesChanged?.Invoke();
        ViewersChanged?.Invoke(_sources.Values
            .Where(s => s.Kind == StreamKind.Monitor)
            .Select(s => s.Session.PeerId)
            .Distinct()
            .ToList());
    }

    /// <summary>
    /// Controlled side: the cursor leaves this device while a window is being moved here, so send that window to
    /// the device that controls us. Returns false when no window is being moved.
    /// </summary>
    public async Task<bool> TrySendMovingWindowAsync(string peerId)
    {
        var settings = _node.Settings.Screen;
        if (_watcher is null || !settings.WindowDragEnabled || !settings.AllowViewing)
        {
            return false;
        }

        var moving = _watcher.Moving;
        if (moving == 0)
        {
            return false;
        }

        // Hide the real window (park it on the virtual monitor) once the drag ends, else put it back where it started.
        if (ShouldHideDraggedWindow(peerId))
        {
            _watcher.MarkParkOnRelease(moving);
        }
        else
        {
            _watcher.MarkSent(moving);
        }

        _logger.LogInformation("Window dragged out to {Peer}", peerId);
        _ = SendWindowAsync(peerId, moving);
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<ActiveScreenSource> Sources =>
        _sources.Values.Select(s => new ActiveScreenSource(s.StreamId, s.Session.PeerName, s.Title)).ToList();

    public void Start()
    {
        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        _node.MediaPacketReceived += OnMediaPacket;
        WinMirroring.Input.Screen.MonitorCatalog.Changed += OnMonitorsChanged;
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                OnSessionReady(session);
            }
        }
    }

    /// <summary>Sending a window by dragging it across a screen edge (needs input sharing to know where the cursor goes).</summary>
    public void AttachWindowDrag(InputSharingService input)
    {
        _input = input;
        _watcher = new WindowDragWatcher(_logger);
        _watcher.ReleasedForPark += window => _ = ParkOnVirtualMonitorAsync(window);
        _watcher.Start();
        input.FocusChanged += OnFocusChanged;
    }

    /// <summary>
    /// A window dragged to <paramref name="peerId"/> should be hidden from this device (parked on the virtual
    /// monitor) when the setting is on, there is a driver, and that peer is not already this device's second monitor.
    /// </summary>
    private bool ShouldHideDraggedWindow(string peerId) =>
        _node.Settings.Screen.HideSentWindow
        && !IsExtendedTo(peerId)
        && Display.VirtualDisplay.FindDeviceName() is not null;

    public static IReadOnlyList<WindowDescription> ShareableWindows() => DisplayCatalog.Windows();

    /// <summary>
    /// A point of an outgoing stream (normalized image coordinates, as the viewer sends them) on this device's
    /// screen, when that viewer may control this device. Used to drop or catch files where the viewer points.
    /// </summary>
    public bool TryMapToScreen(string peerId, uint streamId, float x, float y, out System.Drawing.Point point, bool bringForward = false)
    {
        point = default;
        if (!_node.Settings.Screen.AllowControl || !_sources.TryGetValue(streamId, out var source) || source.Session.PeerId != peerId)
        {
            return false;
        }

        if (bringForward)
        {
            source.BringForward();
        }

        return source.TryMapToScreen(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f), out point);
    }

    /// <summary>Asks <paramref name="peerId"/> to stream one of its monitors here.</summary>
    public async Task<bool> RequestScreenAsync(string peerId, int monitorIndex = 0, string? monitorKey = null)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return false;
        }

        var streamId = NewStreamId();
        _requested[streamId] = peerId;
        try
        {
            // Asking for fewer frames while this device is the one on a battery: what arrives has to be decoded
            // and painted here, and half of sixty is half of that.
            await session.SendControlAsync(new ScreenRequestMessage(streamId, monitorIndex, monitorKey, WantedFps())).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _requested.TryRemove(streamId, out _);
            _logger.LogInformation("Screen request to {Peer} failed: {Message}", session.PeerName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Streams a local window to <paramref name="peerId"/>, which opens it in a window of its own. When
    /// <paramref name="hideSource"/> is set and the setting allows it, the real window is moved off this device's
    /// screens for as long as it is shown there, so the same window is not visible in two places.
    /// </summary>
    public async Task<bool> SendWindowAsync(string peerId, nint window, bool hideSource = false)
    {
        if (!_node.Settings.Screen.AllowViewing || !TryGetReady(peerId, out var session) || !Native.ScreenNative.IsWindow(window))
        {
            return false;
        }

        // Crossing the edge twice during one drag must not open the same window twice over there, also while the
        // first stream is still being set up (creating a capture can take a second).
        var key = (window, peerId);
        if (_sources.Values.Any(s => s.Kind == StreamKind.Window && s.Window == window && s.Session.PeerId == peerId)
            || !_windowsStarting.TryAdd(key, 0))
        {
            return true;
        }

        try
        {
            ScreenSource source;
            try
            {
                source = ScreenSource.ForWindow(_node, session, NewStreamId(), window, _node.Settings.Screen, NegotiateVideo(session), PeerDrawsPointer(session), _logger);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning("Window cannot be captured: {Message}", ex.Message);
                return false;
            }

            var started = await StartSourceAsync(session, source).ConfigureAwait(false);
            if (started && hideSource && _node.Settings.Screen.HideSentWindow)
            {
                await ParkOnVirtualMonitorAsync(window).ConfigureAwait(false);
            }

            return started;
        }
        finally
        {
            _windowsStarting.TryRemove(key, out _);
        }
    }

    /// <summary>How long telling the viewer that a stream has stopped may take before it is stopped regardless.</summary>
    private static readonly TimeSpan StopNoticeWithin = TimeSpan.FromSeconds(2);

    public async Task StopSourceAsync(uint streamId, string reason = "stopped")
    {
        if (!_sources.TryRemove(streamId, out var source))
        {
            return;
        }

        try
        {
            // Not waited for without limit. A stream usually ends because its connection did, and a message handed
            // to a connection that is being torn down can wait for a turn that never comes — which left the stream
            // itself running behind it: capturing a screen for an hour and a half for a viewer that had gone.
            await source.Session.SendControlAsync(new ScreenStopMessage(streamId, reason)).AsTask()
                .WaitAsync(StopNoticeWithin).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
        }

        await source.DisposeAsync().ConfigureAwait(false);
        RaiseSourcesChanged();
        OnSourceRemoved(source);
    }

    // ---- window drag -------------------------------------------------------------------------------------

    private void OnFocusChanged(string? peerId)
    {
        var settings = _node.Settings.Screen;
        if (peerId is null || _watcher is null || _input is null || !settings.WindowDragEnabled || !settings.AllowViewing)
        {
            return;
        }

        var moving = _watcher.Moving;
        if (moving == 0 || !_input.IsLocalButtonPhysicallyDown(Input.Protocol.MouseButton.Left))
        {
            return;
        }

        if (_sources.Values.Any(s => s.Kind == StreamKind.Window && s.Window == moving && s.Session.PeerId == peerId))
        {
            return;
        }

        if (ShouldHideDraggedWindow(peerId))
        {
            _watcher.MarkParkOnRelease(moving);
        }
        else
        {
            _watcher.MarkSent(moving);
        }

        _logger.LogInformation("Window dragged to {Peer}", peerId);
        _ = SendWindowAsync(peerId, moving);
    }

    // ---- sessions and messages ---------------------------------------------------------------------------

    /// <summary>Whether the peer has a virtual display driver (so it can extend its desktop onto us); null until it told us.</summary>
    public bool? PeerVirtualDisplay(string peerId) => _peerCaps.TryGetValue(peerId, out var caps) ? caps.HasVirtualDisplay : null;

    public bool PeerHasVirtualDisplay(string peerId) => PeerVirtualDisplay(peerId) == true;

    /// <summary>The peer's monitors (primary first), or empty until it told us.</summary>
    public IReadOnlyList<RemoteMonitor> PeerMonitors(string peerId) =>
        _peerCaps.TryGetValue(peerId, out var caps) && caps.Monitors is { } monitors ? monitors : [];

    /// <summary>
    /// This device's offer: its driver, its real monitors (the virtual one is left out of the list) and the screen
    /// codecs it can use.
    /// </summary>
    private static DeviceCapabilitiesMessage DescribeCapabilities(bool reply)
    {
        var virtualDevice = Display.VirtualDisplay.FindDeviceName();
        var monitors = DisplayCatalog.Monitors()
            .Where(m => !string.Equals(m.DeviceName, virtualDevice, StringComparison.OrdinalIgnoreCase))
            .Select(m => new RemoteMonitor(m.Index, $"{m.Bounds.Width}×{m.Bounds.Height}", m.IsPrimary, m.DeviceName))
            .ToList();
        return new DeviceCapabilitiesMessage(virtualDevice is not null, reply, monitors, VideoCodecSupport.CanEncodeH264, VideoCodecSupport.CanDecodeH264, DrawsPointer: true);
    }

    /// <summary>Built off the caller's thread: the first codec probe starts Media Foundation and can take a moment.</summary>
    private void SendCapabilities(PeerSession session, bool reply) => _ = Task.Run(async () =>
    {
        try
        {
            await session.SendControlAsync(DescribeCapabilities(reply)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Capabilities to {Peer} not sent: {Message}", session.PeerName, ex.Message);
        }
    });

    /// <summary>Monitors plugged in, removed or rearranged: every connected device gets the new list (as a reply, so it does not answer).</summary>
    private void OnMonitorsChanged()
    {
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                SendCapabilities(session, reply: true);
            }
        }
    }

    /// <summary>H.264 is used only when this device may encode it, the setting allows it, and the peer can decode it.</summary>
    private bool NegotiateVideo(PeerSession session)
    {
        if (_node.Settings.Screen.VideoCodec == VideoCodec.Jpeg || !VideoCodecSupport.CanEncodeH264)
        {
            return false;
        }

        return _peerCaps.TryGetValue(session.PeerId, out var caps) && caps.CanDecodeH264;
    }

    /// <summary>
    /// Whether this viewer draws a streamed pointer itself. Only then does a source leave the pointer out of the
    /// picture and send it on its own; a viewer on an older build still gets it drawn into the frames, which is the
    /// only thing it knows how to show.
    /// </summary>
    private bool PeerDrawsPointer(PeerSession session) =>
        _peerCaps.TryGetValue(session.PeerId, out var caps) && caps.DrawsPointer;

    private void OnSessionReady(PeerSession session)
    {
        session.RegisterHandler(ChannelId.Screen, OnScreenFrameAsync);
        session.ControlMessageReceived += OnControlMessage;

        // Tell the peer what this device can offer. It answers with its own, so even if its first message arrived
        // before we subscribed (the session starts receiving before it is announced as ready), we still learn it.
        SendCapabilities(session, reply: false);
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;
        _peerCaps.TryRemove(session.PeerId, out _);
        foreach (var source in _sources.Values.Where(s => ReferenceEquals(s.Session, session)).ToList())
        {
            if (_sources.TryRemove(source.StreamId, out _))
            {
                _ = source.DisposeAsync().AsTask();
                OnSourceRemoved(source);
            }
        }

        foreach (var viewer in _viewers.Values.Where(v => ReferenceEquals(v.Session, session)).ToList())
        {
            if (_viewers.TryRemove(viewer.StreamId, out _))
            {
                _ = viewer.CloseAsync(notifySource: false, reason: "соединение закрыто", lostConnection: true);
            }
        }

        RaiseSourcesChanged();
    }

    /// <summary>
    /// Tells the device watching stream <paramref name="streamId"/> that its tap put the focus in a text box here (or
    /// out of one), so it can bring its touch keyboard up or down. False when that stream is not shown to that device.
    /// </summary>
    public async Task<bool> SendKeyboardHintAsync(string peerId, uint streamId, bool show)
    {
        if (!_sources.TryGetValue(streamId, out var source) || source.Session.PeerId != peerId)
        {
            return false;
        }

        try
        {
            await source.Session.SendControlAsync(new ScreenKeyboardMessage(streamId, show)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Keyboard hint to {Peer} not sent: {Message}", source.Session.PeerName, ex.Message);
            return false;
        }
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        switch (message)
        {
            case ScreenRequestMessage request:
                _ = Task.Run(() => HandleRequestAsync(session, request));
                break;

            case ScreenStreamMessage info:
                HandleStreamInfo(session, info);
                break;

            case ScreenCodecFallbackMessage fallback:
                if (_sources.TryGetValue(fallback.StreamId, out var fallbackSource) && ReferenceEquals(fallbackSource.Session, session))
                {
                    fallbackSource.FallBackToTiles(fallback.Reason);
                }

                break;

            case DeviceCapabilitiesMessage caps:
                _peerCaps[session.PeerId] = caps;
                if (!caps.IsReply)
                {
                    SendCapabilities(session, reply: true);
                }

                break;

            case ScreenKeyboardMessage keyboard:
                if (_viewers.TryGetValue(keyboard.StreamId, out var tapped) && ReferenceEquals(tapped.Session, session))
                {
                    tapped.OnKeyboardHint(keyboard.Show);
                }

                break;

            case ScreenStatsMessage stats:
                if (_viewers.TryGetValue(stats.StreamId, out var told) && ReferenceEquals(told.Session, session))
                {
                    told.OnStats(stats);
                }

                break;

            case ScreenThumbsMessage thumbs:
                if (_sources.TryGetValue(thumbs.StreamId, out var pictured) && ReferenceEquals(pictured.Session, session))
                {
                    pictured.OnThumbsWanted(thumbs.On);
                }

                break;

            case ScreenThumbMessage thumb:
                if (_viewers.TryGetValue(thumb.StreamId, out var shownTo) && ReferenceEquals(shownTo.Session, session))
                {
                    shownTo.OnThumbnail(thumb);
                }

                break;

            case ScreenMarkMessage mark:
                if (_sources.TryGetValue(mark.StreamId, out var marked) && ReferenceEquals(marked.Session, session))
                {
                    marked.OnMark(mark.Note.Length > 200 ? mark.Note[..200] : mark.Note);
                }

                break;

            case ScreenStopMessage stop:
                if (_sources.TryGetValue(stop.StreamId, out var source) && ReferenceEquals(source.Session, session) && _sources.TryRemove(stop.StreamId, out _))
                {
                    _ = source.DisposeAsync().AsTask();
                    RaiseSourcesChanged();
                    OnSourceRemoved(source);
                }

                if (_viewers.TryGetValue(stop.StreamId, out var viewer) && ReferenceEquals(viewer.Session, session) && _viewers.TryRemove(stop.StreamId, out _))
                {
                    _ = viewer.CloseAsync(notifySource: false, reason: stop.Reason);
                }

                _requested.TryRemove(stop.StreamId, out _);
                break;

            default:
                OnDisplayMessage(session, message);
                break;
        }
    }

    /// <summary>The frame rate to ask a source for, or null when this device is not saving anything.</summary>
    private int? WantedFps() => _node.Power.Saving ? Math.Max(1, _node.Settings.Power.Fps) : null;

    private async Task HandleRequestAsync(PeerSession session, ScreenRequestMessage request)
    {
        try
        {
            if (!_node.Settings.Screen.AllowViewing)
            {
                await session.SendControlAsync(new ScreenStopMessage(request.StreamId, "просмотр экрана запрещён на устройстве")).ConfigureAwait(false);
                return;
            }

            if (_sources.TryGetValue(request.StreamId, out var existing) && ReferenceEquals(existing.Session, session))
            {
                existing.ViewerFps = request.MaxFps;
                existing.SwitchMonitor(request.MonitorIndex, request.MonitorKey);
                await session.SendControlAsync(existing.Describe()).ConfigureAwait(false);
                RaiseSourcesChanged();
                return;
            }

            var source = ScreenSource.ForMonitor(_node, session, request.StreamId, request.MonitorIndex, request.MonitorKey, _node.Settings.Screen, NegotiateVideo(session), PeerDrawsPointer(session), _logger);
            source.ViewerFps = request.MaxFps;
            await StartSourceAsync(session, source).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _logger.LogWarning("Screen request from {Peer} failed: {Message}", session.PeerName, ex.Message);
        }
    }

    private async Task<bool> StartSourceAsync(PeerSession session, ScreenSource source)
    {
        source.Ended += (ended, reason) => _ = StopSourceAsync(ended.StreamId, reason);
        _sources[source.StreamId] = source;
        try
        {
            await session.SendControlAsync(source.Describe()).ConfigureAwait(false);
            source.Start();
            RaiseSourcesChanged();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _sources.TryRemove(source.StreamId, out _);
            await source.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private void HandleStreamInfo(PeerSession session, ScreenStreamMessage info)
    {
        if (_viewers.TryGetValue(info.StreamId, out var existing))
        {
            if (ReferenceEquals(existing.Session, session))
            {
                existing.OnInfo(info);
            }

            return;
        }

        var requested = _requested.TryRemove(info.StreamId, out var peerId) && peerId == session.PeerId;
        var accepted = requested || info.Kind == "window" || (info.Kind == "display" && AcceptsDisplayFrom(session.PeerId));
        if (!accepted)
        {
            return; // monitors are only shown when we asked for them
        }

        var viewer = new ScreenViewer(session, info, _logger, datagram => _node.SendMedia(session.PeerTransportEndPoint, datagram));
        viewer.Closed += (closed, reason) => _viewers.TryRemove(closed.StreamId, out _);
        _viewers[info.StreamId] = viewer;
        _logger.LogInformation("Showing {Title} from {Peer}", info.Title, session.PeerName);
        ViewerOpened?.Invoke(viewer);
    }

    private ValueTask OnScreenFrameAsync(PeerSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var span = payload.Span;
        if (!ScreenCodec.TryReadHeader(span, out var type, out var streamId))
        {
            return ValueTask.CompletedTask;
        }

        // Pictures go to the viewer: changed JPEG tiles, or H.264 video.
        if (type is ScreenMessageType.Frame or ScreenMessageType.VideoFrame)
        {
            if (_viewers.TryGetValue(streamId, out var viewer) && ReferenceEquals(viewer.Session, session))
            {
                viewer.OnFrameFromConnection(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        // So does the pointer, but not through the frame queue: it travels on its own precisely so that it does not
        // wait for a frame to be decoded first.
        if (type is ScreenMessageType.Cursor or ScreenMessageType.CursorShape)
        {
            if (_viewers.TryGetValue(streamId, out var viewer) && ReferenceEquals(viewer.Session, session))
            {
                viewer.OnPointerMessage(span);
            }

            return ValueTask.CompletedTask;
        }

        if (!_sources.TryGetValue(streamId, out var source) || !ReferenceEquals(source.Session, session))
        {
            return ValueTask.CompletedTask;
        }

        switch (type)
        {
            case ScreenMessageType.Ack when ScreenCodec.TryDecodeAck(span, out var ackedNo, out var viewerMs, out var paintMs):
                source.OnAck(ackedNo, viewerMs, paintMs);
                break;

            case ScreenMessageType.Pointer when ScreenCodec.TryDecodePointer(span, out var action, out var button, out var x, out var y, out var wheel):
                source.OnPointer(action, button, x, y, wheel);
                break;

            case ScreenMessageType.Key when ScreenCodec.TryDecodeKey(span, out var vk, out var scan, out var up, out var extended):
                source.OnKey(vk, scan, up, extended);
                break;

            case ScreenMessageType.Crop when ScreenCodec.TryDecodeCrop(span, out var crop, out var cropId):
                source.OnCrop(crop, cropId);
                break;

            case ScreenMessageType.Touch when ScreenCodec.TryDecodeTouch(span, out var touchAction, out var contact, out var tx, out var ty):
                source.OnTouch(touchAction, contact, tx, ty);
                break;

            case ScreenMessageType.Text when ScreenCodec.TryDecodeText(span, out var text):
                source.OnText(text);
                break;

            case ScreenMessageType.Window when ScreenCodec.TryDecodeWindowCommand(span, out var command):
                source.OnWindowCommand(command);
                break;

            case ScreenMessageType.Size when ScreenCodec.TryDecodeSize(span, out var size):
                source.OnViewerSize(size);
                break;

            case ScreenMessageType.UdpReady:
                source.OnUdpReady();
                break;

            case ScreenMessageType.Refresh:
                source.OnRefreshRequested();
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A UDP media datagram: the pictures of a stream whose source has moved them off the connection, or one of
    /// the probes that asked whether it could. Only the stream's own key opens it, so a datagram for someone else's
    /// stream — or a forged one — goes no further than the viewer it claims to belong to.
    /// </summary>
    private void OnMediaPacket(IPEndPoint from, ReadOnlySpan<byte> packet)
    {
        if (!MediaCipher.TryReadHeader(packet, out var kind, out var streamId, out _) || LoseMediaForTest?.Invoke(kind) == true)
        {
            return;
        }

        if (kind is VideoLink.FrameKind or VideoLink.ProbeKind)
        {
            if (_viewers.TryGetValue(streamId, out var viewer))
            {
                viewer.OnMediaPacket(kind, packet);
            }
        }
        else if (kind is ViewerReplies.AckKind or ViewerReplies.ResendKind && _sources.TryGetValue(streamId, out var source))
        {
            // What a viewer says back by datagram (see ViewerReplies). Only the stream's own key opens it.
            source.OnMediaPacket(packet);
        }
    }

    /// <summary>
    /// For tests: asked for each media datagram that arrives here, by its kind, and true loses it. A loopback
    /// network loses nothing, and what this is for is finding out what happens when a network does.
    /// </summary>
    internal Func<byte, bool>? LoseMediaForTest { get; set; }

    private bool TryGetReady(string peerId, out PeerSession session) =>
        _node.Sessions.TryGetSession(peerId, out session) && session.State == SessionState.Ready;

    private static uint NewStreamId() => (uint)Random.Shared.Next(1, int.MaxValue);

    /// <summary>Safe to call more than once: the app disposes it itself, then its service container does again.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        _node.MediaPacketReceived -= OnMediaPacket;
        WinMirroring.Input.Screen.MonitorCatalog.Changed -= OnMonitorsChanged;
        if (_input is not null)
        {
            _input.FocusChanged -= OnFocusChanged;
        }

        _watcher?.Dispose();
        foreach (var id in _sources.Keys.ToList())
        {
            await StopSourceAsync(id, "shutdown").ConfigureAwait(false);
        }

        // Do not leave an empty monitor on the desktop when the app exits; bring parked windows home first.
        if (_displayDevice is { } device)
        {
            RestoreMovedWindows();
            Display.VirtualDisplay.Detach(device);
        }

        foreach (var viewer in _viewers.Values.ToList())
        {
            await viewer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
