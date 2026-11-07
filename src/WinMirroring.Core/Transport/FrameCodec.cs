using System.Buffers;
using System.Buffers.Binary;
using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Transport;

/// <summary>Length-prefixed frames on a stream: 4-byte big-endian payload length, 1-byte channel, payload.</summary>
public static class FrameCodec
{
    public const int HeaderSize = 5;

    public static async ValueTask WriteAsync(Stream stream, ChannelId channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > ProtocolConstants.MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Frame payload exceeds the protocol limit.");
        }

        var total = HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
            buffer[4] = (byte)channel;
            payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads one frame; returns null on a clean end of stream before any header byte.</summary>
    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > ProtocolConstants.MaxFrameSize)
        {
            throw new InvalidDataException($"Invalid frame length {length}.");
        }

        var channel = (ChannelId)header[4];
        var payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return new Frame(channel, payload);
    }
}

public readonly record struct Frame(ChannelId Channel, byte[] Payload);
