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
/// One outgoing stream: capture → Opus (10 ms) → AES-GCM → UDP to the peer's media port.
/// Long stretches of digital silence are not sent at all; the receiver simply plays silence when its buffer runs dry.
/// </summary>
internal sealed class AudioSender : IAsyncDisposable
{
    private const int SilentFramesBeforePause = 20;

    private readonly MirroringNode _node;
    private readonly PeerSession _target;
    private readonly AudioCapture _capture;
    private readonly IOpusEncoder _encoder;
    private readonly MediaCipher _cipher;
    private readonly string _key;
    private readonly ILogger _logger;
    private readonly Func<bool> _suspended;
    private readonly byte[] _plain = new byte[4 + AudioFormat.MaxOpusPacket];
    private readonly byte[] _packet = new byte[MediaCipher.Overhead + 4 + AudioFormat.MaxOpusPacket];

    private ulong _sequence;
    private uint _timestamp;
    private int _silentFrames;
    private long _sentPackets;

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

    private AudioStartMessage StartMessage() =>
        new(StreamId, _key, AudioFormat.Codec, AudioFormat.SampleRate, AudioFormat.Channels, AudioFormat.FrameMs, Automatic);

    /// <summary>Runs on the capture thread.</summary>
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

        int encoded;
        try
        {
            encoded = _encoder.Encode(frame, AudioFormat.FrameSamples, _plain.AsSpan(4), AudioFormat.MaxOpusPacket);
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

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_plain, _timestamp);
        var length = _cipher.Seal(AudioFormat.MediaKind, StreamId, _sequence++, _plain.AsSpan(0, 4 + encoded), _packet);
        if (_node.SendMedia(_target.PeerTransportEndPoint, _packet.AsSpan(0, length)))
        {
            Interlocked.Increment(ref _sentPackets);
        }
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

        _cipher.Dispose();
        _logger.LogInformation("Stopped sending sound to {Peer} ({Packets} packets)", _target.PeerName, SentPackets);
    }
}
