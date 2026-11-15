using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Network;
using WinMirroring.Core.Pairing;
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

    private async Task<(MirroringNode A, MirroringNode B, ShellService ShellA, ShellService ShellB)> ConnectAsync(
        bool allowThere,
        bool agreeThere,
        CancellationToken ct)
    {
        var discoveryA = FreeUdpPort();
        var discoveryB = FreeUdpPort();
        var a = CreateNode("ShellA", discoveryA, $"127.0.0.1:{discoveryB}");
        var b = CreateNode("ShellB", discoveryB, $"127.0.0.1:{discoveryA}");
        b.Settings.Shell.AllowCommands = allowThere;
        await a.StartAsync();
        await b.StartAsync();

        var shellA = CreateShell(a, agree: false);
        var shellB = CreateShell(b, agree: agreeThere);

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

    private ShellService CreateShell(MirroringNode node, bool agree)
    {
        var service = new ShellService(node, NullLogger<ShellService>.Instance);
        service.Confirm = (_, _) => Task.FromResult(agree);
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
