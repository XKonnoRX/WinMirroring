using WinMirroring.Input.Keyboard;

namespace WinMirroring.Input.Tests;

public class KeyboardLayoutTests
{
    private const int EnglishUs = 0x04090409;
    private const int EnglishUsDvorak = unchecked((int)0xF0020409);
    private const int EnglishUk = 0x08090809;
    private const int Russian = 0x04190419;
    private const int German = 0x04070407;

    private static readonly nint Window = 100;
    private static readonly nint OtherWindow = 200;

    [Fact]
    public void MatchPrefersTheSameLayoutThenTheSameLanguageThenTheBaseLanguage()
    {
        Assert.Equal(EnglishUsDvorak, KeyboardLayouts.Match(EnglishUsDvorak, [EnglishUs, EnglishUsDvorak, Russian]));
        Assert.Equal(EnglishUs, KeyboardLayouts.Match(EnglishUsDvorak, [Russian, EnglishUs]));
        Assert.Equal(EnglishUs, KeyboardLayouts.Match(EnglishUk, [Russian, EnglishUs]));
        Assert.Equal(Russian, KeyboardLayouts.Match(Russian, [EnglishUs, Russian]));
    }

    [Fact]
    public void MatchFindsNothingForAMissingLanguage()
    {
        Assert.Null(KeyboardLayouts.Match(German, [EnglishUs, Russian]));
        Assert.Null(KeyboardLayouts.Match(0, [EnglishUs]));
        Assert.Null(KeyboardLayouts.Match(Russian, []));
    }

    [Fact]
    public void DisplayNameIsTheLanguage()
    {
        Assert.StartsWith("English", KeyboardLayouts.DisplayName(EnglishUs), StringComparison.Ordinal);
        Assert.StartsWith("Русский", KeyboardLayouts.DisplayName(Russian), StringComparison.Ordinal);
        Assert.Equal("0x0000", KeyboardLayouts.DisplayName(0));
    }

    [Fact]
    public void TheInstalledListHoldsTheActiveLayout()
    {
        var installed = KeyboardLayouts.Installed();
        Assert.NotEmpty(installed);
        Assert.Contains(KeyboardLayouts.OfCurrentThread(), installed);
        Assert.Equal(KeyboardLayouts.OfCurrentThread(), KeyboardLayouts.Match(KeyboardLayouts.OfCurrentThread(), installed));
    }

    [Fact]
    public void AskingForTheLayoutInFrontChangesNothing()
    {
        Assert.SkipUnless(KeyboardLayouts.TryGetForeground(out _, out var layout), "no window in front");
        Assert.Equal(LayoutSwitchResult.AlreadyActive, KeyboardLayouts.Activate(layout));
    }

    [Fact]
    public void TheUsersSwitchIsReportedOnce()
    {
        var filter = new KeyboardLayoutFilter();
        Assert.Null(filter.Observe(Window, EnglishUs, 0)); // starting point
        Assert.Null(filter.Observe(Window, EnglishUs, 150));
        Assert.Equal(Russian, filter.Observe(Window, Russian, 300));
        Assert.Null(filter.Observe(Window, Russian, 450));
        Assert.Equal(EnglishUs, filter.Observe(Window, EnglishUs, 600));
    }

    [Fact]
    public void ASwitchRequestedByAnotherDeviceIsNotReportedBack()
    {
        var filter = new KeyboardLayoutFilter();
        filter.Observe(Window, EnglishUs, 0);
        filter.Expect(Russian, 100);
        Assert.Null(filter.Observe(Window, EnglishUs, 150)); // the window has not handled the request yet
        Assert.Null(filter.Observe(Window, Russian, 300));
        Assert.Equal(EnglishUs, filter.Observe(Window, EnglishUs, 450)); // and then the user switches back
    }

    [Fact]
    public void ARefusedRequestStopsBeingExpected()
    {
        var filter = new KeyboardLayoutFilter();
        filter.Observe(Window, EnglishUs, 0);
        filter.Expect(Russian, 100);
        Assert.Null(filter.Observe(Window, EnglishUs, 100 + KeyboardLayoutFilter.ExpectMs + 1));
        Assert.Equal(Russian, filter.Observe(Window, Russian, 100 + KeyboardLayoutFilter.ExpectMs + 200));
    }

    [Fact]
    public void AnotherWindowWithItsOwnLayoutIsNotASwitch()
    {
        var filter = new KeyboardLayoutFilter();
        filter.Observe(Window, EnglishUs, 0);
        Assert.Null(filter.Observe(OtherWindow, Russian, 150));
        Assert.Equal(EnglishUs, filter.Observe(OtherWindow, EnglishUs, 300));
    }

    [Fact]
    public void ResetStartsOver()
    {
        var filter = new KeyboardLayoutFilter();
        filter.Observe(Window, EnglishUs, 0);
        filter.Reset();
        Assert.Null(filter.Observe(Window, Russian, 150));
        Assert.Null(filter.Observe(0, EnglishUs, 300));
        Assert.Null(filter.Observe(Window, 0, 450));
    }
}
