using Concentus;
using Concentus.Enums;
using Microsoft.Extensions.Logging;
using WinMirroring.Audio.Capture;
using WinMirroring.Core;
using WinMirroring.Core.Media;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;

namespace WinMirroring.Audio.Streams;

/// <summary>
/// One outgoing stream: capture → queue → Opus → AES-GCM → UDP to the peer's media port.
///
/// The encoding does not happen on the capture callback. Opus here is a managed implementation, and at 160 kbit/s
/// of stereo it is real work; doing it (plus the sealing and a blocking send) inside the callback meant that
/// whenever the device could not finish within one frame's worth of time the capture buffer overran and the sound
/// was lost at its source — where no counter on the receiving side would ever show it. Frames now go into a short
/// queue that the capture thread only writes to, and a thread of our own does the rest. When even that falls
/// behind, the oldest frame is dropped rather than capture being held up; the timestamp a frame carries is assigned
/// at capture, so a drop shows up on the other side as the gap it is.
///
/// Long stretches of digital silence are not sent at all; the receiver plays silence when its buffer runs dry.
/// </summary>
internal sealed class AudioSender : IAsyncDisposable
{
    private const int SilentFramesBeforePause = 10;

    /// <summary>About 160 ms of sound. Deeper would only add delay to a device that cannot keep up at all.</summary>
    private const int QueuedFrames = 8;

    /// <summary>Loss at or above this much, as reported by the receiver, turns on the spare frame in each packet.</summary>
    private const double SpareOnPercent = 0.3;

    /// <summary>...and it goes off again after this long with losses below a twentieth of a percent.</summary>
    private static readonly TimeSpan SpareOffAfter = TimeSpan.FromSeconds(30);

    /// <summary>A datagram kept below this stays in one IP packet on any ordinary link, tunnels included.</summary>
    private const int SafeDatagramBytes = 1200;

    private readonly MirroringNode _node;
    private readonly PeerSession _target;
    private readonly AudioCapture _capture;
    private readonly IOpusEncoder _encoder;
    private readonly MediaCipher _cipher;
    private readonly string _key;
    private readonly ILogger _logger;
    private readonly Func<bool> _suspended;

    // Written by the encoding thread only.
    private readonly byte[] _plain = new byte[AudioPacket.HeaderSize + (2 * AudioFormat.MaxOpusPacket)];
    private readonly byte[] _packet = new byte[MediaCipher.Overhead + AudioPacket.HeaderSize + (2 * AudioFormat.MaxOpusPacket)];
    private readonly byte[] _spare = new byte[AudioFormat.MaxOpusPacket];
    private int _spareLength;

    // The queue between the capture thread and the encoding thread.
    private readonly object _queueGate = new();
    private readonly float[][] _queued = new float[QueuedFrames][];
    private readonly uint[] _queuedTimestamps = new uint[QueuedFrames];
    private readonly SemaphoreSlim _ready = new(0);
    private readonly Thread _worker;
    private int _head;
    private int _count;
    private volatile bool _running = true;

    private ulong _sequence;
    private uint _timestamp;
    private int _silentFrames;
    private long _sentPackets;
    private long _droppedFrames;
    private long _cleanSinceTicks;
    private volatile bool _sendSpare;

    private AudioSender(MirroringNode node, PeerSession target, AudioCapture capture, uint streamId, byte[] key, int bitrateKbps, AudioRouteReason reason, Func<bool> suspended, ILogger logger)
    {
        _node = node;
        _target = target;
        _capture = capture;
        _suspended = suspended;
        _logger = logger;
        StreamId = streamId;
        BitrateKbps = bitrateKbps;
        Reason = reason;
        _key = Convert.ToBase64String(key);
        _cipher = new MediaCipher(key);
        _encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = Math.Clamp(bitrateKbps, 32, 510) * 1000;
        _encoder.Complexity = 6;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        _encoder.UseVBR = true;
        for (var i = 0; i < _queued.Length; i++)
        {
            _queued[i] = new float[AudioFormat.FrameValues];
        }

        _worker = new Thread(Work) { IsBackground = true, Name = "audio-encoder", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        _capture.FrameReady += OnFrame;
    }

    public uint StreamId { get; }

    public PeerSession Target => _target;

    public int BitrateKbps { get; }

    /// <summary>Why the sound goes there; may change without restarting the stream (see <see cref="UpdateReasonAsync"/>).</summary>
    public AudioRouteReason Reason { get; private set; }

    public bool Automatic => new AudioRoute(_target.PeerId, Reason).Automatic;

    public long StartedTicks { get; } = Environment.TickCount64;

    public long SentPackets => Interlocked.Read(ref _sentPackets);

    /// <summary>Frames the encoding thread never got to, because it could not keep up with capture.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Each packet currently carries the previous frame as well (the receiver reported losses).</summary>
    public bool SendsSpareFrames => _sendSpare;

    public string CaptureDescription => _capture.Description;

    /// <summary>True when capture also hears this app's own playback (fallback mode).</summary>
    public bool CaptureIncludesOwnPlayback => _capture.IncludesOwnPlayback;

    public static async Task<AudioSender> StartAsync(MirroringNode node, PeerSession target, int bitrateKbps, AudioRouteReason reason, Func<bool> suspended, ILogger logger)
    {
        var capture = await AudioCapture.CreateAsync(logger).ConfigureAwait(false);
        var streamId = (uint)Random.Shared.Next(1, int.MaxValue);
        var key = MediaCipher.GenerateKey();
        var sender = new AudioSender(node, target, capture, streamId, key, bitrateKbps, reason, suspended, logger);
        try
        {
            await target.SendControlAsync(sender.StartMessage()).ConfigureAwait(false);

            // The sound goes out ahead of everything else on this link (see MediaPriority).
            node.PrioritizeMedia(target.PeerTransportEndPoint);
            capture.Start();
        }
        catch
        {
            await sender.DisposeCoreAsync(sendStop: false).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation("Sending sound to {Peer} ({Reason}; stream {Stream}, {Bitrate} kbps, {Capture})", target.PeerName, reason, streamId, bitrateKbps, capture.Description);
        return sender;
    }

    /// <summary>The same stream goes on for another reason: the receiver only learns whether it is automatic now.</summary>
    public async Task UpdateReasonAsync(AudioRouteReason reason)
    {
        if (reason == Reason)
        {
            return;
        }

        Reason = reason;
        await _target.SendControlAsync(StartMessage()).ConfigureAwait(false);
        _logger.LogInformation("Sound to {Peer} now goes there because: {Reason}", _target.PeerName, reason);
    }

    /// <summary>
    /// The receiver says how much of the stream it is losing. Above a few tenths of a percent every packet starts
    /// carrying the previous frame as well, so a single lost datagram stops being a hole in the sound; after a
    /// clean spell it is turned off again, because it costs about twice the bitrate.
    /// </summary>
    public void OnLossReported(double percentLost)
    {
        var now = Environment.TickCount64;
        if (percentLost >= SpareOnPercent)
        {
            _cleanSinceTicks = 0;
            if (!_sendSpare)
            {
                _sendSpare = true;
                _logger.LogInformation(
                    "{Peer} is losing {Percent:F2}% of the sound; every packet now carries the previous frame as well",
                    _target.PeerName, percentLost);
            }

            return;
        }

        if (!_sendSpare || percentLost >= 0.05)
        {
            return;
        }

        if (_cleanSinceTicks == 0)
        {
            _cleanSinceTicks = now;
        }
        else if (now - _cleanSinceTicks >= SpareOffAfter.TotalMilliseconds)
        {
            _sendSpare = false;
            _cleanSinceTicks = 0;
            _logger.LogInformation("Sound to {Peer} is arriving cleanly; the spare frame is switched off", _target.PeerName);
        }
    }

    private AudioStartMessage StartMessage() =>
        new(StreamId, _key, AudioFormat.Codec, AudioFormat.SampleRate, AudioFormat.Channels, AudioFormat.FrameMs, Automatic);

    /// <summary>Runs on the capture thread: nothing here but a copy, so capture is never held up.</summary>
    private void OnFrame(ReadOnlySpan<float> frame)
    {
        _timestamp += AudioFormat.FrameSamples;
        if (_capture.IncludesOwnPlayback && _suspended())
        {
            // Fallback capture hears our own playback: sending now would loop the other device's sound back to it.
            return;
        }

        if (IsDigitalSilence(frame))
        {
            if (++_silentFrames > SilentFramesBeforePause)
            {
                return;
            }
        }
        else
        {
            _silentFrames = 0;
        }

        lock (_queueGate)
        {
            if (_count == QueuedFrames)
            {
                // The encoding thread is behind. Dropping the oldest keeps the delay from growing.
                _head = (_head + 1) % QueuedFrames;
                _count--;
                Interlocked.Increment(ref _droppedFrames);
            }

            var slot = (_head + _count) % QueuedFrames;
            frame.CopyTo(_queued[slot]);
            _queuedTimestamps[slot] = _timestamp;
            _count++;
        }

        try
        {
            _ready.Release();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SemaphoreFullException)
        {
        }
    }

    private void Work()
    {
        while (_running)
        {
            try
            {
                if (!_ready.Wait(100))
                {
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            float[] frame;
            uint timestamp;
            lock (_queueGate)
            {
                if (_count == 0)
                {
                    continue;
                }

                frame = _queued[_head];
                timestamp = _queuedTimestamps[_head];
                _head = (_head + 1) % QueuedFrames;
                _count--;
            }

            if (!_running)
            {
                return;
            }

            Send(frame, timestamp);
        }
    }

    private void Send(float[] frame, uint timestamp)
    {
        int encoded;
        try
        {
            encoded = _encoder.Encode(frame, AudioFormat.FrameSamples, _plain.AsSpan(AudioPacket.HeaderSize), AudioFormat.MaxOpusPacket);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning("Opus encode failed: {Message}", ex.Message);
            return;
        }

        if (encoded <= 0)
        {
            return;
        }

        // The encoder wrote straight into the place the packet layout wants these bytes, so writing the packet
        // copies them onto themselves, which costs nothing and moves nothing.
        var current = _plain.AsSpan(AudioPacket.HeaderSize, encoded);

        // The spare goes along only while the whole datagram still fits in one piece: a packet split across two
        // IP fragments is lost whenever either half is, which is the opposite of what the spare is for.
        var withSpare = MediaCipher.Overhead + AudioPacket.HeaderSize + encoded + _spareLength;
        var spare = _sendSpare && _spareLength > 0 && withSpare <= SafeDatagramBytes
            ? _spare.AsSpan(0, _spareLength)
            : [];
        var length = AudioPacket.Write(_plain, timestamp, current, spare);
        var datagram = _cipher.Seal(AudioFormat.MediaKind, StreamId, _sequence++, _plain.AsSpan(0, length), _packet);
        if (_node.SendMedia(_target.PeerTransportEndPoint, _packet.AsSpan(0, datagram)))
        {
            Interlocked.Increment(ref _sentPackets);
        }

        // Keep this frame as the spare for the next packet, whether or not spares are being sent right now: the
        // switch can come at any moment, and the frame is already encoded.
        current.CopyTo(_spare);
        _spareLength = encoded;
    }

    private static bool IsDigitalSilence(ReadOnlySpan<float> frame)
    {
        foreach (var sample in frame)
        {
            if (sample is > 1e-5f or < -1e-5f)
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(sendStop: true);

    private async ValueTask DisposeCoreAsync(bool sendStop)
    {
        _capture.FrameReady -= OnFrame;
        await _capture.DisposeAsync().ConfigureAwait(false);
        _running = false;
        _ready.Release();
        if (_worker.IsAlive && !_worker.Join(TimeSpan.FromSeconds(1)))
        {
            _logger.LogDebug("The sound encoding thread did not stop in time");
        }

        _node.ReleaseMediaPriority(_target.PeerTransportEndPoint);
        if (sendStop)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _target.SendControlAsync(new AudioStopMessage(StreamId), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        _ready.Dispose();
        _cipher.Dispose();
        _logger.LogInformation(
            "Stopped sending sound to {Peer} ({Packets} packets, {Dropped} frames dropped before encoding)",
            _target.PeerName, SentPackets, DroppedFrames);
    }
}
