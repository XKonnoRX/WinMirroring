using System.Buffers.Binary;
using System.Text;

namespace WinMirroring.Shell.Protocol;

public enum ShellMessageType : byte
{
    /// <summary>Controller → device: open a console here (one per window).</summary>
    Open = 1,

    /// <summary>Device → controller: it is open, and this is the directory it starts in.</summary>
    Opened = 2,

    /// <summary>Device → controller: it is not open, and why — in Russian, to be shown as it is.</summary>
    Refused = 3,

    /// <summary>Controller → device: run this.</summary>
    Run = 4,

    /// <summary>Device → controller: a piece of what the command said, as it says it.</summary>
    Output = 5,

    /// <summary>Device → controller: the command finished with this code, and here is where it left the console.</summary>
    Done = 6,

    /// <summary>Controller → device: stop what is running.</summary>
    Cancel = 8,

    /// <summary>Either way: this console is finished with.</summary>
    Close = 9,
}

/// <summary>Which of a command's two streams a piece of output came from.</summary>
public enum ShellStream : byte
{
    Output = 0,
    Error = 1,
}

/// <summary>
/// The console channel, little-endian: first byte <see cref="ShellMessageType"/>, then u32 console id, then
/// whatever that kind of message carries. Text is UTF-8 with a u16 length — which is also the limit on how much of
/// anything one message carries, so a command that prints a megabyte arrives as many messages rather than one.
///
/// Why a channel of its own rather than control messages: output arrives in pieces as a command produces it, and
/// that is a stream, not a question and an answer. The control channel is JSON and is used for the things a session
/// says about itself.
/// </summary>
public static class ShellCodec
{
    /// <summary>The most text one message carries. Comfortably inside one frame, and output is a stream anyway.</summary>
    public const int MaxText = 8192;

    private const int Header = 5;

    public static byte[] Encode(ShellMessageType type, uint consoleId) => Encode(type, consoleId, string.Empty);

    /// <summary>Any of the messages that is a type, a console and one piece of text.</summary>
    public static byte[] Encode(ShellMessageType type, uint consoleId, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(Clip(text));
        var message = new byte[Header + 2 + bytes.Length];
        message[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), consoleId);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(Header), (ushort)bytes.Length);
        bytes.CopyTo(message.AsSpan(Header + 2));
        return message;
    }

    /// <summary>Output: which stream it came from, then the text.</summary>
    public static byte[] EncodeOutput(uint consoleId, ShellStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(Clip(text));
        var message = new byte[Header + 1 + 2 + bytes.Length];
        message[0] = (byte)ShellMessageType.Output;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), consoleId);
        message[Header] = (byte)stream;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(Header + 1), (ushort)bytes.Length);
        bytes.CopyTo(message.AsSpan(Header + 3));
        return message;
    }

    /// <summary>Done: the exit code, then the directory the console is now in.</summary>
    public static byte[] EncodeDone(uint consoleId, int exitCode, string directory)
    {
        var bytes = Encoding.UTF8.GetBytes(Clip(directory));
        var message = new byte[Header + 4 + 2 + bytes.Length];
        message[0] = (byte)ShellMessageType.Done;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), consoleId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(Header), exitCode);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(Header + 4), (ushort)bytes.Length);
        bytes.CopyTo(message.AsSpan(Header + 6));
        return message;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> message, out ShellMessageType type, out uint consoleId)
    {
        type = 0;
        consoleId = 0;
        if (message.Length < Header)
        {
            return false;
        }

        type = (ShellMessageType)message[0];
        consoleId = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        return true;
    }

    public static bool TryDecodeText(ReadOnlySpan<byte> message, out string text)
    {
        text = string.Empty;
        if (message.Length < Header + 2)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(message[Header..]);
        if (length > MaxText || message.Length < Header + 2 + length)
        {
            return false;
        }

        text = Encoding.UTF8.GetString(message.Slice(Header + 2, length));
        return true;
    }

    public static bool TryDecodeOutput(ReadOnlySpan<byte> message, out ShellStream stream, out string text)
    {
        stream = ShellStream.Output;
        text = string.Empty;
        if (message.Length < Header + 3 || message[0] != (byte)ShellMessageType.Output)
        {
            return false;
        }

        var kind = message[Header];
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message[(Header + 1)..]);
        if (kind > (byte)ShellStream.Error || length > MaxText || message.Length < Header + 3 + length)
        {
            return false;
        }

        stream = (ShellStream)kind;
        text = Encoding.UTF8.GetString(message.Slice(Header + 3, length));
        return true;
    }

    public static bool TryDecodeDone(ReadOnlySpan<byte> message, out int exitCode, out string directory)
    {
        exitCode = 0;
        directory = string.Empty;
        if (message.Length < Header + 6 || message[0] != (byte)ShellMessageType.Done)
        {
            return false;
        }

        exitCode = BinaryPrimitives.ReadInt32LittleEndian(message[Header..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message[(Header + 4)..]);
        if (length > MaxText || message.Length < Header + 6 + length)
        {
            return false;
        }

        directory = Encoding.UTF8.GetString(message.Slice(Header + 6, length));
        return true;
    }

    /// <summary>
    /// Cut to what one message carries, on a character boundary that UTF-8 can encode in the room available. A
    /// command's output is a stream and is sent in pieces anyway, so cutting here only decides where a piece ends.
    /// </summary>
    private static string Clip(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxText)
        {
            return text;
        }

        // Three bytes per character is the worst case for anything outside the astral planes, and a surrogate pair
        // is never split because the pair counts as one character here.
        var characters = Math.Min(text.Length, MaxText / 3);
        while (characters > 0 && char.IsLowSurrogate(text[characters - 1]))
        {
            characters--;
        }

        return text[..characters];
    }
}
