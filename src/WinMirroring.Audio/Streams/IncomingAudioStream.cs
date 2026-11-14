using System.Diagnostics;
using System.Net;
using Concentus;
using WinMirroring.Audio.Playback;
using WinMirroring.Core.Media;

namespace WinMirroring.Audio.Streams;

/// <summary>
/// One stream another device sends here: decrypt, reorder guard, Opus decode, and a lost packet rebuilt from the
/// spare copy the next one carries (see <see cref="AudioPacket"/>) or, failing that, guessed at by the decoder.
///
/// It is also where the link is measured. The timestamp every packet carries used to be read and thrown away; it is
/// what tells a real gap from the sender simply going quiet, and comparing it against the arrival time gives the
/// spread the playout buffer has to cover. The share of packets lost is reported back to the sender every couple of
/// seconds, which is what turns the spare copies on and off.
/// </summary>
internal sealed class IncomingAudioStream : IDisposable
{
    private const int MaxConcealedFrames = 5;
    private const int MaxDecodeSamples = 5760;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(2);

    private readonly MediaCipher _cipher;
    private readonly IOpusDecoder _decoder;
    private readonly object _gate = new();
    private readonly byte[] _plain = new byte[8192];
    private readonly float[] _pcm = new float[MaxDecodeSamples * AudioFormat.Channels];
    private readonly int _frameSamples;
    private ulong _expected;
    private bool _started;

    private double _jitterMs;
    private uint _lastTimestamp;
    private long _lastArrival;
    private long _reportedAt;
    private long _reportedPackets;
    private long _reportedLost;

    /// <param name="owner">The session that announced the stream (compared by reference).</param>
    /// <param name="expectedSource">Packets from any other address are rejected before decryption.</param>
    public IncomingAudioStream(object owner, IPAddress expectedSource, string peerName, uint streamId, byte[] key, int bufferMs, int frameMs)
    {
        Owner = owner;
        ExpectedSource = expectedSource;
        PeerName = peerName;
        StreamId = streamId;
        FrameMs = Math.Clamp(frameMs, 5, 60);
        _frameSamples = AudioFormat.SampleRate / 1000 * FrameMs;
        _cipher = new MediaCipher(key);
        _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels);
        Buffer = new JitterBuffer(bufferMs, FrameMs);
    }

    public object Owner { get; }

    public string PeerId { get; init; } = string.Empty;

    /// <summary>The key as announced (base64): a repeated announcement with the same key is the same stream.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The sender sends it on its own (follows input or a view), not because a user chose it.</summary>
    public bool Automatic { get; set; }

    public long StartedTicks { get; } = Environment.TickCount64;

    public IPAddress ExpectedSource { get; }

    public string PeerName { get; }

    public uint StreamId { get; }

    /// <summary>Length of one frame as the sender announced it.</summary>
    public int FrameMs { get; }

    public JitterBuffer Buffer { get; }

    public long Packets { get; private set; }

    public long Lost { get; private set; }

    /// <summary>Lost packets rebuilt from the spare copy in the following packet, so they were never heard.</summary>
    public long Recovered { get; private set; }

    public long Rejected { get; private set; }

    /// <summary>Spread in arrival times, smoothed; what the playout buffer sizes itself by.</summary>
    public double JitterMs
    {
        get
        {
            lock (_gate)
            {
                return _jitterMs;
            }
        }
    }

    public long LastPacketTicks { get; private set; } = Environment.TickCount64;

    /// <summary>The share of packets lost since the last report, raised from the receive path every two seconds.</summary>
    public event Action<IncomingAudioStream, double>? LossMeasured;

    public void OnPacket(IPEndPoint from, ReadOnlySpan<byte> packet, ulong sequence)
    {
        double? report = null;
        lock (_gate)
        {
            if (!from.Address.Equals(ExpectedSource) || !_cipher.TryOpen(packet, _plain, out var length)
                || !AudioPacket.TryRead(_plain.AsSpan(0, length), out var timestamp, out var frame, out var spare))
            {
                Rejected++;
                return;
            }

            if (_started && sequence < _expected)
            {
                return; // late or duplicated
            }

            Measure(timestamp);
            if (_started && sequence > _expected)
            {
                FillGap(sequence - _expected, spare);
            }

            Decode(frame);
            _expected = sequence + 1;
            _started = true;
            Packets++;
            _reportedPackets++;
            LastPacketTicks = Environment.TickCount64;
            report = DueReport();
        }

        if (report is { } percent)
        {
            LossMeasured?.Invoke(this, percent);
        }
    }

    /// <summary>
    /// Compares how far the sound moved on against how long the packet took to arrive. Using the timestamp rather
    /// than counting packets is what makes this survive the sender going quiet: a pause in silence moves both by
    /// the same amount and so counts as no jitter at all.
    /// </summary>
    private void Measure(uint timestamp)
    {
        var now = Stopwatch.GetTimestamp();
        if (_started)
        {
            var sound = unchecked(timestamp - _lastTimestamp) / (AudioFormat.SampleRate / 1000.0);
            var arrival = (now - _lastArrival) * 1000.0 / Stopwatch.Frequency;
            _jitterMs += (Math.Abs(arrival - sound) - _jitterMs) / 16;
            Buffer.Aim(_jitterMs);
        }

        _lastTimestamp = timestamp;
        _lastArrival = now;
    }

    /// <summary>
    /// Fills in for packets that never arrived: the last of them from the spare copy this packet carries, where
    /// there is one, and the rest left to the decoder's own concealment.
    /// </summary>
    private void FillGap(ulong missing, ReadOnlySpan<byte> spare)
    {
        Lost += (long)missing;
        _reportedLost += (long)missing;
        var conceal = (int)Math.Min(missing, MaxConcealedFrames);
        var fromSpare = !spare.IsEmpty && conceal > 0;
        if (fromSpare)
        {
            conceal--;
        }

        for (var i = 0; i < conceal; i++)
        {
            Decode(ReadOnlySpan<byte>.Empty);
        }

        if (fromSpare)
        {
            Recovered++;
            Decode(spare);
        }
    }

    /// <summary>Decodes one frame into the playout buffer; an empty span asks the decoder to conceal a lost one.</summary>
    private void Decode(ReadOnlySpan<byte> frame)
    {
        int samples;
        try
        {
            samples = frame.IsEmpty
                ? _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, _frameSamples)
                : _decoder.Decode(frame, _pcm, MaxDecodeSamples);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            Rejected++;
            return;
        }

        if (samples > 0)
        {
            Buffer.Write(_pcm.AsSpan(0, samples * AudioFormat.Channels));
        }
    }

    /// <summary>The share of packets lost since the last report, or null when it is not time for one yet.</summary>
    private double? DueReport()
    {
        var now = Environment.TickCount64;
        if (_reportedAt == 0)
        {
            _reportedAt = now;
            return null;
        }

        if (now - _reportedAt < ReportEvery.TotalMilliseconds)
        {
            return null;
        }

        var total = _reportedPackets + _reportedLost;
        var percent = total == 0 ? 0 : _reportedLost * 100.0 / total;
        _reportedAt = now;
        _reportedPackets = 0;
        _reportedLost = 0;
        return percent;
    }

    public void Dispose() => _cipher.Dispose();
}
