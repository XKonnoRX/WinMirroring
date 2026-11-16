using WinMirroring.Core.Peers;

namespace WinMirroring.Core.Tests;

/// <summary>
/// Noticing a device that has gone without saying so. A sleeping device on Wi-Fi closes nothing: the socket stays
/// open, what is sent to it simply never arrives, and TCP retransmits for the better part of an hour before it
/// gives up. The heartbeat is what is supposed to notice — and it could not, because it was waiting inside the
/// very write that never came back, so the silence it checks was never looked at again. A tablet that fell asleep
/// stayed listed as connected for fifty-three minutes.
/// </summary>
public class HeartbeatTests
{
    private static readonly TimeSpan Within = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task AHeartbeatThatIsTakenInTimeIsNothingToWorryAbout()
    {
        await PeerSession.SentWithinAsync(Task.CompletedTask, Within, "tablet", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AHeartbeatThatCannotBeWrittenInTimeEndsTheSession()
    {
        // A write that never returns, which is what a sleeping peer's socket does.
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var gaveUp = await Assert.ThrowsAsync<TimeoutException>(
            () => PeerSession.SentWithinAsync(stuck.Task, Within, "tablet", TestContext.Current.CancellationToken));

        Assert.Contains("tablet", gaveUp.Message, StringComparison.Ordinal);
        Assert.Contains("asleep or gone", gaveUp.Message, StringComparison.Ordinal);

        // And what became of the abandoned write is nobody's business: the session is closed and the stream with
        // it, so the write fails later, and that failure must not surface anywhere as an unhandled thing.
        stuck.TrySetException(new IOException("the connection went while nobody was waiting"));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Waiting for it is given up on when the session is being closed for its own reasons, too.</summary>
    [Fact]
    public async Task WaitingForAHeartbeatEndsWithTheSession()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closing = new CancellationTokenSource();

        var waiting = PeerSession.SentWithinAsync(stuck.Task, TimeSpan.FromMinutes(5), "tablet", closing.Token);
        await closing.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        stuck.TrySetResult();
    }
}
