using System.Buffers.Binary;
using System.Net;
using Concentus;
using Concentus.Enums;
using WinMirroring.Audio.Streams;
using WinMirroring.Core.Media;

namespace WinMirroring.Audio.Tests;

/// <summary>Encoder side by hand (as AudioSender does it), receiver side through <see cref="IncomingAudioStream"/>.</summary>
public sealed class AudioPipelineTests : IDisposable
{
    private const uint StreamId = 77;
    private static readonly IPEndPoint Sender = new(IPAddress.Parse("192.168.1.191"), 50000);

    private readonly byte[] _key = MediaCipher.GenerateKey();
    private readonly MediaCipher _sealer;
    private readonly IOpusEncoder _encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
    private readonly IncomingAudioStream _stream;
    private long _phase;

    public AudioPipelineTests()
    {
        _sealer = new MediaCipher(_key);
        _stream = new IncomingAudioStream(new object(), Sender.Address, "PC", StreamId, _key, bufferMs: 40);
    }

    public void Dispose()
    {
        _sealer.Dispose();
        _stream.Dispose();
    }

    [Fact]
    public void EncodedSineArrivesDecodedInTheBuffer()
    {
        for (ulong seq = 0; seq < 10; seq++)
        {
            _stream.OnPacket(Sender, NextPacket(seq), seq);
        }

        Assert.Equal(10, _stream.Packets);
        Assert.Equal(0, _stream.Lost);
        Assert.InRange(_stream.Buffer.BufferedMs, 99, 101);

        var output = new float[AudioFormat.MillisecondsToValues(10)];
        _stream.Buffer.Read(output);
        _stream.Buffer.Read(output);
        var peak = output.Max(Math.Abs);
        Assert.InRange(peak, 0.2f, 0.7f); // a 0.4 amplitude sine survives Opus
    }

    [Fact]
    public void LostPacketsAreConcealedAndCounted()
    {
        _stream.OnPacket(Sender, NextPacket(0), 0);
        NextPacket(1);
        NextPacket(2);
        _stream.OnPacket(Sender, NextPacket(3), 3);

        Assert.Equal(2, _stream.Lost);
        Assert.InRange(_stream.Buffer.BufferedMs, 39, 41); // two real frames + two concealed ones
    }

    [Fact]
    public void ForgedForeignAndLatePacketsAreIgnored()
    {
        var packet = NextPacket(5);

        _stream.OnPacket(new IPEndPoint(IPAddress.Parse("192.168.1.66"), 50000), packet, 5);
        Assert.Equal(1, _stream.Rejected);

        var tampered = (byte[])packet.Clone();
        tampered[^1] ^= 0xFF;
        _stream.OnPacket(Sender, tampered, 5);
        Assert.Equal(2, _stream.Rejected);

        using var otherKey = new IncomingAudioStream(new object(), Sender.Address, "x", StreamId, MediaCipher.GenerateKey(), 40);
        otherKey.OnPacket(Sender, packet, 5);
        Assert.Equal(1, otherKey.Rejected);

        _stream.OnPacket(Sender, packet, 5);
        _stream.OnPacket(Sender, NextPacket(4), 4); // late
        Assert.Equal(1, _stream.Packets);
    }

    [Fact]
    public void CipherHeaderRoundTrips()
    {
        var packet = NextPacket(123456789);

        Assert.True(MediaCipher.TryReadHeader(packet, out var kind, out var stream, out var sequence));
        Assert.Equal((AudioFormat.MediaKind, StreamId, 123456789UL), (kind, stream, sequence));
        Assert.False(MediaCipher.TryReadHeader(packet.AsSpan(0, 10), out _, out _, out _));
    }

    private byte[] NextPacket(ulong sequence)
    {
        var pcm = new float[AudioFormat.FrameValues];
        for (var i = 0; i < AudioFormat.FrameSamples; i++, _phase++)
        {
            var v = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * _phase / AudioFormat.SampleRate));
            pcm[i * 2] = v;
            pcm[i * 2 + 1] = v;
        }

        var plain = new byte[4 + AudioFormat.MaxOpusPacket];
        var encoded = _encoder.Encode(pcm, AudioFormat.FrameSamples, plain.AsSpan(4), AudioFormat.MaxOpusPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(plain, (uint)(sequence * AudioFormat.FrameSamples));
        var packet = new byte[MediaCipher.Overhead + 4 + encoded];
        var length = _sealer.Seal(AudioFormat.MediaKind, StreamId, sequence, plain.AsSpan(0, 4 + encoded), packet);
        Assert.Equal(packet.Length, length);
        return packet;
    }
}
