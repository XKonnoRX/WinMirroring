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
    /// <summary>Relative motion from the controller's mouse.</summary>
    MouseMove = 1,
    MouseButton = 2,
    MouseWheel = 3,
    Key = 4,

    /// <summary>Controller → controlled: the cursor arrives at this edge of the controlled screen.</summary>
    Enter = 5,

    /// <summary>Controller → controlled: control left; release everything that is still pressed.</summary>
    Leave = 6,

    /// <summary>Controlled → controller: the cursor is being pushed out across this edge.</summary>
    EdgeReached = 7,
}

/// <summary>One decoded event. Fields that do not apply to <see cref="Kind"/> are zero.</summary>
public readonly record struct InputEvent(
    InputEventKind Kind,
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
    float Relative);

/// <summary>
/// Little-endian binary encoding for the Input channel. A frame holds one or more events back to back;
/// each starts with its kind byte so the reader can walk them without a length prefix.
/// </summary>
public static class InputCodec
{
    private const byte KeyFlagUp = 0x01;
    private const byte KeyFlagExtended = 0x02;

    public static void WriteMouseMove(IBufferWriter<byte> writer, int dx, int dy)
    {
        var span = writer.GetSpan(5);
        span[0] = (byte)InputEventKind.MouseMove;
        BinaryPrimitives.WriteInt16LittleEndian(span[1..], (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(span[3..], (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        writer.Advance(5);
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
        WriteEdge(writer, InputEventKind.Enter, edge, relative);

    public static void WriteEdgeReached(IBufferWriter<byte> writer, Edge edge, float relative) =>
        WriteEdge(writer, InputEventKind.EdgeReached, edge, relative);

    public static void WriteLeave(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(1);
        span[0] = (byte)InputEventKind.Leave;
        writer.Advance(1);
    }

    private static void WriteEdge(IBufferWriter<byte> writer, InputEventKind kind, Edge edge, float relative)
    {
        var span = writer.GetSpan(6);
        span[0] = (byte)kind;
        span[1] = (byte)edge;
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
            case InputEventKind.MouseMove when data.Length >= 5:
                evt = new InputEvent(kind, BinaryPrimitives.ReadInt16LittleEndian(data[1..]), BinaryPrimitives.ReadInt16LittleEndian(data[3..]), MouseButton.None, false, 0, false, 0, 0, false, false, Edge.Left, 0f);
                consumed = 5;
                return true;

            case InputEventKind.MouseButton when data.Length >= 3:
                evt = new InputEvent(kind, 0, 0, (MouseButton)data[1], data[2] != 0, 0, false, 0, 0, false, false, Edge.Left, 0f);
                consumed = 3;
                return true;

            case InputEventKind.MouseWheel when data.Length >= 4:
                evt = new InputEvent(kind, 0, 0, MouseButton.None, false, BinaryPrimitives.ReadInt16LittleEndian(data[1..]), data[3] != 0, 0, 0, false, false, Edge.Left, 0f);
                consumed = 4;
                return true;

            case InputEventKind.Key when data.Length >= 6:
                evt = new InputEvent(kind, 0, 0, MouseButton.None, false, 0, false,
                    BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[3..]),
                    (data[5] & KeyFlagUp) != 0, (data[5] & KeyFlagExtended) != 0, Edge.Left, 0f);
                consumed = 6;
                return true;

            case InputEventKind.Enter or InputEventKind.EdgeReached when data.Length >= 6:
                var edge = data[1] <= (byte)Edge.Bottom ? (Edge)data[1] : Edge.Left;
                evt = new InputEvent(kind, 0, 0, MouseButton.None, false, 0, false, 0, 0, false, false, edge, BinaryPrimitives.ReadSingleLittleEndian(data[2..]));
                consumed = 6;
                return true;

            case InputEventKind.Leave:
                evt = new InputEvent(kind, 0, 0, MouseButton.None, false, 0, false, 0, 0, false, false, Edge.Left, 0f);
                consumed = 1;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Convenience for single-event frames.</summary>
    public static byte[] Single(Action<ArrayBufferWriter<byte>> write)
    {
        var writer = new ArrayBufferWriter<byte>(8);
        write(writer);
        return writer.WrittenSpan.ToArray();
    }
}
