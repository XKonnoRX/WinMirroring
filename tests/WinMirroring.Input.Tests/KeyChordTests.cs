using WinMirroring.Input.Keyboard;

namespace WinMirroring.Input.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T", "Ctrl+Shift+T")]
    [InlineData("shift + ctrl + t", "Ctrl+Shift+T")]
    [InlineData("Control+Escape", "Ctrl+Esc")]
    [InlineData("Win+R", "Win+R")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    [InlineData("RightCtrl", "Ctrl")]
    [InlineData("PgDn", "PageDown")]
    [InlineData("Num5", "Num5")]
    public void ChordsAreReadAndWrittenTheSameWay(string text, string written)
    {
        Assert.True(KeyChord.TryParse(text, out var chord, out var error), error);
        Assert.Equal(written, chord.ToString());
    }

    [Fact]
    public void ASequenceIsSeveralChordsInOrder()
    {
        Assert.True(KeyChord.TryParseSequence("Ctrl+K, Ctrl+C", out var chords, out _));

        Assert.Equal(2, chords.Count);
        Assert.Equal([KeyChord.Ctrl], chords[0].Modifiers);
        Assert.Equal((ushort)'K', chords[0].Key);
        Assert.Equal((ushort)'C', chords[1].Key);
        Assert.Equal("Ctrl+K, Ctrl+C", KeyChord.FormatSequence(chords));
    }

    [Fact]
    public void ModifiersAloneMakeAChordWithoutAKey()
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift", out var chord, out _));

        Assert.True(chord.IsModifiersOnly);
        Assert.Equal([KeyChord.Ctrl, KeyChord.Shift], chord.Modifiers);
    }

    [Theory]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl+Банан")]
    [InlineData("+")]
    public void NonsenseIsRefusedWithAReason(string text)
    {
        Assert.False(KeyChord.TryParseSequence(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NoKeysAtAllIsAnEmptySequence()
    {
        Assert.True(KeyChord.TryParseSequence("  ", out var chords, out _));
        Assert.Empty(chords);
    }

    [Fact]
    public void NavigationKeysAndWinAreExtendedLettersAreNot()
    {
        Assert.True(KeyChord.IsExtended(0x25)); // Left
        Assert.True(KeyChord.IsExtended(KeyChord.Win));
        Assert.False(KeyChord.IsExtended((ushort)'A'));
        Assert.False(KeyChord.IsExtended(KeyChord.Ctrl));
    }

    [Fact]
    public void EveryRightOrGenericModifierMapsToTheLeftOne()
    {
        Assert.Equal(KeyChord.Ctrl, KeyChord.AsModifier(0x11));
        Assert.Equal(KeyChord.Ctrl, KeyChord.AsModifier(0xA3));
        Assert.Equal(KeyChord.Shift, KeyChord.AsModifier(0xA1));
        Assert.Equal(KeyChord.Alt, KeyChord.AsModifier(0x12));
        Assert.Equal(KeyChord.Win, KeyChord.AsModifier(0x5C));
        Assert.Null(KeyChord.AsModifier((ushort)'A'));
    }
}
