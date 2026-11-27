using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Shell;

namespace WinMirroring.Shell.Tests;

/// <summary>
/// The console itself, run on this machine. What has to be true of it: a command's output comes back, it comes back
/// as it is printed rather than all at the end, the exit code is the command's own, and what one command does to
/// the console is still true for the next one — a console that forgets where it is between commands is not a
/// console. And it has to carry text that is not ASCII in both directions, which is the thing that was wrong first
/// time round: the shell's input pipe has an encoding too, and a command typed in Russian arrived as mojibake.
/// </summary>
public class RemoteShellTests : IAsyncLifetime
{
    private readonly RemoteShell _shell;
    private readonly List<(ShellStreamKind Kind, string Text)> _output = [];
    private readonly List<(int Code, string Directory)> _done = [];

    public RemoteShellTests()
    {
        _shell = RemoteShell.TryStart(NullLogger.Instance)!;
        Assert.NotNull(_shell);
        _shell.Output += (kind, text) =>
        {
            lock (_output)
            {
                _output.Add((kind, text));
            }
        };

        _shell.Done += (code, directory) =>
        {
            lock (_done)
            {
                _done.Add((code, directory));
            }
        };

    }

    /// <summary>
    /// The shell settles its own first command — the one that sets the encoding up — before any of this means
    /// anything. That one does not count as a command finishing, on purpose: a caller that saw it would take it
    /// for the answer to the first command it ran.
    /// </summary>
    public async ValueTask InitializeAsync() => await _shell.Ready.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _shell.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ACommandSaysWhatItSaysAndEndsWithItsOwnExitCode()
    {
        Run("Write-Output 'hello'");

        Assert.Contains("hello", Lines());
        Assert.Equal(0, _done[^1].Code);
    }

    /// <summary>The exit code is the command's, not the shell's idea of whether the line parsed.</summary>
    [Fact]
    public void AFailingCommandBringsBackItsCode()
    {
        Run("cmd /c exit 7");

        Assert.Equal(7, _done[^1].Code);
    }

    /// <summary>What a command writes to the error stream is kept apart from what it writes to the other one.</summary>
    [Fact]
    public void TheTwoStreamsStayApart()
    {
        Run("Write-Output 'fine'; Write-Error 'not fine'");

        Assert.Contains(_output, o => o.Kind == ShellStreamKind.Output && o.Text.Contains("fine", StringComparison.Ordinal));
        Assert.Contains(_output, o => o.Kind == ShellStreamKind.Error && o.Text.Contains("not fine", StringComparison.Ordinal));
    }

    /// <summary>
    /// The point of keeping one shell alive: a command that moves the console leaves it moved. Without this a
    /// console would start every line in the same place, which is not something anyone could manage a device with.
    /// </summary>
    [Fact]
    public void WhereTheConsoleIsFollowsWhatTheCommandsDoToIt()
    {
        Run("Set-Location C:\\Windows");

        Assert.Equal("C:\\Windows", _done[^1].Directory, ignoreCase: true);
        Assert.Equal("C:\\Windows", _shell.Directory, ignoreCase: true);

        Run("Write-Output (Get-Location).Path");
        Assert.Contains(Lines(), line => line.Equals("C:\\Windows", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A variable set on one line is still set on the next, for the same reason.</summary>
    [Fact]
    public void WhatACommandSetsUpIsStillThereForTheNextOne()
    {
        Run("$wm = 'remembered'");
        Run("Write-Output $wm");

        Assert.Contains("remembered", Lines());
    }

    /// <summary>
    /// Both directions, with text that is not ASCII: the command goes in as Russian and has to arrive as Russian,
    /// and what it prints has to come back the same way.
    /// </summary>
    [Fact]
    public void TextThatIsNotAsciiSurvivesBothWays()
    {
        Run("Write-Output 'привет, мир'");

        Assert.Contains("привет, мир", Lines());
    }

    /// <summary>
    /// A command that prints as it goes is seen printing, rather than arriving in one piece at the end. This is
    /// what makes a console worth having for something slow — a script, an install, a copy.
    /// </summary>
    [Fact]
    public async Task OutputArrivesWhileTheCommandIsStillRunning()
    {
        // Continuations off this thread on purpose: the event is raised on the thread that reads the shell's
        // output, so waiting on it and then carrying on there would stop the shell being read at all — which is
        // exactly what happened the first time, and is the same trap anything handling these events can fall into.
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shell.Output += (_, text) =>
        {
            if (text.Contains("tick 1", StringComparison.Ordinal))
            {
                seen.TrySetResult();
            }
        };

        var clock = Stopwatch.StartNew();
        Assert.True(_shell.Run("1..6 | ForEach-Object { Write-Output \"tick $_\"; Start-Sleep -Milliseconds 300 }"));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var first = clock.Elapsed;

        WaitForDone(_done.Count + 1);
        Assert.True(
            clock.Elapsed > first + TimeSpan.FromMilliseconds(600),
            $"the whole command took {clock.Elapsed.TotalMilliseconds:F0} ms and the first line came at {first.TotalMilliseconds:F0} ms, "
                + "so it was not being watched as it ran");
    }

    /// <summary>
    /// A command that ends the shell itself still has an answer: the code the shell exited with. It never reaches
    /// the line that says a command is over, so it used to have none — the console just closed, and whoever ran
    /// <c>exit 3</c> was told the device had refused. What it printed first still comes first.
    /// </summary>
    [Fact]
    public async Task ACommandThatEndsTheShellBringsBackTheCodeItEndedWith()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string>? saidBeforeTheCode = null;
        _shell.Done += (_, _) => saidBeforeTheCode ??= Lines();
        _shell.Ended += () => ended.TrySetResult();

        Assert.True(_shell.Run("Write-Output 'last words'; exit 3"));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var only = Assert.Single(_done);
        Assert.Equal(3, only.Code);
        Assert.NotNull(saidBeforeTheCode);
        Assert.Contains("last words", saidBeforeTheCode);
    }

    /// <summary>
    /// A shell ended from here is another matter: nobody is waiting on its command any more, and a code turning up
    /// would be read as the answer to one.
    /// </summary>
    [Fact]
    public async Task AShellEndedFromHereReportsNoCommandAsFinished()
    {
        Assert.True(_shell.Run("Start-Sleep -Seconds 30"));

        // Comes back once the shell is gone: asked to leave, given its moment, then ended outright.
        _shell.Cancel();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        lock (_done)
        {
            Assert.Empty(_done);
        }
    }

    private void Run(string command)
    {
        var want = _done.Count + 1;
        Assert.True(_shell.Run(command));
        WaitForDone(want);
    }

    private void WaitForDone(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            lock (_done)
            {
                if (_done.Count >= count)
                {
                    return;
                }
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"the console did not finish a command in time ({_done.Count} of {count} done); it said: {string.Join(" | ", Lines())}");
    }

    private List<string> Lines()
    {
        lock (_output)
        {
            return [.. _output.Select(o => o.Text.Trim())];
        }
    }
}
