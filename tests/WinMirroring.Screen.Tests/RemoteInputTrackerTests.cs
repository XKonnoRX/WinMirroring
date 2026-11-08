namespace WinMirroring.Screen.Tests;

public sealed class RemoteInputTrackerTests
{
    [Fact]
    public void RecognisesTheViewersPressAtItsPointOnly()
    {
        RemoteInputTracker.NoteDown(500, 300);

        Assert.True(RemoteInputTracker.IsRemote(500, 300));
        Assert.True(RemoteInputTracker.IsRemote(508, 293));
        Assert.False(RemoteInputTracker.IsRemote(560, 300));
        Assert.False(RemoteInputTracker.IsRemote(500, 340));
    }
}
