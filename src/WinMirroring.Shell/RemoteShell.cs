using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinMirroring.Shell;

/// <summary>
/// One PowerShell on this device, kept alive and fed commands, with its output handed back as it appears.
///
/// Kept alive rather than started per command, because a console that forgets where it is between commands is not
/// a console: <c>cd</c>, variables and anything else a person sets up have to still be there for the next line.
/// PowerShell started with <c>-Command -</c> reads commands from its input and runs them as they arrive, which is
/// exactly what is wanted — measured on this machine: a command's output comes back within a few hundred
/// milliseconds of being sent, and a command that prints as it goes is seen printing.
///
/// What a command's output ends with is the problem that has to be solved: there is no prompt to look for and no
/// end-of-output signal in a pipe. So after each command a line is sent that prints a marker made up for this
/// console, the exit code and the current directory. Everything before it belongs to the command; the marker line
/// itself is never shown. The marker is random per console, so nothing a command prints can be mistaken for it.
///
/// Encoding has to be said four times over, and all four matter: the three pipes are UTF-8 here, and the console's
/// own output encoding is set inside the shell so that PowerShell and the native programs it starts also write
/// UTF-8. Without the input one, a command typed in Russian arrives at the shell as mojibake.
/// </summary>
internal sealed class RemoteShell : IHostedShell
{
    /// <summary>How long a shell that has been asked to close is given before it is ended outright.</summary>
    private static readonly TimeSpan GraceOnClose = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly string _marker;
    private readonly ILogger _logger;
    private readonly object _writeGate = new();
    private bool _disposed;

    private RemoteShell(Process process, string marker, ILogger logger)
    {
        _process = process;
        _marker = marker;
        _logger = logger;
    }

    /// <summary>
    /// A piece of what a command is saying, as it says it. Raised on the thread that reads the shell's output, so a
    /// handler that blocks stops the shell being read at all — nothing here may wait on anything.
    /// </summary>
    public event Action<ShellStreamKind, string>? Output;

    /// <summary>A command finished: its exit code, and where the console is now. Same thread, same rule.</summary>
    public event Action<int, string>? Done;

    /// <summary>The shell itself ended — it was told to exit, or it fell over.</summary>
    public event Action? Ended;

    /// <summary>
    /// Completes when the shell has finished its own first command and said where it is. Waited on before the far
    /// side is told the console is open — both so that it is told the right directory, and because that first
    /// command's own "finished" is nobody else's business: a caller that saw it would take it for the answer to the
    /// first command it ran, which is a mistake that looks exactly like a command producing nothing at all.
    /// </summary>
    public Task Ready => _ready.Task;

    public string Directory { get; private set; } = string.Empty;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _opened;

    /// <summary>Null when PowerShell could not be started at all.</summary>
    public static RemoteShell? TryStart(ILogger logger)
    {
        var marker = "wm" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NoLogo -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        try
        {
            if (Process.Start(start) is not { } process)
            {
                return null;
            }

            var shell = new RemoteShell(process, marker, logger);
            shell.Begin();
            return shell;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning("A console could not be started here: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Hands one command over. The answer comes as <see cref="Output"/> and then <see cref="Done"/>.</summary>
    public bool Run(string command)
    {
        // Sent as base64 and decoded on the other side rather than written out as itself. Two reasons, both found
        // the hard way: PowerShell reads its own input in the console's code page whatever encoding the pipe was
        // opened with, so a command with a Russian word in it arrived as mojibake and was faithfully run as
        // mojibake; and a command written as itself has to be one line, because the shell runs each line as it
        // comes. Base64 is ASCII, so no code page can touch it, and it carries newlines without them meaning
        // anything to the shell.
        // A line of our own on the end of what was sent, which is how the command's own success gets out. $? read
        // after Invoke-Expression is about Invoke-Expression: measured, it is false for a command that would not
        // parse and true for one whose last cmdlet failed, which is the common failure and the one a script cares
        // about. Asked inside the command instead, it is the command's own answer.
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(command + "\n$global:WinMirroringOk = $?"));

        // The exit code is cleared first, because $LASTEXITCODE is whatever the last program to exit left there and
        // one console runs many commands: without this, a command that ran "cmd /c exit 7" made every cmdlet after
        // it report 7 as well, right up to the next program. The success is cleared to nothing at all, which is
        // what it still is if the command never got far enough to answer.
        return Write(
            "$global:LASTEXITCODE = 0; $global:WinMirroringOk = $null"
            + Environment.NewLine
            + $"Invoke-Expression ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{payload}')))"
            + Environment.NewLine + MarkerCommand());
    }

    /// <summary>
    /// Stops what is running. There is no gentle way to interrupt one command of a shell reading from a pipe —
    /// Ctrl+C belongs to a console a user is sitting at — so the shell is ended and the caller starts another. The
    /// alternative would be a console that cannot be got out of, which is worse than one that has to be reopened.
    /// </summary>
    public void Cancel() => Dispose();

    /// <summary>
    /// The line that says a command is over: the marker, the exit code, whether the command succeeded at all, and
    /// where the console now is.
    ///
    /// Both numbers, because PowerShell keeps the two apart and a caller wants one answer. $LASTEXITCODE is what a
    /// program exited with and says nothing about a cmdlet or a script that failed — a script with a syntax error
    /// in it came back as a success, which is the worst possible answer for something a script is reading.
    ///
    /// Success is the command's own answer where the command managed to give one (the line added to it on the way
    /// out), and otherwise $? here — which is then about Invoke-Expression, and is false exactly when the command
    /// never ran at all. $? is read first, before anything else on this line: an assignment is itself a statement
    /// that succeeds, and reading $? after one would only say that the assignment went well.
    ///
    /// The code is cast to a number because PowerShell leaves $LASTEXITCODE unset until a program has exited, and
    /// an empty one here made the line a field short, so the directory was read as the exit code and then lost.
    /// </summary>
    private string MarkerCommand() =>
        "$ran = [int]$?; "
        + "$ok = if ($null -eq $global:WinMirroringOk) { $ran } else { [int]$global:WinMirroringOk }; "
        + "$code = [int]$LASTEXITCODE; "
        + $"Write-Output \"{_marker} $code $ok $((Get-Location).Path)\"";

    private void Begin()
    {
        _process.OutputDataReceived += (_, e) => OnLine(e.Data, ShellStreamKind.Output);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data, ShellStreamKind.Error);
        _process.Exited += (_, _) => Ended?.Invoke();
        _process.EnableRaisingEvents = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Before anything else, so that everything this shell and its children print arrives as UTF-8. The marker
        // line it ends with also tells the caller where the console starts.
        Write("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8"
            + Environment.NewLine + MarkerCommand());
    }

    private void OnLine(string? line, ShellStreamKind kind)
    {
        if (line is null)
        {
            return;
        }

        // The marker is looked for anywhere in the line, not only at the start of one. A program that ends its
        // output without a newline — powercfg /getactivescheme is one — leaves the line open, and the marker that
        // follows is then printed onto the end of it. Looked for only at the start, that marker was never seen:
        // the command never appeared to finish, the console sat there, and the whole thing ended as a lost
        // connection. What came before the marker on such a line is the command's own last words, and is passed
        // on as output. The marker itself is made up per console, so finding it inside a line means it is there.
        var at = kind == ShellStreamKind.Output ? line.IndexOf(_marker, StringComparison.Ordinal) : -1;
        if (at >= 0)
        {
            if (at > 0)
            {
                Output?.Invoke(kind, line[..at]);
            }

            // The exit code, whether it worked, and the directory — which is last because a path has spaces in it
            // and the other two never do.
            var parts = line[(at + _marker.Length)..].TrimStart().Split(' ', 3);
            var code = parts.Length > 0 && int.TryParse(parts[0], out var exit) ? exit : 0;
            var worked = parts.Length < 2 || parts[1] != "0";
            if (parts.Length > 2)
            {
                Directory = parts[2];
            }

            if (Interlocked.Exchange(ref _opened, 1) == 0)
            {
                _ready.TrySetResult(); // the shell's own first command; it belongs to nobody
                return;
            }

            // A program's own code when it gave one, and otherwise one if the command failed as PowerShell: a
            // script that would not even parse used to come back as a success, because nothing had exited.
            Done?.Invoke(code != 0 ? code : worked ? 0 : 1, Directory);
            return;
        }

        Output?.Invoke(kind, line);
    }

    private bool Write(string text)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            lock (_writeGate)
            {
                _process.StandardInput.WriteLine(text);
                _process.StandardInput.Flush();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug("The console here is no longer taking commands: {Message}", ex.Message);
            return false;
        }
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Asked to leave first: a shell in the middle of something gets the chance to finish writing.
            _process.StandardInput.Close();
            if (!_process.WaitForExit((int)GraceOnClose.TotalMilliseconds))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
        }

        _process.Dispose();
    }
}

/// <summary>Which of a shell's two streams a line came from.</summary>
internal enum ShellStreamKind
{
    Output,
    Error,
}
