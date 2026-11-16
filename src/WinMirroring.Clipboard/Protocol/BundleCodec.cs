using System.Buffers.Binary;

namespace WinMirroring.Clipboard.Protocol;

/// <summary>What one record in a bundle says about a file.</summary>
public enum BundleRecordKind : byte
{
    /// <summary>The next piece of the file's contents.</summary>
    Data = 1,

    /// <summary>The file is complete.</summary>
    End = 2,

    /// <summary>The file could not be read; its one byte is the <see cref="FileChunkStatus"/>.</summary>
    Error = 3,
}

/// <summary>How a block's bytes travel.</summary>
public enum BundleCodecKind : byte
{
    Raw = 0,
    Brotli = 1,
}

/// <summary>
/// Bulk transfer on the File channel: many files in one stream, pushed by the sender instead of pulled a chunk at a
/// time. Pulling cost a round trip per chunk and at least one per file, which on a link of a few milliseconds turns
/// thousands of small files into minutes of waiting with the line idle. A bundle names a range of an offer's entries
/// once; the sender reads them in order and sends blocks back to back, as far ahead as the receiver allows.
///
/// Messages:
/// <list type="bullet">
/// <item>Request [3] u32 id, i64 offer, i32 first, i32 end (exclusive), i32 window bytes, u8 flags (1: Brotli accepted).</item>
/// <item>Start [4] u32 id, u8 status: the sender has it (Ok) or not (NotFound). Sent at once, so a receiver knows quickly whether the sender understands bundles at all.</item>
/// <item>Block [5] u32 id, u8 codec, i32 raw length, payload.</item>
/// <item>Ack [6] u32 id, i64 bytes consumed so far (as sent): the window moves on.</item>
/// <item>End [7] u32 id, u8 status.</item>
/// <item>Cancel [8] u32 id (receiver → sender).</item>
/// </list>
/// A block, once decoded, is whole records: u8 kind, i32 index, i32 length, then that many bytes. A record never
/// spans two blocks, so every block can be decoded on its own — and compressed on its own, on any core.
/// </summary>
public static class BundleCodec
{
    public const byte RequestMessage = 3;
    public const byte StartMessage = 4;
    public const byte BlockMessage = 5;
    public const byte AckMessage = 6;
    public const byte EndMessage = 7;
    public const byte CancelMessage = 8;

    public const byte FlagBrotli = 1;

    public const int RecordHeaderSize = 9;
    public const int BlockHeaderSize = 10;

    /// <summary>Raw size of a block: big enough to compress well and to keep the line full, small enough to share it.</summary>
    public const int BlockSize = 1024 * 1024;

    public static bool IsBundleMessage(byte type) => type is >= RequestMessage and <= CancelMessage;

    public static byte[] EncodeRequest(uint id, long offerId, int first, int end, int window, byte flags)
    {
        var m = new byte[26];
        m[0] = RequestMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(5), offerId);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(13), first);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(17), end);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(21), window);
        m[25] = flags;
        return m;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> m, out uint id, out long offerId, out int first, out int end, out int window, out byte flags)
    {
        id = 0;
        offerId = 0;
        first = end = window = 0;
        flags = 0;
        if (m.Length < 26 || m[0] != RequestMessage)
        {
            return false;
        }

        id = BinaryPrimitives.ReadUInt32LittleEndian(m[1..]);
        offerId = BinaryPrimitives.ReadInt64LittleEndian(m[5..]);
        first = BinaryPrimitives.ReadInt32LittleEndian(m[13..]);
        end = BinaryPrimitives.ReadInt32LittleEndian(m[17..]);
        window = BinaryPrimitives.ReadInt32LittleEndian(m[21..]);
        flags = m[25];
        return first >= 0 && end >= first && window > 0;
    }

    /// <summary>Start or End: [type] u32 id, u8 status.</summary>
    public static byte[] EncodeStatus(byte type, uint id, FileChunkStatus status)
    {
        var m = new byte[6];
        m[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        m[5] = (byte)status;
        return m;
    }

    public static byte[] EncodeAck(uint id, long consumed)
    {
        var m = new byte[13];
        m[0] = AckMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(5), consumed);
        return m;
    }

    public static byte[] EncodeCancel(uint id)
    {
        var m = new byte[5];
        m[0] = CancelMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        return m;
    }

    /// <summary>The id every bundle message carries right after its type.</summary>
    public static bool TryReadId(ReadOnlySpan<byte> m, out uint id)
    {
        id = 0;
        if (m.Length < 5 || !IsBundleMessage(m[0]))
        {
            return false;
        }

        id = BinaryPrimitives.ReadUInt32LittleEndian(m[1..]);
        return true;
    }

    public static FileChunkStatus ReadStatus(ReadOnlySpan<byte> m) => m.Length >= 6 ? (FileChunkStatus)m[5] : FileChunkStatus.IoError;

    public static long ReadAck(ReadOnlySpan<byte> m) => m.Length >= 13 ? BinaryPrimitives.ReadInt64LittleEndian(m[5..]) : 0;

    /// <summary>A block frame around an already encoded payload.</summary>
    public static byte[] EncodeBlock(uint id, BundleCodecKind codec, int rawLength, ReadOnlySpan<byte> payload)
    {
        var m = new byte[BlockHeaderSize + payload.Length];
        m[0] = BlockMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        m[5] = (byte)codec;
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(6), rawLength);
        payload.CopyTo(m.AsSpan(BlockHeaderSize));
        return m;
    }

    public static bool TryDecodeBlockHeader(ReadOnlySpan<byte> m, out BundleCodecKind codec, out int rawLength)
    {
        codec = BundleCodecKind.Raw;
        rawLength = 0;
        if (m.Length < BlockHeaderSize || m[0] != BlockMessage)
        {
            return false;
        }

        codec = (BundleCodecKind)m[5];
        rawLength = BinaryPrimitives.ReadInt32LittleEndian(m[6..]);
        return rawLength >= 0 && rawLength <= BlockSize + RecordHeaderSize && codec is BundleCodecKind.Raw or BundleCodecKind.Brotli;
    }

    public static void WriteRecordHeader(Span<byte> target, BundleRecordKind kind, int index, int length)
    {
        target[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(target[1..], index);
        BinaryPrimitives.WriteInt32LittleEndian(target[5..], length);
    }

    public static bool TryReadRecordHeader(ReadOnlySpan<byte> source, out BundleRecordKind kind, out int index, out int length)
    {
        kind = 0;
        index = length = 0;
        if (source.Length < RecordHeaderSize)
        {
            return false;
        }

        kind = (BundleRecordKind)source[0];
        index = BinaryPrimitives.ReadInt32LittleEndian(source[1..]);
        length = BinaryPrimitives.ReadInt32LittleEndian(source[5..]);
        return kind is BundleRecordKind.Data or BundleRecordKind.End or BundleRecordKind.Error
            && index >= 0 && length >= 0 && length <= source.Length - RecordHeaderSize;
    }
}
