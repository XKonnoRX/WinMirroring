using System.Net;
using Concentus;
using WinMirroring.Audio.Playback;
using WinMirroring.Core.Media;

namespace WinMirroring.Audio.Streams;

/// <summary>One stream another device sends here: decrypt, reorder guard, Opus decode with loss concealment.</summary>
internal sealed class IncomingAudioStream : IDisposable
{
    private const int MaxConcealedFrames = 5;
    private const int MaxDecodeSamples = 5760;

    private readonly MediaCipher _cipher;
    private readonly IOpusDecoder _decoder;
    private readonly object _gate = new();
    private readonly byte[] _plain = new byte[8192];
    private readonly float[] _pcm = new float[MaxDecodeSamples * AudioFormat.Channels];
    private ulong _expected;
    private bool _started;

    /// <param name="owner">The session that announced the stream (compared by reference).</param>
    /// <param name="expectedSource">Packets from any other address are rejected before decryption.</param>
    public IncomingAudioStream(object owner, IPAddress expectedSource, string peerName, uint streamId, byte[] key, int bufferMs)
    {
        Owner = owner;
        ExpectedSource = expectedSource;
        PeerName = peerName;
        StreamId = streamId;
        _cipher = new MediaCipher(key);
        _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels);
        Buffer = new JitterBuffer(bufferMs);
    }

    public object Owner { get; }

    public IPAddress ExpectedSource { get; }

    public string PeerName { get; }

    public uint StreamId { get; }

    public JitterBuffer Buffer { get; }

    public long Packets { get; private set; }

    public long Lost { get; private set; }

    public long Rejected { get; private set; }

    public long LastPacketTicks { get; private set; } = Environment.TickCount64;

    public void OnPacket(IPEndPoint from, ReadOnlySpan<byte> packet, ulong sequence)
    {
        lock (_gate)
        {
            if (!from.Address.Equals(ExpectedSource) || !_cipher.TryOpen(packet, _plain, out var length) || length <= 4)
            {
                Rejected++;
                return;
            }

            if (_started && sequence < _expected)
            {
                return; // late or duplicated
            }

            if (_started && sequence > _expected)
            {
                var missing = sequence - _expected;
                Lost += (long)missing;
                for (var i = 0UL; i < Math.Min(missing, (ulong)MaxConcealedFrames); i++)
                {
                    var concealed = _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, AudioFormat.FrameSamples);
                    if (concealed > 0)
                    {
                        Buffer.Write(_pcm.AsSpan(0, concealed * AudioFormat.Channels));
                    }
                }
            }

            int samples;
            try
            {
                samples = _decoder.Decode(_plain.AsSpan(4, length - 4), _pcm, MaxDecodeSamples);
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

            _expected = sequence + 1;
            _started = true;
            Packets++;
            LastPacketTicks = Environment.TickCount64;
        }
    }

    public void Dispose() => _cipher.Dispose();
}
