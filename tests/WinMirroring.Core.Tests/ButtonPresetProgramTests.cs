using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Tests;

/// <summary>
/// A set of buttons can be for particular programs, and is then the one shown while one of them is in front on the
/// device being watched. What has to hold is the matching: names as people type them, and nothing chosen for a
/// program no set names.
/// </summary>
public class ButtonPresetProgramTests
{
    private static readonly ButtonPreset Main = new() { Name = "Основные" };
    private static readonly ButtonPreset Browser = new() { Name = "Браузер", Programs = "chrome, MSEdge.exe;firefox" };
    private static readonly ButtonPreset Editor = new() { Name = "Редактор", Programs = "Code  devenv" };

    private static readonly ButtonPreset[] Sets = [Main, Browser, Editor];

    [Theory]
    [InlineData("chrome", "Браузер")]
    [InlineData("msedge", "Браузер")]
    [InlineData("MSEDGE.EXE", "Браузер")]
    [InlineData("firefox", "Браузер")]
    [InlineData("code", "Редактор")]
    [InlineData("Devenv", "Редактор")]
    public void AProgramGetsTheSetThatNamesIt(string process, string expected) =>
        Assert.Equal(expected, ButtonPreset.For(Sets, process)?.Name);

    [Theory]
    [InlineData("notepad")]
    [InlineData("chromium")] // not a prefix match: a different program
    [InlineData("")]
    [InlineData(null)]
    public void AProgramNoSetNamesGetsNone(string? process) =>
        Assert.Null(ButtonPreset.For(Sets, process));

    [Fact]
    public void ASetWithNoProgramsIsNeverChosenByItself() =>
        Assert.Empty(Main.ProgramNames());

    [Fact]
    public void TheFirstSetThatNamesAProgramWins()
    {
        ButtonPreset[] both = [new() { Name = "A", Programs = "code" }, new() { Name = "B", Programs = "code" }];

        Assert.Equal("A", ButtonPreset.For(both, "code")?.Name);
    }

    [Fact]
    public void ACopyOfASetIsForTheSamePrograms() =>
        Assert.Equal(Browser.Programs, Browser.Clone().Programs);
}
