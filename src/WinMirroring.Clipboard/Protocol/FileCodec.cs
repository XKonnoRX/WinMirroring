using System.Buffers.Binary;

namespace WinMirroring.Clipboard.Protocol;

public enum FileChunkStatus : byte
{
    Ok = 0,
    NotFound = 1,
    IoError = 2,
}

/// <summary>
/// File channel. Request: [1] u32 id, i64 offer, i32 index, i64 offset, i32 length.
/// Response: [2] u32 id, u8 status, data (shorter than requested only at end of file).
/// </summary>
public static class FileCodec
{
    public const byte RequestMessage = 1;
    public const byte ResponseMessage = 2;
    public const int RequestSize = 29;
    public const int ResponseHeaderSize = 6;

    /// <summary>Largest chunk a server returns; small chunks keep mouse input responsive on the shared link.</summary>
    public const int MaxChunk = 256 * 1024;

    public static byte[] EncodeRequest(uint id, long offerId, int index, long offset, int length)
    {
        var message = new byte[RequestSize];
        message[0] = RequestMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), id);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(5), offerId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(13), index);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(17), offset);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(25), length);
        return message;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> message, out uint id, out long offerId, out int index, out long offset, out int length)
    {
        id = 0;
        offerId = 0;
        index = 0;
        offset = 0;
        length = 0;
        if (message.Length < RequestSize || message[0] != RequestMessage)
        {
            return false;
        }

        id = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        offerId = BinaryPrimitives.ReadInt64LittleEndian(message[5..]);
        index = BinaryPrimitives.ReadInt32LittleEndian(message[13..]);
        offset = BinaryPrimitives.ReadInt64LittleEndian(message[17..]);
        length = BinaryPrimitives.ReadInt32LittleEndian(message[25..]);
        return index >= 0 && offset >= 0 && length > 0;
    }

    /// <summary>Builds a response; <paramref name="data"/> may be empty.</summary>
    public static byte[] EncodeResponse(uint id, FileChunkStatus status, ReadOnlySpan<byte> data)
    {
        var message = new byte[ResponseHeaderSize + data.Length];
        message[0] = ResponseMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), id);
        message[5] = (byte)status;
        data.CopyTo(message.AsSpan(ResponseHeaderSize));
        return message;
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> message, out uint id, out FileChunkStatus status)
    {
        id = 0;
        status = FileChunkStatus.IoError;
        if (message.Length < ResponseHeaderSize || message[0] != ResponseMessage)
        {
            return false;
        }

        id = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        status = (FileChunkStatus)message[5];
        return true;
    }
}
