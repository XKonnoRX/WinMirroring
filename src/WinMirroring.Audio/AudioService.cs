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

/// <param name="AimedMs">What the playout buffer is aiming to hold, which grows with the measured jitter.</param>
/// <param name="Recovered">Lost packets rebuilt from the spare copy the next packet carried, so never heard.</param>
public sealed record IncomingAudioStatus(
    string PeerId,
    string PeerName,
    double BufferedMs,
    long Lost,
    long Underruns,
    int AimedMs = 0,
    double JitterMs = 0,
    long Recovered = 0);

/// <param name="SendingTo">Name of the device this device's sound plays on, or null when it plays here.</param>
/// <param name="SpeakersMuted">This app holds this device's speakers muted (its sound plays elsewhere).</param>
public sealed record AudioStatus(
    string? SendingTo,
    string? SendingToId,
    AudioRouteReason? Reason,
    string? CaptureMode,
    bool SpeakersMuted,
    IReadOnlyList<IncomingAudioStatus> Receiving);

/// <summary>What this device wants for another device's sound here (see <see cref="AudioService.SetIncomingChoiceAsync"/>).</summary>
public enum IncomingSoundChoice
{
    /// <summary>Plays here when that device sends it on its own (this device watches or controls it).</summary>
    Automatic,

    /// <summary>Asked for: that device sends its sound here.</summary>
    Always,

    /// <summary>Refused: that device keeps its sound, even when it would send it here on its own.</summary>
    Never,
}

/// <summary>
/// Decides where this device's sound goes and plays what others send (rules in <see cref="AudioRouting"/>). By
/// default the sound follows input, then screen views; a user's choice here ("always on …") or there ("play it
/// here", "never here") comes first. Two devices never send to each other on their own, and the speakers are muted
/// only while this device's sound plays elsewhere and nothing from another device plays here, so neither device
/// ends up silent with its sound playing into the other one's muted speakers.
/// </summary>
public sealed class AudioService : IAsyncDisposable
{
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan YieldHold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private readonly MirroringNode _node;
    private readonly ILogger _logger;
    private readonly AudioPlayback _playback;
    private readonly LocalMuteGuard _mute;
    private readonly ConcurrentDictionary<uint, IncomingAudioStream> _incoming = new();
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _muteGate = new();

    // Routing state, under _stateGate.
    private readonly object _stateGate = new();
    private readonly HashSet<string> _listeners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedBy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listeningTo = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedWhileOff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _yielded = new(StringComparer.Ordinal);
    private string? _focusTarget;
    private List<string> _viewers = [];
    private CancellationTokenSource? _pendingStop;

    private AudioSender? _sender;
    private int _reevaluateQueued;
    private int _disposed;

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
        // A previous run may have ended while holding the speakers muted.
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
        _ = ReportLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Writes what the sound is actually doing to the log every ten seconds, for as long as any is flowing. The
    /// numbers were all there already but only ever shown in a status line, which is no use at all afterwards:
    /// when the sound breaks up, whether it was losses, an uneven link or a device that could not keep up should
    /// be in the log of both devices rather than something to be guessed at later.
    /// </summary>
    private async Task ReportLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ReportEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var stream in _incoming.Values)
                {
                    var total = stream.Packets + stream.Lost;
                    _logger.LogInformation(
                        "Sound from {Peer}: {Packets} packets, {Percent:F2}% lost ({Recovered} rebuilt), spread {Jitter:F1} ms, buffer {Buffered:F0} of {Aimed} ms, {Underruns} underruns, {Trims} trims",
                        stream.PeerName,
                        stream.Packets,
                        total == 0 ? 0 : stream.Lost * 100.0 / total,
                        stream.Recovered,
                        stream.JitterMs,
                        stream.Buffer.BufferedMs,
                        stream.Buffer.AimedMs,
                        stream.Buffer.Underruns,
                        stream.Buffer.Trims);
                }

                if (Volatile.Read(ref _sender) is { } sender)
                {
                    _logger.LogInformation(
                        "Sound to {Peer}: {Packets} packets sent, {Dropped} frames dropped before encoding, {Encode:F2} ms to encode a {FrameMs} ms frame, spare frame {Spare}",
                        sender.Target.PeerName,
                        sender.SentPackets,
                        sender.DroppedFrames,
                        sender.EncodeMeanMs,
                        AudioFormat.FrameMs,
                        sender.SendsSpareFrames ? "on" : "off");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Called when another device starts (peer id) or stops (null) controlling this one.</summary>
    public void SetFocusTarget(string? peerId)
    {
        lock (_stateGate)
        {
            _pendingStop?.Cancel();
            _pendingStop = null;
            if (peerId is not null)
            {
                if (_focusTarget != peerId)
                {
                    _focusTarget = peerId;
                    BumpLocked(peerId);
                }

                RequestReevaluate();
                return;
            }

            // Quick cursor trips across an edge must not chop the sound: stop following a moment later.
            var pending = new CancellationTokenSource();
            _pendingStop = pending;
            _ = Task.Delay(StopDelay, pending.Token).ContinueWith(
                _ =>
                {
                    lock (_stateGate)
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

    /// <summary>Devices currently watching one of this device's monitors.</summary>
    public void SetViewers(IReadOnlyCollection<string> peerIds)
    {
        lock (_stateGate)
        {
            var current = new List<string>(_viewers.Where(peerIds.Contains));
            foreach (var peerId in peerIds)
            {
                if (!current.Contains(peerId))
                {
                    // A new view is a new reason: it may take the sound even from a device this one gave way to.
                    current.Add(peerId);
                    BumpLocked(peerId);
                }
            }

            _viewers = current;
        }

        RequestReevaluate();
    }

    /// <summary>True while another device's sound plays here.</summary>
    public bool IsReceivingFrom(string peerId) => _incoming.Values.Any(s => s.PeerId == peerId);

    public IncomingSoundChoice IncomingChoice(string peerId)
    {
        lock (_stateGate)
        {
            return _refusing.Contains(peerId) ? IncomingSoundChoice.Never
                : _listeningTo.Contains(peerId) ? IncomingSoundChoice.Always
                : IncomingSoundChoice.Automatic;
        }
    }

    /// <summary>
    /// Tells <paramref name="peerId"/> what to do with its sound: send it here (always), keep it (never, which also
    /// stops it here at once), or decide on its own (automatic). Lasts until changed or the connection drops.
    /// </summary>
    public async Task<bool> SetIncomingChoiceAsync(string peerId, IncomingSoundChoice choice)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return false;
        }

        lock (_stateGate)
        {
            _refusing.Remove(peerId);
            _listeningTo.Remove(peerId);
            _refusedWhileOff.Remove(peerId);
            if (choice == IncomingSoundChoice.Never)
            {
                _refusing.Add(peerId);
            }
            else if (choice == IncomingSoundChoice.Always)
            {
                _listeningTo.Add(peerId);
            }
        }

        if (choice == IncomingSoundChoice.Never)
        {
            RemoveIncomingFrom(peerId);
        }

        _logger.LogInformation("Sound of {Peer} here: {Choice}", session.PeerName, choice);
        var sent = await SendQuietlyAsync(session, new AudioRouteRequestMessage(
            Listen: choice == IncomingSoundChoice.Always,
            Refuse: choice == IncomingSoundChoice.Never)).ConfigureAwait(false);
        StatusChanged?.Invoke();
        return sent;
    }

    /// <summary>Re-reads audio settings (target, bitrate, buffer, receiving, muting).</summary>
    public void ApplySettings()
    {
        var settings = _node.Settings.Audio;
        foreach (var stream in _incoming.Values)
        {
            stream.Buffer.TargetMs = settings.BufferMs;
        }

        if (!settings.ReceiveEnabled)
        {
            foreach (var stream in _incoming.Values.ToList())
            {
                if (stream.Owner is PeerSession owner)
                {
                    RefuseBecauseReceivingIsOff(owner);
                }

                RemoveIncoming(stream.StreamId);
            }
        }
        else
        {
            List<string> withdraw;
            lock (_stateGate)
            {
                withdraw = _refusedWhileOff.ToList();
                _refusedWhileOff.Clear();
            }

            foreach (var peerId in withdraw)
            {
                if (TryGetReady(peerId, out var session))
                {
                    _ = SendQuietlyAsync(session, new AudioRouteRequestMessage(Listen: false));
                }
            }
        }

        RequestReevaluate();
        UpdateMute();
    }

    public AudioStatus GetStatus()
    {
        var sender = Volatile.Read(ref _sender);
        var receiving = _incoming.Values
            .Select(s => new IncomingAudioStatus(
                s.PeerId, s.PeerName, s.Buffer.BufferedMs, s.Lost, s.Buffer.Underruns, s.Buffer.AimedMs, s.JitterMs, s.Recovered))
            .ToList();
        return new AudioStatus(sender?.Target.PeerName, sender?.Target.PeerId, sender?.Reason, sender?.CaptureDescription, _mute.IsMuted, receiving);
    }

    // ---- routing -----------------------------------------------------------------------------------------

    /// <summary>Several triggers in a row (views, focus, messages) are decided once, a moment later.</summary>
    private void RequestReevaluate()
    {
        if (Interlocked.Exchange(ref _reevaluateQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Delay(ReevaluateDelay).ContinueWith(
            _ =>
            {
                Volatile.Write(ref _reevaluateQueued, 0);
                return ReevaluateAsync();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private AudioRouteInputs BuildInputs()
    {
        var settings = _node.Settings.Audio;
        lock (_stateGate)
        {
            var yielded = _yielded
                .Where(y => _generation.GetValueOrDefault(y.Key) == y.Value)
                .Select(y => y.Key)
                .ToHashSet(StringComparer.Ordinal);
            return new AudioRouteInputs
            {
                SendEnabled = settings.SendEnabled,
                Pinned = settings.PinnedTargetId,
                FollowInput = settings.FollowInput,
                FollowViewer = settings.FollowScreenViewer,
                Listeners = _listeners.ToList(),
                RefusedBy = _refusedBy.ToList(),
                Focus = _focusTarget,
                Viewers = _viewers.ToList(),
                IsReady = peerId => TryGetReady(peerId, out _),
                IsYielded = yielded.Contains,
                SendsHereByChoice = peerId => _incoming.Values.Any(s => s.PeerId == peerId && !s.Automatic),
            };
        }
    }

    private bool TryGetReady(string peerId, out PeerSession session) =>
        _node.Sessions.TryGetSession(peerId, out session) && session.State == SessionState.Ready;

    private async Task ReevaluateAsync()
    {
        try
        {
            await _routeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            var route = AudioRouting.Choose(BuildInputs());
            PeerSession? desired = null;
            if (route is { } chosen && TryGetReady(chosen.PeerId, out var session))
            {
                desired = session;
            }

            var bitrate = _node.Settings.Audio.BitrateKbps;
            var current = _sender;
            if (current is not null && desired is not null && ReferenceEquals(current.Target, desired) && current.BitrateKbps == bitrate)
            {
                // Same device: at most the reason changed (the receiver needs to know whether it is automatic).
                try
                {
                    await current.UpdateReasonAsync(route!.Value.Reason).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not tell {Peer} why the sound goes there", desired.PeerName);
                }

                return;
            }

            if (current is null && desired is null)
            {
                return;
            }

            if (current is not null)
            {
                Volatile.Write(ref _sender, null);
                await current.DisposeAsync().ConfigureAwait(false);
                if (desired is null)
                {
                    _logger.LogInformation("Sound of this device plays here again");
                }
            }

            if (desired is not null && !_cts.IsCancellationRequested)
            {
                try
                {
                    var started = await AudioSender.StartAsync(_node, desired, bitrate, route!.Value.Reason, () => _playback.IsPlaying, _logger).ConfigureAwait(false);
                    Volatile.Write(ref _sender, started);
                    YieldIfTheyStartedFirst(started);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Cannot start sending sound to {Peer}", desired.PeerName);
                }
            }
        }
        finally
        {
            UpdateMute();
            _routeGate.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Their sound arrived while this device's own stream to them was still starting: the same rule as for a start
    /// that arrives later (see <see cref="AudioRouting.ShouldYield"/>), with the ages the other way round.
    /// </summary>
    private void YieldIfTheyStartedFirst(AudioSender sender)
    {
        var theirs = _incoming.Values.FirstOrDefault(s => s.PeerId == sender.Target.PeerId);
        if (theirs is not null)
        {
            ConsiderYielding(sender, theirs);
        }
    }

    /// <summary>Gives way to the device this one sends to, when it now sends its own sound here (and wins).</summary>
    private void ConsiderYielding(AudioSender sender, IncomingAudioStream theirs)
    {
        var now = Environment.TickCount64;
        var yield = AudioRouting.ShouldYield(
            sender.Automatic,
            theirs.Automatic,
            TimeSpan.FromMilliseconds(now - sender.StartedTicks),
            TimeSpan.FromMilliseconds(now - theirs.StartedTicks),
            _node.Identity.Id,
            theirs.PeerId);
        if (!yield)
        {
            return;
        }

        lock (_stateGate)
        {
            _yielded[theirs.PeerId] = _generation.GetValueOrDefault(theirs.PeerId);
        }

        _logger.LogInformation("{Peer} now sends its sound here, so this device's sound stays here", theirs.PeerName);
        RequestReevaluate();
    }

    private void BumpLocked(string peerId) => _generation[peerId] = _generation.GetValueOrDefault(peerId) + 1;

    /// <summary>
    /// Speakers off only while this device's sound plays elsewhere and nothing from another device plays here:
    /// otherwise the sound that does play here would be silenced too.
    /// </summary>
    private void UpdateMute()
    {
        lock (_muteGate)
        {
            var mute = Volatile.Read(ref _sender) is not null
                && _incoming.IsEmpty
                && _node.Settings.Audio.MuteLocalWhileSending
                && Volatile.Read(ref _disposed) == 0;
            if (mute)
            {
                _mute.Mute();
            }
            else
            {
                _mute.Restore();
            }
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
        lock (_stateGate)
        {
            _listeners.Remove(session.PeerId);
            _refusedBy.Remove(session.PeerId);
            _listeningTo.Remove(session.PeerId);
            _refusing.Remove(session.PeerId);
            _refusedWhileOff.Remove(session.PeerId);
            _yielded.Remove(session.PeerId);
        }

        foreach (var stream in _incoming.Values.Where(s => ReferenceEquals(s.Owner, session)).ToList())
        {
            RemoveIncoming(stream.StreamId);
        }

        RequestReevaluate();
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

            case AudioRouteRequestMessage route:
                OnRouteRequest(session, route);
                break;

            case AudioLossReportMessage report
                when Volatile.Read(ref _sender) is { } sender && sender.StreamId == report.StreamId && ReferenceEquals(sender.Target, session):
                sender.OnLossReported(report.PercentLost);
                break;
        }
    }

    /// <summary>
    /// Tells the sender how much of its stream is going missing here, so it can decide whether to carry a spare
    /// copy of each frame. Raised from the receive path, so the message goes out on a thread of its own.
    /// </summary>
    private void OnLossMeasured(IncomingAudioStream stream, double percentLost)
    {
        if (stream.Owner is not PeerSession session)
        {
            return;
        }

        _ = SendQuietlyAsync(session, new AudioLossReportMessage(stream.StreamId, percentLost, stream.JitterMs));
    }

    private void OnRouteRequest(PeerSession session, AudioRouteRequestMessage route)
    {
        lock (_stateGate)
        {
            _listeners.Remove(session.PeerId);
            _refusedBy.Remove(session.PeerId);
            if (route.Listen)
            {
                _listeners.Add(session.PeerId);
            }
            else if (route.Refuse)
            {
                _refusedBy.Add(session.PeerId);
            }
        }

        _logger.LogInformation(
            route.Listen ? "{Peer} asked for this device's sound"
            : route.Refuse ? "{Peer} does not want this device's sound"
            : "{Peer} leaves this device's sound to the automatic choice",
            session.PeerName);
        RequestReevaluate();
    }

    private void StartIncoming(PeerSession session, AudioStartMessage start)
    {
        bool refusing;
        lock (_stateGate)
        {
            refusing = _refusing.Contains(session.PeerId);
        }

        if (!_node.Settings.Audio.ReceiveEnabled || refusing)
        {
            // Say so: otherwise that device would mute its own speakers for sound nobody hears.
            if (refusing)
            {
                _ = SendQuietlyAsync(session, new AudioRouteRequestMessage(Listen: false, Refuse: true));
                _logger.LogInformation("Not playing sound from {Peer}: turned off here for that device", session.PeerName);
            }
            else
            {
                RefuseBecauseReceivingIsOff(session);
            }

            return;
        }

        if (_incoming.TryGetValue(start.StreamId, out var existing))
        {
            if (ReferenceEquals(existing.Owner, session) && existing.Key == start.Key)
            {
                // The same stream again: only the reason changed.
                existing.Automatic = start.Automatic;
                ConsiderYieldingTo(existing);
                return;
            }

            RemoveIncoming(start.StreamId);
        }

        if (start.Codec != AudioFormat.Codec || start.SampleRate != AudioFormat.SampleRate || start.Channels != AudioFormat.Channels
            || start.FrameMs is < 5 or > 60)
        {
            _logger.LogWarning(
                "Unsupported sound stream from {Peer}: {Codec} {Rate} Hz x{Channels}, {FrameMs} ms frames",
                session.PeerName, start.Codec, start.SampleRate, start.Channels, start.FrameMs);
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

        var stream = new IncomingAudioStream(session, session.RemoteEndPoint.Address, session.PeerName, start.StreamId, key, _node.Settings.Audio.BufferMs, start.FrameMs)
        {
            PeerId = session.PeerId,
            Key = start.Key,
            Automatic = start.Automatic,
        };
        stream.LossMeasured += OnLossMeasured;
        _incoming[start.StreamId] = stream;
        _playback.Add(stream.Buffer);
        _logger.LogInformation("Playing sound from {Peer} (stream {Stream}, {Kind})", session.PeerName, start.StreamId, start.Automatic ? "automatic" : "chosen by a user");

        // Sound plays here now: the speakers must be on, and this device may have to stop sending its own back.
        UpdateMute();
        ConsiderYieldingTo(stream);
        RequestReevaluate();
        StatusChanged?.Invoke();
    }

    private void ConsiderYieldingTo(IncomingAudioStream theirs)
    {
        if (Volatile.Read(ref _sender) is { } sender && sender.Target.PeerId == theirs.PeerId)
        {
            ConsiderYielding(sender, theirs);
        }
    }

    private void RefuseBecauseReceivingIsOff(PeerSession session)
    {
        lock (_stateGate)
        {
            _refusedWhileOff.Add(session.PeerId);
        }

        _ = SendQuietlyAsync(session, new AudioRouteRequestMessage(Listen: false, Refuse: true));
        _logger.LogInformation("Not playing sound from {Peer}: receiving sound is off here", session.PeerName);
    }

    private void RemoveIncomingFrom(string peerId)
    {
        foreach (var stream in _incoming.Values.Where(s => s.PeerId == peerId).ToList())
        {
            RemoveIncoming(stream.StreamId);
        }
    }

    private void RemoveIncoming(uint streamId)
    {
        if (!_incoming.TryRemove(streamId, out var stream))
        {
            return;
        }

        stream.LossMeasured -= OnLossMeasured;
        _playback.Remove(stream.Buffer);
        stream.Dispose();
        _logger.LogInformation(
            "Sound from {Peer} ended ({Packets} packets, {Lost} lost, {Recovered} of those rebuilt, {Underruns} underruns, {Trims} trims)",
            stream.PeerName, stream.Packets, stream.Lost, stream.Recovered, stream.Buffer.Underruns, stream.Buffer.Trims);

        UpdateMute();
        StatusChanged?.Invoke();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // This device gave way while that device's sound played here. Once it stops (and does not start again
        // right away, as on a restart), this device's own sound may go there again.
        var peerId = stream.PeerId;
        _ = Task.Delay(YieldHold).ContinueWith(
            _ =>
            {
                if (IsReceivingFrom(peerId))
                {
                    return;
                }

                bool cleared;
                lock (_stateGate)
                {
                    cleared = _yielded.Remove(peerId);
                }

                if (cleared)
                {
                    RequestReevaluate();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
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

    private async Task<bool> SendQuietlyAsync(PeerSession session, ControlMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await session.SendControlAsync(message, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Message} not sent to {Peer}", message.GetType().Name, session.PeerName);
            return false;
        }
    }

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
        await _cts.CancelAsync().ConfigureAwait(false);

        // A sender that is still starting holds the gate; do not wait for it forever on the way out.
        var gated = await _routeGate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _sender) is { } sender)
            {
                Volatile.Write(ref _sender, null);
                await sender.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (gated)
            {
                _routeGate.Release();
            }
        }

        foreach (var id in _incoming.Keys.ToList())
        {
            RemoveIncoming(id);
        }

        lock (_muteGate)
        {
            _mute.Restore();
        }

        await _playback.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
