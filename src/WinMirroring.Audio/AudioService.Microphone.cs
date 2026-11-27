using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinMirroring.Audio.Capture;
using WinMirroring.Audio.Playback;
using WinMirroring.Audio.Streams;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;

namespace WinMirroring.Audio;

/// <summary>How asking another device to take this one's microphone ended.</summary>
public enum MicrophoneOutcome
{
    /// <summary>It is being sent.</summary>
    Started,

    /// <summary>There is no connection to that device.</summary>
    Unreachable,

    /// <summary>That device did not answer: a version from before this existed, most likely.</summary>
    NotSupported,

    /// <summary>Taking other devices' microphones is switched off there.</summary>
    Refused,

    /// <summary>That device has no virtual cable to play it into (see <see cref="MicrophoneOutput"/>).</summary>
    NoOutputThere,

    /// <summary>This device has no microphone that could be opened.</summary>
    NoMicrophoneHere,

    /// <summary>Something else went wrong; the log says what.</summary>
    Failed,
}

/// <param name="Device">Where it is played there, when it started.</param>
public sealed record MicrophoneResult(MicrophoneOutcome Outcome, string? Device = null);

/// <summary>
/// This device's microphone as a microphone of another device.
///
/// The case it is for: sitting at a tablet that shows the desktop computer's screen, and wanting to be heard in a
/// call that runs on the computer. The tablet's microphone is captured and sent as sound is, and the computer
/// plays it into a virtual cable (see <see cref="MicrophoneOutput"/>) whose other end is the microphone the calling
/// program is pointed at.
///
/// It is kept apart from the routing of a device's own sound, on purpose. That has rules about which of two
/// devices gives way, about muting speakers and about following input, and none of them is about a microphone: a
/// microphone goes where the person at it said, for as long as they say, and nothing else decides. So it has its
/// own sender, its own incoming streams and its own output, and the two only meet in the packet format.
///
/// A microphone is never sent unasked. It starts only when someone at this device says so, and before any sound
/// goes the other device is asked whether it takes microphones at all — a version that does not know the question
/// does not answer it, and so is never sent a stream it would play out of its speakers.
/// </summary>
public sealed partial class AudioService
{
    /// <summary>A voice needs far less than music does; this is generous for one.</summary>
    private const int MicrophoneBitrateKbps = 64;

    private static readonly TimeSpan MicrophoneAnswerWithin = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _micGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, IncomingAudioStream> _micIncoming = new();
    private AudioPlayback _micPlayback = null!;
    private AudioSender? _micSender;

    /// <summary>The device the person here chose; kept across a dropped connection so that it starts again.</summary>
    private string? _micWanted;

    private TaskCompletionSource<MicrophoneAnswerMessage>? _micAnswer;
    private string? _micAnswerFrom;

    /// <summary>In place of the real microphone, for tests: a source of known sound.</summary>
    internal Func<ILogger, IAudioSource>? MicrophoneSourceForTest { get; set; }

    private void InitializeMicrophone()
    {
        _micPlayback = new AudioPlayback(
            _logger,
            enumerator => MicrophoneOutput.Open(enumerator, _node.Settings.Audio.MicrophoneOutput),
            "Microphone output");

        // The cable is an output with a signal on it, which is exactly what the muting of speakers looks for.
        _mute.Skip = () => _micPlayback.DeviceId;
    }

    /// <summary>The device this one's microphone is being sent to, or null.</summary>
    public string? MicrophoneTargetId => Volatile.Read(ref _micSender)?.Target.PeerId;

    public bool IsMicrophoneSentTo(string peerId) => MicrophoneTargetId == peerId;

    /// <summary>True while another device's microphone is played here.</summary>
    public bool IsMicrophoneFrom(string peerId) => _micIncoming.Values.Any(s => s.PeerId == peerId);

    /// <summary>
    /// Sends this device's microphone to <paramref name="peerId"/> (and to nobody else: one microphone, one
    /// place). Lasts until stopped; a connection that drops and comes back picks it up again.
    /// </summary>
    public async Task<MicrophoneResult> SendMicrophoneToAsync(string peerId)
    {
        await _micGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await StopMicrophoneSenderAsync().ConfigureAwait(false);
            var result = await StartMicrophoneAsync(peerId).ConfigureAwait(false);
            _micWanted = result.Outcome == MicrophoneOutcome.Started ? peerId : null;
            return result;
        }
        finally
        {
            _micGate.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Stops sending this device's microphone anywhere.</summary>
    public async Task StopMicrophoneAsync()
    {
        await _micGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _micWanted = null;
            await StopMicrophoneSenderAsync().ConfigureAwait(false);
        }
        finally
        {
            _micGate.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task StopMicrophoneSenderAsync()
    {
        if (Interlocked.Exchange(ref _micSender, null) is { } sender)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<MicrophoneResult> StartMicrophoneAsync(string peerId)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return new MicrophoneResult(MicrophoneOutcome.Unreachable);
        }

        var answer = new TaskCompletionSource<MicrophoneAnswerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            _micAnswer = answer;
            _micAnswerFrom = peerId;
        }

        try
        {
            if (!await SendQuietlyAsync(session, new MicrophoneOfferMessage()).ConfigureAwait(false))
            {
                return new MicrophoneResult(MicrophoneOutcome.Unreachable);
            }

            MicrophoneAnswerMessage reply;
            try
            {
                reply = await answer.Task.WaitAsync(MicrophoneAnswerWithin, _cts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("{Peer} did not say whether it takes a microphone; not sending one", session.PeerName);
                return new MicrophoneResult(MicrophoneOutcome.NotSupported);
            }

            if (!reply.Accepted)
            {
                _logger.LogInformation("{Peer} does not take this device's microphone: {Reason}", session.PeerName, reply.Reason ?? "no reason given");
                return new MicrophoneResult(reply.Reason == MicrophoneNoDevice ? MicrophoneOutcome.NoOutputThere : MicrophoneOutcome.Refused);
            }

            // With the system's processing for calls first (echo of what the speakers play taken out, where the
            // device offers that), and plainly if the microphone will not open that way.
            var opened = false;
            foreach (var forCalls in new[] { true, false })
            {
                IAudioSource source;
                try
                {
                    source = MicrophoneSourceForTest?.Invoke(_logger) ?? AudioCapture.CreateForMicrophone(_logger, forCalls);
                    opened = true;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    _logger.LogInformation("The microphone would not open ({Mode}): {Message}", forCalls ? "as for a call" : "plainly", ex.Message);
                    continue;
                }

                try
                {
                    var sender = await AudioSender
                        .StartAsync(_node, session, MicrophoneBitrateKbps, AudioRouteReason.Pinned, () => false, _logger, source, microphone: true)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _micSender, sender);
                    return new MicrophoneResult(MicrophoneOutcome.Started, reply.Device);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Cannot start sending the microphone to {Peer} ({Mode}): {Message}", session.PeerName, forCalls ? "as for a call" : "plainly", ex.Message);
                }
            }

            return new MicrophoneResult(opened ? MicrophoneOutcome.Failed : MicrophoneOutcome.NoMicrophoneHere);
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_micAnswer, answer))
                {
                    _micAnswer = null;
                    _micAnswerFrom = null;
                }
            }
        }
    }

    // ---- the other side ------------------------------------------------------------------------------------

    private const string MicrophoneOff = "off";
    private const string MicrophoneNoDevice = "no-device";

    /// <summary>Another device asks whether it may send its microphone here.</summary>
    private void OnMicrophoneOffer(PeerSession session)
    {
        // Asking the sound system for its outputs can take a moment; not on the thread that reads the connection.
        _ = Task.Run(async () =>
        {
            var settings = _node.Settings.Audio;
            MicrophoneAnswerMessage answer;
            if (!settings.AcceptMicrophone)
            {
                answer = new MicrophoneAnswerMessage(false, Reason: MicrophoneOff);
            }
            else if (MicrophoneOutput.Pick(MicrophoneOutput.List(), settings.MicrophoneOutput) is { } device)
            {
                answer = new MicrophoneAnswerMessage(true, device.Name);
            }
            else
            {
                answer = new MicrophoneAnswerMessage(false, Reason: MicrophoneNoDevice);
            }

            _logger.LogInformation(
                answer.Accepted ? "{Peer} offers its microphone; it will be played into {Device}" : "{Peer} offers its microphone; refused ({Device})",
                session.PeerName,
                answer.Device ?? answer.Reason);
            await SendQuietlyAsync(session, answer).ConfigureAwait(false);
        });
    }

    private void OnMicrophoneAnswer(PeerSession session, MicrophoneAnswerMessage answer)
    {
        TaskCompletionSource<MicrophoneAnswerMessage>? waiting;
        lock (_stateGate)
        {
            waiting = _micAnswerFrom == session.PeerId ? _micAnswer : null;
        }

        waiting?.TrySetResult(answer);
    }

    /// <summary>The microphone of another device starts arriving: into the cable, not the speakers.</summary>
    private void StartIncomingMicrophone(PeerSession session, AudioStartMessage start)
    {
        if (!_node.Settings.Audio.AcceptMicrophone)
        {
            return; // it was told so when it asked; one that sends anyway is not played
        }

        if (_micIncoming.TryGetValue(start.StreamId, out var same) && ReferenceEquals(same.Owner, session) && same.Key == start.Key)
        {
            return;
        }

        // One microphone from a device at a time.
        foreach (var old in _micIncoming.Values.Where(s => ReferenceEquals(s.Owner, session)).ToList())
        {
            RemoveIncomingMicrophone(old.StreamId);
        }

        if (CreateIncoming(session, start) is not { } stream)
        {
            return;
        }

        stream.LossMeasured += OnLossMeasured;
        _micIncoming[start.StreamId] = stream;
        _micPlayback.Add(stream.Buffer);
        if (_micPlayback.DeviceName is { } device)
        {
            _logger.LogInformation("The microphone of {Peer} is played into {Device} (stream {Stream})", session.PeerName, device, start.StreamId);
        }
        else
        {
            _logger.LogWarning("The microphone of {Peer} arrives, but there is no virtual cable to play it into", session.PeerName);
        }

        StatusChanged?.Invoke();
    }

    private bool RemoveIncomingMicrophone(PeerSession session, uint streamId) =>
        _micIncoming.TryGetValue(streamId, out var stream) && ReferenceEquals(stream.Owner, session) && RemoveIncomingMicrophone(streamId);

    private bool RemoveIncomingMicrophone(uint streamId)
    {
        if (!_micIncoming.TryRemove(streamId, out var stream))
        {
            return false;
        }

        stream.LossMeasured -= OnLossMeasured;
        _micPlayback.Remove(stream.Buffer);
        stream.Dispose();
        _logger.LogInformation(
            "The microphone of {Peer} ended ({Packets} packets, {Lost} lost, {Recovered} of those rebuilt, {Underruns} underruns)",
            stream.PeerName, stream.Packets, stream.Lost, stream.Recovered, stream.Buffer.Underruns);
        StatusChanged?.Invoke();
        return true;
    }

    // ---- what the rest of the service tells this part ------------------------------------------------------

    private void MicrophoneSessionReady(PeerSession session)
    {
        if (_micWanted != session.PeerId)
        {
            return;
        }

        // The connection is back: so is the microphone, as the person left it.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                await _micGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_micWanted == session.PeerId && Volatile.Read(ref _micSender) is null)
                {
                    var result = await StartMicrophoneAsync(session.PeerId).ConfigureAwait(false);
                    _logger.LogInformation("The microphone to {Peer} after the connection came back: {Outcome}", session.PeerName, result.Outcome);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _micGate.Release();
                StatusChanged?.Invoke();
            }
        });
    }

    private void MicrophoneSessionClosed(PeerSession session)
    {
        foreach (var stream in _micIncoming.Values.Where(s => ReferenceEquals(s.Owner, session)).ToList())
        {
            RemoveIncomingMicrophone(stream.StreamId);
        }

        var sender = Volatile.Read(ref _micSender);
        if (sender is not null && ReferenceEquals(sender.Target, session) && ReferenceEquals(Interlocked.CompareExchange(ref _micSender, null, sender), sender))
        {
            _ = sender.DisposeAsync().AsTask();
        }

        lock (_stateGate)
        {
            if (_micAnswerFrom == session.PeerId)
            {
                _micAnswer?.TrySetResult(new MicrophoneAnswerMessage(false));
            }
        }
    }

    private void ApplyMicrophoneSettings()
    {
        var settings = _node.Settings.Audio;
        foreach (var stream in _micIncoming.Values)
        {
            stream.Buffer.TargetMs = settings.BufferMs;
        }

        if (!settings.AcceptMicrophone)
        {
            foreach (var id in _micIncoming.Keys.ToList())
            {
                RemoveIncomingMicrophone(id);
            }

            return;
        }

        // Another output may have been chosen: what is playing moves to it.
        _micPlayback.Reopen();
    }

    private async Task DisposeMicrophoneAsync()
    {
        _micWanted = null;
        await StopMicrophoneSenderAsync().ConfigureAwait(false);
        foreach (var id in _micIncoming.Keys.ToList())
        {
            RemoveIncomingMicrophone(id);
        }

        await _micPlayback.DisposeAsync().ConfigureAwait(false);
    }
}
