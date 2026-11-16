using System.Text;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Tests;

/// <summary>
/// A command can be a whole script — the tool reads one from a file, of any length — and one message on this
/// channel holds eight kilobytes. So a long one travels as several messages and runs only when the last arrives.
/// It used to be cut to fit instead, with nothing said, and the front of a script was then run as if it were the
/// script, which is worse than refusing it.
/// </summary>
public class ShellCodecTests
{
    [Fact]
    public void AShortCommandIsOneMessage()
    {
        var messages = ShellCodec.EncodeCommand(7, "Write-Output 'привет'");

        var only = Assert.Single(messages);
        Assert.True(ShellCodec.TryReadHeader(only, out var type, out var console));
        Assert.Equal((ShellMessageType.Run, 7u), (type, console));
        Assert.True(ShellCodec.TryDecodeText(only, out var text));
        Assert.Equal("Write-Output 'привет'", text);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void ALongCommandArrivesWholeAndRunsOnlyWithItsLastPiece(int length)
    {
        var command = Script(length);

        var messages = ShellCodec.EncodeCommand(1, command);

        Assert.True(messages.Count > 1, $"a command of {length} characters should not fit in one message");
        var rejoined = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            Assert.True(ShellCodec.TryReadHeader(messages[i], out var type, out _));
            Assert.Equal(i == messages.Count - 1 ? ShellMessageType.Run : ShellMessageType.RunPart, type);
            Assert.True(ShellCodec.TryDecodeText(messages[i], out var piece));
            rejoined.Append(piece);
        }

        Assert.Equal(command, rejoined.ToString());
    }

    /// <summary>
    /// Characters outside the basic plane, which take two of a string's own units each: a piece must never end
    /// between the halves of one, or the text put back together is not the text that was sent.
    /// </summary>
    [Fact]
    public void APieceNeverEndsInsideACharacter()
    {
        var command = string.Concat(Enumerable.Repeat("😀", 20_000));

        var messages = ShellCodec.EncodeCommand(1, command);

        var rejoined = new StringBuilder();
        foreach (var message in messages)
        {
            Assert.True(ShellCodec.TryDecodeText(message, out var piece));
            Assert.DoesNotContain('�', piece);
            rejoined.Append(piece);
        }

        Assert.Equal(command, rejoined.ToString());
    }

    /// <summary>
    /// The message that says a person is being asked, which is text and nothing else, and which a reader that
    /// does not know the kind must leave alone rather than mistake for an answer.
    /// </summary>
    [Fact]
    public void TheMessageThatSaysSomebodyIsBeingAskedCarriesTheQuestion()
    {
        var message = ShellCodec.Encode(ShellMessageType.Asking, 42, "ждём ответа человека за устройством");

        Assert.True(ShellCodec.TryReadHeader(message, out var type, out var console));
        Assert.Equal((ShellMessageType.Asking, 42u), (type, console));
        Assert.True(ShellCodec.TryDecodeText(message, out var question));
        Assert.Equal("ждём ответа человека за устройством", question);
        Assert.False(ShellCodec.TryDecodeDone(message, out _, out _));
        Assert.False(ShellCodec.TryDecodeOutput(message, out _, out _));
    }

    private static string Script(int length)
    {
        var text = new StringBuilder();
        for (var line = 0; text.Length < length; line++)
        {
            text.AppendLine($"# строка {line}, которая здесь только для длины");
        }

        return text.ToString();
    }
}
