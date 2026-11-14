using System.Buffers.Binary;

namespace WinMirroring.Audio.Streams;

/// <summary>
/// What is inside one sealed sound datagram: the playout position, this frame's encoded bytes, and — when the
/// receiver has reported losing packets — the previous frame's bytes as a spare.
///
/// The spare is what makes a lost datagram inaudible: the next one still carries the frame that went missing, so it
/// is decoded properly instead of being guessed at. It costs roughly twice the sound's bitrate, which on a local
/// network is nothing, and it is only switched on while packets are actually being lost. Opus has its own in-band
/// scheme for this, but it only works in the codec's speech mode; at 160 kbit/s of music Opus runs in its other
/// mode, where that scheme does nothing at all, so the spare is carried here instead.
///
/// <code>
/// timestamp : 4 bytes  samples per channel since the stream began
/// length    : 2 bytes  how many bytes this frame's own data takes
/// frame     : n bytes
/// spare     : rest     the previous frame, or nothing
/// </code>
/// </summary>
internal static class AudioPacket
{
    public const int HeaderSize = 6;

    public static int Write(Span<byte> plain, uint timestamp, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> spare)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(plain, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(plain[4..], (ushort)frame.Length);
        frame.CopyTo(plain[HeaderSize..]);
        spare.CopyTo(plain[(HeaderSize + frame.Length)..]);
        return HeaderSize + frame.Length + spare.Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> plain, out uint timestamp, out ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> spare)
    {
        timestamp = 0;
        frame = default;
        spare = default;
        if (plain.Length < HeaderSize)
        {
            return false;
        }

        timestamp = BinaryPrimitives.ReadUInt32LittleEndian(plain);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(plain[4..]);
        if (length == 0 || HeaderSize + length > plain.Length)
        {
            return false;
        }

        frame = plain.Slice(HeaderSize, length);
        spare = plain[(HeaderSize + length)..];
        return true;
    }
}
