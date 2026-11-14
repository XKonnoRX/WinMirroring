using System.Buffers.Binary;
using System.Drawing;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Protocol;

public enum PointerAction : byte
{
    Move = 1,
    Down = 2,
    Up = 3,
    Wheel = 4,
    HorizontalWheel = 5,
}

public enum ScreenMessageType : byte
{
    /// <summary>Source → viewer: changed tiles.</summary>
    Frame = 1,

    /// <summary>Viewer → source: a frame has been shown; the source may send another.</summary>
    Ack = 2,

    /// <summary>Viewer → source: pointer at normalized coordinates of the streamed image.</summary>
    Pointer = 3,

    /// <summary>Viewer → source: key by virtual key and scan code.</summary>
    Key = 4,

    /// <summary>Viewer → source: stream only this normalized part of the captured image.</summary>
    Crop = 5,

    /// <summary>Viewer → source: a touch contact (real touch is injected on the source).</summary>
    Touch = 6,

    /// <summary>Viewer → source: characters without a key (touch keyboard, IME), typed as Unicode.</summary>
    Text = 7,

    /// <summary>Viewer → source: a command for the captured window (restore it from minimised).</summary>
    Window = 8,

    /// <summary>Source → viewer: one H.264 access unit (the hardware-video path; <see cref="Frame"/> is the fallback).</summary>
    VideoFrame = 9,

    /// <summary>Viewer → source: the size the picture is actually being shown at, so it can be encoded at that size.</summary>
    Size = 10,
}

/// <summary>What a <see cref="ScreenMessageType.Window"/> message asks the captured window to do.</summary>
public enum WindowCommand : byte
{
    Restore = 1,
}

public sealed record ScreenFrame(uint StreamId, uint FrameNo, int Width, int Height, IReadOnlyList<EncodedTile> Tiles);

/// <summary>One H.264 access unit for a stream: the decoded picture is <see cref="Width"/>×<see cref="Height"/>.</summary>
public sealed record ScreenVideoFrame(uint StreamId, uint FrameNo, int Width, int Height, bool Keyframe, byte[] Data);

/// <summary>
/// Screen channel, little-endian, first byte <see cref="ScreenMessageType"/>, then u32 stream id.
/// Frame: u32 frame no, u16 width, u16 height, u16 tile count, tiles (u16 x, y, w, h, i32 length, JPEG).
/// VideoFrame: u32 frame no, u16 width, u16 height, u8 flags (bit0 keyframe), u16 reserved, H.264 access unit.
/// Ack: u32 frame no. Pointer: u8 action, u8 button, f32 x, f32 y, i16 wheel. Key: u16 vk, u16 scan, u8 flags.
/// Crop: f32 x, y, w, h.
/// </summary>
public static class ScreenCodec
{
    private const int FrameHeader = 15;
    private const int TileHeader = 12;

    public static byte[] EncodeFrame(uint streamId, uint frameNo, int width, int height, IReadOnlyList<EncodedTile> tiles)
    {
        var size = FrameHeader + tiles.Sum(t => TileHeader + t.Jpeg.Length);
        var message = new byte[size];
        var span = message.AsSpan();
        span[0] = (byte)ScreenMessageType.Frame;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[5..], frameNo);
        BinaryPrimitives.WriteUInt16LittleEndian(span[9..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[11..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[13..], (ushort)tiles.Count);
        var offset = FrameHeader;
        foreach (var tile in tiles)
        {
            var t = span[offset..];
            BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)tile.X);
            BinaryPrimitives.WriteUInt16LittleEndian(t[2..], (ushort)tile.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(t[4..], (ushort)tile.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(t[6..], (ushort)tile.Height);
            BinaryPrimitives.WriteInt32LittleEndian(t[8..], tile.Jpeg.Length);
            tile.Jpeg.CopyTo(t[TileHeader..]);
            offset += TileHeader + tile.Jpeg.Length;
        }

        return message;
    }

    public static ScreenFrame? TryDecodeFrame(ReadOnlySpan<byte> message)
    {
        if (message.Length < FrameHeader || message[0] != (byte)ScreenMessageType.Frame)
        {
            return null;
        }

        var streamId = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        var frameNo = BinaryPrimitives.ReadUInt32LittleEndian(message[5..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(message[9..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(message[11..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[13..]);
        var tiles = new List<EncodedTile>(count);
        var span = message[FrameHeader..];
        for (var i = 0; i < count; i++)
        {
            if (span.Length < TileHeader)
            {
                return null;
            }

            int x = BinaryPrimitives.ReadUInt16LittleEndian(span);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
            if (length < 0 || length > span.Length - TileHeader || w == 0 || h == 0 || x + w > width || y + h > height)
            {
                return null;
            }

            tiles.Add(new EncodedTile(x, y, w, h, span.Slice(TileHeader, length).ToArray()));
            span = span[(TileHeader + length)..];
        }

        return new ScreenFrame(streamId, frameNo, width, height, tiles);
    }

    private const int VideoHeader = 16;

    /// <summary>VideoFrame: u32 frame no, u16 width, u16 height, u8 flags (bit0 keyframe), then the H.264 bytes.</summary>
    public static byte[] EncodeVideoFrame(uint streamId, uint frameNo, int width, int height, bool keyframe, ReadOnlySpan<byte> data)
    {
        var message = new byte[VideoHeader + data.Length];
        var span = message.AsSpan();
        span[0] = (byte)ScreenMessageType.VideoFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[5..], frameNo);
        BinaryPrimitives.WriteUInt16LittleEndian(span[9..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[11..], (ushort)height);
        span[13] = (byte)(keyframe ? 1 : 0);
        // span[14..16] reserved for alignment / future flags.
        data.CopyTo(span[VideoHeader..]);
        return message;
    }

    public static ScreenVideoFrame? TryDecodeVideoFrame(ReadOnlySpan<byte> message)
    {
        if (message.Length < VideoHeader || message[0] != (byte)ScreenMessageType.VideoFrame)
        {
            return null;
        }

        var streamId = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        var frameNo = BinaryPrimitives.ReadUInt32LittleEndian(message[5..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(message[9..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(message[11..]);
        var keyframe = (message[13] & 1) != 0;
        var data = message[VideoHeader..].ToArray();
        return new ScreenVideoFrame(streamId, frameNo, width, height, keyframe, data);
    }

    public static byte[] EncodeAck(uint streamId, uint frameNo)
    {
        var message = new byte[9];
        message[0] = (byte)ScreenMessageType.Ack;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), frameNo);
        return message;
    }

    public static byte[] EncodePointer(uint streamId, PointerAction action, Input.Protocol.MouseButton button, float x, float y, int wheel)
    {
        var message = new byte[17];
        message[0] = (byte)ScreenMessageType.Pointer;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        message[5] = (byte)action;
        message[6] = (byte)button;
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(7), x);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(11), y);
        BinaryPrimitives.WriteInt16LittleEndian(message.AsSpan(15), (short)Math.Clamp(wheel, short.MinValue, short.MaxValue));
        return message;
    }

    public static byte[] EncodeKey(uint streamId, ushort virtualKey, ushort scanCode, bool up, bool extended)
    {
        var message = new byte[10];
        message[0] = (byte)ScreenMessageType.Key;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(5), virtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(7), scanCode);
        message[9] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return message;
    }

    /// <summary>Touch: u8 action (1 down, 2 move, 3 up, 4 cancel), u8 contact id, f32 x, f32 y.</summary>
    public static byte[] EncodeTouch(uint streamId, byte action, byte contact, float x, float y)
    {
        var message = new byte[15];
        message[0] = (byte)ScreenMessageType.Touch;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        message[5] = action;
        message[6] = contact;
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(7), x);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(11), y);
        return message;
    }

    public static bool TryDecodeTouch(ReadOnlySpan<byte> message, out byte action, out byte contact, out float x, out float y)
    {
        action = contact = 0;
        x = y = 0;
        if (message.Length < 15 || message[0] != (byte)ScreenMessageType.Touch)
        {
            return false;
        }

        action = message[5];
        contact = message[6];
        x = BinaryPrimitives.ReadSingleLittleEndian(message[7..]);
        y = BinaryPrimitives.ReadSingleLittleEndian(message[11..]);
        if (!float.IsFinite(x) || !float.IsFinite(y) || action is < 1 or > 4)
        {
            return false;
        }

        x = Math.Clamp(x, 0f, 1f);
        y = Math.Clamp(y, 0f, 1f);
        return true;
    }

    public const int MaxTextLength = 4096;

    /// <summary>Text: UTF-16 code units, at most <see cref="MaxTextLength"/>.</summary>
    public static byte[] EncodeText(uint streamId, string text)
    {
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        var message = new byte[5 + text.Length * 2];
        message[0] = (byte)ScreenMessageType.Text;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        for (var i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(5 + i * 2), text[i]);
        }

        return message;
    }

    public static bool TryDecodeText(ReadOnlySpan<byte> message, out string text)
    {
        text = string.Empty;
        if (message.Length < 7 || message[0] != (byte)ScreenMessageType.Text || (message.Length - 5) % 2 != 0 || (message.Length - 5) / 2 > MaxTextLength)
        {
            return false;
        }

        var chars = new char[(message.Length - 5) / 2];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(message[(5 + i * 2)..]);
        }

        text = new string(chars);
        return true;
    }

    public static byte[] EncodeWindowCommand(uint streamId, WindowCommand command)
    {
        var message = new byte[6];
        message[0] = (byte)ScreenMessageType.Window;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        message[5] = (byte)command;
        return message;
    }

    public static bool TryDecodeWindowCommand(ReadOnlySpan<byte> message, out WindowCommand command)
    {
        command = 0;
        if (message.Length < 6 || message[0] != (byte)ScreenMessageType.Window)
        {
            return false;
        }

        command = (WindowCommand)message[5];
        return true;
    }

    public static byte[] EncodeCrop(uint streamId, RectangleF crop)
    {
        var message = new byte[21];
        message[0] = (byte)ScreenMessageType.Crop;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(5), crop.X);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(9), crop.Y);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(13), crop.Width);
        BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(17), crop.Height);
        return message;
    }

    /// <summary>
    /// Size: u16 width, u16 height — how large the viewer is actually drawing the picture, in real pixels. The
    /// source has no other way to know: it would otherwise encode, send and decode a full screen for a window
    /// showing it at half that, and every one of those steps costs by the pixel.
    /// </summary>
    public static byte[] EncodeSize(uint streamId, int width, int height)
    {
        var message = new byte[9];
        message[0] = (byte)ScreenMessageType.Size;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), streamId);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(5), (ushort)Math.Clamp(width, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(7), (ushort)Math.Clamp(height, 0, ushort.MaxValue));
        return message;
    }

    /// <summary>A size of zero means "whatever you have": the viewer does not know yet, or does not care.</summary>
    public static bool TryDecodeSize(ReadOnlySpan<byte> message, out Size size)
    {
        size = default;
        if (message.Length < 9 || message[0] != (byte)ScreenMessageType.Size)
        {
            return false;
        }

        size = new Size(
            BinaryPrimitives.ReadUInt16LittleEndian(message[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(message[7..]));
        return true;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> message, out ScreenMessageType type, out uint streamId)
    {
        type = 0;
        streamId = 0;
        if (message.Length < 5)
        {
            return false;
        }

        type = (ScreenMessageType)message[0];
        streamId = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        return true;
    }

    public static bool TryDecodeAck(ReadOnlySpan<byte> message, out uint frameNo)
    {
        frameNo = message.Length >= 9 ? BinaryPrimitives.ReadUInt32LittleEndian(message[5..]) : 0;
        return message.Length >= 9 && message[0] == (byte)ScreenMessageType.Ack;
    }

    public static bool TryDecodePointer(ReadOnlySpan<byte> message, out PointerAction action, out Input.Protocol.MouseButton button, out float x, out float y, out int wheel)
    {
        action = 0;
        button = 0;
        x = y = 0;
        wheel = 0;
        if (message.Length < 17 || message[0] != (byte)ScreenMessageType.Pointer)
        {
            return false;
        }

        action = (PointerAction)message[5];
        button = (Input.Protocol.MouseButton)message[6];
        x = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(message[7..]), 0f, 1f);
        y = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(message[11..]), 0f, 1f);
        wheel = BinaryPrimitives.ReadInt16LittleEndian(message[15..]);
        return float.IsFinite(x) && float.IsFinite(y);
    }

    public static bool TryDecodeKey(ReadOnlySpan<byte> message, out ushort virtualKey, out ushort scanCode, out bool up, out bool extended)
    {
        virtualKey = scanCode = 0;
        up = extended = false;
        if (message.Length < 10 || message[0] != (byte)ScreenMessageType.Key)
        {
            return false;
        }

        virtualKey = BinaryPrimitives.ReadUInt16LittleEndian(message[5..]);
        scanCode = BinaryPrimitives.ReadUInt16LittleEndian(message[7..]);
        up = (message[9] & 1) != 0;
        extended = (message[9] & 2) != 0;
        return true;
    }

    public static bool TryDecodeCrop(ReadOnlySpan<byte> message, out RectangleF crop)
    {
        crop = default;
        if (message.Length < 21 || message[0] != (byte)ScreenMessageType.Crop)
        {
            return false;
        }

        crop = Normalize(new RectangleF(
            BinaryPrimitives.ReadSingleLittleEndian(message[5..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[9..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[13..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[17..])));
        return true;
    }

    /// <summary>Clamps a normalized rectangle into [0,1] with a minimum size; garbage becomes the full image.</summary>
    public static RectangleF Normalize(RectangleF crop)
    {
        if (!float.IsFinite(crop.X) || !float.IsFinite(crop.Y) || !float.IsFinite(crop.Width) || !float.IsFinite(crop.Height))
        {
            return new RectangleF(0, 0, 1, 1);
        }

        var left = Math.Clamp(crop.Left, 0f, 1f);
        var top = Math.Clamp(crop.Top, 0f, 1f);
        var right = Math.Clamp(crop.Right, 0f, 1f);
        var bottom = Math.Clamp(crop.Bottom, 0f, 1f);
        if (right - left < 0.02f || bottom - top < 0.02f)
        {
            return new RectangleF(0, 0, 1, 1);
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>Normalized crop → pixels of a <paramref name="width"/>×<paramref name="height"/> frame.</summary>
    public static Rectangle ToPixels(RectangleF crop, int width, int height)
    {
        var left = (int)Math.Round(crop.Left * width);
        var top = (int)Math.Round(crop.Top * height);
        var right = (int)Math.Round(crop.Right * width);
        var bottom = (int)Math.Round(crop.Bottom * height);
        return Rectangle.FromLTRB(Math.Clamp(left, 0, width), Math.Clamp(top, 0, height), Math.Clamp(right, 0, width), Math.Clamp(bottom, 0, height));
    }
}
