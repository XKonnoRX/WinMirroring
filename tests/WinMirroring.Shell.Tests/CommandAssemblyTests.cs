using System.Text;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Tests;

/// <summary>
/// A command put back together from its pieces either runs whole or does not run. The case that matters is the
/// script too long to be put back together at all: what had arrived was thrown away at the moment the limit was
/// passed, and the pieces after it were then collected as though a new command had begun — so the piece that ended
/// the script ran its tail. Half a script is worse than none of it.
/// </summary>
public class CommandAssemblyTests
{
    private const string Tail = "Remove-Item C:\\important -Recurse";

    [Fact]
    public void AScriptThatIsTooLongRunsNoPartOfItself()
    {
        var arriving = new CommandAssembly();
        var pieces = Pieces(TooLong());

        for (var i = 0; i < pieces.Count; i++)
        {
            var last = i == pieces.Count - 1;
            var result = arriving.Add(pieces[i].Type, pieces[i].Text, out var command);

            // Said once, by the piece that ends the script, and nothing ever comes out of it.
            Assert.Equal(last ? CommandPiece.TooLong : CommandPiece.More, result);
            Assert.Empty(command);
        }
    }

    /// <summary>And the console is not left refusing: what comes after is a command of its own, and whole.</summary>
    [Fact]
    public void TheCommandAfterOneThatWasTooLongIsItsOwn()
    {
        var arriving = new CommandAssembly();
        foreach (var piece in Pieces(TooLong()))
        {
            arriving.Add(piece.Type, piece.Text, out _);
        }

        Assert.Equal(CommandPiece.Whole, arriving.Add(ShellMessageType.Run, "Write-Output 'следующая'", out var next));
        Assert.Equal("Write-Output 'следующая'", next);

        var several = Pieces(new string('#', 20_000) + "\nWrite-Output 'длинная, но в меру'");
        Assert.True(several.Count > 1);
        var whole = string.Empty;
        for (var i = 0; i < several.Count; i++)
        {
            Assert.Equal(
                i == several.Count - 1 ? CommandPiece.Whole : CommandPiece.More,
                arriving.Add(several[i].Type, several[i].Text, out whole));
        }

        Assert.EndsWith("Write-Output 'длинная, но в меру'", whole, StringComparison.Ordinal);
        Assert.Equal(20_000 + 1 + "Write-Output 'длинная, но в меру'".Length, whole.Length);
    }

    /// <summary>A single message that is by itself past the limit cannot happen on the wire, but the answer is the same.</summary>
    [Fact]
    public void ACommandTooLongInOnePieceIsRefusedAtOnce()
    {
        var arriving = new CommandAssembly();

        Assert.Equal(CommandPiece.TooLong, arriving.Add(ShellMessageType.Run, new string('x', CommandAssembly.Longest + 1), out var command));
        Assert.Empty(command);
        Assert.Equal(CommandPiece.Whole, arriving.Add(ShellMessageType.Run, "Get-Date", out var next));
        Assert.Equal("Get-Date", next);
    }

    /// <summary>A script past the limit, whose last line is a command that would do something if it ran alone.</summary>
    private static string TooLong()
    {
        var script = new StringBuilder();
        while (script.Length <= CommandAssembly.Longest + 50_000)
        {
            script.Append("# a line that is only here for its length\n");
        }

        return script.Append(Tail).ToString();
    }

    /// <summary>The script as it travels: the pieces the sending side really makes of it.</summary>
    private static List<(ShellMessageType Type, string Text)> Pieces(string script)
    {
        var pieces = new List<(ShellMessageType, string)>();
        foreach (var message in ShellCodec.EncodeCommand(1, script))
        {
            Assert.True(ShellCodec.TryReadHeader(message, out var type, out _));
            Assert.True(ShellCodec.TryDecodeText(message, out var text));
            pieces.Add((type, text));
        }

        return pieces;
    }
}
