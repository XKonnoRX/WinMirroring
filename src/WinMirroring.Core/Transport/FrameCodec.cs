using System.Buffers;
using System.Buffers.Binary;
using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Transport;

/// <summary>
/// Length-prefixed frames on a stream: 4-byte big-endian length, 1-byte channel, payload.
///
/// The two top bits of the length word mark a frame that carries one part of a larger message, so a big payload can
/// be written in pieces with the connection's write lock released between them — otherwise half a megabyte of tiles
/// or a key frame keeps a key press, an acknowledgement and the heartbeat waiting behind it. A part's payload begins
/// with a 2-byte message id, because parts of different messages (another stream, the other direction of the same
/// channel) interleave freely once the lock is released; parts of one message always arrive in order, because the
/// side writing them writes them one after another.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 5;

    /// <summary>Largest payload written as a single frame; anything longer goes out as parts.</summary>
    public const int MaxUnsplitPayload = 32 * 1024;

    private const int PartFlag = unchecked((int)0x8000_0000);
    private const int LastPartFlag = 0x4000_0000;
    private const int LengthMask = 0x3FFF_FFFF;
    private const int MessageIdSize = 2;

    /// <summary>A peer with more unfinished messages than this on one connection is not talking our protocol.</summary>
    private const int MaxPartialMessages = 32;

    public static ValueTask WriteAsync(Stream stream, ChannelId channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > ProtocolConstants.MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Frame payload exceeds the protocol limit.");
        }

        return WriteFrameAsync(stream, channel, payload, 0, messageId: null, ct);
    }

    /// <summary>Writes one part of a message; the last part is the one that completes it on the other side.</summary>
    public static ValueTask WritePartAsync(Stream stream, ChannelId channel, ushort messageId, ReadOnlyMemory<byte> part, bool last, CancellationToken ct) =>
        WriteFrameAsync(stream, channel, part, PartFlag | (last ? LastPartFlag : 0), messageId, ct);

    private static async ValueTask WriteFrameAsync(Stream stream, ChannelId channel, ReadOnlyMemory<byte> payload, int flags, ushort? messageId, CancellationToken ct)
    {
        var prefix = messageId is null ? 0 : MessageIdSize;
        var total = HeaderSize + prefix + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, (prefix + payload.Length) | flags);
            buffer[4] = (byte)channel;
            if (messageId is { } id)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(HeaderSize), id);
            }

            payload.Span.CopyTo(buffer.AsSpan(HeaderSize + prefix));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads whole messages from one stream, joining the parts of those that were written in pieces. One per
    /// connection: it holds what has arrived of each unfinished message.
    /// </summary>
    public sealed class Reader
    {
        private readonly Dictionary<(ChannelId Channel, ushort MessageId), PartialMessage> _partial = [];
        private readonly byte[] _header = new byte[HeaderSize];

        /// <summary>Reads one whole message; returns null on a clean end of stream before any header byte.</summary>
        public async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    await stream.ReadExactlyAsync(_header, ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return null;
                }

                var word = BinaryPrimitives.ReadInt32BigEndian(_header);
                var length = word & LengthMask;
                var channel = (ChannelId)_header[4];
                if (length < 0 || length > ProtocolConstants.MaxFrameSize)
                {
                    throw new InvalidDataException($"Invalid frame length {length}.");
                }

                if ((word & PartFlag) == 0)
                {
                    var payload = length == 0 ? [] : new byte[length];
                    if (length > 0)
                    {
                        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                    }

                    return new Frame(channel, payload);
                }

                if (length < MessageIdSize)
                {
                    throw new InvalidDataException("A message part without a message id.");
                }

                var part = new byte[length];
                await stream.ReadExactlyAsync(part, ct).ConfigureAwait(false);
                var messageId = BinaryPrimitives.ReadUInt16BigEndian(part);
                if (Join(channel, messageId, part.AsSpan(MessageIdSize), (word & LastPartFlag) != 0) is { } whole)
                {
                    return new Frame(channel, whole);
                }
            }
        }

        /// <summary>Adds a part and returns the whole message once the last one has arrived.</summary>
        private byte[]? Join(ChannelId channel, ushort messageId, ReadOnlySpan<byte> bytes, bool last)
        {
            var key = (channel, messageId);
            if (!_partial.TryGetValue(key, out var message))
            {
                if (last)
                {
                    // A message that fits in one part: nothing to keep.
                    return bytes.ToArray();
                }

                if (_partial.Count >= MaxPartialMessages)
                {
                    throw new InvalidDataException($"More than {MaxPartialMessages} unfinished messages on one connection.");
                }

                message = new PartialMessage();
                _partial[key] = message;
            }

            message.Append(bytes);
            if (!last)
            {
                return null;
            }

            _partial.Remove(key);
            return message.ToArray();
        }

        private sealed class PartialMessage
        {
            private byte[] _bytes = [];
            private int _length;

            public void Append(ReadOnlySpan<byte> bytes)
            {
                if (_length + bytes.Length > ProtocolConstants.MaxFrameSize)
                {
                    throw new InvalidDataException("A message in parts exceeds the protocol limit.");
                }

                if (_bytes.Length < _length + bytes.Length)
                {
                    Array.Resize(ref _bytes, Math.Max(_length + bytes.Length, _bytes.Length * 2));
                }

                bytes.CopyTo(_bytes.AsSpan(_length));
                _length += bytes.Length;
            }

            public byte[] ToArray() => _bytes.Length == _length ? _bytes : _bytes.AsSpan(0, _length).ToArray();
        }
    }
}

public readonly record struct Frame(ChannelId Channel, byte[] Payload);
