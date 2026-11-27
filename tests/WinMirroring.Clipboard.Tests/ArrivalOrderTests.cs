namespace WinMirroring.Clipboard.Tests;

/// <summary>
/// The newest content that reached the device is what stays on its clipboard, whatever order the applying finishes
/// in. The clipboard itself is not touched here: the write is whatever the test passes in.
/// </summary>
public sealed class ArrivalOrderTests
{
    [Fact]
    public void ContentAppliedInTheOrderItArrivedIsAllApplied()
    {
        var order = new ArrivalOrder();
        var applied = new List<string>();
        var first = order.Next();
        var second = order.Next();

        Assert.True(order.Apply(first, () => applied.Add("first")));
        Assert.True(order.Apply(second, () => applied.Add("second")));

        Assert.Equal(["first", "second"], applied);
    }

    [Fact]
    public void WhatArrivedEarlierButIsReadyLaterIsNotApplied()
    {
        var order = new ArrivalOrder();
        var applied = new List<string>();

        // A large image arrives as a reference and has to be fetched; a short text arrives after it and is applied
        // while the image is still on its way.
        var image = order.Next();
        var text = order.Next();
        Assert.False(order.IsSuperseded(image));
        Assert.True(order.Apply(text, () => applied.Add("text")));

        Assert.True(order.IsSuperseded(image));
        Assert.False(order.Apply(image, () => applied.Add("image")));
        Assert.Equal(["text"], applied);
    }

    [Fact]
    public void ANewerOneThatCouldNotBeWrittenDoesNotKeepAnOlderOneOut()
    {
        var order = new ArrivalOrder();
        var older = order.Next();
        var newer = order.Next();

        Assert.Throws<IOException>(() => order.Apply(newer, () => throw new IOException("The clipboard is held by another application.")));

        Assert.False(order.IsSuperseded(older));
        Assert.True(order.Apply(older, () => { }));
    }

    [Fact]
    public void WhatTheUserCopiesHereIsNewerThanWhatIsStillOnItsWayIn()
    {
        var order = new ArrivalOrder();
        var onItsWay = order.Next();

        order.CopiedHere();

        Assert.False(order.Apply(onItsWay, () => Assert.Fail("older content was put over what the user has just copied")));

        // What arrives after that is newer again.
        Assert.True(order.Apply(order.Next(), () => { }));
    }

    [Fact]
    public async Task OfManyAppliedAtOnceTheLastToArriveIsWhatStays()
    {
        var order = new ArrivalOrder();
        var arrivals = Enumerable.Range(0, 200).Select(_ => order.Next()).ToList();
        var onClipboard = 0L;

        // Each on a thread of its own, as the service applies them; the write is under the order's own lock.
        await Task.WhenAll(arrivals.Select(arrival => Task.Run(() => order.Apply(arrival, () => onClipboard = arrival), TestContext.Current.CancellationToken)));

        Assert.Equal(arrivals[^1], onClipboard);
    }
}
