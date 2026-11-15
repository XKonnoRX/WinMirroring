using Microsoft.Extensions.Logging;
using WinMirroring.Cli;
using WinMirroring.Core;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Peers;

var options = CliOptions.Parse(args);
if (options is null)
{
    CliOptions.PrintUsage();
    return 1;
}

// Running a command is meant to be used from a script, so in that mode standard output belongs to the command
// being run and nothing else. Rather than hunting down every line this tool prints, its output is pointed at
// standard error wholesale, and the command's own output is written through the writer kept here, which is the
// real one. Before the log is built, not after: the console logger keeps hold of whichever writer it was made
// with, so a log built first would go on writing to the output a script is reading.
var stdout = Console.Out;
if (options.IsExec)
{
    Console.SetOut(Console.Error);
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(options.Verbose ? LogLevel.Debug : options.IsExec ? LogLevel.Warning : LogLevel.Information));

var nodeOptions = new NodeOptions
{
    DataDirectory = options.DataDirectory ?? WinMirroring.Core.Settings.SettingsStore.DefaultDataDirectory,
    IncludeLoopback = options.Loopback,
    DeviceName = options.Name,
    DiscoveryPort = options.DiscoveryPort,
    TransportPort = options.TransportPort,
    ManualAddresses = options.ManualAddresses,
};

await using var node = new MirroringNode(nodeOptions, new ConsolePairingUi(options.AutoAccept), loggerFactory);

Console.WriteLine($"DEVICE {node.Identity.Name} id={node.Identity.Id} fp={CertificateFingerprint.ToDisplay(node.Identity.Fingerprint)}");
Console.WriteLine($"INTERFACES {string.Join("; ", node.Network.SelectedInterfaces)}");

var ready = new TaskCompletionSource<PeerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
var pairingStarted = 0;

node.Sessions.SessionReady += session =>
{
    Console.WriteLine($"READY {session.PeerName} via {session.RemoteEndPoint} rtt={session.LastRoundTrip?.TotalMilliseconds ?? 0:F0}ms");
    ready.TrySetResult(session);
};
node.Sessions.SessionClosed += session => Console.WriteLine($"CLOSED {session.PeerName}");

node.Peers.Changed += peer =>
{
    Console.WriteLine($"PEER {peer.Name} [{peer.Id[..Math.Min(8, peer.Id.Length)]}] {peer.State} paired={peer.IsPaired} at={peer.TransportEndPoint}");

    if (options.PairWith is null
        || peer.IsPaired
        || peer.TransportEndPoint is null
        || peer.State is not (PeerState.Discovered or PeerState.Connected)
        || !(string.Equals(peer.Name, options.PairWith, StringComparison.OrdinalIgnoreCase)
             || string.Equals(peer.Id, options.PairWith, StringComparison.OrdinalIgnoreCase))
        || Interlocked.CompareExchange(ref pairingStarted, 1, 0) != 0)
    {
        return;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            var ok = await node.PairAsync(peer.Id).ConfigureAwait(false);
            Console.WriteLine(ok ? $"PAIRED {peer.Name}" : $"PAIR FAILED {peer.Name}");
            if (!ok)
            {
                Volatile.Write(ref pairingStarted, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PAIR ERROR {peer.Name}: {ex.Message}");
            Volatile.Write(ref pairingStarted, 0);
        }
    });
};

await node.StartAsync().ConfigureAwait(false);
Console.WriteLine($"LISTENING {string.Join(", ", node.ListeningEndPoints)} discovery udp/{node.Settings.DiscoveryPort}");

WinMirroring.Input.InputSharingService? input = null;
if (options.Input)
{
    input = new WinMirroring.Input.InputSharingService(node, loggerFactory.CreateLogger<WinMirroring.Input.InputSharingService>());
    input.FocusChanged += peerId => Console.WriteLine(peerId is null ? "INPUT home" : $"INPUT on {peerId}");
    input.LayoutChanged += () => Console.WriteLine($"LAYOUT {string.Join("; ", input.Layout.Cells.Select(c => $"{c.DeviceId[..Math.Min(8, c.DeviceId.Length)]}@{c.Column},{c.Row}"))}");
    input.Start();
    Console.WriteLine($"INPUT sharing {(input.IsEnabled ? "enabled" : "disabled in settings")}");
}

await using var inputDisposer = input;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

if (options.TimeoutSeconds is { } seconds)
{
    shutdown.CancelAfter(TimeSpan.FromSeconds(seconds));
}

// A console both ways: this node can run commands on another device, and — with --accept, and only when its own
// settings allow commands at all — let another device run them here. That second half is what makes a headless
// node something a script can manage rather than only something that manages.
await using var shell = new WinMirroring.Shell.ShellService(node, loggerFactory.CreateLogger<WinMirroring.Shell.ShellService>());
shell.Confirm = (_, _) => Task.FromResult(options.AutoAccept);
shell.Notify = (title, text) => Console.WriteLine($"SHELL {title}: {text}");
shell.Start();

if (options.IsExec)
{
    return await Exec.RunAsync(node, shell, options, stdout, shutdown.Token).ConfigureAwait(false);
}

try
{
    if (options.ExitWhenReady)
    {
        await ready.Task.WaitAsync(shutdown.Token).ConfigureAwait(false);
        Console.WriteLine("EXIT ready");
        return 0;
    }

    await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    if (options.ExitWhenReady && !ready.Task.IsCompletedSuccessfully)
    {
        Console.WriteLine("EXIT timeout");
        return 2;
    }
}

Console.WriteLine("EXIT");
return 0;
