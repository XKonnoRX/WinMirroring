namespace WinMirroring.Screen.Tests;

/// <summary>
/// Which views are opened again without being asked. The rule is about who ended it: nobody — the connection went,
/// or the other device's application did — and it comes back; somebody, at either end, and it stays closed.
/// </summary>
public class ViewEndingTests
{
    [Fact]
    public void AViewWhoseConnectionWentIsOpenedAgain()
    {
        Assert.True(ViewEndings.CanResume(lostConnection: true, "соединение закрыто"));
        Assert.True(ViewEndings.CanResume(lostConnection: true, null));
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("error")]
    [InlineData("connection: Unable to write data to the transport connection")]
    public void SoIsOneWhoseSourceWentAwayOrFailed(string reason) =>
        Assert.True(ViewEndings.CanResume(lostConnection: false, reason));

    [Theory]
    [InlineData("stopped")]
    [InlineData("closed by viewer")]
    [InlineData("window closed")]
    [InlineData("viewing is not allowed")]
    [InlineData(null)]
    public void OneThatSomebodyEndedIsNot(string? reason) =>
        Assert.False(ViewEndings.CanResume(lostConnection: false, reason));

    [Fact]
    public void AViewThatKeepsEndingIsNotReopenedForEver()
    {
        var earlier = new List<long>();
        long now = 100_000;

        for (var i = 0; i < ViewEndings.MostInARow; i++)
        {
            Assert.True(ViewEndings.AllowAnother(earlier, now));
            now += 5_000;
        }

        Assert.False(ViewEndings.AllowAnother(earlier, now));

        // Later, when those have stopped counting, it may be tried again.
        now += (long)ViewEndings.InARowWithin.TotalMilliseconds;
        Assert.True(ViewEndings.AllowAnother(earlier, now));
    }
}
