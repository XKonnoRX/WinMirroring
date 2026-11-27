using System.Buffers.Binary;

namespace WinMirroring.Screen.Transport;

/// <summary>
/// What a viewer says back to the source by datagram: which pictures it has dealt with, and which pieces of a
/// picture it is missing.
///
/// Both used to be said on the session's connection only. That was the wrong road for them once the pictures had
/// left it: on a Wi-Fi link that is dropping a packet in a hundred the pictures arrived anyway — that is what the
/// datagrams and their parity are for — but each acknowledgement queued behind whichever segment of the connection
/// had been lost, and a source that waits on acknowledgements stood still for a picture that was already on the
/// viewer's screen. And a picture that lost two pieces was given up whole and a complete new one asked for, when
/// the two pieces were all that was missing and could be had again in the time of one round trip.
///
/// Neither is relied on to arrive. Acknowledgements still go on the connection as well and each datagram repeats
/// the last few, so one lost changes nothing; a request for pieces that gets no answer is repeated once and then
/// the picture is given up as it always was.
/// </summary>
internal static class ViewerReplies
{
    /// <summary>Media datagram kinds going from the viewer to the source (the source's own are in <see cref="VideoLink"/>).</summary>
    public const byte AckKind = 4;
    public const byte ResendKind = 5;

    /// <summary>
    /// The viewer seals with the key the source gave it, so the two must never use the same sequence number: the
    /// number is what makes each datagram's encryption its own. The source counts up from nothing; the viewer
    /// counts up from here, which the source would need centuries to reach.
    /// </summary>
    public const ulong ViewerSequenceBase = 1UL << 63;

    /// <summary>How many of its latest acknowledgements each datagram carries.</summary>
    public const int AcksPerDatagram = 4;

    /// <summary>The most pieces one request may name; a picture missing more than this is asked for whole.</summary>
    public const int MostPiecesAsked = 96;

    public readonly record struct Ack(uint FrameNo, int ViewerMs, int PaintMs);

    private const int AckSize = 8;

    /// <summary>count(1), then per acknowledgement: frame(4) held(2) painted(2).</summary>
    public static int WriteAcks(Span<byte> payload, ReadOnlySpan<Ack> acks)
    {
        var count = Math.Min(acks.Length, AcksPerDatagram);
        payload[0] = (byte)count;
        for (var i = 0; i < count; i++)
        {
            var at = payload[(1 + (i * AckSize))..];
            BinaryPrimitives.WriteUInt32LittleEndian(at, acks[i].FrameNo);
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], (ushort)Math.Clamp(acks[i].ViewerMs, 0, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], (ushort)Math.Clamp(acks[i].PaintMs, 0, ushort.MaxValue));
        }

        return 1 + (count * AckSize);
    }

    public static bool TryReadAcks(ReadOnlySpan<byte> payload, List<Ack> into)
    {
        if (payload.Length < 1 || payload[0] > AcksPerDatagram || payload.Length != 1 + (payload[0] * AckSize))
        {
            return false;
        }

        for (var i = 0; i < payload[0]; i++)
        {
            var at = payload[(1 + (i * AckSize))..];
            into.Add(new Ack(
                BinaryPrimitives.ReadUInt32LittleEndian(at),
                BinaryPrimitives.ReadUInt16LittleEndian(at[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(at[6..])));
        }

        return true;
    }

    /// <summary>frame(4) count(2), then that many piece numbers (2 each). No pieces named means all of the picture.</summary>
    public static int WriteResend(Span<byte> payload, uint frameNo, IReadOnlyList<int> pieces)
    {
        var count = pieces.Count > MostPiecesAsked ? 0 : pieces.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(payload, frameNo);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], (ushort)count);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload[(6 + (i * 2))..], (ushort)pieces[i]);
        }

        return 6 + (count * 2);
    }

    public static bool TryReadResend(ReadOnlySpan<byte> payload, out uint frameNo, List<int> pieces)
    {
        frameNo = 0;
        if (payload.Length < 6)
        {
            return false;
        }

        frameNo = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        if (count > MostPiecesAsked || payload.Length != 6 + (count * 2))
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            pieces.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload[(6 + (i * 2))..]));
        }

        return true;
    }

    /// <summary>The largest either kind of reply can be, for the buffers that hold them.</summary>
    public const int MaxPayload = 6 + (MostPiecesAsked * 2);
}
