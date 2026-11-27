using System.Text;

namespace WinMirroring.Shell.Protocol;

/// <summary>What a piece of an arriving command turned out to be.</summary>
internal enum CommandPiece
{
    /// <summary>Not a piece of a command at all.</summary>
    Ignored,

    /// <summary>Not the end of the command; the rest is still coming.</summary>
    More,

    /// <summary>That was the last piece: the command is whole and is now out.</summary>
    Whole,

    /// <summary>
    /// That was the last piece of a command longer than will be put back together. None of it was kept and none of
    /// it is to run. Said once per command, by the piece that ends it — where the sender expects an answer.
    /// </summary>
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

    /// <summary>What a command that turned out too long is answered with, in words to show as they are.</summary>
    public const string TooLongText = "Команда слишком длинная.";

    private readonly StringBuilder _arriving = new();

    /// <summary>
    /// The command now arriving has already passed <see cref="Longest"/>: the rest of it is dropped as it comes,
    /// up to and including the piece that ends it.
    ///
    /// Without this, what was kept was thrown away at the moment the limit was passed and the pieces after it were
    /// collected as though a new command had begun — so the piece that ended the script ran its tail. Half a
    /// script is worse than none of it: the half that sets things up is the half that went missing.
    /// </summary>
    private bool _tooLong;

    public CommandPiece Add(ShellMessageType type, string text, out string command)
    {
        command = string.Empty;
        if (type is not (ShellMessageType.Run or ShellMessageType.RunPart))
        {
            return CommandPiece.Ignored;
        }

        if (_tooLong || _arriving.Length + text.Length > Longest)
        {
            _arriving.Clear();
            _tooLong = type == ShellMessageType.RunPart;
            return _tooLong ? CommandPiece.More : CommandPiece.TooLong;
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
