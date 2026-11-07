using System.Buffers;
using System.Buffers.Binary;
using WinMirroring.Input.Layout;

namespace WinMirroring.Input.Protocol;

public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

public enum InputEventKind : byte
{
    /// <summary>Pixel motion from the controller (its pointer speed and acceleration already applied), timestamped.</summary>
    MouseMove = 1,
    MouseButton = 2,
    MouseWheel = 3,
    Key = 4,

    /// <summary>Controller → controlled: the cursor arrives at this edge (or the centre) of the controlled screen.</summary>
    Enter = 5,

    /// <summary>Controller → controlled: control left; release everything that is still pressed.</summary>
    Leave = 6,

    /// <summary>Controlled → controller: the cursor is being pushed out across this edge.</summary>
    EdgeReached = 7,

    /// <summary>Both directions while control is active: proves the link is alive when nothing else is sent.</summary>
    Alive = 8,
}

/// <summary>One decoded event. Fields that do not apply to <see cref="Kind"/> are zero.</summary>
public readonly record struct InputEvent(
    InputEventKind Kind,
    uint Timestamp,
    int Dx,
    int Dy,
    MouseButton Button,
    bool Down,
    int WheelDelta,
    bool Horizontal,
    ushort VirtualKey,
    ushort ScanCode,
    bool Up,
    bool Extended,
    Edge Edge,
    float Relative,
    bool AtCenter);

/// <summary>
/// Little-endian binary encoding for the Input channel. A frame holds one or more events back to back;
/// each starts with its kind byte so the reader can walk them without a length prefix.
/// Motion timestamps are the controller's monotonic clock in microseconds (wrapping uint32).
/// </summary>
public static class InputCodec
{
    private const byte KeyFlagUp = 0x01;
    private const byte KeyFlagExtended = 0x02;
    private const byte CenterMarker = 0xFF;

    public static void WriteMouseMove(IBufferWriter<byte> writer, uint timestamp, int dx, int dy)
    {
        var span = writer.GetSpan(9);
        span[0] = (byte)InputEventKind.MouseMove;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], timestamp);
        BinaryPrimitives.WriteInt16LittleEndian(span[5..], (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(span[7..], (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        writer.Advance(9);
    }

    public static void WriteMouseButton(IBufferWriter<byte> writer, MouseButton button, bool down)
    {
        var span = writer.GetSpan(3);
        span[0] = (byte)InputEventKind.MouseButton;
        span[1] = (byte)button;
        span[2] = down ? (byte)1 : (byte)0;
        writer.Advance(3);
    }

    public static void WriteMouseWheel(IBufferWriter<byte> writer, int delta, bool horizontal)
    {
        var span = writer.GetSpan(4);
        span[0] = (byte)InputEventKind.MouseWheel;
        BinaryPrimitives.WriteInt16LittleEndian(span[1..], (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        span[3] = horizontal ? (byte)1 : (byte)0;
        writer.Advance(4);
    }

    public static void WriteKey(IBufferWriter<byte> writer, ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        var span = writer.GetSpan(6);
        span[0] = (byte)InputEventKind.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(span[1..], virtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(span[3..], scanCode);
        span[5] = (byte)((up ? KeyFlagUp : 0) | (extended ? KeyFlagExtended : 0));
        writer.Advance(6);
    }

    public static void WriteEnter(IBufferWriter<byte> writer, Edge edge, float relative) =>
        WriteEdge(writer, InputEventKind.Enter, (byte)edge, relative);

    public static void WriteEnterAtCenter(IBufferWriter<byte> writer) =>
        WriteEdge(writer, InputEventKind.Enter, CenterMarker, 0.5f);

    public static void WriteEdgeReached(IBufferWriter<byte> writer, Edge edge, float relative) =>
        WriteEdge(writer, InputEventKind.EdgeReached, (byte)edge, relative);

    public static void WriteLeave(IBufferWriter<byte> writer) => WriteKind(writer, InputEventKind.Leave);

    public static void WriteAlive(IBufferWriter<byte> writer) => WriteKind(writer, InputEventKind.Alive);

    private static void WriteKind(IBufferWriter<byte> writer, InputEventKind kind)
    {
        var span = writer.GetSpan(1);
        span[0] = (byte)kind;
        writer.Advance(1);
    }

    private static void WriteEdge(IBufferWriter<byte> writer, InputEventKind kind, byte edge, float relative)
    {
        var span = writer.GetSpan(6);
        span[0] = (byte)kind;
        span[1] = edge;
        BinaryPrimitives.WriteSingleLittleEndian(span[2..], relative);
        writer.Advance(6);
    }

    /// <summary>Decodes the event at the start of <paramref name="data"/>; returns false on truncated or unknown input.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out InputEvent evt, out int consumed)
    {
        evt = default;
        consumed = 0;
        if (data.IsEmpty)
        {
            return false;
        }

        var kind = (InputEventKind)data[0];
        switch (kind)
        {
            case InputEventKind.MouseMove when data.Length >= 9:
                evt = new InputEvent { Kind = kind, Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]), Dx = BinaryPrimitives.ReadInt16LittleEndian(data[5..]), Dy = BinaryPrimitives.ReadInt16LittleEndian(data[7..]) };
                consumed = 9;
                return true;

            case InputEventKind.MouseButton when data.Length >= 3:
                evt = new InputEvent { Kind = kind, Button = (MouseButton)data[1], Down = data[2] != 0 };
                consumed = 3;
                return true;

            case InputEventKind.MouseWheel when data.Length >= 4:
                evt = new InputEvent { Kind = kind, WheelDelta = BinaryPrimitives.ReadInt16LittleEndian(data[1..]), Horizontal = data[3] != 0 };
                consumed = 4;
                return true;

            case InputEventKind.Key when data.Length >= 6:
                evt = new InputEvent
                {
                    Kind = kind,
                    VirtualKey = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]),
                    ScanCode = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]),
                    Up = (data[5] & KeyFlagUp) != 0,
                    Extended = (data[5] & KeyFlagExtended) != 0,
                };
                consumed = 6;
                return true;

            case InputEventKind.Enter or InputEventKind.EdgeReached when data.Length >= 6:
                var atCenter = kind == InputEventKind.Enter && data[1] == CenterMarker;
                var edge = data[1] <= (byte)Edge.Bottom ? (Edge)data[1] : Edge.Left;
                evt = new InputEvent { Kind = kind, Edge = edge, Relative = BinaryPrimitives.ReadSingleLittleEndian(data[2..]), AtCenter = atCenter };
                consumed = 6;
                return true;

            case InputEventKind.Leave or InputEventKind.Alive:
                evt = new InputEvent { Kind = kind };
                consumed = 1;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Convenience for single-event frames.</summary>
    public static byte[] Single(Action<ArrayBufferWriter<byte>> write)
    {
        var writer = new ArrayBufferWriter<byte>(16);
        write(writer);
        return writer.WrittenSpan.ToArray();
    }
}
