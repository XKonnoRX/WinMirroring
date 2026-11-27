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

    /// <summary>
    /// A line of output longer than one message arrives whole. It used to be cut to its first few thousand
    /// characters and the rest dropped without a word — and a line that long is exactly what a script asks another
    /// device for: one line of JSON, a base64 blob, a minified file.
    /// </summary>
    [Theory]
    [InlineData("x", 20_000)]
    [InlineData("я", 10_000)]
    [InlineData("😀", 9_000)]
    [InlineData("a😀", 9_000)]
    public void ALineOfOutputTooLongForOneMessageArrivesWhole(string unit, int times)
    {
        var line = string.Concat(Enumerable.Repeat(unit, times));

        var messages = ShellCodec.EncodeOutputLine(3, ShellStream.Error, line);

        Assert.True(messages.Count > 1, "this line should not fit in one message");
        var arriving = new OutputAssembly();
        var lines = new List<string>();
        for (var i = 0; i < messages.Count; i++)
        {
            Assert.True(ShellCodec.TryReadHeader(messages[i], out var type, out var console));
            Assert.Equal((ShellMessageType.Output, 3u), (type, console));
            Assert.True(ShellCodec.TryDecodeOutput(messages[i], out var stream, out var piece, out var goesOn));
            Assert.Equal(ShellStream.Error, stream);
            Assert.Equal(i < messages.Count - 1, goesOn);
            Assert.DoesNotContain('�', piece);
            if (arriving.Add(stream, piece, goesOn, out var whole))
            {
                lines.Add(whole);
            }
        }

        Assert.Equal(line, Assert.Single(lines));
    }

    /// <summary>
    /// What a build older than this makes of the same messages: it knows nothing of a line going on, so each piece
    /// is a line to it — and between them the pieces are still every character of the line. And a line that fits
    /// in one message is, to the byte, the message it always was.
    /// </summary>
    [Fact]
    public void AnOlderBuildLosesNoneOfALongLineAndSeesShortOnesUnchanged()
    {
        var line = string.Concat(Enumerable.Repeat("строка без конца ", 3_000));

        var rejoined = new StringBuilder();
        foreach (var message in ShellCodec.EncodeOutputLine(1, ShellStream.Output, line))
        {
            // The older reader, in full: it stops at the end of the text and never looks past it.
            Assert.True(ShellCodec.TryDecodeOutput(message, out var stream, out var piece));
            Assert.Equal(ShellStream.Output, stream);
            rejoined.Append(piece);
        }

        Assert.Equal(line, rejoined.ToString());

        var only = Assert.Single(ShellCodec.EncodeOutputLine(1, ShellStream.Output, "короткая"));
        Assert.Equal(5 + 1 + 2 + Encoding.UTF8.GetByteCount("короткая"), only.Length);
        Assert.Equal(ShellCodec.EncodeOutput(1, ShellStream.Output, "короткая"), only);
    }

    /// <summary>
    /// The two streams are read apart on the device and their messages arrive interleaved, so a piece of an error
    /// line in the middle of a long output line belongs to neither half of it.
    /// </summary>
    [Fact]
    public void LinesOfTheTwoStreamsArePutTogetherApart()
    {
        var arriving = new OutputAssembly();

        Assert.False(arriving.Add(ShellStream.Output, "начало, ", goesOn: true, out _));
        Assert.True(arriving.Add(ShellStream.Error, "ошибка посередине", goesOn: false, out var error));
        Assert.False(arriving.Add(ShellStream.Error, "вторая ", goesOn: true, out _));
        Assert.True(arriving.Add(ShellStream.Output, "конец", goesOn: false, out var output));
        Assert.True(arriving.Add(ShellStream.Error, "ошибка", goesOn: false, out var second));

        Assert.Equal("ошибка посередине", error);
        Assert.Equal("начало, конец", output);
        Assert.Equal("вторая ошибка", second);
    }

    /// <summary>
    /// A line that is said to go on for ever is not held for ever: this is memory kept on the other side's say-so.
    /// Past the limit it is handed over as it stands and goes on as another line.
    /// </summary>
    [Fact]
    public void ALineThatNeverEndsIsHandedOverRatherThanHeldForEver()
    {
        var arriving = new OutputAssembly();
        var piece = new string('x', 2_000);

        var held = 0;
        string line;
        while (!arriving.Add(ShellStream.Output, piece, goesOn: true, out line))
        {
            held += piece.Length;
            Assert.True(held <= OutputAssembly.Longest, "more was held than the limit allows");
        }

        Assert.Equal(held + piece.Length, line.Length);
        Assert.False(arriving.Add(ShellStream.Output, piece, goesOn: true, out _));
    }

    /// <summary>
    /// The texts that are one message by nature are cut to fit when they are absurdly long, and the cut must not
    /// fall between the halves of one character. It did: the check looked for the second half of a pair where it
    /// should have looked for the first, so it either left the first half dangling or took a whole character apart
    /// to leave one — and a lone half encodes as a replacement mark.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public void TextCutToOneMessageIsNeverCutInsideACharacter(string before)
    {
        var text = before + string.Concat(Enumerable.Repeat("😀", 5_000));

        Assert.True(ShellCodec.TryDecodeText(ShellCodec.Encode(ShellMessageType.Refused, 1, text), out var reason));
        Assert.True(ShellCodec.TryDecodeOutput(ShellCodec.EncodeOutput(1, ShellStream.Output, text), out _, out var output));
        Assert.True(ShellCodec.TryDecodeDone(ShellCodec.EncodeDone(1, 0, text), out _, out var directory));

        foreach (var cut in new[] { reason, output, directory })
        {
            Assert.NotEmpty(cut);
            Assert.DoesNotContain('�', cut);
            Assert.StartsWith(cut, text, StringComparison.Ordinal);
            Assert.False(char.IsHighSurrogate(cut[^1]), "the cut left the first half of a character on the end");
        }
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
