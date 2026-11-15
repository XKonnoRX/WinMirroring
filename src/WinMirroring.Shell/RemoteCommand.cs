using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell;

/// <summary>What became of an attempt to run something somewhere, when it was not the command's own exit code.</summary>
public static class ShellExit
{
    /// <summary>The device was not reachable: not discovered, not connected, or not paired with this one.</summary>
    public const int CouldNotReach = 64;

    /// <summary>The device said no: commands are turned off over there, or whoever is at it refused.</summary>
    public const int Refused = 65;
}

/// <summary>
/// One command, run on one device, reported as if it had been run here. The whole of it — find the device, open a
/// console on it, send the line, pass back what it prints, end with its exit code.
///
/// It lives here, beside the service rather than inside the tool that first needed it, because two callers want
/// exactly this and must not differ in what they do: the command-line tool, when it is the only node on the
/// machine, and the app, when the tool asks it to do the running (see <see cref="Local.LocalShellServer"/>). A
/// device should not be able to tell which of the two asked.
/// </summary>
public static class RemoteCommand
{
    /// <summary>How long a device is waited for when the caller does not say.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <paramref name="output"/> is called with each piece as it arrives, and <paramref name="failed"/> once with
    /// a reason, in Russian, when there is no exit code to give — the device could not be reached, or said no.
    /// </summary>
    public static async Task<int> RunAsync(
        MirroringNode node,
        ShellService shell,
        string target,
        string command,
        Action<ShellStream, string> output,
        Action<string> failed,
        TimeSpan? wait = null,
        bool elevated = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(failed);

        var session = await WaitForDeviceAsync(node, target, wait ?? DefaultWait, ct).ConfigureAwait(false);
        if (session is null)
        {
            failed($"{target} не на связи: устройство должно быть спарено с этим и включено.");
            return ShellExit.CouldNotReach;
        }

        // The service attaches to a session from the session-ready event, which comes after the session already
        // calls itself ready — so a console asked for in between would be asked for down a channel nobody is
        // listening on yet.
        for (var i = 0; i < 50 && !shell.IsAttachedTo(session.PeerId); i++)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        var console = shell.Open(session.PeerId, elevated);
        if (console is null)
        {
            failed($"{session.PeerName}: сессия закрылась, консоль открыть не на чем.");
            return ShellExit.CouldNotReach;
        }

        // Continuations off the thread that raised these: they arrive on the session's receive loop, and anything
        // that waits there stops the session being read at all.
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        console.Opened += _ => console.Run(command);
        console.Refused += reason =>
        {
            failed($"{session.PeerName} отказал: {reason}.");
            finished.TrySetResult(ShellExit.Refused);
        };

        console.Output += (stream, text) => output(stream, text);
        console.Done += (code, _) => finished.TrySetResult(code);
        console.Finished += () => finished.TrySetResult(ShellExit.Refused);

        try
        {
            return await finished.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failed("ожидание команды прервано.");
            console.Cancel();
            return ShellExit.CouldNotReach;
        }
        finally
        {
            console.Close();
        }
    }

    /// <summary>
    /// Waits until the named device has a session that is ready — it has to be discovered, connected and paired,
    /// and all three take a moment from a standing start. Matched on name or id, either case.
    /// </summary>
    private static async Task<PeerSession?> WaitForDeviceAsync(MirroringNode node, string wanted, TimeSpan wait, CancellationToken ct)
    {
        var found = new TaskCompletionSource<PeerSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Consider(PeerSession session)
        {
            if (session.State == SessionState.Ready && Matches(session, wanted))
            {
                found.TrySetResult(session);
            }
        }

        node.Sessions.SessionReady += Consider;
        try
        {
            foreach (var session in node.Sessions.Sessions)
            {
                Consider(session);
            }

            return await found.Task.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            node.Sessions.SessionReady -= Consider;
        }
    }

    private static bool Matches(PeerSession session, string wanted) =>
        string.Equals(session.PeerName, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.PeerId, wanted, StringComparison.OrdinalIgnoreCase);
}
