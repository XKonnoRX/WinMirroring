using Microsoft.Extensions.Logging;
using WinMirroring.Core;
using WinMirroring.Core.Peers;
using WinMirroring.Shell;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Cli;

/// <summary>
/// Running one command on a paired device and reporting it as if it had been run here: its output on this process's
/// output, its errors on this process's errors, its exit code as this process's exit code.
///
/// That last part is the point of the whole mode. A tool that prints a transcript is something a person reads; a
/// tool that behaves like the command it ran is something a script — or anything else that drives a computer — can
/// build on. So nothing of ours is allowed onto standard output, and the code that comes back is the command's and
/// not a verdict on whether the connection worked. The codes that are ours are above anything a command uses:
/// 64 for a device that could not be reached, 65 for one that said no, 66 for a settings directory that belongs to
/// another device. The last one is not about --exec, but it belongs with the others: they are what this tool says
/// about itself rather than about a command.
/// </summary>
internal static class Exec
{
    public const int CouldNotReach = 64;
    public const int Refused = 65;
    public const int DirectoryInUse = 66;

    /// <summary>How long a device is waited for before giving up, when --timeout did not say.</summary>
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        MirroringNode node,
        ShellService shell,
        CliOptions options,
        TextWriter stdout,
        CancellationToken ct)
    {
        var command = await CommandTextAsync(options, ct).ConfigureAwait(false);
        if (command is null)
        {
            return CouldNotReach;
        }

        var wanted = options.ExecOn!;
        var session = await WaitForDeviceAsync(node, wanted, options.TimeoutSeconds, ct).ConfigureAwait(false);
        if (session is null)
        {
            Console.Error.WriteLine($"wm-cli: {wanted} is not connected. It has to be paired with this tool and switched on.");
            return CouldNotReach;
        }

        // The service attaches to a session from the session-ready event, which comes after the session already
        // calls itself ready — so a console asked for in between would be asked for down a channel nobody is
        // listening on yet.
        for (var i = 0; i < 50 && !shell.IsAttachedTo(session.PeerId); i++)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        var console = shell.Open(session.PeerId);
        if (console is null)
        {
            Console.Error.WriteLine($"wm-cli: {session.PeerName} has no session to open a console on.");
            return CouldNotReach;
        }

        // Continuations off the thread that raised these: they arrive on the session's receive loop, and anything
        // that waits there stops the session being read at all.
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        console.Opened += _ => console.Run(command);
        console.Refused += reason =>
        {
            Console.Error.WriteLine($"wm-cli: {session.PeerName} refused: {reason}.");
            finished.TrySetResult(Refused);
        };

        console.Output += (stream, text) =>
        {
            if (stream == ShellStream.Error)
            {
                Console.Error.WriteLine(text);
            }
            else
            {
                stdout.WriteLine(text);
            }
        };

        console.Done += (code, _) => finished.TrySetResult(code);
        console.Finished += () => finished.TrySetResult(Refused);

        try
        {
            var code = await finished.Task.WaitAsync(ct).ConfigureAwait(false);
            stdout.Flush();
            return code;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("wm-cli: stopped waiting for the command.");
            console.Cancel();
            return CouldNotReach;
        }
        finally
        {
            console.Close();
        }
    }

    /// <summary>The command itself, or what the file holds — a whole script, of any length.</summary>
    private static async Task<string?> CommandTextAsync(CliOptions options, CancellationToken ct)
    {
        if (options.CommandFile is not { Length: > 0 } path)
        {
            return options.Command;
        }

        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"wm-cli: {path} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Waits until the named device has a session that is ready — it has to be discovered, connected and paired,
    /// and all three take a moment from a standing start. Matched on name or id, either case.
    /// </summary>
    private static async Task<PeerSession?> WaitForDeviceAsync(MirroringNode node, string wanted, int? timeoutSeconds, CancellationToken ct)
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

            var wait = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultWait;
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
