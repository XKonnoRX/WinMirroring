using WinMirroring.Core;
using WinMirroring.Shell;
using WinMirroring.Shell.Local;
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
/// another device, 67 for a console that ended without giving the command's code. The one exception is 1 for a
/// call that names nothing to run, as for any other mistake on the command line.
///
/// There are two ways to the device and the work of both is the same code (<see cref="RemoteCommand"/>). Through
/// the app running on this machine, which is the usual one and the reason this tool is not a device: the app has
/// the sessions and the pairings already, so nothing new appears in anyone's device list. Or as a device of its
/// own, which is for a machine with no app on it at all.
/// </summary>
internal static class Exec
{
    public const int CouldNotReach = ShellExit.CouldNotReach;
    public const int Refused = ShellExit.Refused;
    public const int DirectoryInUse = 66;
    public const int ConsoleEnded = ShellExit.ConsoleEnded;

    /// <summary>The tool was called wrongly — the same code an unknown argument ends with.</summary>
    public const int BadUsage = 1;

    /// <summary>Through the app on this machine: it runs the command under the identity devices already trust.</summary>
    public static async Task<int> RunThroughAppAsync(
        LocalShellClient app,
        CliOptions options,
        string command,
        TextWriter stdout,
        CancellationToken ct)
    {
        try
        {
            var code = await app.RunAsync(
                options.ExecOn!,
                command,
                (stream, text) => Write(stream, text, stdout),
                Complain,
                ct,
                options.Elevated,
                Waiting).ConfigureAwait(false);

            stdout.Flush();
            return code;
        }
        catch (OperationCanceledException)
        {
            // --timeout, or Ctrl+C. Caught here because this is where the waiting is: left to fall out of the
            // program it was an unhandled exception and a crash code of its own (-532462766), which is no answer
            // at all to something reading exit codes.
            Complain("время ожидания команды вышло.");
            stdout.Flush();
            return CouldNotReach;
        }
    }

    /// <summary>As a device of its own, for a machine where no app is running.</summary>
    public static async Task<int> RunAsync(
        MirroringNode node,
        ShellService shell,
        CliOptions options,
        string command,
        TextWriter stdout,
        CancellationToken ct)
    {
        try
        {
            var code = await RemoteCommand.RunAsync(
                node,
                shell,
                options.ExecOn!,
                command,
                (stream, text) => Write(stream, text, stdout),
                Complain,
                Waiting,
                options.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : RemoteCommand.DefaultWait,
                options.Elevated,
                ct).ConfigureAwait(false);

            stdout.Flush();
            return code;
        }
        catch (OperationCanceledException)
        {
            Complain("время ожидания команды вышло.");
            stdout.Flush();
            return CouldNotReach;
        }
    }

    /// <summary>
    /// The command itself, or what the file holds — a whole script, of any length. Null, with the reason already
    /// said, when there is nothing to run: the file could not be read, or what was given is empty.
    ///
    /// Both are mistakes in how this tool was called and end as one (<see cref="BadUsage"/>), before any device is
    /// looked for. They used not to: a file that was not there came back as 64, which says the device could not be
    /// reached and sends whoever reads it off to wake a tablet; and an empty command was sent all the way to the
    /// device, where a console refuses to run nothing and says nothing either, so the tool waited for an answer
    /// until somebody stopped it.
    /// </summary>
    public static async Task<string?> CommandTextAsync(CliOptions options, CancellationToken ct)
    {
        var path = options.CommandFile is { Length: > 0 } file ? file : null;
        var command = options.Command;
        if (path is not null)
        {
            try
            {
                command = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"wm-cli: {path} could not be read: {ex.Message}");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine(path is not null
                ? $"wm-cli: {path} is empty: there is nothing to run."
                : "wm-cli: the command is empty: there is nothing to run.");
            return null;
        }

        return command;
    }

    private static void Write(ShellStream stream, string text, TextWriter stdout)
    {
        if (stream == ShellStream.Error)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            stdout.WriteLine(text);
        }
    }

    private static void Complain(string reason) => Console.Error.WriteLine($"wm-cli: {reason}");

    /// <summary>
    /// The device is asking a person something before it will say yes. Said out loud, on standard error, because
    /// the alternative is what actually happened: a wait that looks exactly like a broken device, and whoever is
    /// driving this spending an hour on the connection while a dialog sits on a tablet in another room waiting to
    /// be tapped. A command is not lost while this is printed — it runs as soon as that person answers.
    /// </summary>
    private static void Waiting(string what) => Console.Error.WriteLine($"wm-cli: {what}. Ждём, команда пойдёт сразу после ответа.");
}
