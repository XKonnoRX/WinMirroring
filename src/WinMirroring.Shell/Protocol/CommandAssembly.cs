using System.Text;

namespace WinMirroring.Shell.Protocol;

/// <summary>What a piece of an arriving command turned out to be.</summary>
internal enum CommandPiece
{
    /// <summary>Not a piece of a command at all.</summary>
    Ignored,

    /// <summary>Kept; the rest is still coming.</summary>
    More,

    /// <summary>That was the last piece: the command is whole and is now out.</summary>
    Whole,

    /// <summary>More arrived than will be put back together; nothing is kept.</summary>
    TooLong,
}

/// <summary>
/// Puts a command back together from the messages that carried it. One message holds eight kilobytes and a command
/// can be a whole script of any length, so a long one arrives as several <see cref="ShellMessageType.RunPart"/>
/// messages and one <see cref="ShellMessageType.Run"/>, which is the one that ends it.
///
/// Here rather than written out wherever it is needed, because it is needed in three places that must agree to the
/// letter: on the device, where commands arrive from a peer; in the app, where they arrive from a tool on the same
/// machine; and in the elevated helper, where they arrive from the app. Three copies of this would be three chances
/// for a script to be run with its middle missing.
/// </summary>
internal sealed class CommandAssembly
{
    /// <summary>
    /// The longest command that will be put back together. A script is text and a megabyte of it is a very long
    /// script indeed; past that, something is either wrong or not a command, and this is memory held on someone
    /// else's say-so.
    /// </summary>
    public const int Longest = 1 << 20;

    private readonly StringBuilder _arriving = new();

    public CommandPiece Add(ShellMessageType type, string text, out string command)
    {
        command = string.Empty;
        if (type is not (ShellMessageType.Run or ShellMessageType.RunPart))
        {
            return CommandPiece.Ignored;
        }

        if (_arriving.Length + text.Length > Longest)
        {
            _arriving.Clear();
            return CommandPiece.TooLong;
        }

        _arriving.Append(text);
        if (type == ShellMessageType.RunPart)
        {
            return CommandPiece.More;
        }

        command = _arriving.ToString();
        _arriving.Clear();
        return CommandPiece.Whole;
    }
}
