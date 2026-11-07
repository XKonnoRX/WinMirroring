using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Transport;
using WinMirroring.Input;
using WinMirroring.Screen.Capture;
using WinMirroring.Screen.Protocol;

namespace WinMirroring.Screen;

public sealed record ActiveScreenSource(uint StreamId, string PeerName, string Title);

/// <summary>
/// Screen and window streaming between paired devices. A viewer asks a device for one of its monitors; a device
/// pushes one of its windows to another device (from a menu, or by dragging the window across a screen edge).
/// Frames, acknowledgements and remote input travel on the Screen channel of the session.
/// </summary>
public sealed class ScreenStreamingService : IAsyncDisposable
{
    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, ScreenSource> _sources = new();
    private readonly ConcurrentDictionary<uint, ScreenViewer> _viewers = new();
    private readonly ConcurrentDictionary<uint, string> _requested = new();
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

    public IReadOnlyList<ActiveScreenSource> Sources =>
        _sources.Values.Select(s => new ActiveScreenSource(s.StreamId, s.Session.PeerName, s.Title)).ToList();

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
    }

    /// <summary>Sending a window by dragging it across a screen edge (needs input sharing to know where the cursor goes).</summary>
    public void AttachWindowDrag(InputSharingService input)
    {
        _input = input;
        _watcher = new WindowDragWatcher(_logger);
        _watcher.Start();
        input.FocusChanged += OnFocusChanged;
    }

    public static IReadOnlyList<WindowDescription> ShareableWindows() => DisplayCatalog.Windows();

    /// <summary>Asks <paramref name="peerId"/> to stream one of its monitors here.</summary>
    public async Task<bool> RequestScreenAsync(string peerId, int monitorIndex = 0)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return false;
        }

        var streamId = NewStreamId();
        _requested[streamId] = peerId;
        try
        {
            await session.SendControlAsync(new ScreenRequestMessage(streamId, monitorIndex)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _requested.TryRemove(streamId, out _);
            _logger.LogInformation("Screen request to {Peer} failed: {Message}", session.PeerName, ex.Message);
            return false;
        }
    }

    /// <summary>Streams a local window to <paramref name="peerId"/>, which opens it in a window of its own.</summary>
    public async Task<bool> SendWindowAsync(string peerId, nint window)
    {
        if (!_node.Settings.Screen.AllowViewing || !TryGetReady(peerId, out var session) || !Native.ScreenNative.IsWindow(window))
        {
            return false;
        }

        ScreenSource source;
        try
        {
            source = ScreenSource.ForWindow(session, NewStreamId(), window, _node.Settings.Screen, _logger);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning("Window cannot be captured: {Message}", ex.Message);
            return false;
        }

        return await StartSourceAsync(session, source).ConfigureAwait(false);
    }

    public async Task StopSourceAsync(uint streamId, string reason = "stopped")
    {
        if (!_sources.TryRemove(streamId, out var source))
        {
            return;
        }

        try
        {
            await source.Session.SendControlAsync(new ScreenStopMessage(streamId, reason)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        await source.DisposeAsync().ConfigureAwait(false);
        SourcesChanged?.Invoke();
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

        _watcher.MarkSent(moving);
        _logger.LogInformation("Window dragged to {Peer}", peerId);
        _ = SendWindowAsync(peerId, moving);
    }

    // ---- sessions and messages ---------------------------------------------------------------------------

    private void OnSessionReady(PeerSession session)
    {
        session.RegisterHandler(ChannelId.Screen, OnScreenFrameAsync);
        session.ControlMessageReceived += OnControlMessage;
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;
        foreach (var source in _sources.Values.Where(s => ReferenceEquals(s.Session, session)).ToList())
        {
            if (_sources.TryRemove(source.StreamId, out _))
            {
                _ = source.DisposeAsync().AsTask();
            }
        }

        foreach (var viewer in _viewers.Values.Where(v => ReferenceEquals(v.Session, session)).ToList())
        {
            if (_viewers.TryRemove(viewer.StreamId, out _))
            {
                _ = viewer.CloseAsync(notifySource: false, reason: "соединение закрыто");
            }
        }

        SourcesChanged?.Invoke();
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

            case ScreenStopMessage stop:
                if (_sources.TryGetValue(stop.StreamId, out var source) && ReferenceEquals(source.Session, session) && _sources.TryRemove(stop.StreamId, out _))
                {
                    _ = source.DisposeAsync().AsTask();
                    SourcesChanged?.Invoke();
                }

                if (_viewers.TryGetValue(stop.StreamId, out var viewer) && ReferenceEquals(viewer.Session, session) && _viewers.TryRemove(stop.StreamId, out _))
                {
                    _ = viewer.CloseAsync(notifySource: false, reason: stop.Reason);
                }

                _requested.TryRemove(stop.StreamId, out _);
                break;
        }
    }

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
                existing.SwitchMonitor(request.MonitorIndex);
                await session.SendControlAsync(existing.Describe()).ConfigureAwait(false);
                SourcesChanged?.Invoke();
                return;
            }

            var source = ScreenSource.ForMonitor(session, request.StreamId, request.MonitorIndex, _node.Settings.Screen, _logger);
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
            SourcesChanged?.Invoke();
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
        if (!requested && info.Kind != "window")
        {
            return; // monitors are only shown when we asked for them
        }

        var viewer = new ScreenViewer(session, info, _logger);
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

        if (type == ScreenMessageType.Frame)
        {
            if (_viewers.TryGetValue(streamId, out var viewer) && ReferenceEquals(viewer.Session, session))
            {
                viewer.Enqueue(payload.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        if (!_sources.TryGetValue(streamId, out var source) || !ReferenceEquals(source.Session, session))
        {
            return ValueTask.CompletedTask;
        }

        switch (type)
        {
            case ScreenMessageType.Ack:
                source.OnAck();
                break;

            case ScreenMessageType.Pointer when ScreenCodec.TryDecodePointer(span, out var action, out var button, out var x, out var y, out var wheel):
                source.OnPointer(action, button, x, y, wheel);
                break;

            case ScreenMessageType.Key when ScreenCodec.TryDecodeKey(span, out var vk, out var scan, out var up, out var extended):
                source.OnKey(vk, scan, up, extended);
                break;

            case ScreenMessageType.Crop when ScreenCodec.TryDecodeCrop(span, out var crop):
                source.OnCrop(crop);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private bool TryGetReady(string peerId, out PeerSession session) =>
        _node.Sessions.TryGetSession(peerId, out session) && session.State == SessionState.Ready;

    private static uint NewStreamId() => (uint)Random.Shared.Next(1, int.MaxValue);

    public async ValueTask DisposeAsync()
    {
        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        if (_input is not null)
        {
            _input.FocusChanged -= OnFocusChanged;
        }

        _watcher?.Dispose();
        foreach (var id in _sources.Keys.ToList())
        {
            await StopSourceAsync(id, "shutdown").ConfigureAwait(false);
        }

        foreach (var viewer in _viewers.Values.ToList())
        {
            await viewer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
