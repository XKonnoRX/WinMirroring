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
    private readonly byte[] _previous = new byte[AudioFormat.MaxOpusPacket];
    private int _previousLength;
    private long _phase;

    public AudioPipelineTests()
    {
        _sealer = new MediaCipher(_key);
        _stream = Receiver(_key);
    }

    private static IncomingAudioStream Receiver(byte[] key) =>
        new(new object(), Sender.Address, "PC", StreamId, key, bufferMs: 40, frameMs: AudioFormat.FrameMs);

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
        Assert.InRange(_stream.Buffer.BufferedMs, 199, 201);

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
        Assert.Equal(0, _stream.Recovered);

        // Two real frames and two the decoder had to make up.
        Assert.InRange(_stream.Buffer.BufferedMs, 4 * AudioFormat.FrameMs - 1, (4 * AudioFormat.FrameMs) + 1);
    }

    /// <summary>
    /// What the spare copy is for: one datagram goes missing and the next one still carries its frame, so the
    /// sound is rebuilt rather than guessed at.
    /// </summary>
    [Fact]
    public void ALostPacketIsRebuiltFromTheSpareInTheNextOne()
    {
        _stream.OnPacket(Sender, NextPacket(0), 0);
        NextPacket(1); // this one never arrives
        _stream.OnPacket(Sender, NextPacket(2, withSpare: true), 2);

        Assert.Equal(1, _stream.Lost);
        Assert.Equal(1, _stream.Recovered);
        Assert.Equal(2, _stream.Packets);
        Assert.InRange(_stream.Buffer.BufferedMs, 3 * AudioFormat.FrameMs - 1, (3 * AudioFormat.FrameMs) + 1);

        // Enough on top for the buffer to start playing (it aims higher than three frames after a gap like that).
        _stream.OnPacket(Sender, NextPacket(3, withSpare: true), 3);
        _stream.OnPacket(Sender, NextPacket(4, withSpare: true), 4);
        _stream.OnPacket(Sender, NextPacket(5, withSpare: true), 5);

        var output = new float[AudioFormat.MillisecondsToValues(3 * AudioFormat.FrameMs)];
        _stream.Buffer.Read(output);
        Assert.InRange(output.Max(Math.Abs), 0.2f, 0.7f); // all three frames are real sound, the rebuilt one too
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

        using var otherKey = Receiver(MediaCipher.GenerateKey());
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

    [Fact]
    public void PacketLayoutRoundTripsWithAndWithoutASpare()
    {
        var plain = new byte[AudioPacket.HeaderSize + 20];
        byte[] frame = [1, 2, 3, 4, 5];
        byte[] spare = [9, 8, 7];

        var length = AudioPacket.Write(plain, 4711, frame, spare);
        Assert.True(AudioPacket.TryRead(plain.AsSpan(0, length), out var timestamp, out var readFrame, out var readSpare));
        Assert.Equal(4711u, timestamp);
        Assert.Equal(frame, readFrame.ToArray());
        Assert.Equal(spare, readSpare.ToArray());

        length = AudioPacket.Write(plain, 1, frame, []);
        Assert.True(AudioPacket.TryRead(plain.AsSpan(0, length), out _, out readFrame, out readSpare));
        Assert.Equal(frame, readFrame.ToArray());
        Assert.True(readSpare.IsEmpty);

        Assert.False(AudioPacket.TryRead(plain.AsSpan(0, 3), out _, out _, out _));
    }

    private byte[] NextPacket(ulong sequence, bool withSpare = false)
    {
        var pcm = new float[AudioFormat.FrameValues];
        for (var i = 0; i < AudioFormat.FrameSamples; i++, _phase++)
        {
            var v = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * _phase / AudioFormat.SampleRate));
            pcm[i * 2] = v;
            pcm[(i * 2) + 1] = v;
        }

        var plain = new byte[AudioPacket.HeaderSize + (2 * AudioFormat.MaxOpusPacket)];
        var encoded = _encoder.Encode(pcm, AudioFormat.FrameSamples, plain.AsSpan(AudioPacket.HeaderSize), AudioFormat.MaxOpusPacket);
        var frame = plain.AsSpan(AudioPacket.HeaderSize, encoded);
        var spare = withSpare ? _previous.AsSpan(0, _previousLength) : [];
        var length = AudioPacket.Write(plain, (uint)(sequence * AudioFormat.FrameSamples), frame, spare);

        var packet = new byte[MediaCipher.Overhead + length];
        var sealedLength = _sealer.Seal(AudioFormat.MediaKind, StreamId, sequence, plain.AsSpan(0, length), packet);
        Assert.Equal(packet.Length, sealedLength);

        frame.CopyTo(_previous);
        _previousLength = encoded;
        return packet;
    }
}
