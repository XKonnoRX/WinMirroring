using System.Buffers.Binary;
using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Injection;

/// <summary>
/// The tiny binary protocol the normal app and the elevated input helper speak over a local named pipe. The app
/// encodes each injection call and sends it; the helper decodes it and replays it through a
/// <see cref="LocalInjectionSink"/>. Messages are framed by a 4-byte length prefix on the wire.
/// </summary>
public static class InjectionCodec
{
    public const int TokenLength = 32;

    /// <summary>A message larger than this is refused: the frames here are tiny, so this only catches a bad stream.</summary>
    public const int MaxMessageLength = 64 * 1024;

    public enum Op : byte
    {
        Handshake = 1,
        MoveAbsolute = 2,
        MoveRelative = 3,
        Button = 4,
        Wheel = 5,
        Key = 6,
        Unicode = 7,
        TouchFrame = 8,
        Bye = 9,
    }

    public static Op PeekOp(ReadOnlySpan<byte> message) => (Op)message[0];

    public static byte[] Handshake(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength)
        {
            throw new ArgumentException($"token must be {TokenLength} bytes", nameof(token));
        }

        var buffer = new byte[1 + TokenLength];
        buffer[0] = (byte)Op.Handshake;
        token.CopyTo(buffer.AsSpan(1));
        return buffer;
    }

    /// <summary>Reads the token from a handshake message, or null when it is not one / is malformed.</summary>
    public static byte[]? ReadHandshake(ReadOnlySpan<byte> message) =>
        message.Length == 1 + TokenLength && (Op)message[0] == Op.Handshake ? message.Slice(1, TokenLength).ToArray() : null;

    public static byte[] Bye() => [(byte)Op.Bye];

    public static byte[] MoveAbsolute(int x, int y) => Two(Op.MoveAbsolute, x, y);

    public static byte[] MoveRelative(int dx, int dy) => Two(Op.MoveRelative, dx, dy);

    public static byte[] Button(MouseButton button, bool down) => [(byte)Op.Button, (byte)button, (byte)(down ? 1 : 0)];

    public static byte[] Wheel(int delta, bool horizontal)
    {
        var buffer = new byte[6];
        buffer[0] = (byte)Op.Wheel;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), delta);
        buffer[5] = (byte)(horizontal ? 1 : 0);
        return buffer;
    }

    public static byte[] Key(ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        var buffer = new byte[7];
        buffer[0] = (byte)Op.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1), virtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(3), scanCode);
        buffer[5] = (byte)(up ? 1 : 0);
        buffer[6] = (byte)(extended ? 1 : 0);
        return buffer;
    }

    public static byte[] Unicode(char ch, bool up)
    {
        var buffer = new byte[4];
        buffer[0] = (byte)Op.Unicode;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1), ch);
        buffer[3] = (byte)(up ? 1 : 0);
        return buffer;
    }

    public static byte[] TouchFrame(IReadOnlyList<TouchContactInfo> contacts)
    {
        var count = Math.Min(contacts.Count, byte.MaxValue);
        var buffer = new byte[2 + (count * 16)];
        buffer[0] = (byte)Op.TouchFrame;
        buffer[1] = (byte)count;
        var at = 2;
        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), c.Id);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 4), c.X);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 8), c.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 12), c.Flags);
            at += 16;
        }

        return buffer;
    }

    /// <summary>Replays one injection message onto <paramref name="sink"/>. Handshake and Bye are handled by the loop, not here.</summary>
    public static void Apply(ReadOnlySpan<byte> message, IInjectionSink sink)
    {
        if (message.IsEmpty)
        {
            return;
        }

        var body = message[1..];
        switch ((Op)message[0])
        {
            case Op.MoveAbsolute:
                sink.MoveAbsolute(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
                break;
            case Op.MoveRelative:
                sink.MoveRelative(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
                break;
            case Op.Button:
                sink.Button((MouseButton)body[0], body[1] != 0);
                break;
            case Op.Wheel:
                sink.Wheel(BinaryPrimitives.ReadInt32LittleEndian(body), body[4] != 0);
                break;
            case Op.Key:
                sink.Key(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), body[4] != 0, body[5] != 0);
                break;
            case Op.Unicode:
                sink.Unicode((char)BinaryPrimitives.ReadUInt16LittleEndian(body), body[2] != 0);
                break;
            case Op.TouchFrame:
                ApplyTouch(body, sink);
                break;
        }
    }

    public static async Task WriteMessageAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one framed message, or null at end of stream.</summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageLength)
        {
            throw new InvalidDataException($"injection message length {length} is out of range");
        }

        var payload = new byte[length];
        return await ReadExactAsync(stream, payload, ct).ConfigureAwait(false) ? payload : null;
    }

    private static void ApplyTouch(ReadOnlySpan<byte> body, IInjectionSink sink)
    {
        var count = body[0];
        var at = 1;
        var contacts = new List<TouchContactInfo>(count);
        for (var i = 0; i < count; i++)
        {
            contacts.Add(new TouchContactInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(body[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(body[(at + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(body[(at + 8)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 12)..])));
            at += 16;
        }

        sink.TouchFrame(contacts);
    }

    private static byte[] Two(Op op, int a, int b)
    {
        var buffer = new byte[9];
        buffer[0] = (byte)op;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), a);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5), b);
        return buffer;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
