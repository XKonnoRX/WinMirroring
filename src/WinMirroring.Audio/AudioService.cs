using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using WinMirroring.Audio.Playback;
using WinMirroring.Audio.Streams;
using WinMirroring.Core;
using WinMirroring.Core.Media;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;

namespace WinMirroring.Audio;

public sealed record IncomingAudioStatus(string PeerName, double BufferedMs, long Lost, long Underruns);

public sealed record AudioStatus(string? SendingTo, string? CaptureMode, IReadOnlyList<IncomingAudioStatus> Receiving);

/// <summary>
/// Decides where this device's sound goes and plays what others send. By default sound follows input: while
/// another device controls this one with its mouse and keyboard, this device's sound plays there (and the local
/// speakers are muted). A pinned target overrides that. Stopping is delayed a little so quick cursor trips across
/// an edge do not chop the sound.
/// </summary>
public sealed class AudioService : IAsyncDisposable
{
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(2);

    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly AudioPlayback _playback;
    private readonly LocalMuteGuard _mute;
    private readonly ConcurrentDictionary<uint, IncomingAudioStream> _incoming = new();
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _focusGate = new();

    private AudioSender? _sender;
    private string? _focusTarget;
    private CancellationTokenSource? _pendingStop;

    public AudioService(MirroringNode node, ILogger<AudioService> logger)
    {
        _node = node;
        _logger = logger;
        _playback = new AudioPlayback(logger);
        _mute = new LocalMuteGuard(node.Settings.Audio, node.SaveSettings, logger);
    }

    public event Action? StatusChanged;

    public void Start()
    {
        // A previous run may have crashed while holding the speakers muted.
        _mute.Restore();

        _node.Sessions.SessionReady += OnSessionReady;
        _node.Sessions.SessionClosed += OnSessionClosed;
        _node.MediaPacketReceived += OnMediaPacket;
        foreach (var session in _node.Sessions.Sessions)
        {
            if (session.State == SessionState.Ready)
            {
                session.ControlMessageReceived += OnControlMessage;
            }
        }

        RequestReevaluate();
    }

    /// <summary>Called when another device starts (peer id) or stops (null) controlling this one.</summary>
    public void SetFocusTarget(string? peerId)
    {
        lock (_focusGate)
        {
            _pendingStop?.Cancel();
            _pendingStop = null;
            if (peerId is not null)
            {
                _focusTarget = peerId;
                RequestReevaluate();
                return;
            }

            var pending = new CancellationTokenSource();
            _pendingStop = pending;
            _ = Task.Delay(StopDelay, pending.Token).ContinueWith(
                _ =>
                {
                    lock (_focusGate)
                    {
                        if (_pendingStop != pending)
                        {
                            return;
                        }

                        _pendingStop = null;
                        _focusTarget = null;
                    }

                    RequestReevaluate();
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }

    /// <summary>Re-reads audio settings (target, bitrate, buffer).</summary>
    public void ApplySettings()
    {
        foreach (var stream in _incoming.Values)
        {
            stream.Buffer.TargetMs = _node.Settings.Audio.BufferMs;
        }

        RequestReevaluate(restart: true);
    }

    public AudioStatus GetStatus()
    {
        var sender = _sender;
        var receiving = _incoming.Values
            .Select(s => new IncomingAudioStatus(s.PeerName, s.Buffer.BufferedMs, s.Lost, s.Buffer.Underruns))
            .ToList();
        return new AudioStatus(sender?.Target.PeerName, sender?.CaptureDescription, receiving);
    }

    // ---- routing -----------------------------------------------------------------------------------------

    private void RequestReevaluate(bool restart = false) => _ = ReevaluateAsync(restart);

    private PeerSession? DesiredTarget()
    {
        var settings = _node.Settings.Audio;
        if (!settings.SendEnabled)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(settings.PinnedTargetId) && TryGetReady(settings.PinnedTargetId, out var pinned))
        {
            return pinned;
        }

        string? focus;
        lock (_focusGate)
        {
            focus = _focusTarget;
        }

        return settings.FollowInput && focus is not null && TryGetReady(focus, out var followed) ? followed : null;
    }

    private bool TryGetReady(string peerId, out PeerSession session) =>
        _node.Sessions.TryGetSession(peerId, out session) && session.State == SessionState.Ready;

    private async Task ReevaluateAsync(bool restart)
    {
        try
        {
            await _routeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var desired = DesiredTarget();
            if (!restart && ReferenceEquals(_sender?.Target, desired))
            {
                return;
            }

            if (_sender is { } current)
            {
                _sender = null;
                await current.DisposeAsync().ConfigureAwait(false);
                _mute.Restore();
            }

            if (desired is not null && !_cts.IsCancellationRequested)
            {
                try
                {
                    var settings = _node.Settings.Audio;
                    _sender = await AudioSender.StartAsync(_node, desired, settings.BitrateKbps, () => _playback.IsPlaying, _logger).ConfigureAwait(false);
                    if (settings.MuteLocalWhileSending)
                    {
                        _mute.Mute();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Cannot start sending sound to {Peer}", desired.PeerName);
                }
            }
        }
        finally
        {
            _routeGate.Release();
            StatusChanged?.Invoke();
        }
    }

    // ---- receiving ---------------------------------------------------------------------------------------

    private void OnSessionReady(PeerSession session)
    {
        session.ControlMessageReceived += OnControlMessage;
        RequestReevaluate();
    }

    private void OnSessionClosed(PeerSession session)
    {
        session.ControlMessageReceived -= OnControlMessage;
        foreach (var stream in _incoming.Values.Where(s => ReferenceEquals(s.Owner, session)).ToList())
        {
            RemoveIncoming(stream.StreamId);
        }

        if (ReferenceEquals(_sender?.Target, session))
        {
            RequestReevaluate(restart: true);
        }
    }

    private void OnControlMessage(PeerSession session, ControlMessage message)
    {
        switch (message)
        {
            case AudioStartMessage start:
                StartIncoming(session, start);
                break;

            case AudioStopMessage stop when _incoming.TryGetValue(stop.StreamId, out var stream) && ReferenceEquals(stream.Owner, session):
                RemoveIncoming(stop.StreamId);
                break;
        }
    }

    private void StartIncoming(PeerSession session, AudioStartMessage start)
    {
        if (!_node.Settings.Audio.ReceiveEnabled)
        {
            _logger.LogInformation("Ignoring sound from {Peer}: receiving is off", session.PeerName);
            return;
        }

        if (start.Codec != AudioFormat.Codec || start.SampleRate != AudioFormat.SampleRate || start.Channels != AudioFormat.Channels)
        {
            _logger.LogWarning("Unsupported sound stream from {Peer}: {Codec} {Rate} Hz x{Channels}", session.PeerName, start.Codec, start.SampleRate, start.Channels);
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(start.Key);
        }
        catch (FormatException)
        {
            return;
        }

        if (key.Length != MediaCipher.KeySize)
        {
            return;
        }

        RemoveIncoming(start.StreamId);
        var stream = new IncomingAudioStream(session, session.RemoteEndPoint.Address, session.PeerName, start.StreamId, key, _node.Settings.Audio.BufferMs);
        _incoming[start.StreamId] = stream;
        _playback.Add(stream.Buffer);
        _logger.LogInformation("Playing sound from {Peer} (stream {Stream})", session.PeerName, start.StreamId);
        StatusChanged?.Invoke();
    }

    private void RemoveIncoming(uint streamId)
    {
        if (_incoming.TryRemove(streamId, out var stream))
        {
            _playback.Remove(stream.Buffer);
            stream.Dispose();
            _logger.LogInformation("Sound from {Peer} ended ({Packets} packets, {Lost} lost, {Underruns} underruns)",
                stream.PeerName, stream.Packets, stream.Lost, stream.Buffer.Underruns);
            StatusChanged?.Invoke();
        }
    }

    private void OnMediaPacket(IPEndPoint from, ReadOnlySpan<byte> packet)
    {
        if (MediaCipher.TryReadHeader(packet, out var kind, out var streamId, out var sequence)
            && kind == AudioFormat.MediaKind
            && _incoming.TryGetValue(streamId, out var stream))
        {
            stream.OnPacket(from, packet, sequence);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _node.Sessions.SessionReady -= OnSessionReady;
        _node.Sessions.SessionClosed -= OnSessionClosed;
        _node.MediaPacketReceived -= OnMediaPacket;
        await _cts.CancelAsync().ConfigureAwait(false);

        await _routeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sender is { } sender)
            {
                _sender = null;
                await sender.DisposeAsync().ConfigureAwait(false);
            }

            _mute.Restore();
        }
        finally
        {
            _routeGate.Release();
        }

        foreach (var id in _incoming.Keys.ToList())
        {
            RemoveIncoming(id);
        }

        await _playback.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
