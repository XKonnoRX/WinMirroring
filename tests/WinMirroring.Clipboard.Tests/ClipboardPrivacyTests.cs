using WinMirroring.Clipboard.Native;

namespace WinMirroring.Clipboard.Tests;

/// <summary>
/// The marks a password manager puts on what it copies, and what this app does about them. Getting this wrong
/// fails silently — a password simply appears on the other machine and nobody notices — so the whole decision is
/// written out here rather than sampled.
/// </summary>
public class ClipboardPrivacyTests
{
    [Fact]
    public void NothingMarkedMeansNothingForbidden()
    {
        Assert.Null(ClipboardPrivacy.WhySyncIsRefused(false, null, null));

        // The formats are there and say yes.
        Assert.Null(ClipboardPrivacy.WhySyncIsRefused(false, 1, 1));
    }

    [Fact]
    public void ContentExcludedFromMonitorsIsLeftAlone()
    {
        // The format carries nothing worth reading: that it is there at all is the message.
        Assert.Equal(ClipboardPrivacy.MonitorsForbidden, ClipboardPrivacy.WhySyncIsRefused(true, null, null));

        // And it holds even where the other two would have allowed it.
        Assert.Equal(ClipboardPrivacy.MonitorsForbidden, ClipboardPrivacy.WhySyncIsRefused(true, 1, 1));
    }

    [Fact]
    public void ContentThatMayNotLeaveTheMachineIsNotSent()
    {
        Assert.Equal(ClipboardPrivacy.DevicesForbidden, ClipboardPrivacy.WhySyncIsRefused(false, 0, 1));
        Assert.Equal(ClipboardPrivacy.DevicesForbidden, ClipboardPrivacy.WhySyncIsRefused(false, 0, null));
    }

    /// <summary>
    /// Keeping a local history is a smaller thing than sending content to another machine, so an application that
    /// refuses the smaller one is refusing the larger one too.
    /// </summary>
    [Fact]
    public void ContentThatMayNotBeRememberedIsNotSentEither()
    {
        Assert.Equal(ClipboardPrivacy.HistoryForbidden, ClipboardPrivacy.WhySyncIsRefused(false, null, 0));
        Assert.Equal(ClipboardPrivacy.HistoryForbidden, ClipboardPrivacy.WhySyncIsRefused(false, 1, 0));
    }

    /// <summary>A permission's value is a flag, not a count: anything but zero is yes.</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void AnyValueButZeroAllowsIt(uint value)
    {
        Assert.Null(ClipboardPrivacy.WhySyncIsRefused(false, value, value));
    }
}
