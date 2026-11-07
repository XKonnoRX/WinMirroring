using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace WinMirroring.Clipboard.Protocol;

/// <summary>
/// Clipboard channel: [1][flags] data formats, or [2] a file offer (JSON manifest).
/// Data payload (zlib-compressed when flags bit 0 is set): u16 count, then per item u8 kind, u32 length, bytes.
/// </summary>
public static class ClipboardCodec
{
    public const byte DataMessage = 1;
    public const byte OfferMessage = 2;

    /// <summary>[3] i64 blob id, i64 length: a data message too large to send inline, to be pulled over the File channel.</summary>
    public const byte DataRefMessage = 3;

    public static byte[] EncodeDataRef(long blobId, long length)
    {
        var message = new byte[17];
        message[0] = DataRefMessage;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(1), blobId);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(9), length);
        return message;
    }

    public static bool TryDecodeDataRef(ReadOnlySpan<byte> message, out long blobId, out long length)
    {
        blobId = 0;
        length = 0;
        if (message.Length < 17 || message[0] != DataRefMessage)
        {
            return false;
        }

        blobId = BinaryPrimitives.ReadInt64LittleEndian(message[1..]);
        length = BinaryPrimitives.ReadInt64LittleEndian(message[9..]);
        return length > 0;
    }

    private const byte FlagCompressed = 0x01;
    private const int CompressAbove = 8 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] EncodeData(IReadOnlyList<ClipboardItem> items)
    {
        using var raw = new MemoryStream();
        Span<byte> header = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)items.Count);
        raw.Write(header[..2]);
        foreach (var item in items)
        {
            header[0] = (byte)item.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], item.Data.Length);
            raw.Write(header);
            raw.Write(item.Data);
        }

        var compress = raw.Length > CompressAbove;
        using var output = new MemoryStream();
        output.WriteByte(DataMessage);
        output.WriteByte(compress ? FlagCompressed : (byte)0);
        if (compress)
        {
            using var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
            raw.Position = 0;
            raw.CopyTo(zlib);
        }
        else
        {
            raw.Position = 0;
            raw.CopyTo(output);
        }

        return output.ToArray();
    }

    public static byte[] EncodeOffer(FileOfferManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var message = new byte[json.Length + 1];
        message[0] = OfferMessage;
        json.CopyTo(message, 1);
        return message;
    }

    /// <summary>Decodes a data message; null when malformed or larger than <paramref name="maxBytes"/> uncompressed.</summary>
    public static List<ClipboardItem>? TryDecodeData(ReadOnlySpan<byte> message, long maxBytes)
    {
        if (message.Length < 2 || message[0] != DataMessage)
        {
            return null;
        }

        byte[] raw;
        if ((message[1] & FlagCompressed) != 0)
        {
            using var input = new MemoryStream(message[2..].ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            raw = output.ToArray();
        }
        else
        {
            raw = message[2..].ToArray();
        }

        var span = raw.AsSpan();
        if (span.Length < 2)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        span = span[2..];
        var items = new List<ClipboardItem>(count);
        for (var i = 0; i < count; i++)
        {
            if (span.Length < 5)
            {
                return null;
            }

            var kind = (ClipboardItemKind)span[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[1..]);
            if (length < 0 || length > span.Length - 5)
            {
                return null;
            }

            if (Enum.IsDefined(kind))
            {
                items.Add(new ClipboardItem(kind, span.Slice(5, length).ToArray()));
            }

            span = span[(5 + length)..];
        }

        return items;
    }

    public static FileOfferManifest? TryDecodeOffer(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2 || message[0] != OfferMessage)
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<FileOfferManifest>(message[1..], Json);
            return manifest is { Entries: not null } && manifest.Entries.All(IsSafeEntry) ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Relative, no drive, no "..", no empty segments: a remote offer must never point outside its folder.</summary>
    public static bool IsSafeEntry(FileOfferEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Path) || entry.Size < 0 || entry.Path.Length > 1024)
        {
            return false;
        }

        if (entry.Path.Contains('/') || entry.Path.Contains(':') || entry.Path.StartsWith('\\'))
        {
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in entry.Path.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(invalid) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return false;
            }
        }

        return true;
    }
}
