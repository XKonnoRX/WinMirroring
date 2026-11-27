using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
using WinMirroring.Core.Transport;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.Shell.Tests;

/// <summary>
/// A console opened on another device over a paired session, on loopback: the whole way through, which is the only
/// way to know the channel, the asking, the shell and the exit code all line up.
///
/// The two things this is really here to hold are the two that make the feature safe to have: a device that has not
/// turned this on refuses, and a device whose user says no refuses. Both of those are the difference between a
/// convenience and a hole, so neither is allowed to quietly stop working.
/// </summary>
public sealed class ShellIntegrationTests : IAsyncLifetime
{
    private readonly List<MirroringNode> _nodes = [];
    private readonly List<ShellService> _services = [];
    private readonly List<string> _directories = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        foreach (var node in _nodes)
        {
            var thumbprint = node.Settings.CertificateThumbprint;
            await node.DisposeAsync();
            DeviceIdentityStore.RemoveFromStore(thumbprint);
        }

        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ACommandRunsOnTheOtherDeviceAndItsOutputComesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id);
        Assert.NotNull(console);
        var run = Watch(console);

        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.True(console.Run("Write-Output 'hello from over there'"));
        var code = await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
        Assert.Contains(run.Lines, line => line.Contains("hello from over there", StringComparison.Ordinal));
        Assert.NotEmpty(console.Directory);
    }

    /// <summary>The command's own exit code comes back, which is what makes this usable from a script.</summary>
    [Fact]
    public async Task TheCommandsExitCodeComesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run("cmd /c exit 7"));
        Assert.Equal(7, await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    /// <summary>A device that has not turned this on says so, and nothing runs there.</summary>
    [Fact]
    public async Task ADeviceThatHasNotTurnedThisOnRefuses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: false, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);

        var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("выключено", reason, StringComparison.Ordinal);
        Assert.False(shellB.HasConsolesHere);
    }

    /// <summary>And a device whose user says no refuses, however the setting stands.</summary>
    [Fact]
    public async Task ADeviceWhoseUserSaysNoRefuses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: false, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);

        var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("отказал", reason, StringComparison.Ordinal);
        Assert.False(shellB.HasConsolesHere);
    }

    /// <summary>
    /// The console is one console: what one command does to it is still true for the next, over the wire as much as
    /// in the shell itself.
    /// </summary>
    [Fact]
    public async Task TheConsoleKeepsItsPlaceBetweenCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run("Set-Location C:\\Windows"));
        await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal("C:\\Windows", console.Directory, ignoreCase: true);

        var second = Watch(console);
        Assert.True(console.Run("Write-Output (Get-Location).Path"));
        await second.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Contains(second.Lines, line => line.Equals("C:\\Windows", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A command whose output does not end with a newline still finishes, and its last words still arrive.
    /// powercfg /getactivescheme is one such program: it leaves the line open, the marker that says the command
    /// is over gets printed onto the end of that line, and a marker looked for only at the start of a line was
    /// never seen at all — so the command never appeared to finish and the whole thing ended as a lost
    /// connection. Found by using this on a real tablet.
    /// </summary>
    [Fact]
    public async Task ACommandThatEndsWithoutANewlineStillFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run("[Console]::Out.Write('без перевода строки на конце')"));

        Assert.Equal(0, await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Contains(run.Lines, line => line.Contains("без перевода строки на конце", StringComparison.Ordinal));
    }

    /// <summary>
    /// A command that failed as PowerShell rather than as a program comes back as a failure. Nothing exited, so
    /// there is no exit code to report, and what used to be reported instead was zero — a script with a syntax
    /// error in it came back a success, which for something a script reads is the worst answer available.
    /// </summary>
    [Fact]
    public async Task ACommandThatFailedWithoutAProgramComesBackAsAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run("Get-Item C:\\нет-такого-пути-и-не-было"));
        Assert.NotEqual(0, await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    /// <summary>
    /// And one command's exit code is not the next one's. The variable PowerShell keeps it in holds whatever the
    /// last program to exit left there, and a console runs many commands: a "cmd /c exit 7" used to make every
    /// cmdlet after it report 7 too, until the next program came along.
    /// </summary>
    [Fact]
    public async Task AnExitCodeDoesNotStickToTheCommandAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var first = Watch(console);
        await first.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run("cmd /c exit 7"));
        Assert.Equal(7, await first.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));

        var second = Watch(console);
        Assert.True(console.Run("Write-Output 'и это получилось'"));
        Assert.Equal(0, await second.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    /// <summary>
    /// A command sent by a tool on this machine, through the app, rather than by a device of its own. This is the
    /// whole reason the command-line tool is not a device: the app has the session and the pairing already, so the
    /// device on the other end sees a command from the machine it knows and nothing new appears in its list.
    /// </summary>
    [Fact]
    public async Task AToolOnThisMachineRunsSomethingThroughTheApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var lines = new List<string>();
        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var code = await tool.RunAsync(
            b.Identity.Id,
            "Write-Output 'через приложение'",
            (_, text) => Collect(lines, text),
            reasons.Add,
            ct);

        Assert.Equal(0, code);
        Assert.Empty(reasons);
        Assert.Contains(lines, line => line.Contains("через приложение", StringComparison.Ordinal));
    }

    /// <summary>
    /// And it is told why, in words, when there is no exit code to give — with the code that says which kind of
    /// nothing happened, because a script reads that and a person reads the words.
    /// </summary>
    [Fact]
    public async Task AToolIsToldWhyWhenTheDeviceWillNotRunCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: false, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var lines = new List<string>();
        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var code = await tool.RunAsync(b.Identity.Id, "Write-Output 'nope'", (_, text) => Collect(lines, text), reasons.Add, ct);

        Assert.Equal(ShellExit.Refused, code);
        Assert.Contains(reasons, reason => reason.Contains("выключено", StringComparison.Ordinal));
        Assert.Empty(lines);
    }

    /// <summary>
    /// A script longer than one message arrives whole and runs as one script. One message holds eight kilobytes
    /// and a command used to be cut to that without a word, so the front of a script ran as though it were all of
    /// it — and the tool takes a script from a file, of any length, which is exactly how one gets this long.
    /// </summary>
    [Fact]
    public async Task AScriptTooLongForOneMessageArrivesWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var padding = string.Concat(Enumerable.Range(0, 400).Select(i => $"# строка {i}, которая здесь только для длины\n"));
        var script = padding + "Write-Output 'конец длинного скрипта'";
        Assert.True(ShellCodec.EncodeCommand(0, script).Count > 1, "this script should not fit in one message");

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run(script));
        Assert.Equal(0, await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Contains(run.Lines, line => line.Contains("конец длинного скрипта", StringComparison.Ordinal));
    }

    /// <summary>
    /// A script too long to be put back together runs no part of itself, and says so once. Past the limit, what
    /// had arrived used to be thrown away and the rest collected as though a new command had begun — so the last
    /// message ran the tail of the script on its own, in a console that was still open to take it.
    /// </summary>
    [Fact]
    public async Task AScriptTooLongToPutBackTogetherRunsNoPartOfItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var padding = new System.Text.StringBuilder();
        while (padding.Length <= CommandAssembly.Longest + 50_000)
        {
            padding.Append("# a line that is only here for its length\n");
        }

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.True(console.Run(padding + "Write-Output 'хвост слишком длинного скрипта'"));
        Assert.Equal(1, await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(60), ct));

        // The console is still there and still a console: the next command is its own, and by the time it has
        // answered, anything the tail was going to print would have been printed.
        var next = Watch(console);
        Assert.True(console.Run("Write-Output 'следующая команда'"));
        Assert.Equal(0, await next.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));

        // In words that can be read — the same ones the elevated helper answers with, from the same place.
        var said = Said(run);
        Assert.Contains("Команда слишком длинная.", said);
        Assert.Contains("следующая команда", said);
        Assert.DoesNotContain(said, line => line.Contains("хвост", StringComparison.Ordinal));
    }

    /// <summary>
    /// A line of output longer than one message comes back as the one line it is — across both hops, device to
    /// app and app to tool. It used to arrive as its first few thousand characters and nothing else.
    /// </summary>
    [Fact]
    public async Task ALineOfOutputTooLongForOneMessageComesBackWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var lines = new List<string>();
        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var code = await tool.RunAsync(
            b.Identity.Id,
            "[Console]::Out.WriteLine(('я' * 20000) + ' и конец строки')",
            (_, text) => Collect(lines, text),
            reasons.Add,
            ct);

        Assert.Equal(0, code);
        Assert.Empty(reasons);
        Assert.Contains(new string('я', 20_000) + " и конец строки", lines);
    }

    /// <summary>
    /// A command that ends the shell itself comes back with the code it ended it with. <c>exit 3</c> at the end of
    /// a script is as ordinary as scripts get, and it used to come back as a refusal by the device, with no reason.
    /// </summary>
    [Fact]
    public async Task ACommandThatEndsTheShellComesBackWithItsExitCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var lines = new List<string>();
        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var code = await tool.RunAsync(
            b.Identity.Id,
            "Write-Output 'перед выходом'; exit 3",
            (_, text) => Collect(lines, text),
            reasons.Add,
            ct);

        Assert.Equal(3, code);
        Assert.Empty(reasons);
        Assert.Contains("перед выходом", lines);
        await WaitUntilAsync(() => !shellB.HasConsolesHere, ct, "the console that ended itself is gone from the device");
    }

    /// <summary>
    /// And where no code comes at all — a device on a build that does not send one, standing in here as a console
    /// that just ends — it is not called a refusal. Nobody said no; what is known is said, under a code of its own.
    /// </summary>
    [Fact]
    public async Task AConsoleThatEndsWithoutACodeIsNotARefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct, allowElevatedThere: true);
        shellB.StartElevatedHost = _ => Task.FromResult<WinMirroring.Shell.Elevated.IElevatedHost?>(new OneHelper(() => new ShellThatEndsWithoutACode()));

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var code = await tool.RunAsync(b.Identity.Id, "exit 3", (_, _) => { }, reasons.Add, ct, elevated: true);

        Assert.Equal(ShellExit.ConsoleEnded, code);
        Assert.NotEqual(ShellExit.Refused, code);
        Assert.Contains(reasons, reason => reason.Contains("не сообщив код", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.Contains("отказал", StringComparison.Ordinal));
    }

    /// <summary>
    /// Switching commands off closes the consoles already open and forgets every yes. The setting used to be
    /// looked at only when a console was opened: a device that had one kept it for as long as it liked, and a
    /// device agreed to once was back in without a question the moment the switch was turned on again.
    /// </summary>
    [Fact]
    public async Task SwitchingCommandsOffClosesOpenConsolesAndForgetsTheYes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.True(console.Run("Start-Sleep -Seconds 30"));
        Assert.Equal(1, shellB.ConsolesHere);

        b.Settings.Shell.AllowCommands = false;
        shellB.ApplySettings();

        // Gone at once on the device, and the device that had it is told why.
        Assert.Equal(0, shellB.ConsolesHere);
        var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("выключено", reason, StringComparison.Ordinal);
        Assert.False(console.Run("Write-Output 'уже некуда'"));

        // Back on: the same device is a device to be asked about again.
        b.Settings.Shell.AllowCommands = true;
        shellB.ApplySettings();
        Assert.Equal(1, Volatile.Read(ref _asked));

        var again = shellA.Open(b.Identity.Id)!;
        await Watch(again).Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(2, Volatile.Read(ref _asked));
    }

    /// <summary>
    /// Switching only administrator commands off ends the administrator consoles, their yes and the helper that
    /// holds the rights — and leaves an ordinary console alone, since that permission still stands.
    /// </summary>
    [Fact]
    public async Task SwitchingAdministratorCommandsOffEndsOnlyAdministratorConsoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct, allowElevatedThere: true);

        var helpers = new List<OneHelper>();
        shellB.StartElevatedHost = _ =>
        {
            var helper = new OneHelper();
            lock (helpers)
            {
                helpers.Add(helper);
            }

            return Task.FromResult<WinMirroring.Shell.Elevated.IElevatedHost?>(helper);
        };

        var ordinary = shellA.Open(b.Identity.Id)!;
        var ordinaryRun = Watch(ordinary);
        await ordinaryRun.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        var administrator = shellA.Open(b.Identity.Id, elevated: true)!;
        var administratorRun = Watch(administrator);
        await administratorRun.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(2, shellB.ConsolesHere);
        Assert.Equal(2, Volatile.Read(ref _asked));

        b.Settings.Shell.AllowElevatedCommands = false;
        shellB.ApplySettings();

        Assert.Equal(1, shellB.ConsolesHere);
        var reason = await administratorRun.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("администратора", reason, StringComparison.Ordinal);
        await WaitUntilAsync(() => !helpers[0].IsAlive, ct, "the helper that held administrator rights is ended");

        // The ordinary console was never this setting's to close.
        Assert.True(ordinary.Run("Write-Output 'обычная на месте'"));
        Assert.Equal(0, await ordinaryRun.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.False(ordinaryRun.Refused.Task.IsCompleted);

        // Back on: asked about again here, and a helper started again — which, for real, is Windows asking again.
        b.Settings.Shell.AllowElevatedCommands = true;
        shellB.ApplySettings();
        var again = shellA.Open(b.Identity.Id, elevated: true)!;
        await Watch(again).Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(3, Volatile.Read(ref _asked));
        Assert.Equal(2, helpers.Count);
    }

    /// <summary>
    /// A question can sit on a screen for an hour, and the setting can be switched off under it. A yes that comes
    /// after that opens nothing and is not kept.
    /// </summary>
    [Fact]
    public async Task AYesGivenAfterCommandsWereSwitchedOffOpensNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct, dialogThere: answer.Task);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await WaitUntilAsync(() => Volatile.Read(ref _asked) == 1, ct, "the device is asking about the console");

        b.Settings.Shell.AllowCommands = false;
        shellB.ApplySettings();
        answer.TrySetResult(true);

        var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("выключено", reason, StringComparison.Ordinal);
        Assert.False(shellB.HasConsolesHere);

        // And that yes was to something no longer allowed, so it is not the answer to the next asking either.
        b.Settings.Shell.AllowCommands = true;
        shellB.ApplySettings();
        var again = shellA.Open(b.Identity.Id)!;
        await Watch(again).Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(2, Volatile.Read(ref _asked));
    }

    private static List<string> Said(Run run)
    {
        lock (run.Lines)
        {
            return [.. run.Lines];
        }
    }

    private static void Collect(List<string> lines, string text)
    {
        lock (lines)
        {
            lines.Add(text.Trim());
        }
    }

    /// <summary>
    /// The pipe is opened by a loop of its own in the server, so it is listening a moment after Start rather than
    /// at it; a tool in the ordinary way would simply find nothing and do the work itself.
    /// </summary>
    private static async Task<Local.LocalShellClient> ConnectToolAsync(string pipe, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await Local.LocalShellClient.TryConnectAsync(ct, pipe) is { } client)
            {
                return client;
            }

            await Task.Delay(50, ct);
        }

        Assert.Fail("the local command pipe never answered");
        return null!;
    }

    /// <summary>
    /// An administrator console needs a setting of its own, and the ordinary one being on is not it. Refused
    /// before anyone is asked anything: this is not a power the asking device already had, so it does not get to
    /// put a question about administrator rights on someone's screen just by asking.
    /// </summary>
    [Fact]
    public async Task AnElevatedConsoleIsRefusedWhereThatIsNotAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id, elevated: true)!;
        var run = Watch(console);

        var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("администратора", reason, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _asked));
        Assert.False(shellB.HasConsolesHere);
    }

    /// <summary>
    /// A no is remembered for nothing: the next administrator console asks again. Only a yes is remembered, and
    /// only for as long as the app runs.
    /// </summary>
    [Fact]
    public async Task AnElevatedConsoleRefusedByTheUserIsAskedAboutAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: false, ct, allowElevatedThere: true);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var console = shellA.Open(b.Identity.Id, elevated: true)!;
            var run = Watch(console);
            var reason = await run.Refused.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Assert.Contains("отказал", reason, StringComparison.Ordinal);
            Assert.Equal(attempt, Volatile.Read(ref _asked));
        }

        Assert.False(shellB.HasConsolesHere);
    }

    /// <summary>
    /// And a yes is asked for once per run of the app — the dialog here and, with it, Windows's own prompt, since
    /// the helper that prompt is about is started once and kept. Both questions are shown on the device being
    /// managed, which is usually not the device of the person managing it: one script that opened three
    /// administrator consoles used to mean six things to tap on a tablet in another room.
    ///
    /// The helper is faked here because a test cannot answer a UAC prompt. What it holds is the counting: one
    /// dialog, one helper started, and a console out of that same helper every time afterwards.
    /// </summary>
    [Fact]
    public async Task AnElevatedConsoleIsAgreedToOncePerRunOfTheApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct, allowElevatedThere: true);

        var helpers = 0;
        var helper = new OneHelper();
        shellB.StartElevatedHost = _ =>
        {
            Interlocked.Increment(ref helpers);
            return Task.FromResult<WinMirroring.Shell.Elevated.IElevatedHost?>(helper);
        };

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var console = shellA.Open(b.Identity.Id, elevated: true)!;
            var run = Watch(console);
            await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            console.Close();
            await WaitUntilAsync(() => !shellB.HasConsolesHere, ct, $"console {attempt} is closed over there");
        }

        Assert.Equal(1, Volatile.Read(ref _asked));
        Assert.Equal(1, Volatile.Read(ref helpers));
        Assert.Equal(3, helper.Consoles);
    }

    /// <summary>
    /// While somebody at the device is being asked, whoever asked is told so — and goes on waiting, because that
    /// is the right thing to do and the one thing it could not know. A night of this is what prompted it: a tool
    /// waiting on a dialog on a tablet in another room looks exactly like a tool waiting on a broken device, and
    /// what gets blamed is the connection.
    /// </summary>
    [Fact]
    public async Task AToolIsToldThatSomebodyIsBeingAskedAndGoesOnWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct, dialogThere: answer.Task);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var told = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var running = tool.RunAsync(
            b.Identity.Id,
            "Write-Output 'после ответа'",
            (_, text) => Collect(lines, text),
            reasons.Add,
            ct,
            false,
            question => told.TrySetResult(question));

        var asked = await told.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("спрашивают", asked, StringComparison.Ordinal);
        Assert.False(running.IsCompleted);
        Assert.Empty(reasons);

        answer.TrySetResult(true);
        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Contains(lines, line => line.Contains("после ответа", StringComparison.Ordinal));
    }

    /// <summary>
    /// A console given up on while the question is still on somebody's screen is not opened when the answer
    /// finally comes. It used to be: the tool was killed, the dialog was answered an hour later, and a PowerShell
    /// started on the device for a command that no longer existed and sat there until the session closed.
    /// </summary>
    [Fact]
    public async Task AConsoleGivenUpOnWhileBeingAskedAboutIsNeverOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct, dialogThere: answer.Task);

        var abandoned = shellA.Open(b.Identity.Id)!;
        await WaitUntilAsync(() => Volatile.Read(ref _asked) == 1, ct, "the device is asking about the console");
        abandoned.Close();

        // The question is still on the screen over there, and the device now knows nobody is waiting for the
        // answer. Waited for rather than assumed: the whole point is what the device does with an answer that
        // arrives after that.
        await WaitUntilAsync(() => shellB.GivenUpOnHere == 1, ct, "the device knows the asker is gone");
        answer.TrySetResult(true);

        // And the answer has been dealt with: until it has, a second console would find nothing remembered yet
        // and put its own question up, which is a race in this test and not the thing being tested.
        await WaitUntilAsync(() => shellB.OpeningHere == 0, ct, "the device finished with the first console");

        // A second console, agreed to without asking again, is the only one that opens.
        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.Equal(1, Volatile.Read(ref _asked));
        Assert.Equal(1, shellB.ConsolesHere);
    }

    /// <summary>
    /// A console closed from the side that opened it is closed on the device as well. It was not, and for a long
    /// time: the message that says so is sent in both directions, and the device took it for an answer to a
    /// console of its own, found none and dropped it — so every console ever closed from the other end left its
    /// PowerShell running on the managed device until the session itself ended. Days, on a tablet that stays on.
    /// </summary>
    [Fact]
    public async Task AConsoleClosedByTheAskerIsClosedOnTheDeviceToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var console = shellA.Open(b.Identity.Id)!;
        var run = Watch(console);
        await run.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(1, shellB.ConsolesHere);

        console.Close();

        await WaitUntilAsync(() => shellB.ConsolesHere == 0, ct, "the console is closed on the device too");
    }

    /// <summary>
    /// Tools killed while a device is still asking somebody give their instance of the pipe back. There are only
    /// so many instances of one named pipe, and when they are all taken no new one can be created at all — so a
    /// handful of tools killed mid-wait used to leave the whole local console dead for everything until those
    /// dialogs were answered. More tools here than the pipe has instances, every one of them killed mid-wait, and
    /// then one that has to work.
    /// </summary>
    [Fact]
    public async Task ToolsKilledWhileWaitingGiveTheirPipeInstanceBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (a, b, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct, dialogThere: answer.Task);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var killed = new List<Task<int>>();
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var tool = await ConnectToolAsync(pipe, ct);
            killed.Add(tool.RunAsync(b.Identity.Id, "Write-Output 'не дождётся'", (_, _) => { }, _ => { }, ct));
            await WaitUntilAsync(() => Volatile.Read(ref _asked) >= attempt, ct, $"the device is asked for tool {attempt}");
            await tool.DisposeAsync();
        }

        foreach (var abandoned in killed)
        {
            try
            {
                await abandoned.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception)
            {
                // Killed on purpose; what became of each is not the point.
            }
        }

        answer.TrySetResult(true);

        var lines = new List<string>();
        var reasons = new List<string>();
        await using var last = await ConnectToolAsync(pipe, ct);
        var code = await last.RunAsync(b.Identity.Id, "Write-Output 'канал жив'", (_, text) => Collect(lines, text), reasons.Add, ct);

        Assert.Equal(0, code);
        Assert.Contains(lines, line => line.Contains("канал жив", StringComparison.Ordinal));
    }

    /// <summary>
    /// A device that stops answering is not a device that refused, and must not be reported as one. A tablet that
    /// falls asleep while a command is running ends the console exactly as a person saying no does — and the two
    /// call for opposite things from whoever is reading: go and wake the device, or stop asking. It used to read
    /// "отказал" and exit with the code for a refusal, and the night that was spent on it was spent looking for
    /// somebody's no.
    /// </summary>
    [Fact]
    public async Task ADeviceThatStopsAnsweringIsNotADeviceThatRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, shellA, shellB) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(a, shellA, NullLogger<Local.LocalShellServer>.Instance, pipe);
        server.Start();

        var reasons = new List<string>();
        await using var tool = await ConnectToolAsync(pipe, ct);
        var running = tool.RunAsync(b.Identity.Id, "Start-Sleep -Seconds 30", (_, _) => { }, reasons.Add, ct);

        // Open over there first, so what ends it is the device going and not the asking.
        await WaitUntilAsync(() => shellB.HasConsolesHere, ct, "the console is open on the device");

        // And now the device is simply not there any more, which is what sleeping looks like from here.
        Assert.True(a.Sessions.TryGetSession(b.Identity.Id, out var session));
        await session!.CloseAsync("устройство уснуло");

        var code = await running.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(ShellExit.CouldNotReach, code);
        Assert.Contains(reasons, reason => reason.Contains("перестал отвечать", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, reason => reason.Contains("отказал", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tool that connects and never says what it wants is let go of. Anything after that may take as long as it
    /// takes — a command can run for an hour, a person can be asked for a day — but a connection that has not
    /// even asked for anything is waiting for nothing, and it was holding one of the pipe's few instances while
    /// it did.
    /// </summary>
    [Fact]
    public async Task AToolThatNeverSaysWhatToRunIsLetGoOf()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, _, shellA, _) = await ConnectAsync(allowThere: true, agreeThere: true, ct);

        var pipe = $"WinMirroring-tests-{Guid.NewGuid():N}";
        await using var server = new Local.LocalShellServer(
            a,
            shellA,
            NullLogger<Local.LocalShellServer>.Instance,
            pipe,
            TimeSpan.FromMilliseconds(300));
        server.Start();

        await using var silent = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        for (var attempt = 0; attempt < 40 && !silent.IsConnected; attempt++)
        {
            try
            {
                await silent.ConnectAsync(100, ct);
            }
            catch (TimeoutException)
            {
            }
        }

        Assert.True(silent.IsConnected, "the local command pipe never answered");

        // A reason and then a code, as every ending on this pipe is.
        var reader = new FrameCodec.Reader();
        var refusal = await reader.ReadAsync(silent, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(refusal);
        Assert.True(ShellCodec.TryReadHeader(refusal.Value.Payload, out var type, out _));
        Assert.Equal(ShellMessageType.Refused, type);
        Assert.True(ShellCodec.TryDecodeText(refusal.Value.Payload, out var reason));
        Assert.Contains("не сказал", reason, StringComparison.Ordinal);

        var ending = await reader.ReadAsync(silent, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(ending);
        Assert.True(ShellCodec.TryDecodeDone(ending.Value.Payload, out var code, out _));
        Assert.Equal(ShellExit.CouldNotReach, code);
    }

    /// <summary>
    /// An elevated helper with no prompt in front of it. What it is here for is the counting: how many times one
    /// was started, and how many consoles came out of the one that was.
    /// </summary>
    private sealed class OneHelper(Func<IHostedShell>? console = null) : WinMirroring.Shell.Elevated.IElevatedHost
    {
        private int _consoles;
        private int _disposed;

        public int Consoles => Volatile.Read(ref _consoles);

        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        public IHostedShell? Open()
        {
            Interlocked.Increment(ref _consoles);
            return console?.Invoke() ?? new PretendShell();
        }

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }

    /// <summary>
    /// A console whose shell is ended by the command it is given and says nothing first — which is all a device on
    /// a build older than the exit code being reported ever did for <c>exit 3</c>.
    /// </summary>
    private sealed class ShellThatEndsWithoutACode : IHostedShell
    {
        public event Action<ShellStreamKind, string>? Output
        {
            add { }
            remove { }
        }

        public event Action<int, string>? Done
        {
            add { }
            remove { }
        }

        public event Action? Ended;

        public Task Ready => Task.CompletedTask;

        public string Directory => @"C:\Windows\System32";

        public bool Run(string command)
        {
            Ended?.Invoke();
            return true;
        }

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A console that answers, so that what is being counted is the asking and not PowerShell.</summary>
    private sealed class PretendShell : IHostedShell
    {
        public event Action<ShellStreamKind, string>? Output;

        public event Action<int, string>? Done;

        public event Action? Ended;

        public Task Ready => Task.CompletedTask;

        public string Directory => @"C:\Windows\System32";

        public bool Run(string command)
        {
            Output?.Invoke(ShellStreamKind.Output, command);
            Done?.Invoke(0, Directory);
            return true;
        }

        public void Cancel() => Ended?.Invoke();

        public void Dispose() => Ended?.Invoke();
    }

    /// <summary>Everything one console says, collected; the waits are off the receive loop on purpose.</summary>
    private sealed class Run
    {
        public TaskCompletionSource<string> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Lines { get; } = [];
    }

    private static Run Watch(RemoteConsole console)
    {
        var run = new Run();
        console.Opened += directory => run.Opened.TrySetResult(directory);
        console.Refused += reason => run.Refused.TrySetResult(reason);
        console.Output += (_, text) =>
        {
            lock (run.Lines)
            {
                run.Lines.Add(text.Trim());
            }
        };

        console.Done += (code, _) => run.Done.TrySetResult(code);
        return run;
    }

    /// <summary>How many times the device has asked whoever is sitting at it. Only B is ever asked here.</summary>
    private int _asked;

    /// <summary>
    /// <paramref name="dialogThere"/> stands in for the dialog on B: a task that does not finish is a question
    /// still on somebody's screen, which is the state most of the awkward things here happen in.
    /// </summary>
    private async Task<(MirroringNode A, MirroringNode B, ShellService ShellA, ShellService ShellB)> ConnectAsync(
        bool allowThere,
        bool agreeThere,
        CancellationToken ct,
        bool allowElevatedThere = false,
        Task<bool>? dialogThere = null)
    {
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ShellA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("ShellB", discoveryB, $"127.0.0.1:{discoveryA}");
        b.Settings.Shell.AllowCommands = allowThere;
        b.Settings.Shell.AllowElevatedCommands = allowElevatedThere;
        await a.StartAsync();
        await b.StartAsync();

        var shellA = CreateShell(a, agree: false);
        var shellB = CreateShell(b, agree: agreeThere, dialog: dialogThere);

        await WaitUntilAsync(() => b.Peers.TryGet(a.Identity.Id, out var peer) && peer.TransportEndPoint is not null, ct, "B sees A's address");
        Assert.True(await b.PairAsync(a.Identity.Id, ct));

        // Both services attached, not merely both sessions ready: the service attaches from the session-ready
        // event, so for a moment after a session calls itself ready there is nobody on the channel.
        await WaitUntilAsync(
            () => shellA.IsAttachedTo(b.Identity.Id) && shellB.IsAttachedTo(a.Identity.Id),
            ct,
            "both console services are attached to the session");
        return (a, b, shellA, shellB);
    }

    private ShellService CreateShell(MirroringNode node, bool agree, Task<bool>? dialog = null)
    {
        var service = new ShellService(node, NullLogger<ShellService>.Instance);
        service.Confirm = (_, _) =>
        {
            Interlocked.Increment(ref _asked);
            return dialog ?? Task.FromResult(agree);
        };
        service.Start();
        _services.Add(service);
        return service;
    }

    private MirroringNode CreateNode(string name, int discoveryPort, string manualAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinMirroring.ShellTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        var node = new MirroringNode(
            new NodeOptions
            {
                DataDirectory = directory,
                IncludeLoopback = true,
                DeviceName = name,
                DiscoveryPort = discoveryPort,
                TransportPort = FreeTcpPort(),
                ManualAddresses = [manualAddress],
            },
            new AcceptAllPairing(),
            NullLoggerFactory.Instance);
        node.Settings.SelectedInterfaceIds.Add(NetworkInterfaceCatalog.Enumerate(includeLoopback: true).First(i => i.IsLoopback).Id);
        _nodes.Add(node);
        return node;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"waited too long for: {what}");
            await Task.Delay(50, ct);
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class AcceptAllPairing : IPairingUserInterface
    {
        public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct) => Task.FromResult(true);
    }
}
