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

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information));

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
