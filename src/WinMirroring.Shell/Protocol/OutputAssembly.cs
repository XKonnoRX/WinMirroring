using System.Text;

namespace WinMirroring.Shell.Protocol;

/// <summary>
/// Puts a line of a command's output back together from the messages that carried it. One message holds eight
/// kilobytes and a line can be longer — one line of JSON, a base64 blob — so a long one arrives as several output
/// messages, each but the last saying that the line goes on (<see cref="ShellCodec.EncodeOutputLine"/>).
///
/// The two streams are kept apart, because they are read apart on the device and their messages arrive interleaved:
/// a piece of an error line in the middle of a long output line belongs to neither half of it.
///
/// Here rather than written out wherever it is needed, for the reason <see cref="CommandAssembly"/> is: a line
/// crosses up to three hops — helper to app, device to device, app to tool — and each one that receives must put
/// lines together the same way.
/// </summary>
internal sealed class OutputAssembly
{
    /// <summary>
    /// The longest line that is held back waiting for its end. Past it the line is handed over as it stands and
    /// goes on as another: a line broken once a megabyte is better than memory that grows for as long as the other
    /// side keeps saying there is more.
    /// </summary>
    public const int Longest = CommandAssembly.Longest;

    private readonly StringBuilder?[] _arriving = new StringBuilder?[2];

    /// <summary>
    /// True when <paramref name="line"/> is a whole line to show. False when the piece was kept and the rest of its
    /// line is still coming.
    /// </summary>
    public bool Add(ShellStream stream, string piece, bool goesOn, out string line)
    {
        var held = _arriving[(int)stream];
        if (goesOn && (held?.Length ?? 0) + piece.Length <= Longest)
        {
            (_arriving[(int)stream] ??= new StringBuilder()).Append(piece);
            line = string.Empty;
            return false;
        }

        if (held is null or { Length: 0 })
        {
            line = piece; // every line that fits in one message, which is nearly all of them
            return true;
        }

        line = held.Append(piece).ToString();
        _arriving[(int)stream] = null;
        return true;
    }
}
